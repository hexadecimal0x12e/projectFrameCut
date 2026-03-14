using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using IPicture = projectFrameCut.Shared.IPicture;

namespace projectFrameCut.DraftStuff
{
    internal static class DraftImportAndExportHelper
    {
        [return: NotNullIfNotNull(nameof(page))]
        [return: NotNullIfNotNull(nameof(element))]
        public static ClipDraftDTO ExportClipElementFromDraftPage(projectFrameCut.DraftPage? page, ClipElementUI? element, bool wrapSoundtrackAsClip = true)
        {
            if (page == null || element == null) return null!;

            if (!TryFindElementBorder(page, element, out var border, out var trackIndex))
            {
                throw new KeyNotFoundException($"Cannot find clip element '{element.Id}' in current draft page tracks.");
            }

            ClipInfoBuilder.RebuildAllEffects(element);

            if (element.Id.StartsWith("ghost_") || element.Id.StartsWith("shadow_"))
            {
                throw new InvalidOperationException("Ghost/Shadow clips cannot be exported as ClipDraftDTO.");
            }

            return CreateClipDraftDTO(page, border, element, (uint)trackIndex, wrapSoundtrackAsClip);
        }

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
                        ClipInfoBuilder.RebuildAllEffects(elem);

                        if (elem.Id.StartsWith("ghost_") || elem.Id.StartsWith("shadow_")) continue;

                        double startPx = border.TranslationX;
                        double widthPx = (border.WidthRequest > 0) ? border.WidthRequest : ((border.Width > 0) ? border.Width : border.WidthRequest);

                        uint startFrame = (uint)Math.Round(page.PixelToFrame(startPx) / elem.SecondPerFrameRatio);
                        uint durationFrames = (uint)Math.Round(page.PixelToFrame(widthPx) / elem.SecondPerFrameRatio);
                        if (durationFrames == 0) durationFrames = 1;

                        string name = string.IsNullOrWhiteSpace(elem.DisplayName) ? ExtractLabelText(border) ?? elem.Id : elem.DisplayName;

                        if (elem.ClipType == ClipMode.AudioClip)
                        {
                            if (wrapSoundtrackAsClip)
                            {
                                var clipDto = CreateClipDraftDTO(page, border, elem, (uint)trackKey, true);
                                clips.Add(clipDto);
                            }
                            else
                            {
                                var dto = new SoundtrackDTO
                                {
                                    Id = elem.Id,
                                    Name = name,
                                    FromPlugin = string.IsNullOrEmpty(elem.FromPlugin) ? InternalPluginBase.InternalPluginBaseID : elem.FromPlugin,
                                    TypeName = string.IsNullOrEmpty(elem.TypeName) ? "NormalTrack" : elem.TypeName,
                                    TrackType = TrackMode.NormalTrack,
                                    LayerIndex = (uint)trackKey,
                                    StartFrame = startFrame,
                                    RelativeStartFrame = elem.relativeStartFrame,
                                    Duration = durationFrames,
                                    SecondPerFrameRatio = elem.SecondPerFrameRatio,
                                    FilePath = elem.SourcePath,
                                    MetaData = elem.ExtraData
                                };
                                soundtracks.Add(dto);
                            }
                        }
                        else
                        {
                            var dto = CreateClipDraftDTO(page, border, elem, (uint)trackKey, false);

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
                TargetFrameRate = page.ProjectInfo.TargetFrameRate,
                Clips = clips.Cast<object>().ToArray(),
                SoundTracks = soundtracks.Cast<object>().ToArray(),
                Duration = (uint)max,
                SavedAt = DateTime.Now
            };
            if (wrapSoundtrackAsClip) d.AudioDuration = (uint)audMax;
            FixSmallOverlaps(d, 3);
            return d;
        }

        private static bool TryFindElementBorder(projectFrameCut.DraftPage page, ClipElementUI element, [NotNullWhen(true)] out Microsoft.Maui.Controls.Border? border, out int trackIndex)
        {
            var trackKeys = page.Tracks.Keys.OrderBy(k => k).ToArray();
            foreach (var key in trackKeys)
            {
                if (!page.Tracks.TryGetValue(key, out var layout)) continue;
                foreach (var child in layout.Children)
                {
                    if (child is Microsoft.Maui.Controls.Border currentBorder && ReferenceEquals(currentBorder.BindingContext, element))
                    {
                        border = currentBorder;
                        trackIndex = key;
                        return true;
                    }
                }
            }

            border = null;
            trackIndex = -1;
            return false;
        }

        private static ClipDraftDTO CreateClipDraftDTO(projectFrameCut.DraftPage page, Microsoft.Maui.Controls.Border border, ClipElementUI elem, uint layerIndex, bool wrapSoundtrackAsClip)
        {
            double startPx = border.TranslationX;
            double widthPx = (border.WidthRequest > 0) ? border.WidthRequest : ((border.Width > 0) ? border.Width : border.WidthRequest);

            uint startFrame = (uint)Math.Round(page.PixelToFrame(startPx) / elem.SecondPerFrameRatio);
            uint durationFrames = (uint)Math.Round(page.PixelToFrame(widthPx) / elem.SecondPerFrameRatio);
            if (durationFrames == 0) durationFrames = 1;

            string name = string.IsNullOrWhiteSpace(elem.DisplayName) ? ExtractLabelText(border) ?? elem.Id : elem.DisplayName;

            if (elem.ClipType == ClipMode.AudioClip && wrapSoundtrackAsClip)
            {
                // Normalize MetaData so complex objects like TextEntries become JsonElement for reliable serialization
                Dictionary<string, object>? normalizedMeta = null;
                if (elem.ExtraData != null)
                {
                    normalizedMeta = new Dictionary<string, object>(elem.ExtraData.Count);
                    foreach (var kv in elem.ExtraData)
                    {
                        if (kv.Key == "TextEntries")
                        {
                            try { normalizedMeta[kv.Key] = JsonSerializer.SerializeToElement(kv.Value); }
                            catch { normalizedMeta[kv.Key] = kv.Value; }
                        }
                        else normalizedMeta[kv.Key] = kv.Value;
                    }
                }

                return new ClipDraftDTO
                {
                    Id = elem.Id,
                    Name = name,
                    FromPlugin = InternalPluginBase.InternalPluginBaseID,
                    TypeName = nameof(SoundTrackToClipWrapper),
                    ClipType = ClipMode.AudioClip,
                    LayerIndex = layerIndex,
                    StartFrame = startFrame,
                    RelativeStartFrame = elem.relativeStartFrame,
                    Duration = durationFrames,
                    FrameTime = elem.sourceSecondPerFrame,
                    FilePath = elem.SourcePath,
                    SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                    IsInfiniteLength = elem.isInfiniteLength,
                    SecondPerFrameRatio = elem.SecondPerFrameRatio,
                    MetaData = normalizedMeta,
                    Effects = null
                };
            }

            // Normalize MetaData so complex objects like TextEntries become JsonElement for reliable serialization
            Dictionary<string, object>? normalizedMeta2 = null;
            if (elem.ExtraData != null)
            {
                normalizedMeta2 = new Dictionary<string, object>(elem.ExtraData.Count);
                foreach (var kv in elem.ExtraData)
                {
                    if (kv.Key == "TextEntries")
                    {
                        try { normalizedMeta2[kv.Key] = JsonSerializer.SerializeToElement(kv.Value); }
                        catch { normalizedMeta2[kv.Key] = kv.Value; }
                    }
                    else normalizedMeta2[kv.Key] = kv.Value;
                }
            }

            return new ClipDraftDTO
            {
                Id = elem.Id,
                Name = name,
                FromPlugin = elem.FromPlugin,
                TypeName = elem.TypeName,
                ClipType = elem.ClipType,
                LayerIndex = layerIndex,
                StartFrame = startFrame,
                RelativeStartFrame = elem.relativeStartFrame,
                Duration = durationFrames,
                FrameTime = elem.sourceSecondPerFrame,
                FilePath = elem.SourcePath,
                SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                IsInfiniteLength = elem.isInfiniteLength,
                SecondPerFrameRatio = elem.SecondPerFrameRatio,
                MetaData = normalizedMeta2,
                Effects = elem.Effects?.Select((kv) =>
                {
                    var effect = kv.Value;
                    var structure = new EffectAndMixtureJSONStructure
                    {
                        Name = kv.Key,
                        FromPlugin = effect.FromPlugin,
                        TypeName = effect.TypeName,
                        Parameters = effect.Parameters,
                        Index = effect.Index,
                        Enabled = effect.Enabled,
                        RelativeHeight = effect.RelativeHeight,
                        RelativeWidth = effect.RelativeWidth,
                        IsMixture = false,
                        IsContinuousEffect = effect is IContinuousEffect,
                        IsVariableArgumentEffect = effect is IBindableArgumentEffect,
                        BindedEffectGroupID = effect.BindedEffectGroupID ?? "",
                    };

                    if (effect is IBindableArgumentEffect bindableEffect)
                    {
                        structure.Id = bindableEffect.Id;
                        structure.BindedInputID = bindableEffect.BindedArgumentProviderID;
                        if (bindableEffect is IBindableArgumentEffectManyToOneValueProcesser mpe)
                        {
                            structure.BindedInputIDs = mpe.BindedArgumentProviderIDs;
                        }
                        else if (bindableEffect is IBindableArgumentEffectManyInputResultGenerator mpg)
                        {
                            structure.BindedInputIDs = mpg.BindedArgumentProviderIDs;
                        }
                        structure.Enabled = true;
                    }

                    return structure;
                }).ToArray(),
                EffectBundles = elem.EffectBundles?.Values
                    .Select(b => new EffectBundleJSONStructure
                    {
                        Id = b.Id,
                        BundleTypeName = b.TypeName,
                        Parameters = b.Parameters,
                        Name = b.Name,
                        BindedInputId = b.BindedInputId,
                        BindedOutputId = b.BindedOutputId,
                        BindedInputIds = b.BindedInputIds?.ToArray(),
                    }).ToArray()
            };
        }

        public static IClip[] JSONToIClips(DraftStructureJSON json, bool InitAtLoad = true, IPicture.PicturePixelMode? targetPPB = null)
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
                if(InitAtLoad) clipInstance.ReInit(targetPPB ?? throw new NullReferenceException("You must provide a targetPPB."));
                clipsList.Add(clipInstance);

            }
            return clipsList.ToArray();
        }

        public static ISoundTrack[] JSONToISoundTracks(DraftStructureJSON json, bool InitAtLoad = true)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.SoundTracks) ?? throw new NullReferenceException("Failed to cast SoundtrackDTOs to ISoundTracks.");

            var tracksList = new List<ISoundTrack>();

            foreach (var track in elements.Cast<JsonElement>())
            {
                var trackInstance = PluginManager.CreateSoundTrack(track);
                trackInstance.ExtraData = track.Deserialize<SoundtrackDTO>()?.MetaData ?? new();

                if (trackInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        trackInstance.FilePath = AssetDatabase.Assets[trackInstance.FilePath.Substring(1)].Path;
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
                else if (string.IsNullOrEmpty(trackInstance.FilePath) && track.TryGetProperty("FilePath", out var fp) && trackInstance.NeedFilePath)
                {
                    try
                    {
                        trackInstance.FilePath = fp.GetString();
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

                if (InitAtLoad) trackInstance.ReInit();
                tracksList.Add(trackInstance);
            }

            return tracksList.ToArray();
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

                element.DisplayName = string.IsNullOrWhiteSpace(dto.Name) ? element.Id : dto.Name;
                element.origTrack = (int)dto.LayerIndex;
                element.origLength = widthPx;
                element.origX = startPx;
                element.relativeStartFrame = dto.RelativeStartFrame;
                element.maxFrameCount = maxFrames;
                element.isInfiniteLength = dto.IsInfiniteLength;
                element.SourcePath = dto.FilePath ?? (dto.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
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

                if (dto.EffectBundles != null)
                {
                    var dict = new Dictionary<Guid, IEffectBundle>();
                    for (int i = 0; i < dto.EffectBundles.Length; i++)
                    {
                        var b = dto.EffectBundles[i];
                        var f = EffectServices.GetAvailableEffectBundles()[b.BundleTypeName]();
                        f.Id = b.Id;
                        f.Name = b.Name;
                        f.Parameters = b.Parameters ?? new Dictionary<string, object>();
                        f.BindedInputId = b.BindedInputId;
                        f.BindedOutputId = b.BindedOutputId;
                        f.BindedInputIds = b.BindedInputIds?.ToList();
                        dict.Add(b.Id, f);
                    }
                    element.EffectBundles = dict;
                }

                if (element.Effects is null)
                {
                    element.Effects = new Dictionary<string, IEffect>();
                }

                if (element.ClipType == ClipMode.TransformClip)
                {
                    element.LeftHandle.IsVisible = false;
                    element.RightHandle.IsVisible = false;
                    element.LeftHandle.GestureRecognizers.Clear();
                    element.RightHandle.GestureRecognizers.Clear();
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

                element.DisplayName = string.IsNullOrWhiteSpace(dto.Name) ? element.Id : dto.Name;
                element.origTrack = (int)dto.LayerIndex;
                element.origLength = widthPx;
                element.origX = startPx;
                element.relativeStartFrame = dto.RelativeStartFrame;
                element.maxFrameCount = dto.Duration;
                element.isInfiniteLength = false;
                element.SourcePath = dto.FilePath ?? (dto.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
                element.ClipType = ClipMode.AudioClip;
                element.ExtraData = dto.MetaData ?? new();
                element.sourceSecondPerFrame = 1f / proj.TargetFrameRate;
                element.SecondPerFrameRatio = dto.SecondPerFrameRatio;
                element.ApplySpeedRatio();
                element.TypeName = dto.TypeName;
                element.FromPlugin = dto.FromPlugin;
                element.Effects = new Dictionary<string, IEffect>();

                clipsDict.AddOrUpdate(element.Id, element, (_, _) => element);
            }

            return (clipsDict, trackCount);
        }


        public static void FixSmallOverlaps(DraftStructureJSON draft, uint thresholdFrames = 3)
        {
            ArgumentNullException.ThrowIfNull(draft, nameof(draft));

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

            var grouped = dtos.GroupBy(d => d.LayerIndex);
            foreach (var g in grouped)
            {
                var list = g.OrderBy(d => d.StartFrame).ToList();
                for (int i = 0; i < list.Count - 1; i++)
                {
                    var cur = list[i];
                    var next = list[i + 1];
                    ulong curEnd = (ulong)cur.StartFrame + cur.Duration;
                    if (curEnd > next.StartFrame)
                    {
                        ulong overlap = curEnd - next.StartFrame;
                        if (overlap > 0 && overlap < thresholdFrames)
                        {
                            next.StartFrame = (uint)(next.StartFrame + overlap);
                        }
                    }
                }
            }


            ulong max = 0, audMax = 0;
            foreach (var dto in dtos)
            {
                if (dto.ClipType == ClipMode.AudioClip)
                {
                    audMax = Math.Max(audMax, (ulong)dto.StartFrame + dto.Duration);
                }
                else
                {
                    max = Math.Max(max, (ulong)dto.StartFrame + dto.Duration);
                }
            }

            if (max > uint.MaxValue)
            {
                throw new OverflowException($"Project duration overflow, total frames exceed {uint.MaxValue}.");
            }

            draft.Duration = (uint)max;
            if (audMax > 0) draft.AudioDuration = (uint)audMax;

            draft.Clips = dtos.Cast<object>().ToArray();
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

        internal static ClipElementUI ConvertToElement(ClipDraftDTO clip)
        {
            double widthPx = Math.Max(1, (double)clip.Duration);
            uint maxFrames = clip.SourceDuration is null ? clip.Duration : (uint)Math.Max(clip.SourceDuration.Value, clip.Duration);

            var element = ClipElementUI.CreateClip(
                startX: clip.StartFrame,
                width: widthPx,
                trackIndex: (int)clip.LayerIndex,
                id: string.IsNullOrWhiteSpace(clip.Id) ? null : clip.Id,
                labelText: string.IsNullOrWhiteSpace(clip.Name) ? null : clip.Name,
                background: ClipElementUI.DetermineAssetColor(clip.ClipType),
                prototype: null,
                relativeStart: clip.RelativeStartFrame,
                maxFrames: maxFrames
            );

            element.DisplayName = string.IsNullOrWhiteSpace(clip.Name) ? element.Id : clip.Name;
            element.origTrack = (int)clip.LayerIndex;
            element.origLength = widthPx;
            element.origX = clip.StartFrame;
            element.relativeStartFrame = clip.RelativeStartFrame;
            element.maxFrameCount = maxFrames;
            element.isInfiniteLength = clip.IsInfiniteLength;
            element.SourcePath = clip.FilePath ?? (clip.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
            element.ClipType = clip.ClipType;
            element.ExtraData = clip.MetaData ?? new();
            element.sourceSecondPerFrame = clip.FrameTime;
            element.SecondPerFrameRatio = clip.SecondPerFrameRatio;

            // Apply visual properties
            element.ApplySpeedRatio();
            element.ApplyClipColor();

            element.TypeName = clip.TypeName;
            element.FromPlugin = clip.FromPlugin;

            // Reconstruct Effects
            element.Effects = clip.Effects?.ToDictionary(
                e => string.IsNullOrWhiteSpace(e.Name) ? $"Effect-{Guid.NewGuid()}" : e.Name,
                e => PluginManager.CreateEffect(e, 1, 1)
            ) ?? new Dictionary<string, IEffect>();

            // Reconstruct Effect Bundles
            if (clip.EffectBundles != null)
            {
                var dict = new Dictionary<Guid, IEffectBundle>();
                foreach (var b in clip.EffectBundles)
                {
                    if (EffectServices.GetAvailableEffectBundles().TryGetValue(b.BundleTypeName, out var factory))
                    {
                        var f = factory();
                        f.Id = b.Id;
                        f.Name = b.Name;
                        f.Parameters = b.Parameters ?? new Dictionary<string, object>();
                        f.BindedInputId = b.BindedInputId;
                        f.BindedOutputId = b.BindedOutputId;
                        f.BindedInputIds = b.BindedInputIds?.ToList();
                        dict[b.Id] = f;
                    }
                }
                element.EffectBundles = dict;
            }
            else
            {
                element.EffectBundles = new Dictionary<Guid, IEffectBundle>();
            }

            if(element.ClipType  == ClipMode.TransformClip)
            {
                element.LeftHandle.IsVisible = false;
                element.RightHandle.IsVisible = false;
                element.LeftHandle.GestureRecognizers.Clear();
                element.RightHandle.GestureRecognizers.Clear();
            }

            return element;
        }
    }
}
