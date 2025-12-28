using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Asset
{
    public static class AssetDatabase
    {
        public static void Initialize(string json)
        {
            Assets = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(json, DraftPage.DraftJSONOption) ?? new ConcurrentDictionary<string, AssetItem>();
        }

        public static ConcurrentDictionary<string, AssetItem> Assets { get; set; } = new();

        public static bool Add(string path, out AssetItem asset)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var type = AssetItem.GetAssetType(path);

            asset = new AssetItem
            {
                AssetId = Guid.NewGuid().ToString(),
                Name = name,
                AssetType = type,
                CreatedAt = DateTime.Now,
                ThumbnailPath = type == AssetType.Image ? path : null
            };
            var destPath = Path.Combine(MauiProgram.DataPath, "My Assets", $"{Path.GetFileNameWithoutExtension(path)}-{asset.AssetId}{Path.GetExtension(path)}");
            asset.Path = destPath;
            asset.SecondPerFrame = -1;
            asset.FrameCount = 0;
            var thumbnailPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".thumbnails", asset.AssetId + ".png");
            asset.ThumbnailPath = thumbnailPath;
            switch (asset.AssetType)
            {
                case AssetType.Video:
                    {
                        try
                        {
                            var vid = PluginManager.CreateVideoSource(asset.Path);
                            asset.FrameCount = vid.TotalFrames;
                            asset.SecondPerFrame = (float)(1f / vid.Fps);
                            vid.GetFrame(0U, false).SaveAsPng16bpp(thumbnailPath, null);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Add a asset");
                            asset.FrameCount = 1024;
                            asset.SecondPerFrame = 1 / 42f;
                        }
                        break;
                    }

                case AssetType.Audio:
                    {
                        try
                        {

                                var vid = PluginManager.CreateAudioSource(asset.Path);
                                asset.FrameCount = vid.Duration;
                                asset.SecondPerFrame = (float)(1f);

                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Add a asset");
                            asset.FrameCount = 1024;
                            asset.SecondPerFrame = 1 / 42f;
                        }
                        break;
                    }
                case AssetType.Font:
                    {
                        FontHelper.GenerateFontThumbnail(asset.Path).SaveAsPng8bpp(thumbnailPath, null);
                        break;
                    }
                case AssetType.Image:
                    {
                        asset.ThumbnailPath = asset.Path;
                        break;
                    }
            }
            if(AssetDatabase.Assets.TryAdd(asset.AssetId, asset))
            {
                File.Copy(path, destPath);
                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftPage.DraftJSONOption));
                return true;
            }
            throw new InvalidOperationException("Failed to add a asset.");
        }

        public static bool Remove(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            if (Assets.TryRemove(assetId, out var _))
            {
                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftPage.DraftJSONOption));
                return true;
            }
            return false;
        }

        public static bool Rename(string assetId, string newName)
        {
            if (string.IsNullOrEmpty(assetId) || newName == null) return false;
            if (Assets.TryGetValue(assetId, out var asset))
            {
                asset.Name = newName;
                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftPage.DraftJSONOption));
                return true;
            }
            return false;
        }


    }



}
