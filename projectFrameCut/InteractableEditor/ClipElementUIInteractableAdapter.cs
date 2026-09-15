using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.DraftStuff;

namespace projectFrameCut.InteractableEditor;

/// <summary>Adapts the legacy timeline element without exposing it through the common API.</summary>
public sealed class ClipElementUIInteractableAdapter : IInteractableElement
{
    public ClipElementUIInteractableAdapter(ClipElementUI source) => Source = source;
    public ClipElementUI Source { get; }
    public Guid Id => Source.Id;
    public string DisplayName => Source.DisplayName;
    public InteractiveRect LogicalRect
    {
        get => new(Source.TargetX, Source.TargetY,
            Source.TargetWidth > 0 ? Source.TargetWidth : 1,
            Source.TargetHeight > 0 ? Source.TargetHeight : 1);
        set
        {
            Source.TargetX = (int)Math.Round(value.X);
            Source.TargetY = (int)Math.Round(value.Y);
            Source.TargetWidth = (int)Math.Round(value.Width);
            Source.TargetHeight = (int)Math.Round(value.Height);
        }
    }
    public InteractiveElementCapabilities Capabilities => new(
        Source.IsMoveable, Source.IsHorizontalResizable, Source.IsVerticalResizable,
        Source.AllowFreeScaleResize, Source.CanSnapWhilePlacing, Source.CanSnapWhileResizing);
    public bool IsVisible => Source.ShouldDisplayInUI;
    public int Layer => Source.origTrack ?? 0;
    public bool IsSelected { get; set; }
    public bool IsVisibleAtFrame(uint frame) => IsVisible;
}
