using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Render.ClipsAndTracks;

namespace projectFrameCut.InteractableEditor;

public partial class PreviewErrorView : ContentView
{
    private static readonly ImageSource s_errorIcon = ImageSource.FromStream(() => typeof(ClipInitializationFailure).Assembly
        .GetManifestResourceStream("projectFrameCut.Render.Resource.Error.png")!);

    public PreviewErrorView(string message)
    {
        InitializeComponent();
        ErrorIcon.Source = s_errorIcon;
        ErrorMessageLabel.Text = message;
        ToolTipProperties.SetText(this, message);
    }

    internal static View? GetView(PreparedPreview preview)
        => preview.View ?? (string.IsNullOrWhiteSpace(preview.ErrorMessage) ? null : new PreviewErrorView(preview.ErrorMessage));

    private void OnErrorLayoutSizeChanged(object? sender, EventArgs e)
    {
        var w = ErrorLayout.Width;
        var h = ErrorLayout.Height;
        if (w <= 0 || h <= 0) return;

        // Preview hosts scale logical clip dimensions, so content sizes must follow the allocated size.
        var side = Math.Min(w, h) * 0.32;
        AbsoluteLayout.SetLayoutBounds(ErrorIcon, new Rect((w - side) / 2, h * 0.40 - side / 2, side, side));
        ErrorMessageLabel.FontSize = Math.Max(8, Math.Min(w, h) * 0.034);
        AbsoluteLayout.SetLayoutBounds(ErrorMessageLabel, new Rect(w * 0.09, h * 0.62, w * 0.82, h * 0.32));
    }
}
