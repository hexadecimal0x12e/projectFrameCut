using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;

namespace SomePublisher;

public sealed class ExampleAudioSource : IAudioSource<float>
{
    private bool _disposed;

    public ExampleAudioSource(string? path) => SourcePath = path;

    public string? SourcePath { get; }
    public string[] PreferredExtension => [".exampleaudio", ".tone"];
    public uint Duration => 441000;
    public int ChannelCount => 2;
    public int SamplePerSecond => 44100;
    public bool Disposed => _disposed;

    public void Initialize() { }
    public IAudioSource<float> CreateNew(string newSource) => new ExampleAudioSource(newSource);

    public IAudioSamples<float> GetSample(uint startIndex, long count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count < 0 || count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
        int length = (int)count;
        var left = new float[length];
        var right = new float[length];
        for (int i = 0; i < length; i++)
        {
            float sample = (float)Math.Sin((startIndex + i) * Math.PI * 2 * 440 / SamplePerSecond);
            left[i] = sample;
            right[i] = sample;
        }
        return new FloatStereoAudioSamples
        {
            Left = left,
            Right = right,
            SampleCount = length,
            SamplePerSecond = SamplePerSecond
        };
    }

    public float GetSingleSample(uint index) => GetSample(index, 1).GetSamples(0)[0];
    public void Dispose() => _disposed = true;
}
