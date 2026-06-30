using projectFrameCut.Render.RenderAPIBase.Animation;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels;

/// <summary>
/// Display model for a shape in the shape gallery panel.
/// Used as a card in the "Add Shape" FlexLayout.
/// </summary>
public class ShapeGalleryItemViewModel : INotifyPropertyChanged
{
    /// <summary>Type of shape this card represents.</summary>
    public VectorShapeType ShapeType { get; init; }

    /// <summary>Human-readable name for the gallery card.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Unicode icon character for visual identification.</summary>
    public string Icon { get; init; } = "";

    /// <summary>Brief description shown as a tooltip or subtitle.</summary>
    public string Description { get; init; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
