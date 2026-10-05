using System.Text.Json;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public enum TransformSide { Left, Right }
public enum TransformInputMode { OneInput, TwoInput }

public sealed class TransformBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TransformSide Side { get; set; }
    public TransformInputMode InputMode { get; set; }
    public uint Duration { get; set; }
    public Guid LeftClipId { get; set; }
    public Guid RightClipId { get; set; }
    public bool IsAI { get; set; }
    public JsonElement TransformElement { get; set; } = JsonSerializer.SerializeToElement(new Dictionary<string, object>());

    public static string Key(TransformSide side) => side == TransformSide.Left ? "LeftTransform" : "RightTransform";

    public static TransformBinding? Read(Dictionary<string, object>? metadata, TransformSide side)
    {
        if (metadata is null || !metadata.TryGetValue(Key(side), out var value)) return null;
        return value is TransformBinding binding ? binding : value is JsonElement json ? json.Deserialize<TransformBinding>() : null;
    }

    public static void Write(Dictionary<string, object> metadata, TransformSide side, TransformBinding? binding)
    {
        if (binding is null) metadata.Remove(Key(side));
        else metadata[Key(side)] = JsonSerializer.SerializeToElement(binding);
    }

    public void Capture(ITransform transform) => TransformElement = transform.Serialize();

    public static void CopySingleInputs(Dictionary<string, object> metadata, Guid newClipId)
    {
        foreach (var side in Enum.GetValues<TransformSide>())
        {
            var binding = Read(metadata, side);
            if (binding?.InputMode != TransformInputMode.OneInput)
            {
                Write(metadata, side, null);
                continue;
            }
            binding = JsonSerializer.Deserialize<TransformBinding>(JsonSerializer.Serialize(binding))!;
            binding.Id = Guid.NewGuid();
            binding.LeftClipId = newClipId;
            binding.RightClipId = Guid.Empty;
            Write(metadata, side, binding);
        }
    }
}
