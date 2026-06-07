using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Maui.Controls;
using Path = System.IO.Path;
using Image = Microsoft.Maui.Controls.Image;
using projectFrameCut.Asset;
using System.Linq;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base;

namespace projectFrameCut.InteractableEditor
{
    public class OnClipUIPreview(DraftPage page, ClipElementUI clip)
    {
        public View? Update()
        {
            return clip.ClipType switch
            {
                ClipMode.VideoClip => BuildVideoPreview(),
                ClipMode.PhotoClip => BuildPhotoPreview(),
                _ => null
            };
        }

        private const int PreviewWidthFactor = 10; // Adjust this factor to control how many frames are shown in the preview

        private View? BuildVideoPreview()
        {
            var clipId = clip.Id;
            var thumbDir = Path.Combine(page.WorkingPath, "thumbs", "perClip", clipId);
            if (clip.SourcePath?.StartsWith('$') ?? false)
            {
                var assetId = clip.SourcePath[1..];
                if (AssetDatabase.Assets.TryGetValue(assetId, out var asset))
                {
                    thumbDir = Path.Combine(MauiProgram.DataPath, "My Assets", ".perAssetThumb", assetId);
                }
            }

            if (!Directory.Exists(thumbDir))
                return null;

            var pngs = Directory.GetFiles(thumbDir, "*.png");
            if (pngs.Length == 0)
                return null;

            var availableFrames = new List<int>();
            foreach (var png in pngs)
            {
                var name = Path.GetFileNameWithoutExtension(png);
                if (int.TryParse(name, out var frame))
                    availableFrames.Add(frame);
            }
            if (availableFrames.Count == 0)
                return null;
            availableFrames = availableFrames.Order().ToList();

            (var origWidth, var origHeight) = new Picture8bpp(pngs[0]).GetDimensions();

            var rawClipHeight = clip.Clip.HeightRequest > 0
                ? clip.Clip.HeightRequest
                : (clip.Clip.Height > 0 ? clip.Clip.Height : DraftPage.ClipHeight);
            var previewHeight = rawClipHeight;

            var scaleFactor = previewHeight / (double)origHeight;
            var frameWidth = Math.Max(1, (int)Math.Round(origWidth * scaleFactor));

            var clipWidth = clip.Clip.WidthRequest > 0
                ? clip.Clip.WidthRequest
                : (clip.origLength > 0 ? clip.origLength : clip.Clip.Width);
            // Subtract handle widths (30px each) to match the actual content column width
            var availableWidth = Math.Max(1, clipWidth - 60);
            var countOfFrame = (int)(availableWidth / frameWidth) - 1;
            if (countOfFrame <= 0) return null;
            if (Math.Abs((countOfFrame + 1f) * frameWidth - availableWidth) < frameWidth * 0.75f) countOfFrame++;
            var totalFramesWidth = countOfFrame * frameWidth;
            var spacing = countOfFrame > 1 ? (availableWidth - totalFramesWidth) / (countOfFrame - 1) : 0;

            List<int> frameToShow = new(countOfFrame);
            for (int i = 0; i < countOfFrame; i++)
            {
                var idx = countOfFrame > 1
                    ? (int)Math.Floor(i * (availableFrames.Count - 1) / (double)(countOfFrame - 1))
                    : 0;
                frameToShow.Add(availableFrames[idx]);
            }

            var layout = new HorizontalStackLayout
            {
                HeightRequest = previewHeight,
                InputTransparent = true,
                IsClippedToBounds = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                Spacing = spacing / 2,
                Padding = 0
            };
            foreach (var item in frameToShow)
            {
                layout.Children.Add(new Border
                {
                    StrokeThickness = 1,
                    Padding = 0,
                    Content = new Image
                    {
                        Source = ImageSource.FromFile(Path.Combine(thumbDir, $"{item}.png")),
                        InputTransparent = true,
                        VerticalOptions = LayoutOptions.Fill,
                        WidthRequest = frameWidth,
                        Aspect = Aspect.AspectFit,
                    },
                    Margin = new(0),
                });
            }

            return new Grid
            {
                HeightRequest = previewHeight,
                VerticalOptions = LayoutOptions.Fill,
                Padding = 0,
                Children =
                {
                    layout,
                    new Label
                    {
                        Text = clip.DisplayName ?? clip.Id,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        BackgroundColor = Color.FromRgba("#80808080"),
                        MaxLines = 1
                    }
                }
            };
        }



        private View? BuildPhotoPreview()
        {
            var sourcePath = clip.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;

            if (sourcePath.StartsWith('$'))
            {
                var assetId = sourcePath[1..];
                if (page.Assets.TryGetValue(assetId, out var asset))
                    sourcePath = asset.Path;
                else
                    return null;
            }

            if (!File.Exists(sourcePath))
                return null;

            var thumbHeight = Math.Max(28, clip.Clip.HeightRequest - 14);
            var clipWidth = clip.Clip.WidthRequest > 0
                ? clip.Clip.WidthRequest
                : (clip.origLength > 0 ? clip.origLength : clip.Clip.Width);

            var container = new Grid
            {
                HeightRequest = thumbHeight,
                WidthRequest = clipWidth,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                IsClippedToBounds = true,
            };

            container.Children.Add(new Image
            {
                Source = ImageSource.FromFile(sourcePath),
                Aspect = Aspect.AspectFill,
                HeightRequest = thumbHeight,
                WidthRequest = clipWidth,
                InputTransparent = true,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            });

            return container;
        }
    }
}
