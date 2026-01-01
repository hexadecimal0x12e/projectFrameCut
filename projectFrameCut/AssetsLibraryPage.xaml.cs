#nullable enable
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private static AssetsLibraryPage? instance = null;

    private readonly int ItemSize = 200;
    private readonly int ItemSpacing = 5;

    public AssetsLibraryPage()
    {
        InitializeComponent();
        instance = this;    
        SourcePicker.ItemsSource = new string[] { OperatingSystem.IsWindows() ? Environment.MachineName : "Your devices", Localized.AssetPage_AddASource };
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
        await AssetDatabase.Add(path, this);
        if (BindingContext is AssetViewModel vm)
        {
            vm.LoadAssets();
        }
    }


    DateTime pointerDownTime = DateTime.MinValue;
    string lastClick = "";

    private void Border_Loaded(object sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Border border && BindingContext is AssetViewModel vm)
        {
#if WINDOWS || MACCATALYST
            // Windows: Right-click to show context menu
            var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Secondary };
            tap.Tapped += async (_, _) =>
            {
                if (border.BindingContext is AssetItem currentAsset)
                {
                    await ShowContextMenu(currentAsset);
                }
            };

            var play = new TapGestureRecognizer { NumberOfTapsRequired = 2, Buttons = ButtonsMask.Primary };
            play.Tapped += async (s, e) =>
            {
                if (border.BindingContext is AssetItem currentAsset)
                {
                    ShowPreview(currentAsset);
                }
            };

            // remove existing tap to avoid duplicates
            var existing = border.GestureRecognizers.OfType<TapGestureRecognizer>().ToList();
            foreach (var gesture in existing)
            {
                border.GestureRecognizers.Remove(gesture);
            }
            border.GestureRecognizers.Add(tap);
            border.GestureRecognizers.Add(play);
#elif ANDROID || IOS
            var pointerGesture = new PointerGestureRecognizer();
            DateTime pointerDownTime = DateTime.MinValue;

            pointerGesture.PointerPressed += (s, e) =>
            {
                pointerDownTime = DateTime.Now;
            };

            pointerGesture.PointerReleased += async (s, e) =>
            {
                var duration = (DateTime.Now - pointerDownTime).TotalMilliseconds;
                if (border.BindingContext is not AssetItem currentAsset) return;

                if (duration >= 500)
                {
                    Dispatcher.Dispatch(async () =>
                    {
                        try
                        {
                            if (Vibration.Default.IsSupported)
                                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
                            await ShowContextMenu(currentAsset);
                        }
                        catch { }
                    });
                }
                else if (duration > 0)
                {
                    if (lastClick == currentAsset.AssetId)
                    {
                        ShowPreview(currentAsset);
                    }
                    else
                    {
                        lastClick = currentAsset.AssetId;
                        AssetsCollectionView.SelectedItem = currentAsset;

                    }
                }
            };

            var existingPointer = border.GestureRecognizers.OfType<PointerGestureRecognizer>().FirstOrDefault();
            if (existingPointer is not null) border.GestureRecognizers.Remove(existingPointer);

            border.GestureRecognizers.Add(pointerGesture);
#endif

            ToolTipProperties.SetText(border, Localized.AssetPage_DoubleClickToPreview);
        }
    }

    private async void ShowPreview(AssetItem currentAsset)
    {
        try
        {
            if(OperatingSystem.IsAndroid() || (currentAsset.AssetType is AssetType.Font) || (currentAsset.AssetType is AssetType.Other))
            {
                if (!string.IsNullOrWhiteSpace(currentAsset.Path) && File.Exists(currentAsset.Path))
                {
                    await FileSystemService.OpenFileAsync(currentAsset.Path);
                }
            }
            else
            {
                await Dispatcher.DispatchAsync(async () =>
                {
                    await Navigation.PushAsync(new AssetPlaybackPage(currentAsset));
                });
            }

        }
        catch (Exception ex)
        {
            Log(ex, "Showing asset playback popup", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }



    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            AssetPlaybackPage.StopMedia();
        }
        catch (Exception ex)
        {
            Log(ex, "Stopping media on AssetsLibraryPage appearing", this);
        }
    }

    private async Task ShowContextMenu(AssetItem asset)
    {
        if (BindingContext is AssetViewModel vm)
        {
            var verbs = new List<string>
            {
                Localized.AssetPage_ShowPreview,
                Localized.AssetPage_OpenInSystem,
                Localized.HomePage_ProjectContextMenu_Rename,
                Localized.HomePage_ProjectContextMenu_Delete
            };
            var action = await DisplayActionSheetAsync(asset.Name, Localized._Cancel, null, verbs.ToArray());
            await Dispatcher.DispatchAsync(async () =>
            {
                switch (verbs.IndexOf(action))
                {
                    case 0:
                        ShowPreview(asset);
                        break;
                    case 1:
                        if (!string.IsNullOrWhiteSpace(asset.Path) && File.Exists(asset.Path))
                        {
                            await FileSystemService.OpenFileAsync(asset.Path);
                        }
                        break; 
                    case 2:
                        var newName = await DisplayPromptAsync(Localized._Info, Localized.AssetPage_InputNewName, initialValue: asset.Name);
                        if (!string.IsNullOrWhiteSpace(newName) && newName != asset.Name)
                        {
                            vm.RenameAsset(asset, newName);
                        }
                        break;

                    case 3:
                        var confirm0 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(asset.Name), Localized._Confirm, Localized._Cancel);
                        if (!confirm0) return;
                        var confirm1 = await DisplayPromptAsync(Localized._Warn, Localized.AssetPage_DeleteAAsset_Confirm1(asset.Name), Localized._OK, Localized._Cancel, asset.Name);
                        if (confirm1 == asset.Name)
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
                ShowPreview(a);
            }
        }
    }

    private void AssetSearchBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BindingContext is AssetViewModel vm)
        {
            vm.SearchText = e.NewTextValue ?? string.Empty;
        }
    }

    private void SourcePicker_SelectedIndexChanged(object sender, EventArgs e)
    {

    }
}