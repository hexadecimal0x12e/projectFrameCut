using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.DraftStuff;
#if !HEADLESS
using projectFrameCut.ViewModels;
#endif

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
#if !HEADLESS
using CommunityToolkit.Maui.Alerts;
#endif

using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Asset
{
    public static class AssetDatabase
    {
        public const string ImportModeSetting = "Edit_AssetImportMode";

        public static void Initialize(string json)
        {
            Assets = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(json, DraftImportAndExportHelper.DraftJSONOption) ?? new ConcurrentDictionary<string, AssetItem>();

            DecoderContextPJFCProject.GlobalAssetGetter = new(() => Assets);
        }

        public static ConcurrentDictionary<string, AssetItem> Assets { get; set; } = new();


#if !HEADLESS
        public static async Task<bool?> PickImportModeAsync(Page page)
        {
            switch (SettingsManager.GetSetting(ImportModeSetting, "ask"))
            {
                case "copy": return true;
                case "reference": return false;
            }
            var mode = await UIServices.DisplayActionSheetAsync(page, Localized.DraftPage_AssetPanel_ImportMode, Localized._Cancel, null,
                Localized.DraftPage_AssetPanel_ImportCopy, Localized.DraftPage_AssetPanel_ImportReference);
            if (mode == Localized.DraftPage_AssetPanel_ImportCopy) return true;
            if (mode == Localized.DraftPage_AssetPanel_ImportReference) return false;
            return null;
        }

        public static async Task<AssetItem?> Add(string path, Page page, bool? copyToLibrary = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            copyToLibrary ??= await PickImportModeAsync(page);
            if (copyToLibrary is null) return null;
            path = Path.GetFullPath(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var nameInput = await page.DisplayPromptAsync(Localized.AssetPage_AddAAsset_InputName, Path.GetFileName(path), Localized._OK, Localized._Cancel, name, 0, null, name);
            if (nameInput is null) return null;
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
                    {Localized.AssetPage_AssetType_VectorComposition, AssetType.VectorComposition },
                    {Localized.Subtitle_Title, AssetType.Subtitle },
                };
                var selection = await UIServices.DisplayActionSheetAsync(page, Localized.AssetPage_AssetType_Unknown(name), Localized._Cancel, null, map.Keys.ToArray());
                if (selection is null || !map.TryGetValue(selection, out type)) return null;
            }
            string? replaceId = null;
            if (AssetDatabase.Assets.Any(c => c.Value.Name == name))
            {
                var existing = AssetDatabase.Assets.Values.First(v => v.Name == name);
                if (existing is not null)
                {
                    string? opt = await UIServices.DisplayActionSheetAsync(page,
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
                            replaceId = existing.AssetId;
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
            var createProxy = (type == AssetType.Video) && (proxyOption != "never") && ((proxyOption == "always") || await page.DisplayAlertAsync(Localized.DraftPage_CreateProxy(nameInput), Localized.DraftPage_CreateProxy_Info, Localized._Confirm, Localized._Cancel));
            bool ok = false;
            AssetItem? asset = null;
            await Task.Run(() =>
            {
                ok = Add(path, name, type, out asset, createProxy, copyToLibrary.Value);
            });
            if (!ok)
            {
                await page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_Asset_InvaildSrc(name), Localized._OK);
                return null;
            }
            if (replaceId is not null) Remove(replaceId);
            if (type == AssetType.Font) TextServices.LoadFonts();
            return asset;
        }

#endif

        public static bool Add(string path, string name, AssetType type, out AssetItem asset, bool proxyOption = false, bool copyToLibrary = true)
        {
            path = Path.GetFullPath(path);
            var createdAsset = Create(path, name, type);
            if (createdAsset is null)
            {
                asset = null!;
                return false;
            }

            asset = createdAsset;
            asset.IsReference = !copyToLibrary;
            asset.Path = copyToLibrary ? Path.Combine(MauiProgram.DataPath, "My Assets", $"{asset.AssetId}{Path.GetExtension(path)}") : path;
            if (asset.AssetId is null || asset.Path is null) return false;

            if (copyToLibrary)
            {
                try
                {
                    File.Copy(path, asset.Path);
                }
                catch
                {
                    if (File.Exists(asset.Path)) File.Delete(asset.Path);
                    throw;
                }
            }
            if (type == AssetType.Image) asset.ThumbnailPath = asset.Path;
            if (AssetDatabase.Assets.TryAdd(asset.AssetId, asset))
            {
                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftImportAndExportHelper.DraftJSONOption));
                Log($"Imported shared asset '{path}' as {(copyToLibrary ? "copy" : "reference")} at '{asset.Path}'.");

                new Thread(() =>
                {
#if !HEADLESS
                    Toast.Make(Localized.AssetPage_PostProcess_StartPrompt(name));
#endif
                    StartBackgroundProcessing(createdAsset, createdAsset.Path!, type, proxyOption, name);
#if !HEADLESS
                    Toast.Make(Localized.AssetPage_PostProcess_FinishPrompt(name));
#endif

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
                        var temporaryProxy = proxiedPath + ".tmp";
                        VideoResizer.ReencodeToResolution(asset.Path, temporaryProxy, 1280, 720, "libx264");
                        File.Move(temporaryProxy, proxiedPath, true);
                        File.WriteAllText(proxiedPath + ".ready", "");
                        Log($"Proxy created for asset {name}: {proxiedPath}");
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Failed to create proxy for asset {name}");
                    }
                    finally
                    {
                        var temporaryProxy = proxiedPath + ".tmp";
                        if (File.Exists(temporaryProxy)) File.Delete(temporaryProxy);
                    }
                }

                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"),
                    JsonSerializer.Serialize(Assets, DraftImportAndExportHelper.DraftJSONOption));
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
            asset.Duration = 0;
            var thumbnailPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".thumbnails", asset.AssetId + ".png");
            asset.ThumbnailPath = thumbnailPath;
            bool fail = false;
            switch (asset.AssetType)
            {
                case AssetType.Subtitle:
                    asset.ClipType = ClipMode.SubtitleClip;
                    asset.ThumbnailPath = null;
                    break;
                case AssetType.VectorComposition:
                    {
#if HEADLESS
                        if (Path.GetExtension(sourcePath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                            projectFrameCut.Drawing.Vector.ImportExport.SVGToVectorElement.ImportFromFile(sourcePath);
                        else
                        {
                            using var json = JsonDocument.Parse(File.ReadAllText(sourcePath));
                        }
#else
                        VectorClipServices.Import(sourcePath);
#endif
                        asset.ClipType = ClipMode.VectorComponentClip;
                        asset.ThumbnailPath = null;
                        Log($"Created vector composition asset {asset.AssetId}: {sourcePath}");
                        break;
                    }
                case AssetType.Video:
                    {
                        try
                        {
                            var vid = PluginManager.CreateVideoSource(sourcePath);
                            asset.Duration = vid.TotalFrames;
                            asset.SecondPerFrame = (float)(1f / vid.Fps);
                            vid.GetFrame(0U).SaveToPng(thumbnailPath);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Add a asset");
                            asset.Duration = 1024;
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
                            asset.Duration = aud.Duration;
                            asset.SecondPerFrame = (float)(1f);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Add a asset");
                            asset.Duration = 1024;
                            asset.SecondPerFrame = 1 / 42f;
                            fail = true;
                        }
                        try
                        {
                            PluginManager.CreateVideoSource(sourcePath).GetFrame(0U).SaveToPng(thumbnailPath);
                        }
                        catch
                        {
                            try
                            {
                                asset.ThumbnailPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".thumbnails", ".unknown_music.png");

                                if (!File.Exists(asset.ThumbnailPath))
                                {
#if !HEADLESS
                                    var path = FileSystemService.GetAppPackageFileSync("Images","unknown_music.png");
                                    File.Copy(path, asset.ThumbnailPath, true);
#endif
                                }
                            }
                            catch { }


                        }
                        break;
                    }
                case AssetType.Font:
                    {
#if !HEADLESS
                        TextServices.GenerateFontThumbnail(sourcePath).SaveToPng(thumbnailPath);
#endif

                        break;
                    }
                case AssetType.Image:
                    {
                        asset.ThumbnailPath = asset.Path;
                        break;
                    }
            }
#if !HEADLESS
            if (DeviceInfo.DeviceType == DeviceType.Virtual)
            {
                return (AssetItem?)asset;
            }
            else
            {
                return (fail ? null : asset);
            }
#else
            return fail ? null : asset;
#endif
        }

        public static bool Remove(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            try
            {
                if (Assets.TryRemove(assetId, out var asset))
                {
                    if (!asset.IsReference && File.Exists(asset.Path) && !Assets.Values.Any(x => string.Equals(x.Path, asset.Path,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    {
                        File.Delete(asset.Path);
                    }
                    if (File.Exists(asset.ThumbnailPath) && string.Equals(asset.ThumbnailPath,
                        Path.Combine(MauiProgram.DataPath, "My Assets", ".thumbnails", $"{asset.AssetId}.png"),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        File.Delete(asset.ThumbnailPath);
                    }
                    File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftImportAndExportHelper.DraftJSONOption));
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
                File.WriteAllText(Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json"), JsonSerializer.Serialize(Assets, DraftImportAndExportHelper.DraftJSONOption));
                return true;
            }
            return false;
        }


        public static uint DetermineLengthInFrame(AssetItem asset, uint targetFPS)
        {
            if (asset.isInfiniteLength) return 0U;
            return (uint)(asset.AssetType switch
            {
                AssetType.Video => ResolveVideoLengthInTimelineFrames(asset, targetFPS),
                AssetType.Audio => (asset.Duration ?? 0) * targetFPS, // Duration is in seconds, multiply by targetFPS to get frames
                AssetType.Image => 0,
                _ => asset.Duration ?? 0
            });
        }

        private static long ResolveVideoLengthInTimelineFrames(AssetItem asset, uint targetFPS)
        {
            long sourceFrames = asset.Duration ?? 0;
            if (sourceFrames <= 0)
            {
                return 0;
            }

            if (asset.SecondPerFrame <= 0 || targetFPS == 0)
            {
                return sourceFrames;
            }

            double seconds = sourceFrames * asset.SecondPerFrame;
            double timelineFrames = seconds * targetFPS;
            if (timelineFrames < 1d)
            {
                return 1;
            }

            if (timelineFrames >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)Math.Round(timelineFrames, MidpointRounding.AwayFromZero);
        }

    }



}
