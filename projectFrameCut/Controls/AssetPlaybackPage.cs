#nullable enable
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.IO;

namespace projectFrameCut.Controls;

/// <summary>
/// Helper class to build the content for asset playback popup
/// </summary>
public partial class AssetPlaybackPage : ContentPage
{
    private static MediaElement? _currentMediaPlayer;

    /// <summary>
    /// Creates the popup content view for the given asset
    /// </summary>
    public AssetPlaybackPage(AssetItem asset)
    {
        var contentArea = new Grid
        {
            Margin = new Thickness(0, 10)
        };

        // Add appropriate content based on asset type
        switch (asset.AssetType)
        {
            case AssetType.Video:
                AddVideoContent(contentArea, asset);
                break;

            case AssetType.Audio:
                AddAudioContent(contentArea, asset);
                break;

            case AssetType.Image:
                AddImageContent(contentArea, asset);
                break;

            default:
                AddUnsupportedContent(contentArea, asset);
                break;
        }

        // Footer
        var pathLabel = new Label
        {
            Text = asset.Path ?? "No path available",
            FontSize = 11,
            TextColor = Colors.Gray,
            LineBreakMode = LineBreakMode.MiddleTruncation
        };

        var infoItems = new List<string>();
        if (asset.Width > 0 && asset.Height > 0)
        {
            infoItems.Add($"{asset.Width}×{asset.Height}");
        }
        if (asset.FrameCount.HasValue && asset.FrameCount > 0 && asset.SecondPerFrame > 0)
        {
            var duration = TimeSpan.FromSeconds(asset.FrameCount.Value * asset.SecondPerFrame);
            infoItems.Add($"Duration: {duration:hh\\:mm\\:ss\\.ff}");
        }
        infoItems.Add($"Type: {asset.AssetType}");

        var infoLabel = new Label
        {
            Text = string.Join(" | ", infoItems),
            FontSize = 11,
            TextColor = Colors.Gray
        };

        var openInSystemBtn = new Button
        {
            Text = Localized.AssetPage_OpenInSystem,
            FontSize = 12,
            HorizontalOptions = LayoutOptions.End,
            Padding = new Thickness(5, 2)
        };

        openInSystemBtn.Clicked += async (s, e) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(asset.Path) && File.Exists(asset.Path))
                {
                    await FileSystemService.OpenFileAsync(asset.Path);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "open file in system viewer", this);
                if (AppShell.instance?.CurrentPage is not null)
                {
                    await AppShell.instance?.CurrentPage?.DisplayAlertAsync("Error", $"Failed to open file: {ex.Message}", "OK");
                }
            }
        };

        var infoStack = new VerticalStackLayout
        {
            Spacing = 2,
            Padding = new Thickness(5, 0),
            Children = { pathLabel, infoLabel },
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center
        };

        var footerStack = new Grid
        {
            Padding = new Thickness(5, 0, 5, 0),
            Children = { infoStack, openInSystemBtn }
        };

        // Main Grid
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Padding = new Thickness(10),
        };
        mainGrid.Add(contentArea, 0, 0);
        mainGrid.Add(footerStack, 0, 1);

        Content = mainGrid;
        Title = asset.Name ?? "Asset";
    }

    private static void AddVideoContent(Grid contentArea, AssetItem asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Path) && File.Exists(asset.Path))
        {
            _currentMediaPlayer = new MediaElement
            {
                Source = MediaSource.FromFile(asset.Path),
                ShouldAutoPlay = true,
                ShouldShowPlaybackControls = true,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            contentArea.Add(_currentMediaPlayer);
        }
        else
        {
            AddUnsupportedContent(contentArea, asset);
        }
    }

    private static void AddAudioContent(Grid contentArea, AssetItem asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Path) && File.Exists(asset.Path))
        {
            var content = new Grid
            {
                Padding = 8,
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition(new GridLength(4,GridUnitType.Star)),
                    new RowDefinition(new GridLength(1,GridUnitType.Star)),
                }
            };
            // For audio, show thumbnail or icon alongside the media player
            if (!string.IsNullOrWhiteSpace(asset.ThumbnailPath) && File.Exists(asset.ThumbnailPath))
            {
                var imageViewer = new Image
                {
                    Source = ImageSource.FromFile(asset.ThumbnailPath),
                    Aspect = Aspect.AspectFit,
                    Opacity = 1,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };
                content.Add(imageViewer, 0, 0);
            }

            _currentMediaPlayer = new MediaElement
            {
                Source = MediaSource.FromFile(asset.Path),
                ShouldAutoPlay = true,
                ShouldShowPlaybackControls = true,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            content.Add(_currentMediaPlayer, 0, 1);
            contentArea.Add(content);
        }
        else
        {
            AddUnsupportedContent(contentArea, asset);
        }
    }

    private static void AddImageContent(Grid contentArea, AssetItem asset)
    {
        var imagePath = asset.Path;

        // Prefer original path, fallback to thumbnail
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            imagePath = asset.ThumbnailPath;
        }

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            var imageViewer = new Image
            {
                Source = ImageSource.FromFile(imagePath),
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            contentArea.Add(imageViewer);
        }
        else
        {
            AddUnsupportedContent(contentArea, asset);
        }
    }

    private static void AddUnsupportedContent(Grid contentArea, AssetItem asset)
    {
        var iconLabel = new Label
        {
            Text = asset.Icon ?? "❔",
            FontSize = 64,
            HorizontalOptions = LayoutOptions.Center
        };

        var unsupportedView = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 10,
            Children =
            {
                iconLabel,
                new Label
                {
                    Text = "This asset type cannot be previewed",
                    FontSize = 14,
                    TextColor = Colors.Gray,
                    HorizontalOptions = LayoutOptions.Center
                }
            }
        };
        contentArea.Add(unsupportedView);
    }

    /// <summary>
    /// Stops any currently playing media
    /// </summary>
    public static void StopMedia()
    {
        if (_currentMediaPlayer != null)
        {
            _currentMediaPlayer.Stop();
            _currentMediaPlayer.Source = null;
            _currentMediaPlayer = null;
        }
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        StopMedia();
    }
}
