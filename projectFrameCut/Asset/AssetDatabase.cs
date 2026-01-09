using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Maui.Alerts;

namespace projectFrameCut.Asset
{
    public static class AssetDatabase
    {
        public static void Initialize(string json)
        {
            Assets = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(json, DraftPage.DraftJSONOption) ?? new ConcurrentDictionary<string, AssetItem>();
        }

        public static ConcurrentDictionary<string, AssetItem> Assets { get; set; } = new();


        public static async Task<AssetItem?> Add(string path, Page page)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var name = Path.GetFileNameWithoutExtension(path);
            var nameInput = await page.DisplayPromptAsync(Localized.AssetPage_AddAAsset_InputName, Path.GetFileName(path), Localized._OK, Localized._Cancel, name, 0, null, name);
            if (!string.IsNullOrEmpty(nameInput)) name = nameInput;
            var type = AssetItem.GetAssetType(path);
            if (type == AssetType.Other)
            {
                var map = new Dictionary<string, AssetType>
                {
                    {Localized.AssetPage_AssetType_Video, AssetType.Video },
                    {Localized.AssetPage_AssetType_Audio, AssetType.Audio },
                    {Localized.AssetPage_AssetType_Image, AssetType.Image },
                    {Localized.AssetPage_AssetType_Font, AssetType.Font },
                };
                var selection = await page.DisplayActionSheetAsync(Localized.AssetPage_AssetType_Unknown(name), null, null, map.Keys.ToArray());
                if (!map.TryGetValue(selection, out type)) return null;
            }
            if (AssetDatabase.Assets.Any(c => c.Value.Name == name))
            {
                var existing = AssetDatabase.Assets.Values.First((v) => v.Name == Path.GetFileNameWithoutExtension(path));
                if (existing is not null)
                {
                    string opt = await page.DisplayActionSheetAsync(
                        Localized.DraftPage_DuplicatedAsset(Path.GetFileNameWithoutExtension(path), existing.Name),
                        Localized._Cancel,
                        null,
                        [Localized.DraftPage_DuplicatedAsset_Relpace, Localized.DraftPage_DuplicatedAsset_Skip, Localized.DraftPage_DuplicatedAsset_Together]
                    );
                    if (opt is null || opt == Localized._Cancel) //cancelled
                    {
                        return null;
                    }
                    else if (opt == Localized.DraftPage_DuplicatedAsset_Relpace)
                    {
                        if (!string.IsNullOrEmpty(existing.AssetId))
                        {
                            Remove(existing.AssetId);
                            Log($"Replaced existing asset {existing.Name} with new one from {path}");
                        }
                    }
                    else if (opt == Localized.DraftPage_DuplicatedAsset_Skip)
                    {
                        Log($"Skipped adding duplicated asset from {path}");
                        return null;
                    }
                    else
                    {
                        Log($"Adding duplicated asset from {path} together with existing one.");
                    }
                }
            }
            var proxyOption = SettingsManager.GetSetting("Edit_ProxyOption", "none");
            var createProxy = (proxyOption != "never") && ((proxyOption == "always") || await page.DisplayAlertAsync(Localized.DraftPage_CreateProxy(nameInput), Localized.DraftPage_CreateProxy_Info, Localized._Confirm, Localized._Cancel));
            bool ok = false;
            AssetItem? asset = null;
            await Task.Run(() =>
            {
                ok = Add(path, name, type, out asset, createProxy);
            });
            if (!ok)
            {
                await page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_Asset_InvaildSrc(name), Localized._OK);
                return null;
            }
            return asset;
        }

        public static bool Add(string path, string name, AssetType type, out AssetItem asset, bool proxyOption = false)
        {
            var createdAsset = Create(path, name, type);
            if (createdAsset is null)
            {
                asset = null!;
                return false;
            }

            asset = createdAsset;
            asset.Path = Path.Combine(MauiProgram.DataPath,"My Assets",$"{asset.AssetId}{Path.GetExtension(path)}");
            if (asset.AssetId is null || asset.Path is null) return false;

            if (AssetDatabase.Assets.TryAdd(asset.AssetId, asset))
            {
                File.Copy(path, asset.Path);
                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftPage.DraftJSONOption));

                new Thread(() =>
                {
                    Toast.Make(Localized.AssetPage_PostProcess_StartPrompt(name));
                    StartBackgroundProcessing(createdAsset, path, type, proxyOption, name);
                    Toast.Make(Localized.AssetPage_PostProcess_FinishPrompt(name));

                }).Start();
                return true;
            }
            return false;
        }

        private static async void StartBackgroundProcessing(AssetItem asset, string originalPath, AssetType type, bool makeProxy, string name)
        {
            Log($"Start background processing asset {name}...");
            try
            {
                asset.SourceHash = await HashServices.ComputeFileHashAsync(originalPath, null, CancellationToken.None);

                if (type == AssetType.Video && makeProxy && asset.Path is not null)
                {
                    var proxyDir = Path.Combine(MauiProgram.DataPath, "My Assets", ".proxy");
                    Directory.CreateDirectory(proxyDir);
                    var proxiedPath = Path.Combine(proxyDir, $"{asset.AssetId}.mp4");

                    try
                    {
                        VideoResizer.ReencodeToResolution(asset.Path, proxiedPath, 1280, 720, "libx264");
                        Log($"Proxy created for asset {name}: {proxiedPath}");
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Failed to create proxy for asset {name}");
                    }
                }

                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"),
                    JsonSerializer.Serialize(Assets, DraftPage.DraftJSONOption));
            }
            catch (Exception ex)
            {
                Log(ex, $"Background task failed for asset {name}");
            }
            finally
            {
                Log($"Finished background processing asset {name}.");
            }
        }
        public static AssetItem? Create(string sourcePath, string name, AssetType type)
        {
            var asset = new AssetItem
            {
                AssetId = Guid.NewGuid().ToString(),
                Name = name,
                AssetType = type,
                CreatedAt = DateTime.Now,
                ThumbnailPath = type == AssetType.Image ? sourcePath : null
            };
            asset.Path = sourcePath;
            asset.SecondPerFrame = -1;
            asset.FrameCount = 0;
            var thumbnailPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".thumbnails", asset.AssetId + ".png");
            asset.ThumbnailPath = thumbnailPath;
            bool fail = false;
            switch (asset.AssetType)
            {
                case AssetType.Video:
                    {
                        try
                        {
                            var vid = PluginManager.CreateVideoSource(sourcePath);
                            asset.FrameCount = vid.TotalFrames;
                            asset.SecondPerFrame = (float)(1f / vid.Fps);
                            vid.GetFrame(0U, false).SaveAsPng16bpp(thumbnailPath, null);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Add a asset");
                            asset.FrameCount = 1024;
                            asset.SecondPerFrame = 1 / 42f;
                            fail = true;
                        }
                        break;
                    }

                case AssetType.Audio:
                    {
                        try
                        {
                            var aud = PluginManager.CreateAudioSource(sourcePath);
                            asset.FrameCount = aud.Duration;
                            asset.SecondPerFrame = (float)(1f);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Add a asset");
                            asset.FrameCount = 1024;
                            asset.SecondPerFrame = 1 / 42f;
                            fail = true;
                        }
                        try
                        {
                            PluginManager.CreateVideoSource(sourcePath).GetFrame(0U, false).SaveAsPng16bpp(thumbnailPath, null);
                        }
                        catch
                        {
                            try
                            {
                                asset.ThumbnailPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".thumbnails", ".unknown_music.png");

                                if (!File.Exists(asset.ThumbnailPath))
                                {
                                    using var stream = FileSystem.OpenAppPackageFileAsync("Images/unknown_music.png").GetAwaiter().GetResult();
                                    using FileStream fileStream = File.Create(asset.ThumbnailPath);
                                    stream.CopyTo(fileStream);
                                }
                            }
                            catch { }


                        }
                        break;
                    }
                case AssetType.Font:
                    {
                        FontHelper.GenerateFontThumbnail(sourcePath).SaveAsPng8bpp(thumbnailPath, null);
                        break;
                    }
                case AssetType.Image:
                    {
                        asset.ThumbnailPath = asset.Path;
                        break;
                    }
            }
            return fail ? null : asset;
        }

        public static bool Remove(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            try
            {
                if (Assets.TryRemove(assetId, out var asset))
                {
                    if (File.Exists(asset.Path))
                    {
                        File.Delete(asset.Path);
                    }
                    if (File.Exists(asset.ThumbnailPath) && !Path.GetFileName(asset.ThumbnailPath).StartsWith('.'))
                    {
                        File.Delete(asset.ThumbnailPath);
                    }
                    File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftPage.DraftJSONOption));
                    return true;
                }

            }
            catch (Exception ex)
            {
                Log(ex, "Remove a asset");
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


        public static uint DetermineLengthInFrame(AssetItem asset, uint targetFPS)
        {
            if (asset.isInfiniteLength) return 0U;
            return (uint)(asset.AssetType switch
            {
                AssetType.Video => asset.FrameCount ?? 0,
                AssetType.Audio => (asset.FrameCount ?? 0) * targetFPS, // Duration is in seconds, multiply by targetFPS to get frames
                AssetType.Image => 0,
                _ => asset.FrameCount ?? 0
            });
        }

    }



}
