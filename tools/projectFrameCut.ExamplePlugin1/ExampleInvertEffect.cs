using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace SomePublisher;

public sealed class ExampleInvertEffect : INormalEffect
{
    private readonly float _amount;

    public ExampleInvertEffect(float amount)
    {
        _amount = Math.Clamp(amount, 0, 1);
        Parameters = new Dictionary<string, object> { ["Amount"] = _amount };
    }

    public string FromPlugin => ExamplePluginConstants.PluginId;
    public string TypeName => "ExampleInvert";
    public EffectImplementType ImplementType => EffectImplementType.IPicture;
    public string Name { get; set; } = "Example invert";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Parameters { get; }
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => true;
    public string? NeedComputer => null;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }

    public IEffect WithParameters(Dictionary<string, object> parameters)
    {
        object value = parameters.GetValueOrDefault("Amount", _amount);
        if (value is Func<object> getter) value = getter();
        return new ExampleInvertEffect(Convert.ToSingle(value));
    }

    public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
    {
        if (source is IPicture<byte> byteSource)
        {
            var result = new Picture8bpp(byteSource.Width, byteSource.Height)
            {
                HasAlphaChannel = byteSource.HasAlphaChannel,
                a = byteSource.a is null ? null : (float[])byteSource.a.Clone()
            };
            for (int i = 0; i < result.Pixels; i++)
            {
                result.r[i] = Mix(byteSource.r[i], byte.MaxValue);
                result.g[i] = Mix(byteSource.g[i], byte.MaxValue);
                result.b[i] = Mix(byteSource.b[i], byte.MaxValue);
            }
            return result;
        }

        if (source is IPicture<ushort> ushortSource)
        {
            var result = new Picture16bpp(ushortSource.Width, ushortSource.Height)
            {
                HasAlphaChannel = ushortSource.HasAlphaChannel,
                a = ushortSource.a is null ? null : (float[])ushortSource.a.Clone()
            };
            for (int i = 0; i < result.Pixels; i++)
            {
                result.r[i] = Mix(ushortSource.r[i], ushort.MaxValue);
                result.g[i] = Mix(ushortSource.g[i], ushort.MaxValue);
                result.b[i] = Mix(ushortSource.b[i], ushort.MaxValue);
            }
            return result;
        }

        throw new NotSupportedException($"Unsupported picture type '{source.GetType().Name}'.");
    }

    private byte Mix(byte value, byte max) => (byte)Math.Clamp((int)Math.Round(value * (1 - _amount) + (max - value) * _amount), 0, byte.MaxValue);
    private ushort Mix(ushort value, ushort max) => (ushort)Math.Clamp((int)Math.Round(value * (1 - _amount) + (max - value) * _amount), 0, ushort.MaxValue);
}
