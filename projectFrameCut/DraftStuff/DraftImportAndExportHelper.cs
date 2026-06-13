using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Asset;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
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
using System.Text.Encodings.Web;
using System.Text.Json;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.DraftStuff
{
    internal static class DraftImportAndExportHelper
    {
        private const string InternalPlaceEffectName = "__Internal_Place__";
        private const string InternalResizeEffectName = "__Internal_Resize__";
        private const string SolidColorOutputWidthKey = "SolidColorOutputWidth";
        private const string SolidColorOutputHeightKey = "SolidColorOutputHeight";
        private const string SolidColorUseFixedOutputSizeKey = "SolidColorUseFixedOutputSize";

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

        public static DraftStructureJSON ExportFromDraftPage(projectFrameCut.DraftPage page, bool wrapSoundtrackAsClip = false, bool includeUiOnlyClips = true, bool fixOverlap = false)
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
                        if (!includeUiOnlyClips && elem.ClipType == ClipMode.MarkingClip) continue;

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
                Clips = clips.Cast<object>().ToArray(),
                SoundTracks = soundtracks.Cast<object>().ToArray(),
                Duration = (uint)max,
                SavedAt = DateTime.Now
            };
            if (wrapSoundtrackAsClip) d.AudioDuration = (uint)audMax;
            if (fixOverlap)
            {
                try
                {
                    FixSmallOverlaps(d, 3);
                }
                catch (Exception ex)
                {
                    Log(ex, "Fix small overlap");
                }
            }
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
                var normalizedMeta = NormalizeClipMetaData(elem.ExtraData, page.ProjectInfo.TargetFrameRate);

                return new ClipDraftDTO
                {
                    Id = elem.Id,
                    Name = name,
                    FromPlugin = InternalPluginBase.InternalPluginBaseID,
                    TypeName = nameof(SoundTrackToClipWrapper),
                    ClipType = ClipMode.AudioClip,
                    LayerIndex = layerIndex,
                    SubLayerIndex = (uint)Math.Max(0, elem.SubLayerIndex),
                    StartFrame = startFrame,
                    RelativeStartFrame = elem.relativeStartFrame,
                    Duration = durationFrames,
                    FrameTime = elem.sourceSecondPerFrame,
                    FilePath = elem.SourcePath,
                    SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                    IsInfiniteLength = elem.isInfiniteLength,
                    ShouldDisplayInUI = elem.ShouldDisplayInUI,
                    SecondPerFrameRatio = elem.SecondPerFrameRatio,
                    TargetWidth = elem.TargetWidth,
                    TargetHeight = elem.TargetHeight,
                    TargetX = elem.TargetX,
                    TargetY = elem.TargetY,
                    MetaData = normalizedMeta,
                    Effects = null
                };
            }

            var normalizedMeta2 = NormalizeClipMetaData(elem.ExtraData, page.ProjectInfo.TargetFrameRate);

            // For TextClips without explicit dimensions, compute the text bounds from TextEntries
            int exportTargetWidth = elem.TargetWidth;
            int exportTargetHeight = elem.TargetHeight;
            if (elem.ClipType == ClipMode.TextClip
                && exportTargetWidth <= 0
                && elem.ExtraData is not null
                && elem.ExtraData.TryGetValue("TextEntries", out var rawEntries))
            {
                try
                {
                    IReadOnlyList<TextEntry>? entries = rawEntries as IReadOnlyList<TextEntry>;
                    if (entries is null && rawEntries is JsonElement je)
                    {
                        try { entries = je.Deserialize<IReadOnlyList<TextEntry>>(); }
                        catch
                        {
                            // Fall back to old TextClipEntry format
                            try
                            {
                                var oldEntries = je.Deserialize<IReadOnlyList<TextClipEntry>>();
                                if (oldEntries is { Count: > 0 })
                                    entries = TextEntryMigration.MigrateFromTextClipEntries(oldEntries);
                            }
                            catch { }
                        }
                    }
                    if (entries is { Count: > 0 })
                    {
                        var bounds = TextMeasureHelper.MeasureBounds(entries, 1920, 1080);
                        if (bounds.Width > 0 && bounds.Height > 0)
                        {
                            exportTargetWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width));
                            exportTargetHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                        }
                    }
                }
                catch
                {
                    // fall through to use elem.TargetWidth (0 or user-set)
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
                SubLayerIndex = (uint)Math.Max(0, elem.SubLayerIndex),
                StartFrame = startFrame,
                RelativeStartFrame = elem.relativeStartFrame,
                Duration = durationFrames,
                FrameTime = elem.sourceSecondPerFrame,
                FilePath = elem.SourcePath,
                SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                IsInfiniteLength = elem.isInfiniteLength,
                ShouldDisplayInUI = elem.ShouldDisplayInUI,
                SecondPerFrameRatio = elem.SecondPerFrameRatio,
                TargetWidth = exportTargetWidth,
                TargetHeight = exportTargetHeight,
                TargetX = elem.TargetX,
                TargetY = elem.TargetY,
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
                        IsContinuousEffect = effect.TypeOfEffect == EffectType.ContinuousEffect,
                        IsVariableArgumentEffect = effect is IBindableArgumentEffect,
                        ImplementType = effect.ImplementType,
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
                        Enabled = b.Enabled,
                        BindedInputId = b.BindedInputId,
                        BindedOutputId = b.BindedOutputId,
                        BindedInputIds = b.BindedInputIds?.ToArray(),
                    }).ToArray()
            };
        }

        private static Dictionary<string, object> NormalizeClipMetaData(Dictionary<string, object>? source, uint targetFrameRate)
        {
            int capacity = Math.Max(4, source?.Count ?? 0);
            var normalized = new Dictionary<string, object>(capacity);
            if (source != null)
            {
                foreach (var kv in source)
                {
                    if (kv.Key == "TextEntries")
                    {
                        try { normalized[kv.Key] = JsonSerializer.SerializeToElement(kv.Value); }
                        catch { normalized[kv.Key] = kv.Value; }
                    }
                    else
                    {
                        normalized[kv.Key] = kv.Value;
                    }
                }
            }

            normalized[ClipDraftDTO.ProjectFrameRateMetaKey] = targetFrameRate;
            normalized[ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion;
            return normalized;
        }

        public static IClip[] JSONToIClips(DraftStructureJSON json, bool InitAtLoad = true, IPicture.PicturePixelMode? targetPPB = null)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

            if (!elements.Any())
            {
                if (json.Clips.Any())
                {
                    throw new NullReferenceException("Failed to convert DTO to IClip, but the Clips array in JSON is not empty. This may indicate a problem with the JSON structure or the deserialization process.");
                }
                return Array.Empty<IClip>();
            }

            var clipsList = new List<IClip>();

            foreach (var clip in elements.Cast<JsonElement>())
            {
                if (clip.TryGetProperty("ClipType", out var clipTypeProp)
                    && clipTypeProp.ValueKind == JsonValueKind.Number
                    && clipTypeProp.TryGetInt32(out var clipTypeValue)
                    && (ClipMode)clipTypeValue == ClipMode.MarkingClip)
                {
                    continue;
                }

                var clipInstance = PluginManager.CreateClip(clip) ?? throw new NullReferenceException($"PluginManager.CreateClip(clip) failed to create clip for the specific clip.\r\n({JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })})");
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
                if (InitAtLoad) clipInstance.ReInit(targetPPB ?? throw new NullReferenceException("You must provide a targetPPB."));
                clipInstance.EffectsInstances = clipInstance?.Effects?.Select(e => PluginManager.CreateEffect(e, e.ImplementType == EffectImplementType.NotSpecified ? EffectHelper.DefaultImplementsType.GetValueOrDefault($"{e.FromPlugin}.{e.TypeName}", EffectImplementType.NotSpecified) : e.ImplementType))?.ToArray() ?? [];
                if (clipInstance is IVectorContentClip vc && clipInstance.ExtraData.TryGetValue("VectorAntiAliasMode", out var aaObj) && aaObj is string aaStr && !string.IsNullOrEmpty(aaStr))
                {
                    var aaProp = typeof(IVectorContentClip).GetProperty("ClipAntiAliasMode");
                    if (aaProp != null)
                    {
                        var aaType = Nullable.GetUnderlyingType(aaProp.PropertyType) ?? aaProp.PropertyType;
                        if (Enum.TryParse(aaType, aaStr, ignoreCase: true, out var aaMode))
                        {
                            aaProp.SetValue(vc, aaMode);
                        }
                    }
                }
                if (clipInstance is null) throw new NullReferenceException();
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

                if (trackInstance.ExtraData.TryGetValue("Volume", out var trackVolObj))
                {
                    trackInstance.Volume = trackVolObj switch
                    {
                        double d => (float)d,
                        float f => f,
                        System.Text.Json.JsonElement je when je.TryGetDouble(out var jd) => (float)jd,
                        _ when float.TryParse(trackVolObj?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pv) => pv,
                        _ => 1f
                    };
                }

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

        private static bool HasExplicitTargetRect(ClipDraftDTO clip)
            => clip.TargetX != 0 || clip.TargetY != 0 || clip.TargetWidth > 0 || clip.TargetHeight > 0;

        private static bool IsLegacyInternalPlaceEffect(EffectAndMixtureJSONStructure effect)
        {
            if (string.Equals(effect.Name, InternalPlaceEffectName, StringComparison.Ordinal))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(effect.Name)
                && string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacyInternalResizeEffect(EffectAndMixtureJSONStructure effect)
        {
            if (string.Equals(effect.Name, InternalResizeEffectName, StringComparison.Ordinal))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(effect.Name)
                && string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase);
        }

        private static int ScaleLegacyValue(int value, int sourceRelative, int targetRelative, bool clampPositive)
        {
            int scaled = value;
            if (sourceRelative > 0 && targetRelative > 0 && sourceRelative != targetRelative)
            {
                scaled = (int)Math.Round(value * ((double)targetRelative / sourceRelative), MidpointRounding.AwayFromZero);
            }

            return clampPositive ? Math.Max(1, scaled) : scaled;
        }

        private static bool TryReadInt(object? raw, out int value)
        {
            switch (raw)
            {
                case int i:
                    value = i;
                    return true;
                case long l when l <= int.MaxValue && l >= int.MinValue:
                    value = (int)l;
                    return true;
                case uint ui when ui <= int.MaxValue:
                    value = (int)ui;
                    return true;
                case short s:
                    value = s;
                    return true;
                case ushort us:
                    value = us;
                    return true;
                case double d when d <= int.MaxValue && d >= int.MinValue:
                    value = (int)Math.Round(d, MidpointRounding.AwayFromZero);
                    return true;
                case float f when f <= int.MaxValue && f >= int.MinValue:
                    value = (int)Math.Round(f, MidpointRounding.AwayFromZero);
                    return true;
                case JsonElement je when je.ValueKind == JsonValueKind.Number:
                    if (je.TryGetInt32(out var num))
                    {
                        value = num;
                        return true;
                    }

                    if (je.TryGetDouble(out var dbl) && dbl <= int.MaxValue && dbl >= int.MinValue)
                    {
                        value = (int)Math.Round(dbl, MidpointRounding.AwayFromZero);
                        return true;
                    }
                    break;
                case JsonElement je when je.ValueKind == JsonValueKind.String:
                    if (int.TryParse(je.GetString(), out var strNum))
                    {
                        value = strNum;
                        return true;
                    }
                    break;
            }

            if (int.TryParse(raw?.ToString(), out var parsed))
            {
                value = parsed;
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadEffectParameterInt(EffectAndMixtureJSONStructure effect, string key, out int value)
        {
            value = 0;
            if (effect.Parameters == null || !effect.Parameters.TryGetValue(key, out var raw))
            {
                return false;
            }

            return TryReadInt(raw, out value);
        }

        internal static void MigrateLegacyPlaceResizeToTargetRect(ClipDraftDTO dto, ProjectJSONStructure proj)
        {
            if (dto.Effects == null || dto.Effects.Length == 0)
            {
                return;
            }

            var placeEffect = dto.Effects.FirstOrDefault(IsLegacyInternalPlaceEffect);
            var resizeEffect = dto.Effects.FirstOrDefault(IsLegacyInternalResizeEffect);

            if (placeEffect == null && resizeEffect == null)
            {
                return;
            }

            int projectWidth = Math.Max(1, proj.RelativeWidth);
            int projectHeight = Math.Max(1, proj.RelativeHeight);

            if (!HasExplicitTargetRect(dto))
            {
                if (placeEffect != null)
                {
                    int placeRelativeWidth = placeEffect.RelativeWidth > 0 ? placeEffect.RelativeWidth : projectWidth;
                    int placeRelativeHeight = placeEffect.RelativeHeight > 0 ? placeEffect.RelativeHeight : projectHeight;

                    if (TryReadEffectParameterInt(placeEffect, "StartX", out var startX))
                    {
                        dto.TargetX = ScaleLegacyValue(startX, placeRelativeWidth, projectWidth, false);
                    }

                    if (TryReadEffectParameterInt(placeEffect, "StartY", out var startY))
                    {
                        dto.TargetY = ScaleLegacyValue(startY, placeRelativeHeight, projectHeight, false);
                    }
                }

                if (resizeEffect != null)
                {
                    int resizeRelativeWidth = resizeEffect.RelativeWidth > 0 ? resizeEffect.RelativeWidth : projectWidth;
                    int resizeRelativeHeight = resizeEffect.RelativeHeight > 0 ? resizeEffect.RelativeHeight : projectHeight;

                    if (TryReadEffectParameterInt(resizeEffect, "Width", out var width))
                    {
                        dto.TargetWidth = ScaleLegacyValue(width, resizeRelativeWidth, projectWidth, true);
                    }

                    if (TryReadEffectParameterInt(resizeEffect, "Height", out var height))
                    {
                        dto.TargetHeight = ScaleLegacyValue(height, resizeRelativeHeight, projectHeight, true);
                    }
                }
            }

            dto.Effects = dto.Effects
                .Where(e => !IsLegacyInternalPlaceEffect(e) && !IsLegacyInternalResizeEffect(e))
                .ToArray();

            if (dto.Effects.Length == 0)
            {
                dto.Effects = null;
            }

            if (dto.ClipType == ClipMode.SolidColorClip && (dto.TargetWidth > 0 || dto.TargetHeight > 0))
            {
                dto.MetaData ??= new Dictionary<string, object>();
                if (dto.TargetWidth > 0) dto.MetaData[SolidColorOutputWidthKey] = dto.TargetWidth;
                if (dto.TargetHeight > 0) dto.MetaData[SolidColorOutputHeightKey] = dto.TargetHeight;
                dto.MetaData[SolidColorUseFixedOutputSizeKey] = true;
            }
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

            foreach (var dto in dtos)
            {
                MigrateLegacyPlaceResizeToTargetRect(dto, proj);
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
                element.SubLayerIndex = (int)dto.SubLayerIndex;
                element.relativeStartFrame = dto.RelativeStartFrame;
                element.maxFrameCount = maxFrames;
                element.isInfiniteLength = dto.IsInfiniteLength;
                element.ShouldDisplayInUI = dto.ShouldDisplayInUI;
                element.Clip.IsVisible = dto.ShouldDisplayInUI;
                element.SourcePath = dto.FilePath ?? (dto.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
                element.ClipType = dto.ClipType;
                element.ExtraData = dto.MetaData ?? new();
                element.sourceSecondPerFrame = dto.FrameTime;
                element.TargetWidth = dto.TargetWidth;
                element.TargetHeight = dto.TargetHeight;
                element.TargetX = dto.TargetX;
                element.TargetY = dto.TargetY;
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
                        f.Enabled = b.Enabled;
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

                // Rebuild generated effects from bundles before applying UI width from speed ratio.
                ClipInfoBuilder.RebuildAllEffects(element);
                element.ApplySpeedRatio();

                if (element.ClipType == ClipMode.TransformClip || element.ClipType == ClipMode.MarkingClip)
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
                //element.SecondPerFrameRatio = dto.SecondPerFrameRatio;
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
            element.SubLayerIndex = (int)clip.SubLayerIndex;
            element.relativeStartFrame = clip.RelativeStartFrame;
            element.maxFrameCount = maxFrames;
            element.isInfiniteLength = clip.IsInfiniteLength;
            element.ShouldDisplayInUI = clip.ShouldDisplayInUI;
            element.Clip.IsVisible = clip.ShouldDisplayInUI;
            element.SourcePath = clip.FilePath ?? (clip.MetaData?.TryGetValue("FilePath", out var filePath) == true ? filePath?.ToString() : null);
            element.ClipType = clip.ClipType;
            element.ExtraData = clip.MetaData ?? new();
            element.sourceSecondPerFrame = clip.FrameTime;
            //element.SecondPerFrameRatio = clip.SecondPerFrameRatio;
            element.TargetWidth = clip.TargetWidth;
            element.TargetHeight = clip.TargetHeight;
            element.TargetX = clip.TargetX;
            element.TargetY = clip.TargetY;

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

            // Rebuild generated effects from bundles before applying UI width from speed ratio.
            ClipInfoBuilder.RebuildAllEffects(element);

            // Apply visual properties after speed/effects are fully restored.
            element.ApplySpeedRatio();
            element.ApplyClipColor();

            if (element.ClipType == ClipMode.TransformClip || element.ClipType == ClipMode.MarkingClip)
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
