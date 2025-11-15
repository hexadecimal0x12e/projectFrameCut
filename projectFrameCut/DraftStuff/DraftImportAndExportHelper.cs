using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using projectFrameCut.VideoMakeEngine;

namespace projectFrameCut.DraftStuff
{
    internal static class DraftImportAndExportHelper
    {
        public static DraftStructureJSON ExportFromDraftPage(projectFrameCut.DraftPage page, uint targetFrameRate = 30)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));

            var clips = new List<ClipDraftDTO>();

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

                        var dto = new ClipDraftDTO
                        {
                            Id = elem.Id,
                            Name = name,
                            ClipType = elem.ClipType,
                            LayerIndex = (uint)trackKey,
                            StartFrame = startFrame,
                            RelativeStartFrame = elem.relativeStartFrame,
                            Duration = durationFrames,
                            FrameTime = elem.sourceSecondPerFrame,
                            MixtureMode = MixtureMode.Overlay,
                            FilePath = elem.sourcePath,
                            SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                            SecondPerFrameRatio = elem.SecondPerFrameRatio,
                            MetaData = elem.ExtraData,
                            Effects = elem.Effects?.Select((kv) => 
                            new EffectAndMixtureJSONStructure
                            {
                                TypeName = kv.Key,
                                Parameters = kv.Value.Parameters
                            }).ToArray()
                        };

                        clips.Add(dto);
                    }
                }
            }

            long max = 0;
            foreach (var clip in clips)
            {
                if (clip is ClipDraftDTO dto)
                {
                    max = Math.Max(dto.StartFrame + dto.Duration, max);
                }

            }

            if (max > uint.MaxValue)
            {
                throw new OverflowException($"Project duration overflow, total frames exceed {uint.MaxValue}.");
            }



            return new DraftStructureJSON
            {
                targetFrameRate = targetFrameRate,
                Clips = clips.Cast<object>().ToArray(),
                Duration = (uint)max
            };
        }



        public static ConcurrentDictionary<string, AssetItem> ImportAssetsFromJSON(string json)
        {
            var assets = JsonSerializer.Deserialize<IEnumerable<AssetItem>>(json);
            if (assets is null) return new();
            var assetDict = assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a);
            return new ConcurrentDictionary<string, AssetItem>(assetDict);
        }

        public static (ConcurrentDictionary<string, ClipElementUI>, int) ImportFromJSON(DraftStructureJSON draft)
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
                    background: null,
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
                element.isInfiniteLength = dto.SourceDuration is null;
                element.sourcePath = dto.FilePath;
                element.ClipType = dto.ClipType;
                element.ExtraData = dto.MetaData ?? new();
                element.sourceSecondPerFrame = dto.FrameTime;
                element.SecondPerFrameRatio = dto.SecondPerFrameRatio;
                element.ApplySpeedRatio();
                element.Effects = dto.Effects?.ToDictionary(
                    e => e.TypeName,
                    EffectHelper.CreateFromJSONStructure
                );

                if(element.Effects is null ) 
                {
                    element.Effects = new Dictionary<string, IEffect>();
                }
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

        public static List<OverlapInfo> FindOverlaps(IEnumerable<ClipDraftDTO>? clips, uint allowedOverlapFrames = 5)
        {
            var result = new List<OverlapInfo>();
            if (clips == null) return result;

            var groups = clips
                .Where(c => c != null)
                .GroupBy(c => c.LayerIndex);

            foreach (var group in groups)
            {
                var ordered = group.OrderBy(c => c.StartFrame).ToList();
                int n = ordered.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = ordered[i];
                    long aStart = (long)a.StartFrame;
                    long aEnd = aStart + (long)a.Duration;

                    for (int j = i + 1; j < n; j++)
                    {
                        var b = ordered[j];
                        long bStart = (long)b.StartFrame;

                        if (bStart >= aEnd)
                        {
                            break;
                        }

                        long overlap = aEnd - bStart;
                        if (overlap > (long)allowedOverlapFrames)
                        {
                            result.Add(new OverlapInfo($"{a.Id ?? "unknown ID"} ({a.Name ?? "unknown Name"})", $"{b.Id ?? "unknown ID"} ({b.Name ?? "unknown Name"})", overlap, a.LayerIndex));
                        }
                    }
                }
            }

            return result;
        }

        public static bool HasOverlap(IEnumerable<ClipDraftDTO>? clips, uint allowedOverlapFrames = 5)
            => FindOverlaps(clips, allowedOverlapFrames).Count > 0;





        public class OverlapInfo
        {
            public required string ClipAId { get; set; }
            public required string ClipBId { get; set; }
            public required long OverlapFrames { get; set; }
            public required uint LayerIndex { get; set; }

            [SetsRequiredMembers]
            public OverlapInfo(string clipAId, string clipBId, long overlapFrames, uint layerIndex)
            {
                ClipAId = clipAId;
                ClipBId = clipBId;
                OverlapFrames = overlapFrames;
                LayerIndex = layerIndex;
            }


        }

    }
}
