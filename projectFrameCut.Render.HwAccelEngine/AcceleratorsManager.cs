#if WINDOWS || LINUX || WINNETCORE
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.HwAccelEngine
{
    public static class AcceleratorsManager
    {
        private static readonly object InitializationLock = new();
        private static readonly List<Accelerator> ownedAccelerators = [];
        private static Context? context;
        private static Accelerator? defaultAccelerator;
        private static Accelerator[] acceleratorsForRendering = Array.Empty<Accelerator>();
        private static volatile bool isDefaultInitialized;
        private static volatile bool areRenderingAcceleratorsInitialized;
        private static volatile bool useExternalBackend;
        private static bool isMultiAccelEnabled;

        public static bool UseExternalBackend
        {
            get => useExternalBackend;
            set
            {
                lock (InitializationLock)
                {
                    if (useExternalBackend == value) return;
                    useExternalBackend = value;
                    if (value) DisposeOwnedResources();
                    Logger.Log(value
                        ? "[AcceleratorsManager] Local ILGPU initialization disabled; compute is handled by the RPC Worker."
                        : "[AcceleratorsManager] RPC Worker is unavailable; local ILGPU initialization is enabled.");
                }
            }
        }

        public static Accelerator? DefaultAccelerator
        {
            get { EnsureInitialized(); return defaultAccelerator; }
            set
            {
                lock (InitializationLock)
                {
                    defaultAccelerator = value;
                    isDefaultInitialized = true;
                }
            }
        }

        public static Accelerator[] AcceleratorsForRendering
        {
            get { EnsureInitialized(true); return acceleratorsForRendering; }
            set
            {
                lock (InitializationLock)
                {
                    acceleratorsForRendering = value ?? Array.Empty<Accelerator>();
                    areRenderingAcceleratorsInitialized = true;
                    isMultiAccelEnabled = acceleratorsForRendering.Length > 1;
                }
            }
        }

        public static bool IsRendering = false;

        public static bool IsMultiAccelEnabled
        {
            get
            {
                if (!areRenderingAcceleratorsInitialized)
                {
                    var store = ReadConfiguration();
                    return store is not null && store.EnableMultiAccel &&
                        store.RenderingAcceleratorNames.Distinct().Skip(1).Any();
                }
                return isMultiAccelEnabled;
            }
            private set => isMultiAccelEnabled = value;
        }

        /// <summary>
        /// Gets the accelerators for the current workload. The first access creates
        /// the ILGPU context and devices; loading the plugin itself does not.
        /// </summary>
        public static Accelerator[] Accelerators
        {
            get
            {
                EnsureInitialized(IsRendering);
                if (IsRendering) return acceleratorsForRendering;
                return defaultAccelerator is not null ? [defaultAccelerator] : Array.Empty<Accelerator>();
            }
        }

        /// <summary>
        /// Enumerate all available ILGPU devices for settings UI using a temporary context.
        /// </summary>
        public static AcceleratorDeviceInfo[] DiscoverDevices()
        {
            try
            {
                using var discoveryContext = Context.Create(b => b.Default().EnableAlgorithms());
                var list = discoveryContext.Devices.ToList();
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

        private static void EnsureInitialized(bool forRendering = false)
        {
            if (useExternalBackend || (forRendering ? areRenderingAcceleratorsInitialized : isDefaultInitialized)) return;
            lock (InitializationLock)
            {
                if (!useExternalBackend && !(forRendering ? areRenderingAcceleratorsInitialized : isDefaultInitialized))
                    InitializeCore(forRendering);
            }
        }

        /// <summary>
        /// Explicitly reloads accels.json. Normal callers should use the accelerator
        /// properties and let them initialize on demand.
        /// </summary>
        public static void InitializeAccelerators()
        {
            lock (InitializationLock)
            {
                DisposeOwnedResources();
                if (!useExternalBackend) InitializeCore(IsRendering);
            }
        }

        private static void InitializeCore(bool initializeRendering)
        {
            try
            {
                var store = ReadConfiguration();
                context ??= Context.Create(builder => builder.Default().EnableAlgorithms());
                var allDevices = context.Devices.ToList();
                var nonCpuDevices = allDevices.Where(c => c.AcceleratorType != AcceleratorType.CPU).ToArray();

                Accelerator GetOrCreateAccelerator(ILGPU.Runtime.Device device)
                {
                    var existing = ownedAccelerators.FirstOrDefault(a => a.Name == device.Name);
                    if (existing is not null) return existing;
                    var accelerator = device.CreateAccelerator(context);
                    ownedAccelerators.Add(accelerator);
                    return accelerator;
                }

                if (!isDefaultInitialized)
                {
                    var mainDevice = store is not null
                        ? allDevices.FirstOrDefault(c => c.Name == store.MainAcceleratorName)
                        : null;
                    mainDevice ??= nonCpuDevices.FirstOrDefault();
                    defaultAccelerator = mainDevice is not null ? GetOrCreateAccelerator(mainDevice) : null;
                    isDefaultInitialized = true;
                    Logger.Log($"[AcceleratorsManager] Initialized default accelerator: {defaultAccelerator?.Name ?? "none"}.");
                }

                if (initializeRendering && !areRenderingAcceleratorsInitialized)
                {
                    var enableMultiAccel = store is not null && store.EnableMultiAccel &&
                        store.RenderingAcceleratorNames.Length > 0;
                    acceleratorsForRendering = enableMultiAccel
                        ? store!.RenderingAcceleratorNames
                        .Select(name => allDevices.FirstOrDefault(c => c.Name == name))
                        .Where(device => device is not null)
                        .Select(device => GetOrCreateAccelerator(device!))
                        .Distinct()
                        .ToArray()
                        : defaultAccelerator is not null ? [defaultAccelerator] : Array.Empty<Accelerator>();
                    enableMultiAccel = acceleratorsForRendering.Length > 1;
                    IsMultiAccelEnabled = enableMultiAccel;
                    areRenderingAcceleratorsInitialized = true;
                    Logger.Log($"[AcceleratorsManager] Initialized {acceleratorsForRendering.Length} rendering accelerator(s).");
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "initialize ILGPU accelerators");
                DisposeOwnedResources();
                // Avoid loading GPU drivers and retrying on every property read.
                isDefaultInitialized = true;
                areRenderingAcceleratorsInitialized = initializeRendering;
            }
        }

        private static AcceleratorsStore? ReadConfiguration()
        {
#if WINDOWS || LINUX
            var dataPath = HwAccelEnginePlugin.dataRootPath is not null
                ? Path.Combine(HwAccelEnginePlugin.dataRootPath, "accels.json")
                : null;
            if (dataPath is null || !File.Exists(dataPath)) return null;

            try
            {
                return JsonSerializer.Deserialize<AcceleratorsStore>(File.ReadAllText(dataPath));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "read ILGPU accelerator configuration");
                return null;
            }
#else
            return null;
#endif
        }

        private static void DisposeOwnedResources()
        {
            // Only a non-null context marks accelerators owned by this manager.
            if (context is not null)
            {
                foreach (var accelerator in ownedAccelerators) accelerator.Dispose();
                context.Dispose();
            }

            ownedAccelerators.Clear();
            context = null;
            defaultAccelerator = null;
            acceleratorsForRendering = Array.Empty<Accelerator>();
            IsMultiAccelEnabled = false;
            isDefaultInitialized = false;
            areRenderingAcceleratorsInitialized = false;
        }

        /// <summary>
        /// Persist accelerator selection and invalidate the current devices. The new
        /// selection is created only when an accelerator property is next requested.
        /// </summary>
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

            lock (InitializationLock)
            {
                DisposeOwnedResources();
            }
#endif
        }

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

    public class AcceleratorDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
#endif
