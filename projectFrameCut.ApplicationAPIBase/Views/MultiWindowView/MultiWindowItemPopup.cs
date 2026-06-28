
namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    /// <summary>
    /// Configures a popup to be displayed inside a <see cref="MultiWindowItem"/>
    /// via <see cref="MultiWindowItem.ShowPopupAsync(MultiWindowItemPopup)"/>.
    /// Modeled after CommunityToolkit.Maui.Views.Popup but operates within the MDI window overlay.
    /// </summary>
    public class MultiWindowItemPopup
    {
        /// <summary>
        /// The content view displayed inside the popup.
        /// </summary>
        public View? Content { get; set; }

        /// <summary>
        /// Whether tapping the semi-transparent backdrop outside the popup content dismisses it.
        /// Default true.
        /// </summary>
        public bool CanBeDismissedByTappingOutsideOfPopup { get; set; } = true;

        /// <summary>
        /// Horizontal placement of the popup container within the overlay.
        /// Default <see cref="LayoutOptions.Center"/>.
        /// </summary>
        public LayoutOptions HorizontalOptions { get; set; } = LayoutOptions.Center;

        /// <summary>
        /// Vertical placement of the popup container within the overlay.
        /// Default <see cref="LayoutOptions.Center"/>.
        /// </summary>
        public LayoutOptions VerticalOptions { get; set; } = LayoutOptions.Center;

        /// <summary>
        /// Background color of the overlay backdrop.
        /// Default #AA000000 (semi-transparent black).
        /// </summary>
        public Color BackgroundColor { get; set; } = Color.FromArgb("#AA000000");

        /// <summary>
        /// Background color of the popup content container.
        /// Default is the current theme's window background (#252526 dark / White light).
        /// </summary>
        public Color? PopupBackgroundColor { get; set; }

        /// <summary>
        /// Corner radius of the popup container border.
        /// Default 12.
        /// </summary>
        public double CornerRadius { get; set; } = 12;

        /// <summary>
        /// Padding applied to the popup container border around the content.
        /// Default 0.
        /// </summary>
        public Thickness Padding { get; set; } = new Thickness(0);

        /// <summary>
        /// Width of the popup container. If null, auto-sizes to content.
        /// </summary>
        public double? WidthRequest { get; set; }

        /// <summary>
        /// Height of the popup container. If null, auto-sizes to content.
        /// </summary>
        public double? HeightRequest { get; set; }

        /// <summary>
        /// Raised after the popup has been displayed and the show animation has completed.
        /// </summary>
        public event EventHandler? Opened;

        /// <summary>
        /// Raised after the popup has been dismissed and the close animation has completed.
        /// </summary>
        public event EventHandler? Closed;

        /// <summary>
        /// Raised when the popup is dismissed by tapping outside the popup content.
        /// Not raised when <see cref="MultiWindowItem.HidePopupAsync(MultiWindowItemPopup, bool)"/> is called programmatically.
        /// </summary>
        public event EventHandler? DismissedByTappingOutside;

        // Internal — used for async coordination
        internal TaskCompletionSource<bool>? DismissedTcs { get; set; }

        internal void RaiseOpened() => Opened?.Invoke(this, EventArgs.Empty);
        internal void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
        internal void RaiseDismissedByTappingOutside() => DismissedByTappingOutside?.Invoke(this, EventArgs.Empty);
    }
}
