#if WINDOWS || LINUX
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Shared;
using System.Text.Json;
using projectFrameCut.Render.HwAccelEngine;

namespace projectFrameCut.Render.HwAccelEngine
{
    public static class AcceleratorsManager
    {
        public static Accelerator? DefaultAccelerator { get; set; }
        public static Accelerator[] AcceleratorsForRendering { get; set; } = Array.Empty<Accelerator>();
        public static bool IsRendering = false;
        public static bool IsMultiAccelEnabled { get; private set; } = false;

        public static Accelerator[] Accelerators
        {
            get
            {
                if (IsRendering) return AcceleratorsForRendering;
                return DefaultAccelerator is not null ? [DefaultAccelerator] : Array.Empty<Accelerator>();
            }
        }

        /// <summary>
        /// Enumerate all available ILGPU devices for settings UI.
        /// Creates a temporary context; safe to call from any thread.
        /// </summary>
        public static AcceleratorDeviceInfo[] DiscoverDevices()
        {
            try
            {
                var ctx = Context.Create(b => b.Default().EnableAlgorithms());
                var list = ctx.Devices.ToList();
                var result = new AcceleratorDeviceInfo[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    result[i] = new AcceleratorDeviceInfo
                    {
                        Index = i,
                        Name = list[i].Name,
                        Type = list[i].AcceleratorType.ToString()
                    };
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "discover ILGPU accelerators");
                return Array.Empty<AcceleratorDeviceInfo>();
            }
        }

        /// <summary>
        /// Initialize (or re-initialize) accelerator set from accels.json configuration.
        /// Called automatically during plugin load.
        /// </summary>
        public static void InitializeAccelerators()
        {
#if WINDOWS || LINUX
            var dataPath = HwAccelEnginePlugin.dataRootPath is not null
                ? Path.Combine(HwAccelEnginePlugin.dataRootPath, "accels.json")
                : null;

            if (dataPath is null || !File.Exists(dataPath))
            {
                InitializeWithDefaults();
            }
            else
            {
                try
                {
                    var data = JsonSerializer.Deserialize<AcceleratorsStore>(File.ReadAllText(dataPath));
                    if (data is null) { InitializeWithDefaults(); return; }

                    var ctx = Context.Create(builder => builder.Default().EnableAlgorithms());
                    var allDevices = ctx.Devices.ToList();

                    // Resolve main accelerator
                    DefaultAccelerator = allDevices
                        .FirstOrDefault(c => c.Name == data.MainAcceleratorName)
                        ?.CreateAccelerator(ctx)
                        ?? allDevices.FirstOrDefault(c => c.AcceleratorType != AcceleratorType.CPU)
                            ?.CreateAccelerator(ctx);

                    IsMultiAccelEnabled = data.EnableMultiAccel && data.RenderingAcceleratorNames.Length > 0;

                    if (IsMultiAccelEnabled)
                    {
                        AcceleratorsForRendering = data.RenderingAcceleratorNames
                            .Select(name => allDevices.FirstOrDefault(c => c.Name == name)?.CreateAccelerator(ctx))
                            .Where(a => a is not null)
                            .Cast<Accelerator>()
                            .ToArray();
                    }
                    else
                    {
                        AcceleratorsForRendering = DefaultAccelerator is not null
                            ? [DefaultAccelerator]
                            : Array.Empty<Accelerator>();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "initialize ILGPU accelerators from accels.json");
                    InitializeWithDefaults();
                }
            }
#else
            InitializeWithDefaults();
#endif
        }

        private static void InitializeWithDefaults()
        {
            try
            {
                var ctx = Context.Create(builder => builder.Default().EnableAlgorithms());
                var nonCpuDevices = ctx.Devices.Where(c => c.AcceleratorType != AcceleratorType.CPU).ToArray();
                AcceleratorsForRendering = nonCpuDevices.Select(c => c.CreateAccelerator(ctx)).ToArray();
                DefaultAccelerator = AcceleratorsForRendering.Length > 0 ? AcceleratorsForRendering[0] : null;
                IsMultiAccelEnabled = AcceleratorsForRendering.Length > 1;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "initialize default ILGPU accelerators");
                AcceleratorsForRendering = Array.Empty<Accelerator>();
                DefaultAccelerator = null;
                IsMultiAccelEnabled = false;
            }
        }

        /// <summary>
        /// Persist accelerator selection to accels.json and re-initialize.
        /// Call this from settings UI when the user changes accelerator preferences.
        /// </summary>
        /// <param name="mainDeviceName">Name of the primary/default accelerator device.</param>
        /// <param name="renderingDeviceNames">Names of accelerators to use for multi-accelerator rendering.</param>
        /// <param name="enableMultiAccel">Whether to use multiple accelerators during rendering.</param>
        public static void ApplyConfiguration(string mainDeviceName, string[] renderingDeviceNames, bool enableMultiAccel)
        {
#if WINDOWS || LINUX
            if (HwAccelEnginePlugin.dataRootPath is null) return;

            var dataPath = Path.Combine(HwAccelEnginePlugin.dataRootPath, "accels.json");
            var store = new AcceleratorsStore
            {
                MainAcceleratorName = mainDeviceName,
                RenderingAcceleratorNames = renderingDeviceNames,
                EnableMultiAccel = enableMultiAccel
            };
            var dir = Path.GetDirectoryName(dataPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(dataPath, JsonSerializer.Serialize(store));

            InitializeAccelerators();
#endif
        }

        /// <summary>
        /// Quick helper: save a single-accelerator configuration (the common case).
        /// </summary>
        public static void SetDefaultAccelerator(string deviceName)
        {
            ApplyConfiguration(deviceName, [deviceName], false);
        }

        private class AcceleratorsStore
        {
            public string MainAcceleratorName { get; set; } = "";
            public string[] RenderingAcceleratorNames { get; set; } = Array.Empty<string>();
            public bool EnableMultiAccel { get; set; }
        }
    }

    /// <summary>Device information exposed to settings UI.</summary>
    public class AcceleratorDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
#endif