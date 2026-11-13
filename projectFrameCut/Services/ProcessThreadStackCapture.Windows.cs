#if WINDOWS && DEBUG
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace projectFrameCut.Services
{
    internal static class ProcessThreadStackCapture
    {
        private static readonly object Gate = new();

        internal static (string? MainStack, List<string> AllStacks) Capture(Process process, string reason, uint uiThreadId = 0)
        {
            // DbgHelp 的所有调用必须串行；释放快照后再写日志。
            lock (Gate)
                return ReadThreadStacks(process, reason, uiThreadId);
        }

        internal static string SaveToLogDirectory(int processId, string prefix, List<string> stacks)
        {
            string path = Path.Combine(Path.GetDirectoryName(MauiProgram.LogPath)
                ?? throw new InvalidOperationException("Log directory is unavailable."),
                $"{prefix}-threads-{DateTime.Now:yyyy-MM-dd-HH-mm-ss-fffffff}-{processId}.log");
            File.WriteAllLines(path, stacks, Encoding.UTF8);
            return path;
        }

        private static (string? MainStack, List<string> AllStacks) ReadThreadStacks(Process process, string reason, uint uiThreadId)
        {
            var stacks = new List<string>();
            string? uiStack = null;
            uint contextFlags = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => 0x10001F,
                Architecture.Arm64 => 0x400007,
                _ => throw new PlatformNotSupportedException($"Thread stack capture does not support {RuntimeInformation.ProcessArchitecture}.")
            };
            if (!IsWow64Process2(process.Handle, out ushort processMachine, out ushort nativeMachine))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((processMachine == 0 ? nativeMachine : processMachine) !=
                (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0xAA64 : 0x8664))
                throw new PlatformNotSupportedException("Thread stack capture requires matching process architectures.");

            // 同时捕获内存和线程上下文，避免用运行中线程的寄存器读取旧快照。
            CheckSnapshotResult(PssCaptureSnapshot(process.Handle, 0x181, contextFlags, out IntPtr snapshot));
            IntPtr clone = IntPtr.Zero;
            try
            {
                CheckSnapshotResult(PssQuerySnapshot(snapshot, 1, out clone, IntPtr.Size));
                var contexts = new Dictionary<uint, byte[]>();
                CheckSnapshotResult(PssWalkMarkerCreate(IntPtr.Zero, out IntPtr marker));
                try
                {
                    int error;
                    while ((error = PssWalkSnapshot(snapshot, 3, marker, out PssThreadEntry t, Marshal.SizeOf<PssThreadEntry>())) == 0)
                    {
                        byte[] context = t.ContextRecord == IntPtr.Zero ? [] : new byte[t.SizeOfContextRecord];
                        if (context.Length > 0)
                            Marshal.Copy(t.ContextRecord, context, 0, context.Length);
                        contexts[t.ThreadId] = context;
                    }
                    if (error != 259) // ERROR_NO_MORE_ITEMS
                        CheckSnapshotResult(error);
                }
                finally
                {
                    CheckSnapshotResult(PssWalkMarkerFree(marker));
                }

                stacks.Add($"{reason} snapshot at {DateTime.UtcNow:O}: PID={process.Id}, {contexts.Count} OS threads{(uiThreadId == 0 ? "" : $", UI thread={uiThreadId}")}.");
                var options = new DataTargetOptions
                {
                    SymbolPaths = [],
                    Limits = new DataTargetLimits { MaxStackFrames = 512 }
                };
                using var cloneTarget = DataTarget.AttachToProcess((int)GetProcessId(clone), suspend: false, options: options);
                using var target = new DataTarget(new SnapshotThreadReader(cloneTarget.DataReader, contexts), options);
                var managed = new Dictionary<uint, string>();
                foreach (ClrInfo clr in target.ClrVersions)
                {
                    try
                    {
                        using ClrRuntime runtime = clr.CreateRuntime();
                        foreach (ClrThread t in runtime.Threads)
                        {
                            if (!t.IsAlive)
                                continue;

                            var stack = new StringBuilder($"Managed thread {t.ManagedThreadId}, state={t.State}:\n");
                            try
                            {
                                int count = 0;
                                foreach (ClrStackFrame f in t.EnumerateStackTrace())
                                {
                                    stack.AppendLine($"  SP=0x{f.StackPointer:X16} IP=0x{f.InstructionPointer:X16} {f}");
                                    if (++count >= 512)
                                    {
                                        stack.AppendLine("  Stack truncated at 512 frames.");
                                        break;
                                    }
                                }
                                if (count == 0)
                                    stack.AppendLine("  No managed frames available.");
                            }
                            catch (Exception ex)
                            {
                                stack.AppendLine($"  Managed stack capture failed: {ex}");
                            }
                            managed[t.OSThreadId] = stack.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        stacks.Add($"Managed stack capture failed for CLR {clr.Version}: {ex}");
                    }
                }

                ModuleInfo[] modules = target.EnumerateModules().ToArray();
                uint symbolOptions = SymGetOptions();
                bool symbolsReady = false;
                try
                {
                    SymSetOptions(symbolOptions | 0x80006); // NO_PROMPTS | DEFERRED_LOADS | UNDNAME
                    symbolsReady = SymInitialize(clone, $"{AppContext.BaseDirectory};{RuntimeEnvironment.GetRuntimeDirectory()}", true);
                    int symbolError = symbolsReady ? 0 : Marshal.GetLastWin32Error();
                    foreach (var t in contexts)
                    {
                        var stack = new StringBuilder($"OS thread {t.Key} (0x{t.Key:X}){(t.Key == uiThreadId ? ", UI thread" : "")}:\n");
                        stack.Append(managed.GetValueOrDefault(t.Key, "No managed thread information available.\n"));
                        stack.AppendLine("Native stack:");
                        try
                        {
                            if (symbolsReady)
                                ReadNativeStack(clone, target.DataReader, t.Key, t.Value, modules, stack);
                            else
                                stack.AppendLine($"  Symbol handler initialization failed: {new Win32Exception(symbolError).Message}");
                        }
                        catch (Exception ex)
                        {
                            stack.AppendLine($"  Native stack capture failed: {ex}");
                        }
                        string text = stack.ToString();
                        if (t.Key == uiThreadId)
                            uiStack = text;
                        stacks.Add(text);
                    }
                }
                finally
                {
                    if (symbolsReady)
                        SymCleanup(clone);
                    SymSetOptions(symbolOptions);
                }
            }
            finally
            {
                if (clone != IntPtr.Zero && !TerminateProcess(clone, 0))
                    stacks.Add($"Failed to terminate thread stack snapshot clone: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                int error = PssFreeSnapshot(new IntPtr(-1), snapshot);
                if (error != 0)
                    stacks.Add($"Failed to free thread stack snapshot: {new Win32Exception(error).Message}");
            }
            return (uiStack, stacks);
        }

        private static unsafe void ReadNativeStack(IntPtr process, IDataReader reader, uint threadId,
            byte[] context, ModuleInfo[] modules, StringBuilder stack)
        {
            bool arm64 = reader.Architecture == Architecture.Arm64;
            int pcOffset = arm64 ? 0x108 : 0xF8;
            if (context.Length < pcOffset + sizeof(ulong))
            {
                stack.AppendLine("  Thread context unavailable.");
                return;
            }

            var frame = new NativeStackFrame
            {
                Pc = new NativeAddress { Offset = BitConverter.ToUInt64(context, pcOffset), Mode = 3 },
                Frame = new NativeAddress { Offset = BitConverter.ToUInt64(context, arm64 ? 0xF0 : 0xA0), Mode = 3 },
                Stack = new NativeAddress { Offset = BitConverter.ToUInt64(context, arm64 ? 0x100 : 0x98), Mode = 3 }
            };
            ReadMemoryCallback readMemory = (IntPtr _, ulong address, IntPtr buffer, uint size, out uint read) =>
            {
                read = 0;
                try
                {
                    read = (uint)reader.Read(address, new Span<byte>(buffer.ToPointer(), checked((int)size)));
                    return read == size;
                }
                catch
                {
                    return false;
                }
            };

            byte[] symbol = new byte[88 + 1024];
            BinaryPrimitives.WriteUInt32LittleEndian(symbol, 88); // sizeof(SYMBOL_INFO)
            BinaryPrimitives.WriteUInt32LittleEndian(symbol.AsSpan(80), 1024);
            byte* buffer = stackalloc byte[context.Length + 15];
            byte* c = (byte*)(((nuint)buffer + 15) & ~(nuint)15);
            context.AsSpan().CopyTo(new Span<byte>(c, context.Length));
            fixed (byte* s = symbol)
            {
                var seen = new HashSet<(ulong, ulong)>();
                for (int i = 0; i < 512 && frame.Pc.Offset != 0; i++)
                {
                    if (i > 0 && !StackWalk64(arm64 ? 0xAA64u : 0x8664u, process, (IntPtr)threadId, ref frame,
                        (IntPtr)c, readMemory, SymFunctionTableAccess64, SymGetModuleBase64, IntPtr.Zero))
                    {
                        stack.AppendLine("  Native stack walk ended; frames beyond managed/JIT code may be unavailable.");
                        return;
                    }
                    if (frame.Pc.Offset == 0)
                        return;
                    if (!seen.Add((frame.Pc.Offset, frame.Stack.Offset)))
                    {
                        if (i == 1)
                            continue;
                        stack.AppendLine("  Stack walk stopped at a repeated frame.");
                        return;
                    }

                    ulong address = frame.Pc.Offset;
                    ModuleInfo? module = modules.FirstOrDefault(m => address >= m.ImageBase && address - m.ImageBase < (ulong)m.ImageSize);
                    string location = module == null ? "unknown module" : $"{Path.GetFileName(module.FileName)}+0x{address - module.ImageBase:X}";
                    if (SymFromAddr(process, address, out ulong offset, (IntPtr)s))
                        location = $"{Path.GetFileName(module?.FileName)}!{Marshal.PtrToStringAnsi((IntPtr)(s + 84))}+0x{offset:X}";
                    stack.AppendLine($"  SP=0x{frame.Stack.Offset:X16} IP=0x{address:X16} {location}");
                }
                if (frame.Pc.Offset != 0)
                    stack.AppendLine("  Stack truncated at 512 frames.");
            }
        }

        private static void CheckSnapshotResult(int error)
        {
            if (error != 0)
                throw new Win32Exception(error);
        }

        private sealed class SnapshotThreadReader(IDataReader reader, Dictionary<uint, byte[]> contexts) : IDataReader
        {
            public string DisplayName => reader.DisplayName;
            public bool IsThreadSafe => reader.IsThreadSafe;
            public OSPlatform TargetPlatform => reader.TargetPlatform;
            public Architecture Architecture => reader.Architecture;
            public int ProcessId => reader.ProcessId;
            public int PointerSize => reader.PointerSize;
            public IEnumerable<ModuleInfo> EnumerateModules() => reader.EnumerateModules();
            public int Read(ulong address, Span<byte> buffer) => reader.Read(address, buffer);
            public bool Read<T>(ulong address, out T value) where T : unmanaged => reader.Read(address, out value);
            public T Read<T>(ulong address) where T : unmanaged => reader.Read<T>(address);
            public bool ReadPointer(ulong address, out ulong value) => reader.ReadPointer(address, out value);
            public ulong ReadPointer(ulong address) => reader.ReadPointer(address);
            public void FlushCachedData() => reader.FlushCachedData();

            public bool GetThreadContext(uint threadId, uint flags, Span<byte> context)
            {
                if (!contexts.TryGetValue(threadId, out byte[]? captured) || captured.Length == 0)
                    return false;
                context.Clear();
                captured.AsSpan(0, Math.Min(captured.Length, context.Length)).CopyTo(context);
                return true;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PssThreadEntry
        {
            public uint ExitStatus;
            public IntPtr TebBaseAddress;
            public uint ProcessId, ThreadId;
            public UIntPtr AffinityMask;
            public int Priority, BasePriority;
            public IntPtr LastSyscallFirstArgument;
            public ushort LastSyscallNumber;
            public FILETIME CreateTime, ExitTime, KernelTime, UserTime;
            public IntPtr Win32StartAddress;
            public FILETIME CaptureTime;
            public uint Flags;
            public ushort SuspendCount, SizeOfContextRecord;
            public IntPtr ContextRecord;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeAddress
        {
            public ulong Offset;
            public ushort Segment;
            public int Mode;
        }

        // Windows 64 位 STACKFRAME64；其余字段由 DbgHelp 使用。
        [StructLayout(LayoutKind.Explicit, Size = 264)]
        private struct NativeStackFrame
        {
            [FieldOffset(0)] public NativeAddress Pc;
            [FieldOffset(32)] public NativeAddress Frame;
            [FieldOffset(48)] public NativeAddress Stack;
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool ReadMemoryCallback(IntPtr process, ulong address, IntPtr buffer, uint size, out uint read);
        private delegate IntPtr FunctionTableCallback(IntPtr process, ulong address);
        private delegate ulong ModuleBaseCallback(IntPtr process, ulong address);

        [DllImport("kernel32.dll")]
        private static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern int PssCaptureSnapshot(IntPtr process, uint flags, uint contextFlags, out IntPtr snapshot);
        [DllImport("kernel32.dll")]
        private static extern int PssQuerySnapshot(IntPtr snapshot, int informationClass, out IntPtr buffer, int length);
        [DllImport("kernel32.dll")]
        private static extern int PssWalkMarkerCreate(IntPtr allocator, out IntPtr marker);
        [DllImport("kernel32.dll")]
        private static extern int PssWalkSnapshot(IntPtr snapshot, int informationClass, IntPtr marker, out PssThreadEntry entry, int length);
        [DllImport("kernel32.dll")]
        private static extern int PssWalkMarkerFree(IntPtr marker);
        [DllImport("kernel32.dll")]
        private static extern int PssFreeSnapshot(IntPtr process, IntPtr snapshot);
        [DllImport("dbghelp.dll", EntryPoint = "SymInitializeW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SymInitialize(IntPtr process, string searchPath, [MarshalAs(UnmanagedType.Bool)] bool invadeProcess);
        [DllImport("dbghelp.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SymCleanup(IntPtr process);
        [DllImport("dbghelp.dll")]
        private static extern uint SymGetOptions();
        [DllImport("dbghelp.dll")]
        private static extern uint SymSetOptions(uint options);
        [DllImport("dbghelp.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
        [DllImport("dbghelp.dll")]
        private static extern IntPtr SymFunctionTableAccess64(IntPtr process, ulong address);
        [DllImport("dbghelp.dll")]
        private static extern ulong SymGetModuleBase64(IntPtr process, ulong address);
        [DllImport("dbghelp.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StackWalk64(uint machine, IntPtr process, IntPtr thread, ref NativeStackFrame frame,
            IntPtr context, ReadMemoryCallback readMemory, FunctionTableCallback functionTable, ModuleBaseCallback moduleBase, IntPtr translateAddress);
    }
}
#endif
