using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Maui.Controls;
using Path = System.IO.Path;
using Image = Microsoft.Maui.Controls.Image;

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

        private View? BuildVideoPreview()
        {
            var clipId = clip.Id;
            var thumbDir = Path.Combine(page.WorkingPath, "thumbs", "perClip", clipId);
            if (clipId.StartsWith('$'))
            {
                var assetId = clip.SourcePath[1..];
                if (page.Assets.TryGetValue(assetId, out var asset))
                {
                    thumbDir = Path.Combine(MauiProgram.DataPath, "My Assets", ".perAssetThumb", clipId);
                }
            }

            if (!Directory.Exists(thumbDir))
                return null;

            var pngs = Directory.GetFiles(thumbDir, "*.png");
            if (pngs.Length == 0)
                return null;

            var frameFiles = new List<(int frame, string path)>();
            foreach (var png in pngs)
            {
                var name = Path.GetFileNameWithoutExtension(png);
                if (int.TryParse(name, out var frame))
                    frameFiles.Add((frame, png));
            }

            if (frameFiles.Count == 0)
                return null;

            frameFiles.Sort((a, b) => a.frame.CompareTo(b.frame));

            var clipWidth = clip.origLength > 0 ? clip.origLength : clip.Clip.WidthRequest;
            if (clipWidth <= 0)
                return null;

            var maxFrames = clip.maxFrameCount > 0 ? (int)clip.maxFrameCount : 1;
            var relativeStart = (int)clip.relativeStartFrame;
            var thumbHeight = Math.Max(28, clip.Clip.HeightRequest - 14);

            var layout = new AbsoluteLayout
            {
                HeightRequest = thumbHeight,
                InputTransparent = true,
                IsClippedToBounds = true,
                VerticalOptions = LayoutOptions.Center,
            };

            foreach (var (frame, path) in frameFiles)
            {
                var ratio = (double)(frame - relativeStart) / maxFrames;
                var xPos = ratio * clipWidth;

                if (xPos < -thumbHeight || xPos > clipWidth + thumbHeight)
                    continue;

                var img = new Image
                {
                    Source = ImageSource.FromFile(path),
                    Aspect = Aspect.AspectFit,
                    HeightRequest = thumbHeight,
                    InputTransparent = true,
                };

                AbsoluteLayout.SetLayoutBounds(img, new Rect(xPos, 0, AbsoluteLayout.AutoSize, thumbHeight));
                layout.Children.Add(img);
            }

            return layout.Children.Count > 0 ? layout : null;
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

            var container = new Grid
            {
                HeightRequest = thumbHeight,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Center,
            };

            container.Children.Add(new Image
            {
                Source = ImageSource.FromFile(sourcePath),
                Aspect = Aspect.AspectFill,
                HeightRequest = thumbHeight,
                InputTransparent = true,
            });

            return container;
        }
    }
}
