using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Globalization;
using System.Text.Json;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

namespace projectFrameCut.DraftStuff;

public partial class ClipInfoBuilder
{
    private record SubtitleRow(SubtitleCue Cue, string Display);

    private async Task<View> BuildSubtitleOptionTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
    {
        SubtitleData data;
        try
        {
            data = clip.ExtraData.TryGetValue(SubtitleData.MetadataKey, out var raw) ? SubtitleData.Restore(raw).Clone() : new();
        }
        catch (Exception ex)
        {
            Log(ex, $"Read subtitles for clip {clip.Id}", page);
            return new Label { Text = Localized._ExceptionTemplate(ex), TextColor = Colors.OrangeRed };
        }
        var fonts = await Task.Run(() =>
        {
            if (TextServices.LoadedFonts.Count == 0) TextServices.LoadFonts();
            var items = TextServices.LoadedFonts.Values.ToList();
            if (!items.Any(f => string.Equals(f.FontName, data.Style.FontName, StringComparison.OrdinalIgnoreCase))
                && TextClipFontRegistry.ResolveFont(data.Style.FontName, data.Style.FontStyle) is { } current)
                items.Insert(0, new FontItem { FontName = data.Style.FontName, DisplayName = current.DisplayName, InnerFont = current });
            return items;
        });
        var root = new VerticalStackLayout { Padding = 10, Spacing = 8, IsEnabled = !page.IsReadonly };
        var error = new Label { TextColor = Colors.OrangeRed, IsVisible = false };
        var list = new CollectionView { HeightRequest = 220, SelectionMode = SelectionMode.Single };
        list.ItemTemplate = new DataTemplate(() =>
        {
            var label = new Label { Padding = 8, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
            label.SetBinding(Label.TextProperty, nameof(SubtitleRow.Display));
            return label;
        });
        var start = new Entry { Placeholder = "00:00:00.000" };
        var end = new Entry { Placeholder = "00:00:02.000" };
        var markdown = new Editor { MinimumHeightRequest = 120, AutoSize = EditorAutoSizeOption.TextChanges };
        var preview = new ContentView { BackgroundColor = Colors.Black, Padding = 8 };
        var apply = new Button { Text = Localized._Apply };
        var delete = new Button { Text = Localized.Subtitle_DeleteCue };
        var jump = new Button { Text = Localized.Subtitle_GoToCue };
        SubtitleCue? selected = null;
        bool selecting = false;

        void ShowError(Exception ex)
        {
            Log(ex, $"Edit subtitles for clip {clip.Id}", page);
            error.Text = Localized.Subtitle_InvalidData + "\n" + ex.Message;
            error.IsVisible = true;
        }

        void Publish()
        {
            data.Validate();
            foreach (var cue in data.Cues) SubtitleData.FrameAt(cue.EndMilliseconds, page.ProjectInfo.TargetFrameRate);
            var saved = data.Clone();
            clip.ExtraData.TryGetValue(SubtitleData.MetadataKey, out var old);
            if (old is not null && JsonSerializer.Serialize(old) == JsonSerializer.Serialize(saved)) return;
            clip.ExtraData[SubtitleData.MetadataKey] = saved;
            handler?.Invoke(this, new(SubtitleData.MetadataKey, saved, old));
            error.IsVisible = false;
        }

        bool SaveCurrent()
        {
            if (selected is null) return true;
            long oldStart = selected.StartMilliseconds, oldEnd = selected.EndMilliseconds;
            string oldText = selected.Markdown;
            try
            {
                selected.StartMilliseconds = SubtitleData.ParseTime(start.Text ?? "");
                selected.EndMilliseconds = SubtitleData.ParseTime(end.Text ?? "");
                selected.Markdown = markdown.Text ?? "";
                Publish();
                return true;
            }
            catch (Exception ex)
            {
                selected.StartMilliseconds = oldStart;
                selected.EndMilliseconds = oldEnd;
                selected.Markdown = oldText;
                ShowError(ex);
                return false;
            }
        }

        void RefreshPreview()
        {
            preview.Content = Markdown2XAML.ConvertText(markdown.Text ?? "", new()
            {
                BodyFontSize = 22,
                TextColor = new Color(data.Style.FillR / 65535f, data.Style.FillG / 65535f, data.Style.FillB / 65535f, (float)data.Style.FillA)
            });
        }

        void Select(SubtitleCue? cue)
        {
            selected = cue;
            start.Text = cue is null ? "" : SubtitleData.FormatTime(cue.StartMilliseconds);
            end.Text = cue is null ? "" : SubtitleData.FormatTime(cue.EndMilliseconds);
            markdown.Text = cue?.Markdown ?? "";
            start.IsEnabled = end.IsEnabled = markdown.IsEnabled = apply.IsEnabled = delete.IsEnabled = jump.IsEnabled = cue is not null;
            RefreshPreview();
        }

        void RefreshList(Guid? id)
        {
            selecting = true;
            var rows = data.Cues.OrderBy(c => c.StartMilliseconds).Select(c => new SubtitleRow(c,
                $"{SubtitleData.FormatTime(c.StartMilliseconds)} → {SubtitleData.FormatTime(c.EndMilliseconds)}\n{c.Markdown}")).ToArray();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(r => r.Cue.Id == id);
            selecting = false;
        }

        list.SelectionChanged += (_, e) =>
        {
            if (selecting) return;
            var next = (e.CurrentSelection.FirstOrDefault() as SubtitleRow)?.Cue;
            if (next?.Id == selected?.Id) return;
            if (!SaveCurrent()) { RefreshList(selected?.Id); return; }
            Select(next);
            RefreshList(next?.Id);
        };
        markdown.TextChanged += (_, _) => RefreshPreview();
        apply.Clicked += (_, _) => { if (SaveCurrent()) RefreshList(selected?.Id); };
        delete.Clicked += (_, _) =>
        {
            if (selected is null) return;
            var removed = selected;
            int index = data.Cues.IndexOf(removed);
            data.Cues.RemoveAt(index);
            try
            {
                Publish();
                Select(data.Cues.FirstOrDefault());
                RefreshList(selected?.Id);
            }
            catch (Exception ex)
            {
                data.Cues.Insert(index, removed);
                ShowError(ex);
            }
        };
        jump.Clicked += async (_, _) =>
        {
            if (!SaveCurrent() || selected is null) return;
            try { await page.JumpToSubtitleAsync(clip, selected); }
            catch (Exception ex) { ShowError(ex); }
        };
        var add = new Button { Text = Localized.Subtitle_AddCue };
        add.Clicked += (_, _) =>
        {
            if (!SaveCurrent()) return;
            SubtitleCue? cue = null;
            try
            {
                long time = data.Cues.Count == 0 ? 0 : data.Cues.Max(c => c.EndMilliseconds);
                cue = new SubtitleCue { StartMilliseconds = time, EndMilliseconds = checked(time + 2000) };
                data.Cues.Add(cue);
                Publish();
                Select(cue);
                RefreshList(cue.Id);
            }
            catch (Exception ex)
            {
                if (cue is not null) data.Cues.Remove(cue);
                ShowError(ex);
            }
        };
        var import = new Button { Text = Localized.Subtitle_Import };
        import.Clicked += async (_, _) =>
        {
            if (!SaveCurrent()) return;
            import.IsEnabled = false;
            try
            {
                var imported = await SubtitleServices.PickAsync(page);
                if (imported is null) return;
                foreach (var cue in imported.Value.Result.Data.Cues) SubtitleData.FrameAt(cue.EndMilliseconds, page.ProjectInfo.TargetFrameRate);
                data.Cues = imported.Value.Result.Data.Cues;
                Publish();
                Select(data.Cues.FirstOrDefault());
                RefreshList(selected?.Id);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { import.IsEnabled = true; }
        };
        var fit = new Button { Text = Localized.Subtitle_FitLength };
        fit.Clicked += (_, _) =>
        {
            if (!SaveCurrent() || data.Cues.Count == 0) return;
            try
            {
                uint last = SubtitleData.FrameAt(data.Cues.Max(c => c.EndMilliseconds), page.ProjectInfo.TargetFrameRate);
                if (last <= clip.relativeStartFrame) throw new InvalidOperationException(Localized.Subtitle_OutsideClip);
                uint old = clip.lengthInFrame;
                uint length = last - clip.relativeStartFrame;
                if ((ulong)page.GetClipFrameRange(clip).Start + ClipTiming.EffectiveDuration(length, clip.SpeedProvider) > uint.MaxValue)
                    throw new OverflowException(Localized.Subtitle_InvalidData);
                clip.lengthInFrame = length;
                clip.ApplySpeedRatio();
                handler?.Invoke(this, new("timing", length, old));
            }
            catch (Exception ex) { ShowError(ex); }
        };

        root.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { add, delete, import } });
        root.Children.Add(list);
        root.Children.Add(new Label { Text = Localized.Subtitle_Start });
        root.Children.Add(start);
        root.Children.Add(new Label { Text = Localized.Subtitle_End });
        root.Children.Add(end);
        root.Children.Add(new Label { Text = Localized.Subtitle_Markdown });
        root.Children.Add(markdown);
        root.Children.Add(new Label { Text = Localized.Subtitle_MarkdownHint, FontSize = 12 });
        root.Children.Add(preview);
        root.Children.Add(error);
        root.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { apply, jump, fit } });

        var style = new PropertyPanelBuilder();
        style.AddText(new Label { Text = Localized.Subtitle_Style, FontAttributes = FontAttributes.Bold, FontSize = 18 });
        style.AddDialogFontPicker("font", PPLocalizedResources.TextOption_Font, PPLocalizedResources.TextOption_Font,
            data.Style.FontName, fonts, page, font =>
            {
                if (!SaveCurrent()) return;
                if (font.InnerFont is not null) TextClipFontRegistry.RegisterFontFace(font.InnerFont);
                else if (!string.IsNullOrEmpty(font.Path)) TextClipFontRegistry.AddFont(font.Path);
                data.Style.FontName = font.FontName;
                Publish();
                RefreshPreview();
            }, TextServices.RenderFontPreviewAsync);
        style.AddEntry("size", PPLocalizedResources.TextOption_Size, data.Style.FontSize.ToString(CultureInfo.InvariantCulture), "48");
        style.AddPicker("fontStyle", PPLocalizedResources.TextOption_Style, ["Regular", "Bold", "Italic", "BoldItalic"], data.Style.FontStyle);
        style.AddEntry("stroke", PPLocalizedResources.TextOption_Stroke, data.Style.StrokeThickness.ToString(CultureInfo.InvariantCulture), "2");
        style.AddEntry("spacing", PPLocalizedResources.TextOption_LineSpacing, data.Style.LineSpacing.ToString(CultureInfo.InvariantCulture), "0.3");
        style.AddEntry("wrap", PPLocalizedResources.TextOption_WrapW, data.Style.GetWrappingWidth()?.ToString(CultureInfo.InvariantCulture) ?? "", "");
        style.AddEntry("margin", Localized.Subtitle_BottomMargin, data.BottomMargin.ToString(CultureInfo.InvariantCulture), "60");
        style.AddEntry("x", "X", data.Style.X.ToString(CultureInfo.InvariantCulture), "0");
        style.AddEntry("y", "Y", data.Style.Y.ToString(CultureInfo.InvariantCulture), "0");
        style.AddPicker("align", PPLocalizedResources.TextOption_HorizonOption, Enum.GetNames<TextAlignment>(), data.Style.Alignment.ToString());
        style.AddCollapsibleSection(PPLocalizedResources.TextOption_Color, b => AddColor(b, "fill", new Color(data.Style.FillR / 65535f, data.Style.FillG / 65535f, data.Style.FillB / 65535f, (float)data.Style.FillA)));
        style.AddCollapsibleSection(PPLocalizedResources.TextOption_Stroke, b => AddColor(b, "outline", new Color(data.Style.StrokeR / 65535f, data.Style.StrokeG / 65535f, data.Style.StrokeB / 65535f, (float)data.Style.StrokeA)));
        style.PropertyChanged += (_, e) =>
        {
            if (!SaveCurrent()) return;
            var before = data.Clone();
            try
            {
                float Number() => float.Parse(e.Value?.ToString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture);
                switch (e.Id)
                {
                    case "size": data.Style.FontSize = Number(); break;
                    case "fontStyle": data.Style.FontStyle = e.Value?.ToString() ?? "Regular"; break;
                    case "stroke": data.Style.StrokeThickness = Number(); break;
                    case "spacing": data.Style.LineSpacing = Number(); break;
                    case "wrap": data.Style.SetWrappingWidth(string.IsNullOrWhiteSpace(e.Value?.ToString()) ? null : Number()); break;
                    case "margin": data.BottomMargin = Number(); break;
                    case "x": data.Style.X = Number(); break;
                    case "y": data.Style.Y = Number(); break;
                    case "align": data.Style.Alignment = Enum.Parse<TextAlignment>(e.Value!.ToString()!); break;
                    case "fill" when e.Value is Color c:
                        data.Style.FillR = (ushort)Math.Round(c.Red * ushort.MaxValue); data.Style.FillG = (ushort)Math.Round(c.Green * ushort.MaxValue);
                        data.Style.FillB = (ushort)Math.Round(c.Blue * ushort.MaxValue); data.Style.FillA = (Half)c.Alpha;
                        break;
                    case "outline" when e.Value is Color outline:
                        data.Style.StrokeR = (ushort)Math.Round(outline.Red * ushort.MaxValue); data.Style.StrokeG = (ushort)Math.Round(outline.Green * ushort.MaxValue);
                        data.Style.StrokeB = (ushort)Math.Round(outline.Blue * ushort.MaxValue); data.Style.StrokeA = (Half)outline.Alpha;
                        break;
                    default: return;
                }
                Publish();
                RefreshPreview();
            }
            catch (Exception ex)
            {
                data.Style = before.Style;
                data.BottomMargin = before.BottomMargin;
                ShowError(ex);
            }
        };
        root.Children.Add(style.Build());
        Select(data.Cues.FirstOrDefault());
        RefreshList(selected?.Id);
        return new ScrollView { Content = root };
    }

    private static void AddColor(PropertyPanelBuilder panel, string id, Color color)
    {
        panel.AddCustomChild(changed =>
        {
            var picker = new ColorPicker { SelectedColor = color };
            picker.SelectedColorChanged += (_, c) => changed(c);
            return picker;
        }, id, color);
    }
}
