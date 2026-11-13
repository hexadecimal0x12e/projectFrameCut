namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

/// <summary>Maps clip-local timeline frames directly to source offsets, in project frames.</summary>
public interface IDirectSpeedVarianceProvider : ISpeedVarianceProvider
{
    SpeedVarianceFrameMap GetFrameMap(uint length, uint? sourceLength);
}

public sealed class SpeedVarianceFrameMap
{
    private readonly Func<uint, uint> getSourceFrame;
    private readonly double singleFrameStep;
    public uint Length { get; }

    public SpeedVarianceFrameMap(uint[] frames) : this((uint)frames.Length, frame => frames[checked((int)frame)]) { }

    public SpeedVarianceFrameMap(uint length, Func<uint, uint> getSourceFrame, double singleFrameStep = 1)
    {
        Length = length;
        this.getSourceFrame = getSourceFrame;
        this.singleFrameStep = singleFrameStep;
    }

    public uint GetSourceFrame(uint frame) => frame < Length ? getSourceFrame(frame) : throw new ArgumentOutOfRangeException(nameof(frame));
    public double GetSourceFrameStep(uint frame) => frame + 1 < Length
        ? (double)GetSourceFrame(frame + 1) - GetSourceFrame(frame)
        : frame > 0 ? (double)GetSourceFrame(frame) - GetSourceFrame(frame - 1) : singleFrameStep;
}
