using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

[ProtoContract]
public sealed class FrameSequenceRenderOptions
{
    [ProtoMember(1)] public int MaxThreads { get; set; } = 8;
    [ProtoMember(2)] public bool AllowWorkerOversubscription { get; set; } = true;
    [ProtoMember(3)] public int RenderAheadFrames { get; set; } = 0;
    [ProtoMember(4)] public bool EnableMemoryBudget { get; set; } = false;
    [ProtoMember(5)] public long MaxBufferedBytes { get; set; } = 0;
    [ProtoMember(6)] public bool EnableThreadAffinity { get; set; } = true;
    [ProtoMember(7)] public int[] WorkerCPUCoreIndexs { get; set; } = [];
    [ProtoMember(8)] public int GCOption { get; set; } = 0;
    [ProtoMember(9)] public bool ReuseDynamicPreviewCache { get; set; } = false;
    [ProtoMember(10)] public bool ResumeInterruptedRender { get; set; } = true;
    [ProtoMember(11)] public bool LogProcessStack { get; set; } = false;
    [ProtoMember(12)] public bool UseHDR { get; set; } = false;
    [ProtoMember(13)] public int MaximumHDRBrightness { get; set; } = 1000;
    [ProtoMember(14)] public int SDRClipsBrightnessInHDRMode { get; set; } = 203;
    [ProtoMember(15)] public uint ChunkFrames { get; set; } = 0;
}
