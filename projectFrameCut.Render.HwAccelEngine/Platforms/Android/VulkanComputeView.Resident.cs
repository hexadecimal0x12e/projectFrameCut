#if ANDROID
using Silk.NET.Vulkan;
using projectFrameCut.Render.HwAccelEngine.Effect;
using Microsoft.Maui.ApplicationModel;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android;
public unsafe partial class VulkanComputeView
{
    private sealed class ResidentStorage(VulkanComputeView view, BufferResource buffer) : ResidentBuffer
    {
        internal readonly BufferResource Buffer = buffer;
        internal override int Length => checked((int)(Buffer.Size));
        internal override void Destroy()
        {
            lock (view._sync) view.DestroyBuffer(Buffer);
        }
    }

    internal ResidentBuffer AllocateResident(int count, byte[]? values = null)
    {
        lock (_sync)
        {
            var createInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = checked((ulong)count),
                Usage = (BufferUsageFlags)StorageBufferUsageFlag,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, in createInfo, null, out VulkanBuffer buffer), "create resident image");
            DeviceMemory memory = default;
            try
            {
                _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
                var allocate = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryTypeIndex(requirements.MemoryTypeBits, (MemoryPropertyFlags)(HostVisibleFlag | HostCoherentFlag)),
                };
                Check(_vk.AllocateMemory(_device, in allocate, null, out memory), "allocate resident image");
                Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind resident image");
                if (values is not null)
                {
                    void* mapped = null;
                    Check(_vk.MapMemory(_device, memory, 0, createInfo.Size, MemoryMapFlags.None, &mapped), "upload resident image");
                    try
                    {
                        fixed (byte* data = values)
                            System.Buffer.MemoryCopy(data, mapped, createInfo.Size, createInfo.Size);
                    }
                    finally
                    {
                        _vk.UnmapMemory(_device, memory);
                    }
                }

                return new ResidentStorage(this, new(buffer, memory, createInfo.Size));
            }
            catch
            {
                _vk.DestroyBuffer(_device, buffer, null);
                if (memory.Handle != 0)
                    _vk.FreeMemory(_device, memory, null);
                throw;
            }
        }
    }

    private readonly List<ResidentCommand> residentCommands = [];
    private sealed record ResidentCommand(DescriptorPool Pool, CommandBuffer Command, Fence Fence);

    internal void WriteResident(ResidentBuffer storage, byte[] values)
    {
        lock (_sync)
        {
            var buffer = ((ResidentStorage)storage).Buffer;
            void* mapped = null;
            Check(_vk.MapMemory(_device, buffer.Memory, 0, buffer.Size, MemoryMapFlags.None, &mapped), "upload resident image");
            try
            {
                fixed (byte* data = values)
                    System.Buffer.MemoryCopy(data, mapped, buffer.Size, checked((ulong)values.Length));
            }
            finally { _vk.UnmapMemory(_device, buffer.Memory); }
        }
    }

    internal void CompleteResident()
    {
        lock (_sync)
        {
            Exception? error = null;
            foreach (var pending in residentCommands)
            {
                var fence = pending.Fence;
                try { Check(_vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue), "wait for resident images"); }
                catch (Exception ex) { error ??= ex; }
            }
            foreach (var pending in residentCommands)
            {
                var command = pending.Command;
                _vk.FreeCommandBuffers(_device, _commandPool, 1, in command);
                _vk.DestroyFence(_device, pending.Fence, null);
                _vk.DestroyDescriptorPool(_device, pending.Pool, null);
            }
            residentCommands.Clear();
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    internal void ReleaseResidentResources()
    {
        try
        {
            try { CompleteResident(); }
            finally { ReleaseResources(); }
        }
        finally
        {
            _shaderc?.Dispose();
            _vk.Dispose();
            Dispose();
        }
    }

    internal byte[] ReadResident(ResidentBuffer storage)
    {
        lock (_sync)
        {
            var buffer = ((ResidentStorage)storage).Buffer;
            var result = new byte[checked((int)buffer.Size)];
            void* mapped = null;
            Check(_vk.MapMemory(_device, buffer.Memory, 0, buffer.Size, MemoryMapFlags.None, &mapped), "download resident image");
            try
            {
                fixed (byte* data = result)
                    System.Buffer.MemoryCopy(mapped, data, buffer.Size, buffer.Size);
            }
            finally
            {
                _vk.UnmapMemory(_device, buffer.Memory);
            }

            return result;
        }
    }

    internal void DispatchResident(ResidentBuffer source, ResidentBuffer second, ResidentBuffer parameters, ResidentBuffer output, int count)
    {
        lock (_sync)
        {
            var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 4 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Check(_vk.CreateDescriptorPool(_device, in poolInfo, null, out DescriptorPool pool), "create resident descriptors");
            CommandBuffer command = default;
            Fence fence = default;
            try
            {
                var layout = _descriptorSetLayout;
                var setInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = pool,
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                Check(_vk.AllocateDescriptorSets(_device, in setInfo, out DescriptorSet set), "allocate resident descriptors");
                var buffers = new[] { source, second, parameters, output };
                for (int i = 0; i < buffers.Length; i++)
                {
                    var buffer = ((ResidentStorage)buffers[i]).Buffer;
                    var info = new DescriptorBufferInfo { Buffer = buffer.Buffer, Offset = 0, Range = buffer.Size };
                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = set,
                        DstBinding = (uint)i,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = &info,
                    };
                    _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
                }
                var allocate = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = _commandPool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1,
                };
                Check(_vk.AllocateCommandBuffers(_device, in allocate, out command), "allocate resident command");
                var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                Check(_vk.CreateFence(_device, in fenceInfo, null, out fence), "create resident fence");
                var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                Check(_vk.BeginCommandBuffer(command, in begin), "begin resident command");
                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.HostWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                };
                _vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit, 0, 1, in barrier, 0, null, 0, null);
                _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
                _vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, in set, 0, null);
                _vk.CmdDispatch(command, (uint)((count + 255) / 256), 1, 1);
                barrier.SrcAccessMask = AccessFlags.ShaderWriteBit;
                barrier.DstAccessMask = AccessFlags.HostReadBit;
                _vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, in barrier, 0, null, 0, null);
                Check(_vk.EndCommandBuffer(command), "end resident command");
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &command };
                var pending = new ResidentCommand(pool, command, fence);
                residentCommands.EnsureCapacity(residentCommands.Count + 1);
                Check(_vk.QueueSubmit(_queue, 1, in submit, fence), "submit resident command");
                residentCommands.Add(pending);
            }
            catch
            {
                if (command.Handle != 0) _vk.FreeCommandBuffers(_device, _commandPool, 1, in command);
                if (fence.Handle != 0) _vk.DestroyFence(_device, fence, null);
                _vk.DestroyDescriptorPool(_device, pool, null);
                throw;
            }
        }
    }
}

internal sealed class VulkanResidentPictureContext : ResidentPictureContext
{
    private readonly VulkanComputeView view;
    internal VulkanResidentPictureContext()
    {
        view = new VulkanComputeView(Platform.AppContext, ResidentShaders.Vulkan, new float[32], new float[32], new float[32]);
        if (!view.IsInitialized)
        {
            view.ReleaseResidentResources();
            throw new NotSupportedException("Vulkan resident images are unavailable.");
        }
    }

    protected override ResidentBuffer CreateBuffer(int length) => view.AllocateResident(length);
    protected override void WriteBuffer(ResidentBuffer buffer, byte[] values) => view.WriteResident(buffer, values);
    protected override byte[] Read(ResidentBuffer buffer) => view.ReadResident(buffer);
    protected override ResidentBuffer Dispatch(ResidentBuffer source, ResidentBuffer second, float[] parameters, int count)
    {
        BeginSubmission();
        var output = RentBuffer(checked((int)parameters[3] * (int)parameters[4] * ResidentPixel.Size));
        try
        {
            using var p = UploadBuffer(ResidentPixel.PackParameters(parameters));
            view.DispatchResident(source, second, p, output, count);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    protected override void Synchronize() => view.CompleteResident();
    protected override void Release()
    {
        view.ReleaseResidentResources();
    }
}
#endif
