using FFmpeg.AutoGen;
using Microsoft.Maui.Storage;
using projectFrameCut.PropertyPanel;
using projectFrameCut.Render;
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
    public PropertyPanel.PropertyPanelBuilder rootPPB;
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
            .AddButton("OpenBaseUserdata", SettingLocalizedResources.Diag_OpenBaseData)
            .AddButton("MakeDiagReportButton", SettingLocalizedResources.Diag_GenerateReport)
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
            })
            .ListenToChanges(SettingInvoker);
        Content = new ScrollView { Content = rootPPB.Build() };
        
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
                case "MakeDiagReportButton":
                    await MakeDiagReport();
                    break;
                case "PerformanceTestButton":

                    break;
                case "OpenBaseUserdata":
#if WINDOWS
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = MauiProgram.BasicDataPath, UseShellExecute = true });
#elif ANDROID

#elif iDevices

#endif
                    break;  
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
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


    private string GetAppInfo()
    {
        bool IsPackaged = false;
#if WINDOWS
        IsPackaged = MauiProgram.IsPackaged();
#endif
        string GetAttributeData(IEnumerable<CustomAttributeData> attributes)
        {
            StringBuilder builder = new();
            foreach (CustomAttributeData cad in attributes)
            {
                builder.AppendLine($"   {cad}");
                builder.AppendLine($"      Constructor: '{cad.Constructor}'");

                builder.AppendLine("      Constructor arguments:");
                foreach (CustomAttributeTypedArgument cata
                    in cad.ConstructorArguments)
                {
                    ShowValueOrArray(cata);
                }

                builder.AppendLine("      Named arguments:");
                foreach (CustomAttributeNamedArgument cana
                    in cad.NamedArguments)
                {
                    builder.AppendLine($"         MemberInfo: '{cana.MemberInfo}'");
                    ShowValueOrArray(cana.TypedValue);
                }
            }

            return builder.ToString();
        }

        string ShowValueOrArray(CustomAttributeTypedArgument cata)
        {
            StringBuilder builder = new StringBuilder();
            if (cata.Value.GetType() == typeof(ReadOnlyCollection<CustomAttributeTypedArgument>))
            {
                builder.AppendLine($"         Array of '{cata.ArgumentType}':");

                foreach (CustomAttributeTypedArgument cataElement in
                    (ReadOnlyCollection<CustomAttributeTypedArgument>)cata.Value)
                {
                    builder.AppendLine($"             Type: '{cataElement.ArgumentType}'  Value: '{cataElement.Value}'");
                }
            }
            else
            {
                builder.AppendLine($"         Type: '{cata.ArgumentType}'  Value: '{cata.Value}'");
            }
            return builder.ToString();
        }

        List<string> ModulesInfo = new();
        var asb = Assembly.GetExecutingAssembly();
        foreach (var item in asb.GetModules())
        {
            ModulesInfo.Add($"{item.Assembly.FullName}: \r\nin '{item.FullyQualifiedName}',uuid: {item.ModuleVersionId}\r\nAttributes:\r\n{GetAttributeData(item.CustomAttributes)}\r\n\r\n");
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


        return
            $"""
            {Localized.AppBrand} - {AppInfo.PackageName},{AppInfo.VersionString} on {AppContext.TargetFrameworkName} ({AppInfo.BuildString})
            - CPU arch: {RuntimeInformation.ProcessArchitecture}
            - AppDataPath: {MauiProgram.BasicDataPath}
            - UserDataPath: {MauiProgram.DataPath}
            - {(OperatingSystem.IsWindows() ? $"IsPackaged: {IsPackaged}" : "")}

            Assembly: {asb.FullName}
            - Runtime version: {asb.ImageRuntimeVersion}
            - HostContext: {asb.HostContext}
            - Modules:
            {string.Join("\r\n  -", ModulesInfo)}

            Internal FFmpeg:
            - version: {internalFFmpegVersion}
            - config: {internalFFmpegCfg}
            - Codecs: 
            {string.Join("\r\n",codecs.Select(c => $"{c.Id}: {c.Name}, decoder:{c.IsDecoder}, encoder:{c.IsEncoder}"))}

            """;
    }


    private string GetOSInfo()
    {
        StringBuilder builder = new();
        builder.AppendLine(
            $"""
            brief OS version from CLR: {Environment.OSVersion.Platform} {Environment.OSVersion.Version} ({RuntimeInformation.OSDescription})
            
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
            catch {  }

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
            catch {  }

            try
            {
                totalMemory = Foundation.NSProcessInfo.ProcessInfo.PhysicalMemory;
            }
            catch {  }

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
                var accelsInfo = RenderSettingPage.GetAccelInfo();
                try
                {
                    accels = accelsInfo?.Select(a => $"- Accelerator #{a.index}: {a.name} ({a.Type})\r\n").ToArray() ?? ["Unknown"];
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
        return builder.ToString();
    }
}