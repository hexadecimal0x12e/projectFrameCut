namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

// Keeps a grouped element associated with its child clip's image effects.
public interface IVectorClipElementTag
{
    Guid SourceClipId { get; }
}
