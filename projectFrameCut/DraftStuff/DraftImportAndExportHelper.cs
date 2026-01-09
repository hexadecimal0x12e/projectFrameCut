using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace projectFrameCut.DraftStuff
{
    internal static class DraftImportAndExportHelper
    {
        public static DraftStructureJSON ExportFromDraftPage(projectFrameCut.DraftPage page, bool wrapSoundtrackAsClip = false)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));

            var clips = new List<ClipDraftDTO>();
            var soundtracks = new List<SoundtrackDTO>();

            var trackKeys = page.Tracks.Keys.OrderBy(k => k).ToArray();
            foreach (var trackKey in trackKeys)
            {
                if (!page.Tracks.TryGetValue(trackKey, out var layout)) continue;

                foreach (var child in layout.Children)
                {
                    if (child is Microsoft.Maui.Controls.Border border)
                    {
                        if (border.BindingContext is not ClipElementUI elem) continue;

                        if (elem.Id.StartsWith("ghost_") || elem.Id.StartsWith("shadow_")) continue;

                        double startPx = border.TranslationX;
                        double widthPx = (border.Width > 0) ? border.Width : border.WidthRequest;

                        uint startFrame = (uint)Math.Round(page.PixelToFrame(startPx) / elem.SecondPerFrameRatio);
                        uint durationFrames = (uint)Math.Round(page.PixelToFrame(widthPx) / elem.SecondPerFrameRatio);
                        if (durationFrames == 0) durationFrames = 1;

                        string name = string.IsNullOrWhiteSpace(elem.displayName) ? ExtractLabelText(border) ?? elem.Id : elem.displayName;

                        if (elem.ClipType == ClipMode.AudioClip)
                        {
                            if (wrapSoundtrackAsClip)
                            {
                                var clipDto = new ClipDraftDTO
                                {
                                    Id = elem.Id,
                                    Name = name,
                                    FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase",
                                    TypeName = nameof(SoundTrackToClipWrapper),
                                    ClipType = ClipMode.AudioClip,
                                    LayerIndex = (uint)trackKey,
                                    StartFrame = startFrame,
                                    RelativeStartFrame = elem.relativeStartFrame,
                                    Duration = durationFrames,
                                    FrameTime = elem.sourceSecondPerFrame,
                                    MixtureMode = MixtureMode.Overlay,
                                    FilePath = elem.sourcePath,
                                    SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                                    IsInfiniteLength = elem.isInfiniteLength,
                                    SecondPerFrameRatio = elem.SecondPerFrameRatio,
                                    MetaData = elem.ExtraData,
                                    Effects = null
                                };
                                clips.Add(clipDto);
                            }
                            else
                            {
                                var dto = new SoundtrackDTO
                                {
                                    Id = elem.Id,
                                    Name = name,
                                    FromPlugin = string.IsNullOrEmpty(elem.FromPlugin) ? "projectFrameCut.Render.Plugins.InternalPluginBase" : elem.FromPlugin,
                                    TypeName = string.IsNullOrEmpty(elem.TypeName) ? "NormalTrack" : elem.TypeName,
                                    TrackType = TrackMode.NormalTrack,
                                    LayerIndex = (uint)trackKey,
                                    StartFrame = startFrame,
                                    RelativeStartFrame = elem.relativeStartFrame,
                                    Duration = durationFrames,
                                    SecondPerFrameRatio = elem.SecondPerFrameRatio,
                                    FilePath = elem.sourcePath,
                                    MetaData = elem.ExtraData
                                };
                                soundtracks.Add(dto);
                            }
                        }
                        else
                        {
                            var dto = new ClipDraftDTO
                            {
                                Id = elem.Id,
                                Name = name,
                                FromPlugin = elem.FromPlugin,
                                TypeName = elem.TypeName,
                                ClipType = elem.ClipType,
                                LayerIndex = (uint)trackKey,
                                StartFrame = startFrame,
                                RelativeStartFrame = elem.relativeStartFrame,
                                Duration = durationFrames,
                                FrameTime = elem.sourceSecondPerFrame,
                                MixtureMode = MixtureMode.Overlay,
                                FilePath = elem.sourcePath,
                                SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                                IsInfiniteLength = elem.isInfiniteLength,
                                SecondPerFrameRatio = elem.SecondPerFrameRatio,
                                MetaData = elem.ExtraData,
                                Effects = elem.Effects?.Select((kv) =>
                                new EffectAndMixtureJSONStructure
                                {
                                    Name = kv.Key,
                                    FromPlugin = kv.Value.FromPlugin,
                                    TypeName = kv.Value.TypeName,
                                    Parameters = kv.Value.Parameters,
                                    Index = kv.Value.Index,
                                    Enabled = kv.Value.Enabled,
                                    RelativeHeight = kv.Value.RelativeHeight,
                                    RelativeWidth = kv.Value.RelativeWidth,
                                    IsMixture = false
                                }).ToArray()
                            };

                            clips.Add(dto);
                        }
                    }
                }
            }

            long max = 0, audMax = 0;
            foreach (var clip in clips)
            {
                if (clip is ClipDraftDTO dto)
                {
                    if (dto.ClipType == ClipMode.AudioClip)
                    {
                        if (wrapSoundtrackAsClip)
                        {
                            audMax = Math.Max(dto.StartFrame + dto.Duration, audMax);
                        }
                    }
                    else
                    {
                        max = Math.Max(dto.StartFrame + dto.Duration, max);
                    }
                }


            }

            if (max > uint.MaxValue)
            {
                throw new OverflowException($"Project duration overflow, total frames exceed {uint.MaxValue}.");
            }

            var d = new DraftStructureJSON
            {
                targetFrameRate = page.ProjectInfo.targetFrameRate,
                Clips = clips.Cast<object>().ToArray(),
                SoundTracks = soundtracks.Cast<object>().ToArray(),
                Duration = (uint)max,
                SavedAt = DateTime.Now
            };
            if (wrapSoundtrackAsClip) d.AudioDuration = (uint)audMax;
            return d;
        }

        public static IClip[] JSONToIClips(DraftStructureJSON json)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

            var clipsList = new List<IClip>();

            foreach (var clip in elements.Cast<JsonElement>())
            {
                var clipInstance = PluginManager.CreateClip(clip);
                if (clipInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        clipInstance.FilePath = AssetDatabase.Assets[clipInstance.FilePath.Substring(1)].Path;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (string.IsNullOrEmpty(clipInstance.FilePath) && clip.TryGetProperty("FilePath", out var fp) && clipInstance.NeedFilePath)
                {
                    try
                    {
                        clipInstance.FilePath = fp.GetString();
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                clipInstance.ReInit();
                clipsList.Add(clipInstance);

            }
            return clipsList.ToArray();
        }

        public static ConcurrentDictionary<string, AssetItem> ImportAssetsFromJSON(string json)
        {
            var assets = JsonSerializer.Deserialize<IEnumerable<AssetItem>>(json);
            if (assets is null) return new();
            var assetDict = assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a);
            return new ConcurrentDictionary<string, AssetItem>(assetDict);
        }

        public static (ConcurrentDictionary<string, ClipElementUI>, int) ImportFromJSON(DraftStructureJSON draft, ProjectJSONStructure proj)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            var dtos = new List<ClipDraftDTO>();
            foreach (var obj in draft.Clips ?? Array.Empty<object>())
            {
                switch (obj)
                {
                    case JsonElement je:
                        try
                        {
                            var dto = je.Deserialize<ClipDraftDTO>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (dto != null) dtos.Add(dto);
                        }
                        catch { }
                        break;
                    case ClipDraftDTO dto:
                        dtos.Add(dto);
                        break;
                }
            }

            int trackCount = dtos.Count == 0 ? 1 : (int)(dtos.Max(d => (int)d.LayerIndex) + 1);

            var clipsDict = new ConcurrentDictionary<string, ClipElementUI>();

            foreach (var dto in dtos.OrderBy(d => d.LayerIndex).ThenBy(d => d.StartFrame))
            {
                double startPx = dto.StartFrame;
                double widthPx = Math.Max(1, (double)dto.Duration);

                uint maxFrames = dto.SourceDuration is null ? dto.Duration : (uint)Math.Max(dto.SourceDuration.Value, dto.Duration);

                var element = ClipElementUI.CreateClip(
                    startX: startPx,
                    width: widthPx,
                    trackIndex: (int)dto.LayerIndex,
                    id: string.IsNullOrWhiteSpace(dto.Id) ? null : dto.Id,
                    labelText: string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name,
                    background: ClipElementUI.DetermineAssetColor(dto.ClipType),
                    prototype: null,
                    relativeStart: dto.RelativeStartFrame,
                    maxFrames: maxFrames
                );

                element.displayName = string.IsNullOrWhiteSpace(dto.Name) ? element.Id : dto.Name;
                element.origTrack = (int)dto.LayerIndex;
                element.origLength = widthPx;
                element.origX = startPx;
                element.relativeStartFrame = dto.RelativeStartFrame;
                element.maxFrameCount = maxFrames;
                element.isInfiniteLength = dto.IsInfiniteLength;
                element.sourcePath = dto.FilePath ?? (dto.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
                element.ClipType = dto.ClipType;
                element.ExtraData = dto.MetaData ?? new();
                element.sourceSecondPerFrame = dto.FrameTime;
                element.SecondPerFrameRatio = dto.SecondPerFrameRatio;
                element.ApplySpeedRatio();
                element.TypeName = dto.TypeName;
                element.FromPlugin = dto.FromPlugin;
                element.Effects = dto.Effects?.ToDictionary(
                    e => string.IsNullOrWhiteSpace(e.Name) ? $"Effect-{Guid.NewGuid()}" : e.Name,
                    e => PluginManager.CreateEffect(e, proj.RelativeWidth, proj.RelativeHeight)
                );

                if (element.Effects is null)
                {
                    element.Effects = new Dictionary<string, IEffect>();
                }
                clipsDict.AddOrUpdate(element.Id, element, (_, _) => element);
            }

            // Import SoundTracks
            var soundtrackDtos = new List<SoundtrackDTO>();
            foreach (var obj in draft.SoundTracks ?? Array.Empty<object>())
            {
                switch (obj)
                {
                    case JsonElement je:
                        try
                        {
                            var dto = je.Deserialize<SoundtrackDTO>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (dto != null) soundtrackDtos.Add(dto);
                        }
                        catch { }
                        break;
                    case SoundtrackDTO dto:
                        soundtrackDtos.Add(dto);
                        break;
                }
            }

            // Update trackCount to include soundtrack layers
            if (soundtrackDtos.Count > 0)
            {
                trackCount = Math.Max(trackCount, (int)(soundtrackDtos.Max(d => (int)d.LayerIndex) + 1));
            }

            foreach (var dto in soundtrackDtos.OrderBy(d => d.LayerIndex).ThenBy(d => d.StartFrame))
            {
                double startPx = dto.StartFrame;
                double widthPx = Math.Max(1, (double)dto.Duration);

                var element = ClipElementUI.CreateClip(
                    startX: startPx,
                    width: widthPx,
                    trackIndex: (int)dto.LayerIndex,
                    id: string.IsNullOrWhiteSpace(dto.Id) ? null : dto.Id,
                    labelText: string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name,
                    background: ClipElementUI.DetermineAssetColor(ClipMode.AudioClip),
                    prototype: null,
                    relativeStart: dto.RelativeStartFrame,
                    maxFrames: dto.Duration
                );

                element.displayName = string.IsNullOrWhiteSpace(dto.Name) ? element.Id : dto.Name;
                element.origTrack = (int)dto.LayerIndex;
                element.origLength = widthPx;
                element.origX = startPx;
                element.relativeStartFrame = dto.RelativeStartFrame;
                element.maxFrameCount = dto.Duration;
                element.isInfiniteLength = false;
                element.sourcePath = dto.FilePath ?? (dto.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
                element.ClipType = ClipMode.AudioClip;
                element.ExtraData = dto.MetaData ?? new();
                element.sourceSecondPerFrame = 1f / proj.targetFrameRate;
                element.SecondPerFrameRatio = dto.SecondPerFrameRatio;
                element.ApplySpeedRatio();
                element.TypeName = dto.TypeName;
                element.FromPlugin = dto.FromPlugin;
                element.Effects = new Dictionary<string, IEffect>();

                clipsDict.AddOrUpdate(element.Id, element, (_, _) => element);
            }

            return (clipsDict, trackCount);
        }

        private static string? ExtractLabelText(Microsoft.Maui.Controls.Border border)
        {
            try
            {
                if (border.Content is Microsoft.Maui.Controls.Grid g)
                {
                    foreach (var child in g.Children)
                    {
                        if (child is Microsoft.Maui.Controls.Label l) return l.Text;
                        if (child is Microsoft.Maui.Controls.Layout layout)
                        {
                            foreach (var sub in layout.Children)
                            {
                                if (sub is Microsoft.Maui.Controls.Label sl) return sl.Text;
                            }
                        }
                    }
                }
                else if (border.Content is Microsoft.Maui.Controls.Layout layout)
                {
                    foreach (var sub in layout.Children)
                    {
                        if (sub is Microsoft.Maui.Controls.Label sl) return sl.Text;
                    }
                }
            }
            catch { }
            return null;
        }



    }
}
