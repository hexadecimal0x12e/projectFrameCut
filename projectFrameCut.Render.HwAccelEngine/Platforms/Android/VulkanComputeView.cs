using Android.Content;
using Android.Util;
using Android.Views;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView;
using AndroidView = Android.Views.View;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;
using VulkanDevice = Silk.NET.Vulkan.Device;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android
{
    public unsafe class VulkanComputeView : AndroidView
    {
        private const uint ComputeQueueFlag = 0x2;
        private const uint StorageBufferUsageFlag = 0x20;
        private const uint ComputeStageFlag = 0x20;
        private const uint HostVisibleFlag = 0x2;
        private const uint HostCoherentFlag = 0x4;
        private const uint StorageDescriptorType = 7;
        private const uint ComputeBindPoint = 1;

        private readonly object _sync = new();
        private readonly Vk _vk = Vk.GetApi();
        private Shaderc _shaderc = null!; // Lazy initialized when needed
        private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Instance _instance;
        private PhysicalDevice _physicalDevice;
        private VulkanDevice _device;
        private Queue _queue;
        private uint _queueFamilyIndex;
        private CommandPool _commandPool;
        private DescriptorSetLayout _descriptorSetLayout;
        private PipelineLayout _pipelineLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _descriptorSet;
        private Pipeline _pipeline;
        private ShaderModule _shaderModule;
        private BufferResource[] _inputBuffers = Array.Empty<BufferResource>();
        private BufferResource _outputBuffer;
        private string _shaderSource;
        private float[][] _inputs;
        private int _length;
        private int _workGroupSize;
        private OutputElementType _outputElementType;
        private ShaderKind _shaderKind;
        private bool _initialized;
        private bool _vulkanComputeDisabled;
        private string? _disableReason;

        private readonly struct BufferResource
        {
            public readonly VulkanBuffer Buffer;
            public readonly DeviceMemory Memory;
            public readonly ulong Size;

            public BufferResource(VulkanBuffer buffer, DeviceMemory memory, ulong size)
            {
                Buffer = buffer;
                Memory = memory;
                Size = size;
            }
        }

        /// <summary>
        /// Gets whether Vulkan compute resources have been initialized.
        /// </summary>
        internal bool IsInitialized => _initialized;
        internal bool IsVulkanComputeDisabled => _vulkanComputeDisabled;

        public VulkanComputeView(Context context, string shaderSource, params float[][] inputs)
            : this(context, shaderSource, 256, ShaderKind.ComputeShader, OutputElementType.Float32, inputs)
        {
        }

        public VulkanComputeView(Context context, string shaderSource, int workGroupSize, ShaderKind shaderKind, OutputElementType outputElementType, params float[][] inputs)
            : base(context)
        {
            if (string.IsNullOrWhiteSpace(shaderSource))
            {
                throw new ArgumentNullException(nameof(shaderSource));
            }

            // Only validate if inputs are provided
            if (inputs != null && inputs.Length > 0)
            {
                ValidateInputs(inputs);
                _shaderSource = shaderSource;
                _inputs = inputs;
                _length = inputs[0].Length;
                _workGroupSize = workGroupSize > 0 ? workGroupSize : 256;
                _shaderKind = shaderKind;
                _outputElementType = outputElementType;

                try
                {
                    InitializeVulkan();
                    RebuildResources();
                    _initialized = true;
                    _readyTcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    // 记录错误信息
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize Vulkan: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                    
                    // 清理已分配的资源
                    try
                    {
                        DestroyResources();
                    }
                    catch (Exception cleanupEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to cleanup resources: {cleanupEx.Message}");
                    }
                    
                    DisableVulkanCompute("Vulkan compute initialization failed.", ex);
                }
            }
            else
            {
                // Placeholder initialization for later use
                _shaderSource = shaderSource;
                _inputs = inputs ?? Array.Empty<float[]>();
                _length = 1;
                _workGroupSize = workGroupSize > 0 ? workGroupSize : 256;
                _shaderKind = shaderKind;
                _outputElementType = outputElementType;
                
                // Mark ready since this is just a placeholder
                _readyTcs.TrySetResult(true);
            }
        }

        public Task WaitUntilReadyAsync() => _readyTcs.Task;

        public Task<Array> RunComputeAsync() => RunComputeAsync(_outputElementType);

        public Task<Array> RunComputeAsync(OutputElementType outputElementType)
        {
            if (!_initialized)
            {
                // Return zero-filled array of correct type if not initialized
                // Use parameter, not field, since field might not be set yet
                if (_length <= 0)
                {
                    return Task.FromResult<Array>(
                        outputElementType == OutputElementType.UInt32 
                            ? Array.Empty<uint>() 
                            : (Array)Array.Empty<float>()
                    );
                }

                // Return array filled with zeros if not initialized
                return Task.FromResult<Array>(
                    outputElementType == OutputElementType.UInt32 
                        ? new uint[_length] 
                        : (Array)new float[_length]
                );
            }

            return Task.Run(() => RunCompute(outputElementType));
        }

        public void UpdateInputs(float[][] inputs, string shaderSource, int workGroupSize, ShaderKind shaderKind, OutputElementType outputElementType)
        {
            if (string.IsNullOrWhiteSpace(shaderSource))
            {
                throw new ArgumentNullException(nameof(shaderSource));
            }

            ValidateInputs(inputs);

            lock (_sync)
            {
                _shaderSource = shaderSource;
                _inputs = inputs;
                _length = inputs[0].Length;
                _workGroupSize = workGroupSize > 0 ? workGroupSize : 256;
                _shaderKind = shaderKind;
                _outputElementType = outputElementType;

                if (_vulkanComputeDisabled)
                {
                    return;
                }

                if (_initialized)
                {
                    try
                    {
                        RebuildResources();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"WARNING: Failed to rebuild resources: {ex.Message}");
                        if (IsShaderCompilerDependencyFailure(ex))
                        {
                            DestroyResources();
                            _initialized = false;
                            DisableVulkanCompute("Shader compiler native dependency is unavailable.", ex);
                        }
                    }
                }
                else if (inputs.Length > 0)
                {
                    // First initialization with valid inputs
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("UpdateInputs: Performing initial Vulkan setup...");
                        InitializeVulkan();
                        RebuildResources();
                        _initialized = true;
                        _readyTcs.TrySetResult(true);
                        System.Diagnostics.Debug.WriteLine("UpdateInputs: Vulkan initialization complete");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR: Initial Vulkan setup failed: {ex.GetType().Name}: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                        _initialized = false;
                        DestroyResources();
                        if (IsShaderCompilerDependencyFailure(ex))
                        {
                            DisableVulkanCompute("Shader compiler native dependency is unavailable.", ex);
                        }
                    }
                }
            }
        }

        public void ReleaseResources()
        {
            lock (_sync)
            {
                DestroyResources();
            }
        }

        private void InitializeVulkan()
        {
            var applicationInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
            };

            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
            };

            Check(_vk.CreateInstance(in instanceCreateInfo, null, out _instance), "create Vulkan instance");

            uint physicalDeviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &physicalDeviceCount, null), "enumerate physical devices");
            if (physicalDeviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var physicalDevices = new PhysicalDevice[physicalDeviceCount];
            fixed (PhysicalDevice* pPhysicalDevices = physicalDevices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &physicalDeviceCount, pPhysicalDevices), "enumerate physical devices");
                _physicalDevice = physicalDevices[0];
            }

            _queueFamilyIndex = FindComputeQueueFamilyIndex(_physicalDevice);

            float queuePriority = 1f;
            float* pQueuePriority = &queuePriority;
            var queueCreateInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = pQueuePriority,
            };

            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
            };

            Check(_vk.CreateDevice(_physicalDevice, in deviceCreateInfo, null, out _device), "create logical device");

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);

            var commandPoolCreateInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
            };

            Check(_vk.CreateCommandPool(_device, in commandPoolCreateInfo, null, out _commandPool), "create command pool");
        }

        private uint FindComputeQueueFamilyIndex(PhysicalDevice physicalDevice)
        {
            uint queueFamilyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);

            var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
            {
                _vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, pQueueFamilies);
            }

            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if ((queueFamilies[(int)i].QueueFlags & (QueueFlags)ComputeQueueFlag) != 0 && queueFamilies[(int)i].QueueCount > 0)
                {
                    return i;
                }
            }

            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if (queueFamilies[(int)i].QueueCount > 0)
                {
                    return i;
                }
            }

            throw new InvalidOperationException("No usable Vulkan queue family was found.");
        }

        private void RebuildResources()
        {
            DestroyResources();

            var spirv = CompileShaderToSpirv();
            _shaderModule = CreateShaderModule(spirv);
            CreatePipelineLayoutAndDescriptors();
            CreatePipelineAndBuffers();
        }

        private void CreatePipelineLayoutAndDescriptors()
        {
            var bindingCount = (uint)(_inputs.Length + 1);
            var bindings = new DescriptorSetLayoutBinding[bindingCount];

            for (uint i = 0; i < _inputs.Length; i++)
            {
                bindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = i,
                    DescriptorCount = 1,
                    DescriptorType = (DescriptorType)StorageDescriptorType,
                    StageFlags = (ShaderStageFlags)ComputeStageFlag,
                };
            }

            bindings[_inputs.Length] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)_inputs.Length,
                DescriptorCount = 1,
                DescriptorType = (DescriptorType)StorageDescriptorType,
                StageFlags = (ShaderStageFlags)ComputeStageFlag,
            };

            fixed (DescriptorSetLayoutBinding* pBindings = bindings)
            {
                var layoutCreateInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = bindingCount,
                    PBindings = pBindings,
                };

                Check(_vk.CreateDescriptorSetLayout(_device, in layoutCreateInfo, null, out _descriptorSetLayout), "create descriptor set layout");
            }

            fixed (DescriptorSetLayout* pLayout = &_descriptorSetLayout)
            {
                var pipelineLayoutCreateInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1,
                    PSetLayouts = pLayout,
                };

                Check(_vk.CreatePipelineLayout(_device, in pipelineLayoutCreateInfo, null, out _pipelineLayout), "create pipeline layout");
            }

            var descriptorPoolSizes = new DescriptorPoolSize[1]
            {
                new DescriptorPoolSize
                {
                    Type = (DescriptorType)StorageDescriptorType,
                    DescriptorCount = (uint)(_inputs.Length + 1),
                }
            };

            fixed (DescriptorPoolSize* pPoolSizes = descriptorPoolSizes)
            {
                var descriptorPoolCreateInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = 1,
                    PPoolSizes = pPoolSizes,
                };

                Check(_vk.CreateDescriptorPool(_device, in descriptorPoolCreateInfo, null, out _descriptorPool), "create descriptor pool");
            }

            fixed (DescriptorSetLayout* pLayout = &_descriptorSetLayout)
            {
                var descriptorSetAllocateInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = pLayout,
                };

                Check(_vk.AllocateDescriptorSets(_device, in descriptorSetAllocateInfo, out _descriptorSet), "allocate descriptor set");
            }
        }

        private void CreatePipelineAndBuffers()
        {
            var hostVisibleCoherent = (MemoryPropertyFlags)(HostVisibleFlag | HostCoherentFlag);
            _inputBuffers = new BufferResource[_inputs.Length];
            for (int i = 0; i < _inputs.Length; i++)
            {
                _inputBuffers[i] = CreateStorageBuffer(_inputs[i], hostVisibleCoherent);
            }

            _outputBuffer = CreateStorageBuffer(new float[_length], hostVisibleCoherent);

            UpdateDescriptorSets();

            var mainBytes = Encoding.UTF8.GetBytes("main\0");
            fixed (byte* pMain = mainBytes)
            {
                var stageCreateInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = (ShaderStageFlags)ComputeStageFlag,
                    Module = _shaderModule,
                    PName = pMain,
                };

                var pipelineCreateInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stageCreateInfo,
                    Layout = _pipelineLayout,
                };

                Check(_vk.CreateComputePipelines(_device, default, 1, in pipelineCreateInfo, null, out _pipeline), "create compute pipeline");
            }
        }

        private void UpdateDescriptorSets()
        {
            var writeCount = _inputs.Length + 1;
            var bufferInfos = new DescriptorBufferInfo[writeCount];
            for (int i = 0; i < _inputs.Length; i++)
            {
                bufferInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = _inputBuffers[i].Buffer,
                    Offset = 0,
                    Range = _inputBuffers[i].Size,
                };
            }

            bufferInfos[_inputs.Length] = new DescriptorBufferInfo
            {
                Buffer = _outputBuffer.Buffer,
                Offset = 0,
                Range = _outputBuffer.Size,
            };

            var writes = new WriteDescriptorSet[writeCount];
            fixed (DescriptorBufferInfo* pBufferInfos = bufferInfos)
            {
                for (int i = 0; i < writeCount; i++)
                {
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = _descriptorSet,
                        DstBinding = (uint)i,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = (DescriptorType)StorageDescriptorType,
                        PBufferInfo = pBufferInfos + i,
                    };
                }

                fixed (WriteDescriptorSet* pWrites = writes)
                {
                    _vk.UpdateDescriptorSets(_device, (uint)writeCount, pWrites, 0, null);
                }
            }
        }

        private BufferResource CreateStorageBuffer(float[] data, MemoryPropertyFlags properties)
        {
            var byteSize = (ulong)(data.Length * sizeof(float));
            var createInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = byteSize,
                Usage = (BufferUsageFlags)StorageBufferUsageFlag,
                SharingMode = (SharingMode)0,
            };

            Check(_vk.CreateBuffer(_device, in createInfo, null, out VulkanBuffer buffer), "create buffer");

            MemoryRequirements memoryRequirements = default;
            _vk.GetBufferMemoryRequirements(_device, buffer, out memoryRequirements);
            var memoryTypeIndex = FindMemoryTypeIndex(memoryRequirements.MemoryTypeBits, properties);

            var allocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
            };

            Check(_vk.AllocateMemory(_device, in allocateInfo, null, out DeviceMemory memory), "allocate buffer memory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind buffer memory");

            fixed (float* pData = data)
            {
                void* mapped = null;
                Check(_vk.MapMemory(_device, memory, 0, byteSize, MemoryMapFlags.None, &mapped), "map buffer memory for upload");
                System.Buffer.MemoryCopy(pData, mapped, byteSize, byteSize);
                _vk.UnmapMemory(_device, memory);
            }

            return new BufferResource(buffer, memory, byteSize);
        }

        private uint FindMemoryTypeIndex(uint typeFilter, MemoryPropertyFlags desiredProperties)
        {
            PhysicalDeviceMemoryProperties memoryProperties = default;
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out memoryProperties);

            for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
            {
                if ((typeFilter & (1u << (int)i)) != 0)
                {
                    var memoryType = memoryProperties.MemoryTypes[(int)i];
                    if ((memoryType.PropertyFlags & desiredProperties) == desiredProperties)
                    {
                        return i;
                    }
                }
            }

            throw new InvalidOperationException("No compatible Vulkan memory type was found.");
        }

        private ShaderModule CreateShaderModule(uint[] spirvWords)
        {
            fixed (uint* pCode = spirvWords)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)(spirvWords.Length * sizeof(uint)),
                    PCode = pCode,
                };

                Check(_vk.CreateShaderModule(_device, in createInfo, null, out ShaderModule shaderModule), "create shader module");
                return shaderModule;
            }
        }

        private uint[] CompileShaderToSpirv()
        {
            // Lazy initialize Shaderc on first use.
            try
            {
                _shaderc ??= Shaderc.GetApi();
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is BadImageFormatException || ex is TypeInitializationException || ex is System.IO.FileNotFoundException)
            {
                throw new PlatformNotSupportedException(
                    "Shaderc native library is unavailable on this Android runtime (e.g. missing libm.so.6 dependency).", ex);
            }

            var compiler = _shaderc.CompilerInitialize();
            var options = _shaderc.CompileOptionsInitialize();
            if (compiler == null || options == null)
            {
                throw new InvalidOperationException("Failed to initialize shaderc compiler.");
            }

            try
            {
                var sourceSize = (nuint)Encoding.UTF8.GetByteCount(_shaderSource);
                var result = _shaderc.CompileIntoSpv(compiler, _shaderSource, sourceSize, _shaderKind, "shader.glsl", "main", options);
                if (result == null)
                {
                    throw new InvalidOperationException("shaderc returned a null compilation result.");
                }

                try
                {
                    var status = _shaderc.ResultGetCompilationStatus(result);
                    if (status != CompilationStatus.Success)
                    {
                        var errorMessage = _shaderc.ResultGetErrorMessageS(result);
                        throw new InvalidOperationException($"shaderc compilation failed: {status} {errorMessage}");
                    }

                    var byteLength = _shaderc.ResultGetLength(result);
                    if ((byteLength & 0x3) != 0)
                    {
                        throw new InvalidOperationException($"shaderc returned a SPIR-V payload with invalid byte length {byteLength}.");
                    }

                    var wordCount = checked((int)(byteLength / sizeof(uint)));
                    var words = new uint[wordCount];
                    fixed (uint* pWords = words)
                    {
                        System.Buffer.MemoryCopy(_shaderc.ResultGetBytes(result), pWords, byteLength, byteLength);
                    }

                    return words;
                }
                finally
                {
                    _shaderc.ResultRelease(result);
                }
            }
            finally
            {
                _shaderc.CompileOptionsRelease(options);
                _shaderc.CompilerRelease(compiler);
            }
        }

        private bool IsShaderCompilerDependencyFailure(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                if (current is PlatformNotSupportedException || current is DllNotFoundException || current is BadImageFormatException || current is System.IO.FileNotFoundException)
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        private void DisableVulkanCompute(string reason, Exception? ex = null)
        {
            if (_vulkanComputeDisabled)
            {
                return;
            }

            _vulkanComputeDisabled = true;
            _disableReason = reason;
            _initialized = false;

            System.Diagnostics.Debug.WriteLine($"Vulkan compute disabled: {reason}");
            if (ex != null)
            {
                System.Diagnostics.Debug.WriteLine($"Disable reason detail: {ex.GetType().Name}: {ex.Message}");
            }

            _readyTcs.TrySetResult(true);
        }

        private Array RunCompute(OutputElementType outputElementType)
        {
            lock (_sync)
            {
                if (_length <= 0)
                {
                    return outputElementType == OutputElementType.UInt32 ? Array.Empty<uint>() : Array.Empty<float>();
                }

                var commandBufferAllocateInfo = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = _commandPool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1,
                };

                Check(_vk.AllocateCommandBuffers(_device, in commandBufferAllocateInfo, out CommandBuffer commandBuffer), "allocate command buffer");

                try
                {
                    var beginInfo = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = (CommandBufferUsageFlags)1,
                    };

                    Check(_vk.BeginCommandBuffer(commandBuffer, in beginInfo), "begin command buffer");
                    _vk.CmdBindPipeline(commandBuffer, (PipelineBindPoint)ComputeBindPoint, _pipeline);
                    _vk.CmdBindDescriptorSets(commandBuffer, (PipelineBindPoint)ComputeBindPoint, _pipelineLayout, 0, 1, in _descriptorSet, 0, null);

                    var groupCount = (uint)Math.Max(1, (_length + _workGroupSize - 1) / _workGroupSize);
                    _vk.CmdDispatch(commandBuffer, groupCount, 1, 1);
                    Check(_vk.EndCommandBuffer(commandBuffer), "end command buffer");

                    var submitInfo = new SubmitInfo
                    {
                        SType = StructureType.SubmitInfo,
                        CommandBufferCount = 1,
                        PCommandBuffers = &commandBuffer,
                    };

                    Check(_vk.QueueSubmit(_queue, 1, in submitInfo, default), "submit compute work");
                    Check(_vk.QueueWaitIdle(_queue), "wait for compute queue");

                    var byteSize = (nuint)(_length * (outputElementType == OutputElementType.UInt32 ? sizeof(uint) : sizeof(float)));
                    void* mapped = null;
                    Check(_vk.MapMemory(_device, _outputBuffer.Memory, 0, (ulong)byteSize, MemoryMapFlags.None, &mapped), "map output buffer");

                    try
                    {
                        if (outputElementType == OutputElementType.UInt32)
                        {
                            var result = new uint[_length];
                            fixed (uint* pResult = result)
                            {
                                System.Buffer.MemoryCopy(mapped, pResult, byteSize, byteSize);
                            }

                            return result;
                        }

                        var resultFloats = new float[_length];
                        fixed (float* pResult = resultFloats)
                        {
                            System.Buffer.MemoryCopy(mapped, pResult, byteSize, byteSize);
                        }

                        return resultFloats;
                    }
                    finally
                    {
                        _vk.UnmapMemory(_device, _outputBuffer.Memory);
                    }
                }
                finally
                {
                    _vk.FreeCommandBuffers(_device, _commandPool, 1, in commandBuffer);
                }
            }
        }

        private void DestroyResources()
        {
            // Destroy device resources only if device exists
            if (_device.Handle != 0)
            {
                // Destroy in reverse order of creation
                if (_pipeline.Handle != 0)
                {
                    _vk.DestroyPipeline(_device, _pipeline, null);
                    _pipeline = default;
                }

                if (_shaderModule.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, _shaderModule, null);
                    _shaderModule = default;
                }

                if (_descriptorPool.Handle != 0)
                {
                    _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
                    _descriptorPool = default;
                }

                if (_pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                    _pipelineLayout = default;
                }

                if (_descriptorSetLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
                    _descriptorSetLayout = default;
                }

                foreach (var bufferResource in _inputBuffers)
                {
                    DestroyBuffer(bufferResource);
                }

                _inputBuffers = Array.Empty<BufferResource>();
                DestroyBuffer(_outputBuffer);
                _outputBuffer = default;

                if (_commandPool.Handle != 0)
                {
                    _vk.DestroyCommandPool(_device, _commandPool, null);
                    _commandPool = default;
                }

                _vk.DestroyDevice(_device, null);
                _device = default;
            }

            // Destroy instance (doesn't require device)
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }
        }

        private void DestroyBuffer(BufferResource bufferResource)
        {
            if (bufferResource.Buffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, bufferResource.Buffer, null);
            }

            if (bufferResource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, bufferResource.Memory, null);
            }
        }

        private static void ValidateInputs(float[][] inputs)
        {
            if (inputs == null || inputs.Length == 0 || inputs.Length > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), "Must provide between 1 and 6 input arrays.");
            }

            var expectedLength = inputs[0].Length;
            if (inputs.Any(input => input.Length != expectedLength))
            {
                throw new InvalidOperationException("All input arrays must have the same length.");
            }
        }

        private static void Check(Result result, string action)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"Failed to {action}: {result}");
            }
        }
    }

    public class NativeVulkanSurfaceView : Microsoft.Maui.Controls.View
    {
        public static readonly BindableProperty InputsProperty =
            BindableProperty.Create(nameof(Inputs), typeof(float[][]), typeof(NativeVulkanSurfaceView), null);

        public float[][]? Inputs
        {
            get => (float[][]?)GetValue(InputsProperty);
            set => SetValue(InputsProperty, value);
        }

        public static readonly BindableProperty ShaderSourceProperty =
            BindableProperty.Create(nameof(ShaderSource), typeof(string), typeof(NativeVulkanSurfaceView), string.Empty);

        public string ShaderSource
        {
            get => (string)GetValue(ShaderSourceProperty);
            set => SetValue(ShaderSourceProperty, value);
        }

        public static readonly BindableProperty WorkGroupSizeProperty =
            BindableProperty.Create(nameof(WorkGroupSize), typeof(int), typeof(NativeVulkanSurfaceView), 256);

        public int WorkGroupSize
        {
            get => (int)GetValue(WorkGroupSizeProperty);
            set => SetValue(WorkGroupSizeProperty, value);
        }

        public static readonly BindableProperty OutputElementTypeProperty =
            BindableProperty.Create(nameof(OutputElementType), typeof(OutputElementType), typeof(NativeVulkanSurfaceView), OutputElementType.Float32);

        public OutputElementType OutputElementType
        {
            get => (OutputElementType)GetValue(OutputElementTypeProperty);
            set => SetValue(OutputElementTypeProperty, value);
        }

        public static readonly BindableProperty ShaderKindProperty =
            BindableProperty.Create(nameof(ShaderKind), typeof(ShaderKind), typeof(NativeVulkanSurfaceView), ShaderKind.ComputeShader);

        public ShaderKind ShaderKind
        {
            get => (ShaderKind)GetValue(ShaderKindProperty);
            set => SetValue(ShaderKindProperty, value);
        }
    }

    public class NativeVulkanSurfaceViewHandler : ViewHandler<NativeVulkanSurfaceView, AndroidView>
    {
        public static void MapInputs(NativeVulkanSurfaceViewHandler handler, NativeVulkanSurfaceView view)
        {
            if (handler.PlatformView is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(view.ShaderSource))
            {
                System.Diagnostics.Debug.WriteLine("ERROR: shaderSource can't be null or whitespace.");
                return;
            }

            if (view.Inputs == null || view.Inputs.Length == 0 || view.Inputs.Length > 6)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR: Must provide between 1 and 6 input arrays. Got {view.Inputs?.Length ?? 0}");
                return;
            }

            if (handler.PlatformView is VulkanComputeView platformView)
            {
                // 如果这是第一次设置有效的输入，需要进行初始化
                if (!platformView.IsInitialized)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("MapInputs: Initializing VulkanComputeView with actual inputs...");
                        platformView.UpdateInputs(view.Inputs, view.ShaderSource, view.WorkGroupSize, view.ShaderKind, view.OutputElementType);
                        if (platformView.IsInitialized)
                        {
                            System.Diagnostics.Debug.WriteLine("MapInputs: VulkanComputeView initialized successfully");
                        }
                        else if (platformView.IsVulkanComputeDisabled)
                        {
                            System.Diagnostics.Debug.WriteLine("MapInputs: Vulkan compute unavailable on this device. Using fallback output.");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR: Failed to initialize Vulkan compute: {ex.GetType().Name}: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                    }
                }
                else
                {
                    // 如果已初始化，更新输入
                    try
                    {
                        platformView.UpdateInputs(view.Inputs, view.ShaderSource, view.WorkGroupSize, view.ShaderKind, view.OutputElementType);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR: Failed to update Vulkan inputs: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        public static IPropertyMapper<NativeVulkanSurfaceView, NativeVulkanSurfaceViewHandler> PropertyMapper =
            new PropertyMapper<NativeVulkanSurfaceView, NativeVulkanSurfaceViewHandler>(ViewHandler.ViewMapper)
            {
                [nameof(NativeVulkanSurfaceView.Inputs)] = MapInputs,
                [nameof(NativeVulkanSurfaceView.ShaderSource)] = MapInputs,
                [nameof(NativeVulkanSurfaceView.WorkGroupSize)] = MapInputs,
                [nameof(NativeVulkanSurfaceView.OutputElementType)] = MapInputs,
                [nameof(NativeVulkanSurfaceView.ShaderKind)] = MapInputs,
            };

        public NativeVulkanSurfaceViewHandler() : base(PropertyMapper)
        {
        }

        protected override AndroidView CreatePlatformView()
        {
            try
            {
                // Create view with minimal initialization
                // Actual Vulkan initialization will be deferred to MapInputs
                var view = new VulkanComputeView(Context, "void main() {}", Array.Empty<float[]>());
                return view;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create VulkanComputeView: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                
                // Return an empty placeholder view instead of throwing
                // This prevents MAUI handler from failing completely
                var placeholder = new AndroidView(Context);
                return placeholder;
            }
        }

        protected override void DisconnectHandler(AndroidView platformView)
        {
            if (platformView is VulkanComputeView computeView)
            {
                computeView.ReleaseResources();
            }

            base.DisconnectHandler(platformView);
        }
    }
}