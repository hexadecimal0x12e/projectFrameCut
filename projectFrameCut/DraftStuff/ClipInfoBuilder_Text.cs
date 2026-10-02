using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;
using GridLength = Microsoft.Maui.GridLength;
using Switch = Microsoft.Maui.Controls.Switch;
using TextAlignment = Microsoft.Maui.TextAlignment;
using Thickness = Microsoft.Maui.Thickness;

#if WINDOWS
using Microsoft.UI.Xaml;

#endif

#if IOS
using projectFrameCut.Platforms.iOS;

#endif

namespace projectFrameCut.DraftStuff
{
    public partial class ClipInfoBuilder
    {
        #region text

        public static View BuildTextEntryUI(TextClipEntry e, int idx, IEnumerable<FontItem> fontItems,
            Action<int, TextClipEntry> onChanged,
            Action<int> onRemove,
            bool canDeleteEntry = true,
            bool showAllOptions = false,
            Action<FontPicker>? ShowPicker = null,
            Action? HidePicker = null)
        {
            var currentEntry = e;
            Label SecLabel(string t) => new Label
            {
                Text = t,
                FontSize = 10,
                TextColor = Colors.White,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 6, 0, 2)
            };
            BoxView Divider() => new BoxView
            {
                HeightRequest = 1,
                Color = Colors.White.WithAlpha(0.06f),
                Margin = new Thickness(0, 4)
            };

            var stack = new VerticalStackLayout { Spacing = 4 };
            var glyphWarning = new Label
            {
                TextColor = Colors.OrangeRed,
                FontSize = 12,
                IsVisible = false,
                LineBreakMode = LineBreakMode.WordWrap
            };

            void UpdateGlyphWarning()
            {
                var warning = TextServices.GetMissingGlyphWarning(currentEntry.fontFamily, currentEntry.text, currentEntry.fontSize);
                glyphWarning.Text = warning;
                glyphWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
            }

            var headerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                }
            };
            headerGrid.Add(new Label
            {
                Text = PPLocalizedResources.TextOption_EntryTitle(idx + 1),
                FontAttributes = FontAttributes.Bold,
                FontSize = 13,
                TextColor = Color.FromArgb("#A8B8CC"),
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            var removeBtn = new Button
            {
                Text = "✕",
                WidthRequest = 28,
                HeightRequest = 28,
                Padding = 0,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#FF6060"),
                FontSize = 13,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center,
                IsVisible = canDeleteEntry
            };
            removeBtn.Clicked += (s, ev) => { onRemove?.Invoke(idx); };
            headerGrid.Add(removeBtn, 1, 0);
            stack.Children.Add(headerGrid);
            stack.Children.Add(Divider());

            // CONTENT
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Content));
            var editor = new Editor
            {
                Text = e.text,
                AutoSize = EditorAutoSizeOption.TextChanges,
                MinimumHeightRequest = 64,
                Placeholder = PPLocalizedResources.TextOption_Content_Placeholder
            };
            editor.TextChanged += (s, ev) =>
            {
                currentEntry = currentEntry with { text = editor.Text ?? string.Empty };
                UpdateGlyphWarning();
            };
            editor.Unfocused += (s, ev) =>
            {
                currentEntry = currentEntry with { text = editor.Text ?? string.Empty };
                onChanged?.Invoke(idx, currentEntry);
                UpdateGlyphWarning();
            };
            stack.Children.Add(editor);

            // POSITION
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Position));
            var posGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 6
            };
            var xEntry = new Entry { Text = e.x.ToString(), Placeholder = "0" };
            var yEntry = new Entry { Text = e.y.ToString(), Placeholder = "0" };
            xEntry.Unfocused += (s, ev) => { if (int.TryParse(xEntry.Text, out var nx)) onChanged?.Invoke(idx, e with { x = nx }); };
            yEntry.Unfocused += (s, ev) => { if (int.TryParse(yEntry.Text, out var ny)) onChanged?.Invoke(idx, e with { y = ny }); };
            posGrid.Add(new Label { Text = "X", VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 0, 0);
            posGrid.Add(xEntry, 1, 0);
            posGrid.Add(new Label { Text = "Y", VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 2, 0);
            posGrid.Add(yEntry, 3, 0);
            stack.Children.Add(posGrid);

            // FONT
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Font));
            var fonts = fontItems.Select(x => x.FontName).ToList();
            var currentFontName = fonts.Contains(e.fontFamily) ? e.fontFamily : fonts.FirstOrDefault() ?? string.Empty;
            currentEntry = currentEntry with { fontFamily = currentFontName };

            var fontGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(new GridLength(72))
                },
                ColumnSpacing = 6
            };
            var sizeEntry = new Entry { Text = e.fontSize.ToString(), Placeholder = "24" };
            sizeEntry.Unfocused += (s, ev) => { if (float.TryParse(sizeEntry.Text, out var ns)) onChanged?.Invoke(idx, e with { fontSize = ns }); };
            fontGrid.Add(new Label { Text = PPLocalizedResources.TextOption_Size, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 1, 0);
            fontGrid.Add(sizeEntry, 2, 0);

            var fontSelectBtn = new Button
            {
                Text = currentFontName,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = Color.FromArgb("#1AFFFFFF"),
                TextColor = Colors.White,
                FontSize = 13,
                Padding = new Thickness(8, 4),
                CornerRadius = 6
            };

            void FontChanged(object? sender, FontItem font)
            {
                if (font == null) return;

                if (font.InnerFont is not null)
                    TextClipFontRegistry.RegisterFontFace(font.InnerFont);
                else if (!string.IsNullOrWhiteSpace(font.Path))
                    TextClipFontRegistry.AddFont(font.Path);

                fontSelectBtn.Text = font.DisplayName;
                currentEntry = currentEntry with { fontFamily = font.FontName };
                UpdateGlyphWarning();
                HidePicker?.Invoke();
                onChanged?.Invoke(idx, currentEntry);
            }
            if (ShowPicker is not null)
            {

                var fontPickerControl = new projectFrameCut.ApplicationAPIBase.Views.Pickers.FontPicker
                {
                    FontsSource = fontItems.GroupBy(c => TextHelper.DetectTextLanguage(c.DisplayName))
                                           .OrderByDescending(g => g.Count())
                                           .SelectMany(c => c),
                    PreviewRenderer = TextServices.RenderFontPreviewAsync,
                    Title = PPLocalizedResources.TextOption_Font
                };

                fontPickerControl.SelectedFontChanged += FontChanged;

                fontSelectBtn.Clicked += (s, ev) =>
                {
                    ShowPicker?.Invoke(fontPickerControl);
                };

                fontGrid.Add(fontSelectBtn, 0, 0);
            }
            else
            {
                var picker = new Picker { ItemsSource = fonts, SelectedIndex = Array.IndexOf(fonts.ToArray(), e.fontFamily) };
                picker.SelectedIndexChanged += (s, e) =>
                {
                    FontChanged(null, fontItems.FirstOrDefault(c => c.FontName == picker.SelectedItem as string, null));
                };
                fontGrid.Add(picker, 0, 0);

            }

            stack.Children.Add(fontGrid);
            UpdateGlyphWarning();
            stack.Children.Add(glyphWarning);


            var stylePicker = new Picker { Title = PPLocalizedResources.TextOption_Style, ItemsSource = new[] { PPLocalizedResources.TextOption_Style_Regular, PPLocalizedResources.TextOption_Style_Bold, PPLocalizedResources.TextOption_Style_Italic, PPLocalizedResources.TextOption_Style_BoldItalic }, SelectedItem = e.fontStyle switch { ClipFontStyle.Regular => PPLocalizedResources.TextOption_Style_Regular, ClipFontStyle.Bold => PPLocalizedResources.TextOption_Style_Bold, ClipFontStyle.Italic => PPLocalizedResources.TextOption_Style_Italic, ClipFontStyle.BoldItalic => PPLocalizedResources.TextOption_Style_BoldItalic, _ => PPLocalizedResources.TextOption_Style_Regular, } };
            stylePicker.SelectedIndexChanged += (s, ev) =>
            {
                if (stylePicker.SelectedItem is string sel)
                {
                    var fs = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_Style_Bold => ClipFontStyle.Bold,
                        var v when v == PPLocalizedResources.TextOption_Style_Italic => ClipFontStyle.Italic,
                        var v when v == PPLocalizedResources.TextOption_Style_BoldItalic => ClipFontStyle.BoldItalic,
                        _ => ClipFontStyle.Regular,
                    };
                    onChanged?.Invoke(idx, e with { fontStyle = fs });
                }
            };
            stack.Children.Add(stylePicker);

            // TEXT COLOR
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Color));
            var colorSwatch = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Stroke = Colors.White.WithAlpha(0.12f),
                VerticalOptions = LayoutOptions.Center
            };
            try { colorSwatch.Background = new SolidColorBrush(Color.FromRgba(e.r / 65535.0, e.g / 65535.0, e.b / 65535.0, e.a ?? 1f)); } catch { }
            var colorEntry = new Entry { Text = $"#{((int)Math.Round(e.r / 257.0)):X2}{((int)Math.Round(e.g / 257.0)):X2}{((int)Math.Round(e.b / 257.0)):X2}" };
            colorEntry.Unfocused += (s, ev) =>
            {
                try
                {
                    var c = Color.FromArgb(colorEntry.Text);
                    ushort r = (ushort)Math.Round(c.Red * 65535);
                    ushort g = (ushort)Math.Round(c.Green * 65535);
                    ushort b = (ushort)Math.Round(c.Blue * 65535);
                    float a = (float)c.Alpha;
                    var updated = e with { r = r, g = g, b = b, a = a };
                    colorSwatch.Background = new SolidColorBrush(c);
                    onChanged?.Invoke(idx, updated);
                }
                catch { }
            };
            var colorRow = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 8
            };
            colorRow.Add(colorSwatch, 0, 0);
            colorRow.Add(colorEntry, 1, 0);
            stack.Children.Add(colorRow);

            // ADVANCED (collapsible): ALIGNMENT + TYPOGRAPHY + STROKE
            var advancedStack = new VerticalStackLayout { Spacing = 4, IsVisible = false };

            // ALIGNMENT
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_LangType));
            var langTypePicker = new Picker
            {
                ItemsSource = new string[] { PPLocalizedResources.TextOption_LangType_Auto }.Concat(Enum.GetValues<TextLanguage>().Skip(1).Select(TextClipEntry.LocalizeLanguageName)).ToList(),
                SelectedItem = (int)e.Language
            };
            langTypePicker.SelectedIndexChanged += (s, ev) =>
            {
                onChanged?.Invoke(idx, e with { Language = (TextLanguage)langTypePicker.SelectedIndex });
            };
            advancedStack.Children.Add(langTypePicker);
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Alignment));
            var alignGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(new GridLength(80))
                },
                ColumnSpacing = 6
            };
            var hAlignPicker = new Picker { Title = PPLocalizedResources.TextOption_HorizonOption, ItemsSource = new[] { PPLocalizedResources.TextOption_HorizonOption_Left, PPLocalizedResources.TextOption_HorizonOption_Center, PPLocalizedResources.TextOption_HorizonOption_Right }, SelectedItem = e.horizontalAlignment switch { ClipHorizontalAlignment.Left => PPLocalizedResources.TextOption_HorizonOption_Left, ClipHorizontalAlignment.Center => PPLocalizedResources.TextOption_HorizonOption_Center, ClipHorizontalAlignment.Right => PPLocalizedResources.TextOption_HorizonOption_Right, _ => PPLocalizedResources.TextOption_HorizonOption_Left, } };
            hAlignPicker.SelectedIndexChanged += (s, ev) =>
            {
                if (hAlignPicker.SelectedItem is string sel)
                {
                    ClipHorizontalAlignment ha = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_HorizonOption_Center => ClipHorizontalAlignment.Center,
                        var v when v == PPLocalizedResources.TextOption_HorizonOption_Right => ClipHorizontalAlignment.Right,
                        _ => ClipHorizontalAlignment.Left,
                    };
                    onChanged?.Invoke(idx, e with { horizontalAlignment = ha });
                }
            };
            var vAlignPicker = new Picker { Title = PPLocalizedResources.TextOption_VerticalOption, ItemsSource = new[] { PPLocalizedResources.TextOption_VerticalOption_Top, PPLocalizedResources.TextOption_VerticalOption_Center, PPLocalizedResources.TextOption_VerticalOption_Bottom }, SelectedItem = e.verticalAlignment switch { ClipVerticalAlignment.Top => PPLocalizedResources.TextOption_VerticalOption_Top, ClipVerticalAlignment.Center => PPLocalizedResources.TextOption_VerticalOption_Center, ClipVerticalAlignment.Bottom => PPLocalizedResources.TextOption_VerticalOption_Bottom, _ => PPLocalizedResources.TextOption_VerticalOption_Top, } };
            vAlignPicker.SelectedIndexChanged += (s, ev) =>
            {
                if (vAlignPicker.SelectedItem is string sel)
                {
                    ClipVerticalAlignment va = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_VerticalOption_Center => ClipVerticalAlignment.Center,
                        var v when v == PPLocalizedResources.TextOption_VerticalOption_Bottom => ClipVerticalAlignment.Bottom,
                        _ => ClipVerticalAlignment.Top,
                    };
                    onChanged?.Invoke(idx, e with { verticalAlignment = va });
                }
            };
            var wrapEntry = new Entry { Text = e.wrappingWidth?.ToString() ?? string.Empty, Placeholder = PPLocalizedResources.TextOption_WrapW_Hint };
            wrapEntry.Unfocused += (s, ev) =>
            {
                if (float.TryParse(wrapEntry.Text, out var w)) onChanged?.Invoke(idx, e with { wrappingWidth = w });
                else onChanged?.Invoke(idx, e with { wrappingWidth = null });
            };
            alignGrid.Add(hAlignPicker, 0, 0);
            alignGrid.Add(vAlignPicker, 1, 0);
            alignGrid.Add(new Label { Text = PPLocalizedResources.TextOption_WrapW, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 2, 0);
            alignGrid.Add(wrapEntry, 3, 0);
            advancedStack.Children.Add(alignGrid);


            // TYPOGRAPHY
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Typography));
            var verticalLayoutSwitch = new Switch { IsToggled = e.UseVerticalLayout, VerticalOptions = LayoutOptions.Center };
            var keepNonCJKHorizontalSwitch = new Switch { IsToggled = e.KeepNonCJKTextAsHorizontal, VerticalOptions = LayoutOptions.Center, IsVisible = verticalLayoutSwitch.IsToggled };
            var verticalLabel = new Label { Text = PPLocalizedResources.TextOption_UseVerticalLayout, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White };
            var nonCJKHorizentalLabel = new Label { Text = PPLocalizedResources.TextOption_KeepNonCJKHorizontal, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White, IsVisible = verticalLayoutSwitch.IsToggled };

            verticalLayoutSwitch.Toggled += (s, ev) => { keepNonCJKHorizontalSwitch.IsVisible = verticalLayoutSwitch.IsToggled; nonCJKHorizentalLabel.IsVisible = verticalLayoutSwitch.IsToggled; onChanged?.Invoke(idx, e with { UseVerticalLayout = verticalLayoutSwitch.IsToggled }); };
            keepNonCJKHorizontalSwitch.Toggled += (s, ev) => { onChanged?.Invoke(idx, e with { KeepNonCJKTextAsHorizontal = keepNonCJKHorizontalSwitch.IsToggled }); };

            var verticalLayoutGrid = new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    verticalLabel,
                    verticalLayoutSwitch,
                    nonCJKHorizentalLabel,
                    keepNonCJKHorizontalSwitch
                }
            };
            advancedStack.Children.Add(verticalLayoutGrid);

            var kerningSwitch = new Switch { IsToggled = e.applyKerning, VerticalOptions = LayoutOptions.Center };
            kerningSwitch.Toggled += (s, ev) => { onChanged?.Invoke(idx, e with { applyKerning = kerningSwitch.IsToggled }); };
            var lineSpacingEntry = new Entry { Text = e.lineSpacing.ToString() };
            lineSpacingEntry.Unfocused += (s, ev) => { if (float.TryParse(lineSpacingEntry.Text, out var ls)) onChanged?.Invoke(idx, e with { lineSpacing = ls }); };
            var typRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(80))
                },
                ColumnSpacing = 8
            };
            typRow.Add(new Label { Text = PPLocalizedResources.TextOption_Kerning, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 0, 0);
            typRow.Add(kerningSwitch, 1, 0);
            typRow.Add(new Label { Text = PPLocalizedResources.TextOption_LineSpacing, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White, HorizontalOptions = LayoutOptions.End }, 2, 0);
            typRow.Add(lineSpacingEntry, 3, 0);
            advancedStack.Children.Add(typRow);

            var rotEntry = new Entry { Text = e.rotation.ToString(), Placeholder = "0" };
            rotEntry.Unfocused += (s, ev) => { if (float.TryParse(rotEntry.Text, out var r)) onChanged?.Invoke(idx, e with { rotation = r }); };
            var dpiEntry = new Entry { Text = e.dpi?.ToString() ?? string.Empty, Placeholder = "auto" };
            dpiEntry.Unfocused += (s, ev) =>
            {
                if (float.TryParse(dpiEntry.Text, out var d)) onChanged?.Invoke(idx, e with { dpi = d });
                else onChanged?.Invoke(idx, e with { dpi = null });
            };
            var rotDpiGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 6
            };
            rotDpiGrid.Add(new Label { Text = PPLocalizedResources.TextOption_Rotation, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 0, 0);
            rotDpiGrid.Add(rotEntry, 1, 0);
            rotDpiGrid.Add(new Label { Text = "DPI", VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 2, 0);
            rotDpiGrid.Add(dpiEntry, 3, 0);
            advancedStack.Children.Add(rotDpiGrid);

            // STROKE
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Stroke));
            var strokeWidthEntry = new Entry { Text = e.strokeWidth?.ToString() ?? string.Empty, Placeholder = PPLocalizedResources.TextOption_Stroke_Hint, MinimumWidthRequest = 150 };
            strokeWidthEntry.Unfocused += (s, ev) =>
            {
                if (float.TryParse(strokeWidthEntry.Text, out var sw)) onChanged?.Invoke(idx, e with { strokeWidth = sw });
                else onChanged?.Invoke(idx, e with { strokeWidth = null });
            };
            var strokeSwatch = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Stroke = Colors.White.WithAlpha(0.12f),
                VerticalOptions = LayoutOptions.Center
            };
            try { strokeSwatch.Background = new SolidColorBrush(Color.FromRgba(e.strokeR / 65535.0, e.strokeG / 65535.0, e.strokeB / 65535.0, 1.0)); } catch { }
            var strokeEntry = new Entry { Text = $"#{((int)Math.Round(e.strokeR / 257.0)):X2}{((int)Math.Round(e.strokeG / 257.0)):X2}{((int)Math.Round(e.strokeB / 257.0)):X2}" };
            strokeEntry.Unfocused += (s, ev) =>
            {
                try
                {
                    var c = Color.FromArgb(strokeEntry.Text);
                    ushort r = (ushort)Math.Round(c.Red * 65535);
                    ushort g = (ushort)Math.Round(c.Green * 65535);
                    ushort b = (ushort)Math.Round(c.Blue * 65535);
                    var updated = e with { strokeR = r, strokeG = g, strokeB = b };
                    strokeSwatch.Background = new SolidColorBrush(c);
                    onChanged?.Invoke(idx, updated);
                }
                catch { }
            };
            var strokeGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 8
            };
            strokeGrid.Add(strokeWidthEntry, 0, 0);
            strokeGrid.Add(strokeSwatch, 1, 0);
            strokeGrid.Add(strokeEntry, 2, 0);
            advancedStack.Children.Add(strokeGrid);

            if (showAllOptions)
            {
                var subTrackSwitch = new Switch { IsToggled = e.ShouldInSubtrack, VerticalOptions = LayoutOptions.Center };
                var subTrackLabel = new Label { Text = PPLocalizedResources.TextOption_PlaceInSubtrack, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White };
                subTrackSwitch.Toggled += (s, ev) => { onChanged?.Invoke(idx, e with { ShouldInSubtrack = subTrackSwitch.IsToggled }); };
                var subTrackGrid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star)
                    },
                    ColumnSpacing = 8
                };
                subTrackGrid.Add(subTrackLabel, 0, 0);
                subTrackGrid.Add(subTrackSwitch, 1, 0);
                advancedStack.Children.Add(subTrackGrid);
            }

            // Advanced toggle button
            var advancedToggleBtn = new Button
            {
                Text = PPLocalizedResources.TextOption_Advanced_Collapsed,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#A8B8CC"),
                FontSize = 11,
                Padding = new Thickness(0, 4),
                Margin = new Thickness(0, 4, 0, 0)
            };
            advancedToggleBtn.Clicked += (s, ev) =>
            {
                advancedStack.IsVisible = !advancedStack.IsVisible;
                advancedToggleBtn.Text = advancedStack.IsVisible
                    ? PPLocalizedResources.TextOption_Advanced_Expanded
                    : PPLocalizedResources.TextOption_Advanced_Collapsed;
            };
            stack.Children.Add(advancedToggleBtn);
            stack.Children.Add(advancedStack);

            var border = new Border
            {
                Stroke = Colors.White.WithAlpha(0.10f),
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(12, 10),
                Background = new SolidColorBrush(Color.FromArgb("#0FFFFFFF")),
                Content = stack
            };
            return border;
        }


        private async Task<View> BuildTextOptionTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string providerFrom = "";
            ITextClipStyleProvider? styleProvider;
            GetAndUpdateTextClipEntries(clip, out providerFrom, out styleProvider);

            if (styleProvider == null)
            {
                List<TextClipEntry>? entries = null;
                if (clip.ExtraData != null && clip.ExtraData.TryGetValue("TextEntries", out var teObj))
                {
                    if (teObj is List<TextClipEntry> list)
                        entries = list;
                    else if (teObj is System.Text.Json.JsonElement je)
                    {
                        try { entries = System.Text.Json.JsonSerializer.Deserialize<List<TextClipEntry>>(je); }
                        catch { }
                    }
                }

                if (entries is { Count: 1 })
                {
                    var entry = entries[0];

                    static string ToArgbHex(ushort r, ushort g, ushort b, float a)
                    {
                        var ab = (byte)Math.Round(a * 255);
                        var rb = (byte)Math.Round(r / 65535.0 * 255);
                        var gb = (byte)Math.Round(g / 65535.0 * 255);
                        var bb = (byte)Math.Round(b / 65535.0 * 255);
                        return $"#{ab:X2}{rb:X2}{gb:X2}{bb:X2}";
                    }
                    static string ToArgbHexNoAlpha(ushort r, ushort g, ushort b)
                    {
                        var rb = (byte)Math.Round(r / 65535.0 * 255);
                        var gb = (byte)Math.Round(g / 65535.0 * 255);
                        var bb = (byte)Math.Round(b / 65535.0 * 255);
                        return $"#FF{rb:X2}{gb:X2}{bb:X2}";
                    }

                    var basic = new projectFrameCut.ApplicationPluginBase.Text.BasicTextStyleProvider
                    {
                        Parameters = new Dictionary<string, string>
                        {
                            ["Text"] = entry.text ?? string.Empty,
                            ["FontFamily"] = entry.fontFamily ?? "Arial",
                            ["FontSize"] = entry.fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Color"] = ToArgbHex(entry.r, entry.g, entry.b, entry.a ?? 1f),
                            ["FontStyle"] = entry.fontStyle.ToString(),
                            ["HorizontalAlignment"] = entry.horizontalAlignment.ToString(),
                            ["VerticalAlignment"] = entry.verticalAlignment.ToString(),
                            ["WrappingWidth"] = entry.wrappingWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            ["ApplyKerning"] = entry.applyKerning.ToString(),
                            ["LineSpacing"] = entry.lineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Rotation"] = entry.rotation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["StrokeWidth"] = entry.strokeWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            ["StrokeColor"] = ToArgbHexNoAlpha(entry.strokeR, entry.strokeG, entry.strokeB),
                            ["Dpi"] = entry.dpi?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            ["UseVerticalLayout"] = entry.UseVerticalLayout.ToString(),
                            ["KeepNonCJKTextAsHorizontal"] = entry.KeepNonCJKTextAsHorizontal.ToString(),
                            ["LayoutMode"] = "FillClip"
                        }
                    };
                    styleProvider = basic;
                }
                else
                {
                    var fallback = new projectFrameCut.ApplicationPluginBase.Text.OldTextClipEntryTextStyleProvider();
                    if (clip.ExtraData != null && clip.ExtraData.TryGetValue("TextEntries", out var teObj2))
                    {
                        try
                        {
                            if (teObj2 is System.Text.Json.JsonElement je)
                            {
                                fallback.Parameters["TextEntriesJson"] = je.GetRawText();
                            }
                            else
                            {
                                fallback.Parameters["TextEntriesJson"] = System.Text.Json.JsonSerializer.Serialize(teObj2);
                            }
                        }
                        catch { }
                    }

                    styleProvider = fallback;
                }
            }

            var providerPpb = styleProvider.BuildPropertyPanel();
            var providerHost = new PropertyPanelBuilder();
            var fontItems = TextServices.LoadedFonts
                            .Select(x => x.Value)
                            .GroupBy(c => TextHelper.DetectTextLanguage(c.DisplayName))
                            .OrderByDescending(g => g.Count())
                            .SelectMany(g => g)
                            .ToList();
            providerHost.AddText(new SingleLineLabel(styleProvider.TypeName, 18, FontAttributes.Bold));
            providerHost.AddSeparator();
            //providerPpb.UseDialogFontPicker(
            //    page,
            //    "FontFamily",
            //   ,
            //    styleProvider.Parameters.TryGetValue("FontFamily", out var selectedFontName) ? selectedFontName : null,
            //   ,
            //    ,
            //    PPLocalizedResources.TextOption_Font);

            // Add LayoutMode picker managed centrally via ITextClipStyleProvider.LayoutMode
            Dictionary<string, TextClipLayoutMode> LocalizedLayoutOptionKVP = new Dictionary<string, TextClipLayoutMode>
            {
                { PPLocalizedResources.TextOption_LayoutMode_FixedWidth, TextClipLayoutMode.FixedWidth },
                { PPLocalizedResources.TextOption_LayoutMode_FillClip, TextClipLayoutMode.FillClip },
                { PPLocalizedResources.TextOption_LayoutMode_FixedSize, TextClipLayoutMode.FixedSize },
            }; //FixedHeight mode is buggy so hide now
            providerHost
            .AppendWhen(styleProvider.ShowLayoutModePicker,
                c => c.AddPicker("LayoutMode", PPLocalizedResources.TextOption_LayoutMode, LocalizedLayoutOptionKVP.Keys.ToArray(), LocalizedLayoutOptionKVP.ReverseLookup(styleProvider.LayoutMode, PPLocalizedResources.TextOption_LayoutMode_FixedWidth), picker =>
                {
#if iDevices
                    picker.Closed += (s, e) =>
                    {
                        if (picker.SelectedItem is string modeStr && !string.IsNullOrWhiteSpace(modeStr))
                        {
                            if (LocalizedLayoutOptionKVP.TryGetValue(modeStr, out var parsedMode))
                                styleProvider.LayoutMode = parsedMode;
                            HandlePanelChange(styleProvider, new PropertyPanelPropertyChangedEventArgs("LayoutMode", modeStr, styleProvider.Parameters.TryGetValue("LayoutMode", out var m) ? m : "FillClip"));
                        }
                    };
#else
                    picker.SelectedIndexChanged += (s, e) =>
                    {
                        if (picker.SelectedItem is string modeStr && !string.IsNullOrWhiteSpace(modeStr))
                        {
                            if (LocalizedLayoutOptionKVP.TryGetValue(modeStr, out var parsedMode))
                                styleProvider.LayoutMode = parsedMode;
                            HandlePanelChange(styleProvider, new PropertyPanelPropertyChangedEventArgs("LayoutMode", modeStr, styleProvider.Parameters.TryGetValue("LayoutMode", out var m) ? m : "FillClip"));
                        }
                    };
#endif
                })
            )
            .AppendWhen(styleProvider.ShowDefaultTextEditor,
                c => c.AddCustomChild(
                (c) =>
                {
                    var editor = new Editor
                    {
                        MinimumHeightRequest = 150,
                        Text = styleProvider.BasicText,
                        IsSpellCheckEnabled = true,
                        IsTextPredictionEnabled = true,
                        Placeholder = PPLocalizedResources.TextOption_Content_Placeholder
                    };
                    editor.Unfocused += (_, _) => c(editor.Text);
                    return editor;
                },
                "Text", styleProvider.BasicText)
                .AddButton(Localized._Apply, (_, _) => { })
            )
            .AppendWhen(styleProvider.ShowFontPicker,
                c => c.AddDialogFontPicker(
                "FontFamily",
                PPLocalizedResources.TextOption_Font,
                PPLocalizedResources.TextOption_Font,
                styleProvider.Parameters.TryGetValue("FontFamily", out var selectedFontName) ? selectedFontName : null,
                fontItems,
                page,
                font =>
                {
                    if (font.InnerFont is not null)
                        TextClipFontRegistry.RegisterFontFace(font.InnerFont);
                    else if (!string.IsNullOrWhiteSpace(font.Path))
                        TextClipFontRegistry.AddFont(font.Path);

                    HandlePanelChange(styleProvider, new PropertyPanelPropertyChangedEventArgs("FontFamily", font.FontName, styleProvider.Parameters.TryGetValue("FontFamily", out var f) ? f : string.Empty));
                },
                TextServices.RenderFontPreviewAsync)
            )
            .AddFromAnother(providerPpb, styleProvider)
            .AddSeparator()
            .AddButton(PPLocalizedResources.TextOption_ChangeStyle, async (_, _) =>
            {
                var available = TextStyleServices.GetAvailableTextStyleProviders();
                if (available.Count == 0) return;

                var styleNames = available.Keys.ToArray();
                var currentStyle = styleProvider.TypeName;
                var picked = await page.DisplayActionSheetAsync(
                    PPLocalizedResources.TextOption_ChangeStyle, null, null, styleNames);

                if (string.IsNullOrWhiteSpace(picked) || picked == currentStyle) return;
                if (!available.TryGetValue(picked, out var factory)) return;

                var newProvider = factory();
                // 保留基础文本
                newProvider.BasicText = styleProvider.BasicText;
                // 保留布局模式和换行宽度
                newProvider.Parameters["LayoutMode"] = styleProvider.LayoutMode.ToString();
                if (styleProvider.Parameters.TryGetValue("WrappingWidth", out var ww) && !string.IsNullOrWhiteSpace(ww))
                    newProvider.Parameters["WrappingWidth"] = ww;
                if (styleProvider.Parameters.TryGetValue("TextStyleManualSize", out var ms) && !string.IsNullOrWhiteSpace(ms))
                    newProvider.Parameters["TextStyleManualSize"] = ms;

                // 测量新样式的自然尺寸
                var newEntries = newProvider.BuildEntries();
                var newRect = TextServices.MeasureBounds(newEntries, 1920, 1080);
                var newW = Math.Max(1, (int)Math.Ceiling(newRect.Width));
                var newH = Math.Max(1, (int)Math.Ceiling(newRect.Height));

                // 持久化新样式到 clip
                clip.ExtraData ??= new();
                clip.ExtraData[TextStyleProviderFromKey] = newProvider.FromPlugin;
                clip.ExtraData[TextStyleProviderTypeKey] = newProvider.TypeName;
                clip.ExtraData[TextStyleProviderParamsKey] = new Dictionary<string, string>(newProvider.Parameters);
                if (newEntries.Length > 0)
                    clip.ExtraData["TextEntries"] = newEntries.ToList();

                clip.TargetWidth = newW;
                clip.TargetHeight = newH;
                clip.IsMoveable = true;
                clip.IsHorizontalResizable = newProvider.IsHorizontalResizable;
                clip.IsVerticalResizable = newProvider.IsVerticalResizable;
                clip.CanSnapWhileResizing = newProvider.CanSnapWhileResizing;

                handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextStyleChanged", newProvider, newProvider));
            })
            .AddButton(PPLocalizedResources.TextOption_ReLayout, (_, _) =>
            {
                // 清除所有布局约束，强制重新测量文本的自然尺寸
                styleProvider.Parameters.Remove("WrappingWidth");
                styleProvider.Parameters.Remove("TextStyleManualSize");
                styleProvider.Parameters.Remove("FixedHeightValue");
                styleProvider.LayoutMode = TextClipLayoutMode.FillClip;

                // 重新测量文本的自然宽高
                var resetEntries = styleProvider.BuildEntries();
                var resetRect = TextServices.MeasureBounds(resetEntries, 1920, 1080);
                var resetW = Math.Max(1, (int)Math.Ceiling(resetRect.Width));
                var resetH = Math.Max(1, (int)Math.Ceiling(resetRect.Height));

                clip.TargetWidth = resetW;
                clip.TargetHeight = resetH;
                clip.IsMoveable = true;
                clip.IsHorizontalResizable = styleProvider.IsHorizontalResizable;
                clip.IsVerticalResizable = styleProvider.IsVerticalResizable;
                clip.CanSnapWhileResizing = styleProvider.CanSnapWhileResizing;

                clip.ExtraData ??= new();
                // 持久化更新后的参数
                clip.ExtraData[TextStyleProviderParamsKey] = new Dictionary<string, string>(styleProvider.Parameters);

                if (resetEntries.Length > 0)
                {
                    clip.ExtraData["TextEntries"] = resetEntries.ToList();
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", resetEntries, resetEntries));
                }
            }, (c) => c.TextColor = Color.FromArgb("#FF8080"));

            void HandlePanelChange(object? s, PropertyPanelPropertyChangedEventArgs e)
            {
                if (s is not ITextClipStyleProvider provider) return;

                // 字体变更时，确保 FontFace 已在 TextClipFontRegistry 中注册，
                // 避免 TextLayoutPipeline.ResolveFont 回退到 fallback 字体导致测量异常。
                if (e.Id == "FontFamily")
                {
                    var fontName = e.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(fontName) && !TextClipFontRegistry.TryGetFont(fontName, out _))
                    {
                        if (TextServices.LoadedFonts.TryGetValue(fontName, out var fontItem))
                        {
                            if (fontItem.InnerFont is not null)
                                TextClipFontRegistry.RegisterFontFace(fontItem.InnerFont);
                            else if (!string.IsNullOrWhiteSpace(fontItem.Path))
                                TextClipFontRegistry.AddFont(fontItem.Path);
                        }
                    }
                }

                // Text changes are managed centrally by ClipInfoBuilder rather than
                // each individual style provider.
                if (e.Id == "Text")
                {
                    provider.BasicText = e.Value?.ToString() ?? string.Empty;
                    provider.Parameters["Text"] = provider.BasicText;
                }

                (var updated, var newW, var newH) = provider.HandlePropertyPanelChange(e);
                if (updated != null)
                {
                    provider.Parameters = updated;
                    clip.ExtraData[TextStyleProviderParamsKey] = updated;
                }
                clip.TargetWidth = newW > 0 ? newW : clip.TargetWidth;
                clip.TargetHeight = newH > 0 ? newH : clip.TargetHeight;
                clip.IsMoveable = true;
                clip.IsHorizontalResizable = provider.IsHorizontalResizable;
                clip.IsVerticalResizable = provider.IsVerticalResizable;
                clip.CanSnapWhileResizing = provider.CanSnapWhileResizing;
                var updatedEntries = provider.BuildEntries();
                if (updatedEntries.Length > 0)
                {
                    clip.ExtraData["TextEntries"] = updatedEntries.ToList();
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", updatedEntries, updatedEntries));
                }
            }
            ;
            providerHost.PropertyChanged += (_, e) => HandlePanelChange(styleProvider, e);

            return providerHost.BuildWithScrollView();
        }

        private static void GetAndUpdateTextClipEntries(ClipElementUI clip, out string? providerFrom, out ITextClipStyleProvider? styleProvider)
        {
            clip.ExtraData ??= new Dictionary<string, object>();


            if (clip.ExtraData.TryGetValue("TextEntries", out var entriesObj) && entriesObj is JsonElement je)
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize<List<TextClipEntry>>(je);
                    if (deserialized is { Count: > 0 })
                        clip.ExtraData["TextEntries"] = TextEntryMigration.MigrateFromTextClipEntries(deserialized);
                }
                catch { }
            }

            string? ReadStringValue(object? raw)
            {
                if (raw is string s) return s;
                if (raw is JsonElement elem && elem.ValueKind == JsonValueKind.String) return elem.GetString();
                return raw?.ToString();
            }

            Dictionary<string, string>? ReadParameters(object? raw)
            {
                if (raw is Dictionary<string, string> dict) return new Dictionary<string, string>(dict);
                if (raw is Dictionary<string, object> objDict)
                    return objDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty);
                if (raw is JsonElement elem)
                {
                    try { return JsonSerializer.Deserialize<Dictionary<string, string>>(elem); }
                    catch { return null; }
                }
                if (raw is string json && !string.IsNullOrWhiteSpace(json))
                {
                    try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
                    catch { return null; }
                }
                return null;
            }

            providerFrom = clip.ExtraData.TryGetValue(TextStyleProviderFromKey, out var providerFromObj) ? ReadStringValue(providerFromObj) : null;
            var providerType = clip.ExtraData.TryGetValue(TextStyleProviderTypeKey, out var providerTypeObj) ? ReadStringValue(providerTypeObj) : null;
            var providerParameters = clip.ExtraData.TryGetValue(TextStyleProviderParamsKey, out var providerParamsObj) ? ReadParameters(providerParamsObj) : null;

            styleProvider = null;
            if (!string.IsNullOrWhiteSpace(providerFrom) && !string.IsNullOrWhiteSpace(providerType))
            {
                styleProvider = TextStyleServices.RestoreTextStyleProvider(providerFrom, providerType, providerParameters);
                if (styleProvider != null && providerParameters != null)
                {
                    styleProvider.Parameters = new Dictionary<string, string>(providerParameters);
                }

                var rebuiltEntries = styleProvider?.BuildEntries();
                if (rebuiltEntries is { Length: > 0 })
                {
                    clip.ExtraData["TextEntries"] = rebuiltEntries.ToList();
                    //handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", rebuiltEntries, rebuiltEntries));
                }

                if (styleProvider != null)
                {
                    clip.IsMoveable = true;
                    clip.IsHorizontalResizable = styleProvider.IsHorizontalResizable;
                    clip.IsVerticalResizable = styleProvider.IsVerticalResizable;
                    clip.CanSnapWhileResizing = styleProvider.CanSnapWhileResizing;
                }
            }
        }

        private View BuildTextOptionClassicTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.ExtraData ??= new Dictionary<string, object>();

            List<TextClipEntry>? entries = null;
            if (clip.ExtraData.TryGetValue("TextEntries", out var entriesObj))
            {
                if (entriesObj is List<TextClipEntry> list)
                    entries = list;
                else if (entriesObj is JsonElement je)
                {
                    try { entries = JsonSerializer.Deserialize<List<TextClipEntry>>(je); }
                    catch { entries = null; }
                }
            }

            if (entries == null)
            {
                entries = new List<TextClipEntry>
                {
                    new TextClipEntry
                    {
                        text = "",
                        x = 0,
                        y = 0,
                        fontFamily = TextClipFontRegistry.GetAllFonts().FirstOrDefault()?.UniqueName ?? TextClipFontRegistry.FallbackFamilyName ?? "Arial",
                        fontSize = 24f,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 1f
                    }
                };
                clip.ExtraData["TextEntries"] = entries;
            }

            var fonts = TextServices.LoadedFonts.Select(c => c.Value);
            var entriesContainer = new VerticalStackLayout { Spacing = 8 };

            entriesContainer.Add(new Label { Text = PPLocalizedResources.TextOption_TabTitle_Classic_Warn, TextColor = Colors.Yellow });

            void UpdateStoredEntries()
            {
                clip.ExtraData["TextEntries"] = entries;
                handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", entries, entries));
            }

            void RebuildEntriesUI()
            {
                entriesContainer.Children.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    int idx = i;
                    var e = entries[idx];
                    var view = BuildTextEntryUI(e, idx, fonts,
                        (id, newE) => { entries[id] = newE; UpdateStoredEntries(); },
                        (id) => { entries.RemoveAt(id); UpdateStoredEntries(); RebuildEntriesUI(); },
                        entries.Count > 1,
                        false,
                        (pickerView) =>
                        {
                            page.Dispatcher.Dispatch(async () =>
                            {
                                await page.ShowAPopup(pickerView, mode: "dialog");
                            });
                        },
                        () =>
                        {
                            page.Dispatcher.Dispatch(async () =>
                            {
                                await page.HidePopup();
                            });
                        });
                    entriesContainer.Children.Add(view);
                }
            }

            RebuildEntriesUI();

            var addBtn = new Button
            {
                Text = PPLocalizedResources.TextOption_AddAEntry,
                HorizontalOptions = LayoutOptions.Fill,
                CornerRadius = 8,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 4, 0, 0)
            };
            addBtn.Clicked += async (s, e) =>
            {
                Dictionary<string, TextClipEntry> t = new();
                Setting.SettingPages.EditSettingPage.LoadTextTemplates(ref t);
                var picked = await page.DisplayActionSheetAsync(PPLocalizedResources.TextOption_AddAEntry, null, null, t.Keys.ToArray());
                if (string.IsNullOrWhiteSpace(picked)) return;
                if (!t.TryGetValue(picked, out var value))
                {
                    value = new TextClipEntry
                    {
                        text = "",
                        x = 0,
                        y = 0,
                        fontFamily = TextClipFontRegistry.FallbackFamilyName ?? "Arial",
                        fontSize = 24f,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 1f
                    };
                }
                entries.Add(value);
                UpdateStoredEntries();
                RebuildEntriesUI();
            };

            entriesContainer.Children.Add(addBtn);

            var grid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                },
                Padding = 8
            };

            var scroll = new ScrollView
            {
                Content = entriesContainer,
                VerticalOptions = LayoutOptions.Start
            };
            grid.Add(scroll, 0, 1);

            return new ScrollView { Content = grid };
        }
        #endregion
    }
}
