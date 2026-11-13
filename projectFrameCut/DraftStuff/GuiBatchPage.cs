using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace projectFrameCut.DraftStuff;

internal sealed class GuiBatchPage : ContentPage
{
    private readonly Label _status = new()
    {
        Text = Localized.DraftPage_RpcProcessing,
        TextColor = Colors.White,
        HorizontalTextAlignment = TextAlignment.Center,
    };
    private readonly Button _cancel = new()
    {
        Text = Localized.DraftPage_RpcBatch_Cancel,
        HorizontalOptions = LayoutOptions.Center,
    };

    public GuiBatchPage(Func<Task> cancel)
    {
        BackgroundColor = Color.FromArgb("#A0000000");
        Padding = 0;
        Shell.SetNavBarIsVisible(this, false);
        Microsoft.Maui.Controls.NavigationPage.SetHasNavigationBar(this, false);
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);
        Content = new Grid
        {
            Children =
            {
                new VerticalStackLayout
                {
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    MaximumWidthRequest = 640,
                    Padding = 24,
                    Spacing = 16,
                    Children = { new ActivityIndicator { IsRunning = true, Color = Colors.White }, _status, _cancel },
                },
            },
        };
        _cancel.Clicked += async (_, _) => await cancel();
        Loaded += (_, _) => _cancel.Focus();
    }

    public void SetCancelling()
    {
        _cancel.IsEnabled = false;
        _status.Text = Localized.DraftPage_Tasks_Status_Cancelling;
    }

    public void ShowError(Exception ex)
    {
        _status.Text = Localized._ExceptionTemplate(ex);
        _cancel.IsEnabled = true;
    }

    public void DisableCancellation() => _cancel.IsEnabled = false;

    protected override bool OnBackButtonPressed() => true;
}
