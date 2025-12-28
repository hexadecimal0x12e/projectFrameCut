#nullable enable
using projectFrameCut.ViewModels;
using projectFrameCut.Asset;
using projectFrameCut.Render.RenderAPIBase.Project;
using Microsoft.Maui.Controls;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using projectFrameCut.Services;
using System.Threading.Tasks;


#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
#endif

#if iDevices
using Foundation;
using UIKit;

#endif

namespace projectFrameCut;

public partial class AssetsLibraryPage : ContentPage
{
    private readonly int ItemSize = 200;
    private readonly int ItemSpacing = 5;

    public AssetsLibraryPage()
    {
        InitializeComponent();
        SourcePicker.ItemsSource = new string[] { Environment.MachineName, Localized.AssetPage_AddASource };
        SourcePicker.SelectedIndex = 0;
    }

    private void OnCollectionViewSizeChanged(object sender, EventArgs e)
    {
        if (sender is CollectionView collectionView && collectionView.ItemsLayout is GridItemsLayout gridLayout)
        {
            var width = collectionView.Width;
            if (width > 0)
            {
                var span = Math.Max(1, (int)((width + ItemSpacing) / (ItemSize + ItemSpacing)));
                if (gridLayout.Span != span)
                {
                    gridLayout.Span = span;
                }
            }
        }
    }

    public async void OnDrop(object sender, DropEventArgs e)
    {
        List<string> filePaths = new List<string>();
        if (BindingContext is AssetViewModel vm)
        {
            foreach (var item in await FileDropHelper.GetFilePathsFromDrop(e))
            {
                await AddAsset(item);
            }


        }
    }

    private async void AddAAsset_Clicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync();
            if (result != null)
            {
                await AddAsset(result.FullPath);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Add a asset", this);
            AppShell.instance?.CurrentPage?.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    public async Task AddAsset(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (BindingContext is not AssetViewModel vm) return;
        await Task.Run(() =>
        {
            if (AssetDatabase.Add(path, out var asset))
            {
                vm.Assets.Add(asset);
            }
        });
    }


    DateTime pointerDownTime = DateTime.MinValue;


    private async void TapGestureRecognizer_Tapped(object sender, TappedEventArgs e)
    {
        if (BindingContext is AssetViewModel vm && sender is Border menuItem && menuItem.BindingContext is AssetItem asset)
        {
            await ShowContextMenu(asset);
        }
    }

    private void Border_Loaded(object sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Border border && BindingContext is AssetViewModel vm && border.BindingContext is AssetItem asset)
        {
#if WINDOWS || MACCATALYST
            // Windows: Right-click to show context menu
            var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Secondary };
            tap.Tapped += async (_, _) =>
            {
                await ShowContextMenu(asset);
            };

            // remove existing tap to avoid duplicates
            var existing = border.GestureRecognizers.OfType<TapGestureRecognizer>().FirstOrDefault();
            if (existing is not null) border.GestureRecognizers.Remove(existing);
            border.GestureRecognizers.Add(tap);
#elif ANDROID || IOS
            // Android/iOS: Single tap to open, long press (>500ms) to show context menu
            var pointerGesture = new PointerGestureRecognizer();
            DateTime pointerDownTime = DateTime.MinValue;

            pointerGesture.PointerPressed += (s, e) =>
            {
                pointerDownTime = DateTime.Now;
            };

            pointerGesture.PointerReleased += async (s, e) =>
            {
                var duration = (DateTime.Now - pointerDownTime).TotalMilliseconds;
                if (duration >= 500)
                {
                    Dispatcher.Dispatch(async () =>
                    {
                        try
                        {
                            if (Vibration.Default.IsSupported)
                                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
                            await ShowContextMenu(asset);
                        }
                        catch { }
                    });
                }
                else if (duration > 0)
                {
                    AssetsCollectionView.SelectedItem = asset;
                }
            };

            // Remove any existing pointer gesture recognizer to avoid duplicates
            var existingPointer = border.GestureRecognizers.OfType<PointerGestureRecognizer>().FirstOrDefault();
            if (existingPointer is not null) border.GestureRecognizers.Remove(existingPointer);

            border.GestureRecognizers.Add(pointerGesture);
#endif
        }
    }


    private async Task ShowContextMenu(AssetItem asset)
    {
        if (BindingContext is AssetViewModel vm)
        {
            var verbs = new List<string>
            {
                Localized.HomePage_ProjectContextMenu_Rename,
                Localized.HomePage_ProjectContextMenu_Delete
            };
            var action = await DisplayActionSheetAsync(asset.Name, Localized._Cancel, null, verbs.ToArray());
            await Dispatcher.DispatchAsync(async () =>
            {
                switch (verbs.IndexOf(action))
                {
                    case 0:
                        var newName = await DisplayPromptAsync("Rename", "New name:", initialValue: asset.Name);
                        if (!string.IsNullOrWhiteSpace(newName) && newName != asset.Name)
                        {
                            vm.RenameAsset(asset, newName);
                        }
                        break;

                    case 1:
                        var confirm0 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(asset.Name), Localized._Confirm, Localized._Cancel);
                        if (!confirm0) return;
#if WINDOWS
                        bool confirm2 = false;
                        Microsoft.UI.Xaml.Controls.ContentDialog lastDiag = new Microsoft.UI.Xaml.Controls.ContentDialog
                        {
                            Title = Localized._Warn,
                            Content = Localized.HomePage_ProjectContextMenu_Delete_Confirm1(asset.Name),
                            PrimaryButtonText = Localized.HomePage_ProjectContextMenu_Delete_Confirm3(asset.Name),
                            CloseButtonText = Localized._Cancel,
                            PrimaryButtonStyle = new Microsoft.UI.Xaml.Style(typeof(Microsoft.UI.Xaml.Controls.Button))
                            {
                                Setters =
                                {
                                    new Microsoft.UI.Xaml.Setter(
                                        Microsoft.UI.Xaml.Controls.Control.BackgroundProperty,
                                        Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"]
                                    )
                                }
                            }
                        };
                        var services = Application.Current?.Handler?.MauiContext?.Services;
                        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
                        if (dialogueHelper != null)
                        {
                            var result = await dialogueHelper.ShowContentDialogue(lastDiag);
                            confirm2 = result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
                        }
#else
                        var confirm2 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm1(asset.Name), Localized.HomePage_ProjectContextMenu_Delete_Confirm3(asset.Name), Localized._Cancel);
#endif
                        if (confirm2) 
                        {
                            vm.DeleteAsset(asset);
                            await DisplayAlertAsync(Localized._Info, Localized.HomePage_ProjectContextMenu_Delete_Deleted(asset.Name), Localized._OK);

                        }
                        break;
                    default:
                        break;
                }
            });
        }
    }

    private async void OnAssetPreviewDoubleClicked(object sender, TappedEventArgs e)
    {
        if (sender is Image i)
        {
            if (i.BindingContext is AssetItem a)
            {
                if (!string.IsNullOrWhiteSpace(a.Path) && File.Exists(a.Path))
                {
                    await FileSystemService.OpenFileAsync(a.Path);

                }
            }
        }
    }

    private void AssetSearchBar_TextChanged(object sender, TextChangedEventArgs e)
    {

    }

    private void SourcePicker_SelectedIndexChanged(object sender, EventArgs e)
    {

    }
}