using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;

namespace projectFrameCut.DraftStuff
{
    internal static class DraftImportAndExportHelper
    {
        /// <summary>
        /// Export a DraftPage's current native-view clips into DraftStructureJSON.
        /// Uses the DraftPage instance to convert pixels↔frames via its PixelToFrame method.
        /// </summary>
        public static DraftStructureJSON ExportFromDraftPage(projectFrameCut.DraftPage page, uint targetFrameRate = 30)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));

            var clips = new List<ClipDraftDTO>();

            // iterate tracks in sorted order
            var trackKeys = page.Tracks.Keys.OrderBy(k => k).ToArray();
            foreach (var trackKey in trackKeys)
            {
                if (!page.Tracks.TryGetValue(trackKey, out var layout)) continue;

                // enumerate children that are Borders (clips)
                foreach (var child in layout.Children)
                {
                    if (child is Microsoft.Maui.Controls.Border border)
                    {
                        if (border.BindingContext is not ClipElementUI elem) continue;

                        // skip ghost/shadow entries if any
                        if (elem.Id.StartsWith("ghost_") || elem.Id.StartsWith("shadow_")) continue;

                        // compute start and duration in frames using page helpers
                        double startPx = border.TranslationX;
                        double widthPx = (border.Width > 0) ? border.Width : border.WidthRequest;

                        uint startFrame = page.PixelToFrame(startPx);
                        uint durationFrames = page.PixelToFrame(widthPx);
                        if (durationFrames == 0) durationFrames = 1;

                        // try to extract a textual name from the clip content (if any)
                        string name = ExtractLabelText(border) ?? elem.Id;

                        var dto = new ClipDraftDTO
                        {
                            Id = elem.Id,
                            Name = name,
                            ClipType = ClipMode.Special,
                            LayerIndex = (uint)trackKey,
                            StartFrame = startFrame,
                            RelativeStartFrame = elem.relativeStartFrame,
                            Duration = durationFrames,
                            FrameTime = 1f / targetFrameRate,
                            MixtureMode = RenderMode.Overlay,
                            FilePath = null,
                            SourceDuration = elem.maxFrameCount > 0 ? (long?)elem.maxFrameCount : null,
                        };

                        clips.Add(dto);
                    }
                }
            }

            return new DraftStructureJSON
            {
                Name = "Exported Draft",
                targetFrameRate = targetFrameRate,
                Clips = clips.Cast<object>().ToArray()
            };
        }

        /// <summary>
        /// Create a DraftPage (native view) from DraftStructureJSON by constructing ClipElementUI entries
        /// and invoking the DraftPage(concurrent clips, trackCount) constructor.
        /// </summary>
        public static projectFrameCut.DraftPage ImportToDraftPage(DraftStructureJSON draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            // collect DTOs
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

            // For placement we will assume DraftPage defaults (FramePerPixel==1, tracksZoomOffest==1)
            // so Frame -> Pixel is identity. We set reasonable fields on ClipElementUI so DraftPage.RegisterClip
            // can place them properly.
            foreach (var dto in dtos.OrderBy(d => d.LayerIndex).ThenBy(d => d.StartFrame))
            {
                double startPx = dto.StartFrame; // frame -> pixel (1:1)
                double widthPx = Math.Max(1, (double)dto.Duration);

                uint maxFrames = dto.SourceDuration is null ? dto.Duration : (uint)Math.Max(dto.SourceDuration.Value, dto.Duration);

                var element = projectFrameCut.DraftPage.CreateClip(
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

                element.origTrack = (int)dto.LayerIndex;
                element.origLength = widthPx;
                element.origX = startPx;
                element.relativeStartFrame = dto.RelativeStartFrame;
                element.maxFrameCount = maxFrames;
                element.isInfiniteLength = dto.SourceDuration is null;

                clipsDict.AddOrUpdate(element.Id, element, (_, _) => element);
            }

            // create DraftPage via constructor
            var page = new projectFrameCut.DraftPage(clipsDict, trackCount);
            return page;
        }

        private static string? ExtractLabelText(Microsoft.Maui.Controls.Border border)
        {
            try
            {
                if (border.Content is Microsoft.Maui.Controls.Grid g)
                {
                    // look for Label in grid children or nested layouts
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
