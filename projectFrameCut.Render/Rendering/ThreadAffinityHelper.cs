using System.ComponentModel;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.Rendering
{
    public static class ThreadAffinityHelper
    {
        public sealed record CpuCoreGroup(
            string GroupName,
            IReadOnlyList<int> CpuIndexes,
            byte? EfficiencyClass = null,
            int? Capacity = null,
            int? MaxFrequencyKHz = null);

        private const int LinuxCpuSetSizeBytes = 128; // Linux CPU_SETSIZE is usually 1024 bits.
        private const int MaxMaskBitIndex = 63;
        private const int ErrorInsufficientBuffer = 122;

        /// <summary>
        /// Set CPU affinity for the current calling thread.
        /// </summary>
        /// <param name="affinityMask">
        /// CPU bitmask. Bit 0 = CPU0, bit 1 = CPU1 ...
        /// </param>
        public static void SetCurrentThreadAffinity(ulong affinityMask)
        {
            if (affinityMask == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(affinityMask), affinityMask, "Affinity mask cannot be zero.");
            }

            if (OperatingSystem.IsWindows())
            {
                SetCurrentThreadAffinityWindows(affinityMask);
                return;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                SetCurrentThreadAffinityLinux(affinityMask);
                return;
            }

            throw new PlatformNotSupportedException("Current platform does not support CPU affinity in this helper.");
        }

        /// <summary>
        /// Build affinity bitmask from CPU indexes and set for current thread.
        /// </summary>
        public static void SetCurrentThreadAffinity(params int[] cpuIndexes)
        {
            SetCurrentThreadAffinity(BuildAffinityMask(cpuIndexes));
        }

        /// <summary>
        /// Build a CPU affinity bitmask from CPU indexes.
        /// </summary>
        public static ulong BuildAffinityMask(params int[] cpuIndexes)
        {
            ArgumentNullException.ThrowIfNull(cpuIndexes);
            if (cpuIndexes.Length == 0)
            {
                throw new ArgumentException("At least one CPU index must be provided.", nameof(cpuIndexes));
            }

            ulong mask = 0;
            foreach (int cpuIndex in cpuIndexes)
            {
                if (cpuIndex < 0 || cpuIndex > MaxMaskBitIndex)
                {
                    throw new ArgumentOutOfRangeException(nameof(cpuIndexes), cpuIndex, $"CPU index must be between 0 and {MaxMaskBitIndex}.");
                }

                mask |= 1UL << cpuIndex;
            }

            return mask;
        }

        /// <summary>
        /// Read CPU core groups from platform APIs/OS topology files.
        /// Typical result on hybrid CPUs: P-core / E-core style groups.
        /// </summary>
        public static IReadOnlyList<CpuCoreGroup> GetCpuCoreGroups()
        {
            if (OperatingSystem.IsWindows())
            {
                return GetCpuCoreGroupsWindows();
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                return GetCpuCoreGroupsLinux();
            }

            throw new PlatformNotSupportedException("Current platform does not support CPU core grouping in this helper.");
        }

        private static void SetCurrentThreadAffinityWindows(ulong affinityMask)
        {
            if (IntPtr.Size == 4 && affinityMask > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(affinityMask), affinityMask, "On 32-bit Windows, affinity mask cannot exceed 32 bits.");
            }

            nint currentThread = GetCurrentThread();
            nuint previousMask = SetThreadAffinityMask(currentThread, (nuint)affinityMask);
            if (previousMask == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadAffinityMask failed.");
            }
        }

        private static void SetCurrentThreadAffinityLinux(ulong affinityMask)
        {
            Span<byte> cpuSet = stackalloc byte[LinuxCpuSetSizeBytes];
            for (int bit = 0; bit <= MaxMaskBitIndex; bit++)
            {
                if ((affinityMask & (1UL << bit)) == 0)
                {
                    continue;
                }

                int byteIndex = bit / 8;
                int bitOffset = bit % 8;
                cpuSet[byteIndex] |= (byte)(1 << bitOffset);
            }

            int result = sched_setaffinity(0, (nuint)cpuSet.Length, ref MemoryMarshal.GetReference(cpuSet));
            if (result != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "sched_setaffinity failed.");
            }
        }

        private static IReadOnlyList<CpuCoreGroup> GetCpuCoreGroupsWindows()
        {
            int bufferLength = 0;
            _ = GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, nint.Zero, ref bufferLength);
            int firstCallError = Marshal.GetLastWin32Error();
            if (bufferLength <= 0 || (firstCallError != 0 && firstCallError != ErrorInsufficientBuffer))
            {
                throw new Win32Exception(firstCallError, "GetLogicalProcessorInformationEx probe call failed.");
            }

            nint buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref bufferLength))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetLogicalProcessorInformationEx failed.");
                }

                var groupedByEfficiency = new Dictionary<byte, HashSet<int>>();
                int offset = 0;
                int headerSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX_HEADER>();
                int processorRelationshipFixedSize = Marshal.SizeOf<PROCESSOR_RELATIONSHIP_FIXED>();
                int groupAffinitySize = Marshal.SizeOf<GROUP_AFFINITY>();
                int bitsPerGroup = nint.Size * 8;

                while (offset < bufferLength)
                {
                    nint entryPtr = buffer + offset;
                    var header = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX_HEADER>(entryPtr);
                    if (header.Size <= 0)
                    {
                        break;
                    }

                    if (header.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                    {
                        nint processorPtr = entryPtr + headerSize;
                        var processor = Marshal.PtrToStructure<PROCESSOR_RELATIONSHIP_FIXED>(processorPtr);
                        if (!groupedByEfficiency.TryGetValue(processor.EfficiencyClass, out var cpuSet))
                        {
                            cpuSet = new HashSet<int>();
                            groupedByEfficiency[processor.EfficiencyClass] = cpuSet;
                        }

                        nint groupMaskStart = processorPtr + processorRelationshipFixedSize;
                        for (int i = 0; i < processor.GroupCount; i++)
                        {
                            nint groupMaskPtr = groupMaskStart + i * groupAffinitySize;
                            var groupAffinity = Marshal.PtrToStructure<GROUP_AFFINITY>(groupMaskPtr);
                            ulong mask = (ulong)groupAffinity.Mask;

                            for (int bit = 0; bit < bitsPerGroup; bit++)
                            {
                                if ((mask & (1UL << bit)) != 0)
                                {
                                    int logicalCpu = groupAffinity.Group * bitsPerGroup + bit;
                                    cpuSet.Add(logicalCpu);
                                }
                            }
                        }
                    }

                    offset += header.Size;
                }

                var result = groupedByEfficiency
                    .OrderByDescending(x => x.Key) // higher class usually indicates higher performance.
                    .Select(x =>
                    {
                        List<int> cpus = x.Value.OrderBy(v => v).ToList();
                        string name = x.Key == 0
                            ? $"EfficiencyClass {x.Key} (efficient)"
                            : $"EfficiencyClass {x.Key} (performance)";
                        return new CpuCoreGroup(name, cpus, EfficiencyClass: x.Key);
                    })
                    .ToList();

                if (result.Count == 0)
                {
                    throw new InvalidOperationException("No processor-core topology information was returned by the OS.");
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IReadOnlyList<CpuCoreGroup> GetCpuCoreGroupsLinux()
        {
            const string cpuRoot = "/sys/devices/system/cpu";
            if (!Directory.Exists(cpuRoot))
            {
                throw new PlatformNotSupportedException("Linux CPU topology path '/sys/devices/system/cpu' was not found.");
            }

            var cpuEntries = new List<(int CpuIndex, int? Capacity, int? MaxFrequencyKHz)>();
            foreach (string dir in Directory.EnumerateDirectories(cpuRoot, "cpu*"))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.Length <= 3 || !int.TryParse(name.AsSpan(3), out int cpuIndex))
                {
                    continue;
                }

                int? capacity = TryReadInt(Path.Combine(dir, "cpu_capacity"))
                    ?? TryReadInt(Path.Combine(dir, "topology", "cpu_capacity"));
                int? maxFrequencyKHz = TryReadInt(Path.Combine(dir, "cpufreq", "cpuinfo_max_freq"))
                    ?? TryReadInt(Path.Combine(dir, "cpufreq", "scaling_max_freq"));

                cpuEntries.Add((cpuIndex, capacity, maxFrequencyKHz));
            }

            if (cpuEntries.Count == 0)
            {
                throw new InvalidOperationException("No CPU entries were discovered under '/sys/devices/system/cpu'.");
            }

            IEnumerable<CpuCoreGroup> groups;
            if (cpuEntries.Any(x => x.Capacity.HasValue))
            {
                groups = cpuEntries
                    .GroupBy(x => x.Capacity ?? int.MinValue)
                    .OrderByDescending(x => x.Key)
                    .Select(x =>
                    {
                        int? capacity = x.Key == int.MinValue ? null : x.Key;
                        List<int> cpus = x.Select(v => v.CpuIndex).OrderBy(v => v).ToList();
                        int? maxFreq = x.Select(v => v.MaxFrequencyKHz).Where(v => v.HasValue).DefaultIfEmpty().Max();
                        string groupName = capacity.HasValue ? $"Capacity {capacity.Value}" : "Capacity Unknown";
                        return new CpuCoreGroup(groupName, cpus, Capacity: capacity, MaxFrequencyKHz: maxFreq);
                    });
            }
            else if (cpuEntries.Any(x => x.MaxFrequencyKHz.HasValue))
            {
                groups = cpuEntries
                    .GroupBy(x => x.MaxFrequencyKHz ?? int.MinValue)
                    .OrderByDescending(x => x.Key)
                    .Select(x =>
                    {
                        int? maxFreq = x.Key == int.MinValue ? null : x.Key;
                        List<int> cpus = x.Select(v => v.CpuIndex).OrderBy(v => v).ToList();
                        string groupName = maxFreq.HasValue ? $"MaxFreq {maxFreq.Value} KHz" : "MaxFreq Unknown";
                        return new CpuCoreGroup(groupName, cpus, MaxFrequencyKHz: maxFreq);
                    });
            }
            else
            {
                List<int> all = cpuEntries.Select(x => x.CpuIndex).OrderBy(v => v).ToList();
                groups = [new CpuCoreGroup("Unknown Topology", all)];
            }

            return groups.ToList();
        }

        private static int? TryReadInt(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path).Trim();
            if (int.TryParse(raw, out int parsed))
            {
                return parsed;
            }

            return null;
        }


        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP relationshipType,
            nint buffer,
            ref int returnedLength);

        [DllImport("libc", SetLastError = true)]
        private static extern int sched_setaffinity(int pid, nuint cpusetsize, ref byte mask);

        private enum LOGICAL_PROCESSOR_RELATIONSHIP : int
        {
            RelationProcessorCore = 0
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX_HEADER
        {
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public int Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_RELATIONSHIP_FIXED
        {
            public byte Flags;
            public byte EfficiencyClass;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;

            public ushort GroupCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GROUP_AFFINITY
        {
            public nuint Mask;
            public ushort Group;
            public ushort Reserved0;
            public ushort Reserved1;
            public ushort Reserved2;
        }
    }
}
