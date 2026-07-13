using FFmpeg.AutoGen;
using Microsoft.Maui.Storage;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut.Setting.SettingPages;

public partial class DiagnosticSettingPage : ContentPage
{
    public PropertyPanelBuilder rootPPB;
    string OSInfo = "?", ApplicationInfo = "?", DeviceInfo = "?";
    bool infoGetted = false;
    public DiagnosticSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_DiagnosticPage;
        BuildPPB();
    }

    private async void BuildPPB()
    {
        if (!infoGetted)
        {
            SetBusy();
            await Task.Run(() =>
            {
                OSInfo = GetOSInfo();
                ApplicationInfo = GetAppInfo();
                DeviceInfo = GetDeviceInfo();
            });
            infoGetted = true;
        }
        rootPPB = new PropertyPanelBuilder()
            .AddButton(SettingLocalizedResources.Diag_GenerateReport, async (s, e) => await MakeDiagReport())
            .AddSeparator()
            .AddText(SettingLocalizedResources.Diag_InfoSection_App)
            .AddCustomChild(new Editor
            {
                Text = ApplicationInfo,
                IsReadOnly = true
            }).AddSeparator()
            .AddText(SettingLocalizedResources.Diag_InfoSection_OperatingSystem)
            .AddCustomChild(new Editor
            {
                Text = OSInfo,
                IsReadOnly = true
            }).AddSeparator()
            .AddText(SettingLocalizedResources.Diag_InfoSection_Hardware)
            .AddCustomChild(new Editor
            {
                Text = DeviceInfo,
                IsReadOnly = true
            });
        Content = rootPPB.BuildWithScrollView();

    }

    void SetBusy()
    {
        Content = new VerticalStackLayout
        {
            Children =
                {
                    new ActivityIndicator
                    {
                        IsRunning = true,
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalOptions = LayoutOptions.Center
                    },
                    new Label
                    {
                        Text = SettingLocalizedResources.Diag_MakingReport,
                        FontSize = 20,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                    },
                    new Label
                    {
                        Text = SettingLocalizedResources.Diag_MakingReport_Sub,
                        FontSize = 28,
                        TextColor = Colors.OrangeRed,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    }

                },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
    }

    private async Task MakeDiagReport()
    {
        string workingPath = Path.Combine(FileSystem.AppDataDirectory, "diag", $"DiagReport-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}");
        Directory.CreateDirectory(workingPath);
        //1 OS info
        await File.WriteAllTextAsync(Path.Combine(workingPath, "OSinfo.txt"), OSInfo, default);
        //2 App info
        await File.WriteAllTextAsync(Path.Combine(workingPath, "Appinfo.txt"), ApplicationInfo, default);
        //3 Device info
        await File.WriteAllTextAsync(Path.Combine(workingPath, "Deviceinfo.txt"), DeviceInfo, default);
        //4 recent Crashlogs 
        Directory.CreateDirectory(Path.Combine(workingPath, "RecentCrashlogs"));
#if WINDOWS
        var crashLogs = Directory.GetFiles(Path.Combine(MauiProgram.DataPath, "Crashlogs"));
#elif iDevices
        var crashLogs = Directory.GetFiles(System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging", "crashlog"));
#endif
#if WINDOWS || iDevices
        var files = crashLogs.Select(s => new FileInfo(s))
            .OrderByDescending(f => f.CreationTime)
            .Take(5);
        foreach (var file in files)
        {
            File.Copy(file.FullName, Path.Combine(workingPath, "RecentCrashlogs", file.Name), true);
        }

#elif ANDROID
        var crashLogs = Directory.GetFiles(Path.Combine(MauiProgram.DataPath, "logging"));
        var files = crashLogs.Select(s => new FileInfo(s))
            .Where(s => s.Name.StartsWith("java") || s.Name.StartsWith("anr") || s.Name.StartsWith("native"))
            .OrderByDescending(f => f.CreationTime)
            .Take(5);
        foreach (var file in files)
        {
            File.Copy(file.FullName, Path.Combine(workingPath, "RecentCrashlogs", file.Name), true);
        }
#endif
        //5 recent logs
        Directory.CreateDirectory(Path.Combine(workingPath, "RecentLogs"));
#if WINDOWS
        var loggingPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
#else
        var loggingPath = Path.Combine(MauiProgram.DataPath, "logging");
#endif
        var logs = Directory.GetFiles(loggingPath).Select(s => new FileInfo(s))
            .OrderByDescending(f => f.CreationTime)
            .Take(5);
        foreach (var file in logs)
        {
            File.Copy(file.FullName, Path.Combine(workingPath, "RecentLogs", file.Name), true);
        }

        //last: package
        string zipPath = $"{workingPath}.zip";
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
        await Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(workingPath, zipPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        });

        //last: share
        await Share.RequestAsync(new ShareFileRequest()
        {
            File = new ShareFile(zipPath),
            Title = Path.GetFileNameWithoutExtension(zipPath)
        });

    }


    public static string GetAppInfo(bool includeCodec = true, bool includeAssembly = true)
    {
        bool IsPackaged = false;
        string PackageName = "Unknown";
#if WINDOWS
        IsPackaged = WinUI.App.IsPackaged();
        PackageName = WinUI.App.GetPackageFullName();
#elif ANDROID
        PackageName = Android.App.Application.Context.PackageName;
#elif iDevices
        PackageName = Foundation.NSBundle.MainBundle.BundleIdentifier ?? "Unknown";
#endif
        string GetAssemblyInfo()
        {
            StringBuilder builder = new StringBuilder();
            List<string> printedAsb = new();
            foreach (var asb in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (printedAsb.Contains(asb.FullName))
                    {
                        continue;
                    }
                    printedAsb.Add(asb.FullName);
                    var guid = asb.GetCustomAttribute<GuidAttribute>()?.Value ?? "none";
                    string asbHash = "";
                    try
                    {
                        asbHash = !asb.IsDynamic && Path.Exists(asb.Location) ? HashServices.ComputeFileHash(asb.Location) : "unknown";
                    }
                    catch { asbHash = "unknown"; }

                    builder.AppendLine($"Assembly {asb.FullName}, {asb.GetName().Version} GUID:{guid} hash:{asbHash}");
                }
                catch
                {
                    builder.AppendLine($"{asb.FullName}, cannot get assembly info.");
                }
                finally
                {
                    builder.AppendLine();
                }
            }

            return builder.ToString();
        }


        string internalFFmpegVersion = "unknown", internalFFmpegCfg = "unknown";
        List<FFmpegHelper.CodecUtils.CodecInfo> codecs = new();
        try
        {
            internalFFmpegVersion = $"version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}";
            internalFFmpegCfg = ffmpeg.avcodec_configuration();
            codecs = FFmpegHelper.CodecUtils.GetAllCodecs();

        }
        catch { }
        string renderHash = "unknown", drawingHash = "unknown", programDate = "?", drawingDate = "?", drawingCommit = "?";
        var renderType = typeof(Renderer).Assembly;
        var drawingType = typeof(Drawing.Base.IPicture).Assembly;
        var appType = typeof(MauiProgram).Assembly;

        try
        {
            try
            {
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
            }
            catch { renderHash = "unknown"; }
            try
            {
                drawingHash = !drawingType.IsDynamic && Path.Exists(drawingType.Location) ? HashServices.ComputeFileHash(drawingType.Location) : "unknown";

            }
            catch
            {
                drawingHash = "unknown";

            }

            try
            {
                programDate = !appType.IsDynamic && Path.Exists(appType.Location) ? File.GetLastWriteTime(appType.Location).ToString("yyyy-MM-dd HH:mm:ss") : "unknown";

            }
            catch
            {
                programDate = "?";
            }
            try
            {
                drawingDate = !drawingType.IsDynamic && Path.Exists(drawingType.Location) ? File.GetLastWriteTime(drawingType.Location).ToString("yyyy-MM-dd HH:mm:ss") : "unknown";

            }
            catch
            {
                drawingDate = "?";
            }
            try
            {
                drawingCommit = (drawingType.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last().Substring(0, 8);

            }
            catch { drawingCommit = "unknown"; }
        }
        catch { }


        return
            $"""
            {Localized.AppBrand} - {AppInfo.PackageName},{AppInfo.VersionString} on {AppContext.TargetFrameworkName} ({AppInfo.BuildString})
            IPluginBase API: v{IPluginBase.CurrentPluginAPIVersion} | IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}
            {MauiProgram.AssemblyName}: {MauiProgram.ProgramConfig}@{MauiProgram.ProgramCommit} on {programDate} 
            {renderType.GetName().Name}: v{renderType.GetName().Version} hash:{renderHash}
            {drawingType.GetName().Name}: v{drawingType.GetName().Version}({drawingCommit.Substring(0, 8)} {drawingDate}) hash:{drawingHash}

            AppDataPath: {MauiProgram.BasicDataPath}
            UserDataPath: {MauiProgram.DataPath}
            {(OperatingSystem.IsWindows() ? $"IsPackaged: {IsPackaged}" : "")}
            Bundle Identifier / Package Name: {PackageName}

            CmdLine:
            {string.Join(' ', MauiProgram.CmdlineArgs)}

            """
            + (includeAssembly ?
            $"""
            Assembly: 
            {GetAssemblyInfo()}

            """ : "")
            + (includeCodec ?
            $"""
            Internal FFmpeg:
            - lib location: {MauiProgram.FFmpegRoot}
            - version: {internalFFmpegVersion}
            - config: {internalFFmpegCfg}
            - Codecs: 
            {string.Join("\r\n", codecs.Select(c => $"{c.Id}: {c.Name}, decoder:{c.IsDecoder}, encoder:{c.IsEncoder}"))}
            - Binding verification result: 
            {ffmpeg.BindingVerificationResult?.Failures?.Aggregate("", (a, b) => $"{a}{b.FunctionName} of {b.LibraryName} fails: {b.Message}\r\n")}
            """ : "");
    }


    private string GetOSInfo()
    {
        StringBuilder builder = new();
        builder.AppendLine(
            $"""
            brief OS version from CLR: {Environment.OSVersion.Platform} {Environment.OSVersion.Version} ({RuntimeInformation.OSDescription})
            CPU Arch: {RuntimeInformation.ProcessArchitecture}
            
            """);
#if ANDROID
        builder.AppendLine(
            $"""
            Android version: {Android.OS.Build.VERSION.Release} (SDK {Android.OS.Build.VERSION.SdkInt})
            OS Build: {Android.OS.Build.Display}/{Android.OS.Build.Id}
            OS Tags: {Android.OS.Build.Tags}
            """);
#elif iDevices
        builder.AppendLine(
            $"""
            OS version: {UIKit.UIDevice.CurrentDevice.SystemVersion}
            Device model: {UIKit.UIDevice.CurrentDevice.Model} ({UIKit.UIDevice.CurrentDevice.Name})
            """);
#elif WINDOWS
        builder.AppendLine(
            $"""
            OS BuildLabEx: {Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "BuildLabEx", "Unknown")}
            OS InstallationType: {Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType", "Unknown")}
            """);

#endif

        return builder.ToString();

    }


    private string GetDeviceInfo()
    {
        StringBuilder builder = new();
#if ANDROID
        try
        {
            string manufacturer = Android.OS.Build.Manufacturer ?? "Unknown";
            string model = Android.OS.Build.Model ?? "Unknown";
            string hw = Android.OS.Build.Hardware ?? "Unknown";
            string device = $"{manufacturer} {model} (hardware {hw})".Trim();

            string cpuName = "Unknown";
            try
            {
                if (System.IO.File.Exists("/proc/cpuinfo"))
                {
                    var lines = System.IO.File.ReadAllLines("/proc/cpuinfo");
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("Processor", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length == 2)
                            {
                                cpuName = parts[1].Trim();
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            string totalMem = "Unknown";
            try
            {
                if (System.IO.File.Exists("/proc/meminfo"))
                {
                    var lines = System.IO.File.ReadAllLines("/proc/meminfo");
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length == 2)
                            {
                                totalMem = parts[1].Trim();
                                break;
                            }
                        }
                    }
                }
            }
            catch { }
            string gpuInfo = "Unknown";
            try
            {
                gpuInfo = GLInfoHelper.GetGLESInfo();
            }
            catch { }

            builder.AppendLine($"""
            Device: {device}
            CPU: {cpuName}, {Environment.ProcessorCount} cores/threads
            Total Memory: {totalMem}

            GPU info:
            {gpuInfo}
            """);
        }
        catch (Exception ex)
        {
            builder.AppendLine($"Android: exception reading device info: {ex.Message}");
        }

#elif iDevices
        try
        {
            string deviceModel = "Unknown";
            string hwMachine = "Unknown";
            string cpuName = "Unknown";
            ulong totalMemory = 0;

            try
            {
                deviceModel = UIKit.UIDevice.CurrentDevice.Model ?? "Unknown";
            }
            catch { }

            try
            {
                var size = IntPtr.Zero;
                if (sysctlbyname_getsize("hw.machine", ref size) == 0 && size != IntPtr.Zero)
                {
                    var buf = new byte[size.ToInt32()];
                    if (sysctlbyname("hw.machine", buf, ref size, IntPtr.Zero, IntPtr.Zero) == 0)
                    {
                        hwMachine = Encoding.UTF8.GetString(buf, 0, buf.Length).TrimEnd('\0');
                    }
                }
            }
            catch { }

            try
            {
                var size = IntPtr.Zero;
                if (sysctlbyname_getsize("machdep.cpu.brand_string", ref size) == 0 && size != IntPtr.Zero)
                {
                    var buf = new byte[size.ToInt32()];
                    if (sysctlbyname("machdep.cpu.brand_string", buf, ref size, IntPtr.Zero, IntPtr.Zero) == 0)
                    {
                        cpuName = Encoding.UTF8.GetString(buf, 0, buf.Length).TrimEnd('\0');
                    }
                }
            }
            catch { }

            try
            {
                totalMemory = Foundation.NSProcessInfo.ProcessInfo.PhysicalMemory;
            }
            catch { }

            string gpuName = "Unknown";
            try
            {
                var metalDevice = Metal.MTLDevice.SystemDefault;
                if (metalDevice != null)
                {
                    gpuName = metalDevice.Name ?? "Unknown";
                }
            }
            catch { }

            builder.AppendLine($"""
            Device: {deviceModel} ({hwMachine})
            CPU: {cpuName}, {Environment.ProcessorCount} cores
            Total Memory: {totalMemory} bytes

            GPU (Metal): {gpuName}
            """);
        }
        catch (Exception ex)
        {
            builder.AppendLine($"iOS: exception reading device info: {ex.Message}");
        }

        [System.Runtime.InteropServices.DllImport("__Internal", EntryPoint = "sysctlbyname")]
        static extern int sysctlbyname([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string name, byte[] oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);

        static int sysctlbyname_getsize(string name, ref IntPtr size)
        {
            try
            {
                size = IntPtr.Zero;
                return sysctlbyname(name, null, ref size, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return -1;
            }
        }

#elif WINDOWS
        try
        {
            string manufacturer = "Unknown";
            string model = "Unknown";
            string cpuName = "Unknown";
            string totalMemory = "Unknown";

            try
            {
                try
                {
                    var searcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
                    foreach (System.Management.ManagementObject mo in searcher.Get())
                    {
                        manufacturer = mo["Manufacturer"]?.ToString() ?? manufacturer;
                        model = mo["Model"]?.ToString() ?? model;
                        if (mo["TotalPhysicalMemory"] != null)
                        {
                            if (ulong.TryParse(mo["TotalPhysicalMemory"].ToString(), out var memBytes))
                            {
                                totalMemory = $"{memBytes} bytes";
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    var searcherCpu = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                    foreach (System.Management.ManagementObject mo in searcherCpu.Get())
                    {
                        cpuName = mo["Name"]?.ToString() ?? cpuName;
                        break;
                    }
                }
                catch { }
            }
            catch { }

            string[] accels = ["Unknown"];

            try
            {
                var accelsInfo = ILGPU.Context.CreateDefault().Devices;
                try
                {
                    accels = accelsInfo.Index().Select(a => $"- Accelerator #{a.Index}: {a.Item.Name} ({a.Item.AcceleratorType})\r\n").ToArray() ?? ["Unknown"];
                }
                catch (Exception ex) { Log(ex); }
            }
            catch { }

            builder.AppendLine($"""
            Device: {manufacturer} {model}
            CPU: {cpuName}, {Environment.ProcessorCount} threads
            Total Memory: {totalMemory}

            Accelerators got by ILGPU: 
            {string.Concat(accels)}
            """);




        }
        catch (Exception ex)
        {
            builder.AppendLine($"Windows: exception reading device info: {ex.Message}");
        }
#else
        builder.AppendLine("Device info: platform not recognized or unsupported for detailed info.");
#endif
        try
        {
            builder.AppendLine();
            builder.AppendLine("CPU Core group from ThreadAffinityHelper:");
            foreach (var group in ThreadAffinityHelper.GetCpuCoreGroups())
            {
                builder.AppendLine($"- Group ({string.Join(", ", group.CpuIndexes)}): {group}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"Exception reading CPU core groups: {ex.Message}");
        }

        return builder.ToString();
    }
}