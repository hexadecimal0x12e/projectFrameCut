using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.AI;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platform;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Controls;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using DataTemplate = Microsoft.Maui.Controls.DataTemplate;
using Environment = System.Environment;
using GridLength = Microsoft.Maui.GridLength;
using GridUnitType = Microsoft.Maui.GridUnitType;
using Switch = Microsoft.Maui.Controls.Switch;
using Thickness = Microsoft.Maui.Thickness;







#if WINDOWS
using Microsoft.UI.Xaml;

#endif

#if IOS
using projectFrameCut.Platforms.iOS;

#endif

namespace projectFrameCut.DraftStuff
{
    public class ClipInfoBuilder
    {
        #region init
        DraftPage page;

        static JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        /// <summary>
        /// Gets the default color hex string based on clip type.
        /// </summary>
        private static string GetDefaultColorHex(projectFrameCut.Shared.ClipMode clipType)
        {
            return clipType switch
            {
                projectFrameCut.Shared.ClipMode.VideoClip => Colors.CornflowerBlue.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.PhotoClip => Colors.MediumSeaGreen.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.AudioClip => Colors.Goldenrod.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.SubtitleClip => Colors.SlateGray.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.SolidColorClip => Colors.OrangeRed.ToArgbHex(),
                _ => Colors.Gray.ToArgbHex(),
            };
        }


        public ClipInfoBuilder(DraftPage page)
        {
            this.page = page;
            PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
        }


        public async Task<View> Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            TabbedView t = new();
            t.Background = page.Background;
            t.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.MainSettingsPage_Tab_General,
                Content = BuildGeneralTab(clip, handler)
            });
            if (clip.ClipType == ClipMode.TextClip || clip.ClipType == ClipMode.SubtitleClip)
            {
                t.TabItems.Add(new TabbedViewItem
                {
                    Header =  PPLocalizedResources.TextOption_TabTitle,
                    Content = await BuildTextOptionTab(clip, handler)
                });
            }
            t.TabItems.Add(new TabbedViewItem
            {
                Header = PPLocalizedResources.Tabs_Effect,
                Content = await BuildEffectTab(clip, handler)
            });
            if (SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"))
            {
                t.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Effect_Classic,
                    Content = BuildClassicEffectTab(clip, handler)
                });
            }

            t.TabItems.Add(new TabbedViewItem
            {
                Header = PPLocalizedResources.Tabs_SpeedRatio,
                Content = BuildSpeedAndRatioTab(clip, handler)
            });
            return t;
        }

        #endregion

        #region general

        public View BuildGeneralTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string currentColorHex = clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);

            int valX = 0, valY = 0;
            int valW = page.ProjectInfo.RelativeWidth;
            int valH = page.ProjectInfo.RelativeHeight;

            if (clip.Effects != null)
            {
                if (clip.Effects.TryGetValue("__Internal_Place__", out var e) && e is PlaceEffect_ImageSharp p)
                {
                    valX = p.StartX;
                    valY = p.StartY;
                    if (p.RelativeWidth > 0 && p.RelativeWidth != page.ProjectInfo.RelativeWidth)
                    {
                        valX = (int)(p.StartX * ((double)page.ProjectInfo.RelativeWidth / p.RelativeWidth));
                        valY = (int)(p.StartY * ((double)page.ProjectInfo.RelativeHeight / p.RelativeHeight));
                    }
                }
                if (clip.Effects.TryGetValue("__Internal_Resize__", out var e2) && e2 is ResizeEffect_ImageSharp r)
                {
                    valW = r.Width;
                    valH = r.Height;
                    if (r.RelativeWidth > 0 && r.RelativeWidth != page.ProjectInfo.RelativeWidth)
                    {
                        valW = (int)(r.Width * ((double)page.ProjectInfo.RelativeWidth / r.RelativeWidth));
                        valH = (int)(r.Height * ((double)page.ProjectInfo.RelativeHeight / r.RelativeHeight));
                    }
                }
            }

            var ppb = new PropertyPanelBuilder()
            .AddText(new SingleLineLabel(Localized.PropertyPanel_General, 20))
            .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.DisplayName, clip.DisplayName)
            .AddCustomChild(PPLocalizedResources.General_DisplayColor, (invoker) =>
            {
                var colorPreview = new BoxView
                {
                    WidthRequest = 30,
                    HeightRequest = 30,
                    CornerRadius = 5,
                    Color = Color.FromArgb(currentColorHex),
                    VerticalOptions = LayoutOptions.Center
                };

                var colorEntry = new Entry
                {
                    Text = currentColorHex,
                    Placeholder = "#RRGGBB",
                    WidthRequest = 100,
                    VerticalOptions = LayoutOptions.Center
                };

                colorEntry.TextChanged += (s, e) =>
                {
                    try
                    {
                        var color = Color.FromArgb(e.NewTextValue);
                        colorPreview.Color = color;
                    }
                    catch { }
                };

                colorEntry.Unfocused += (s, e) =>
                {
                    invoker(colorEntry.Text);
                };

                var resetButton = new Button
                {
                    Text = "↺",
                    WidthRequest = 40,
                    HeightRequest = 35,
                    Padding = 0,
                    VerticalOptions = LayoutOptions.Center
                };
                resetButton.Clicked += (s, e) =>
                {
                    var defaultColor = GetDefaultColorHex(clip.ClipType);
                    colorEntry.Text = defaultColor;
                    colorPreview.Color = Color.FromArgb(defaultColor);
                    invoker(null!); // Reset to default
                };

                var layout = new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { colorPreview, colorEntry, resetButton }
                };

                return layout;
            }, "clipColor", currentColorHex)
            .AddSeparator(null)
            .AppendWhen(clip.ClipType != ClipMode.TextClip && clip.ClipType != ClipMode.SubtitleClip,
            (c) => c.AddText(new SingleLineLabel(PPLocalizedResources.General_LocationAndSize, 20))
                    .AddEntry("placeX", PPLocalizedResources.General_LocationX, valX.ToString(), "0", null, default)
                    .AddEntry("placeY", PPLocalizedResources.General_LocationY, valY.ToString(), "0", null, default)
                    .AddEntry("resizeW", PPLocalizedResources._Width, valW.ToString(), page.ProjectInfo.RelativeWidth.ToString(), null, default)
                    .AddEntry("resizeH", PPLocalizedResources._Height, valH.ToString(), page.ProjectInfo.RelativeHeight.ToString(), null, default));
            


            ppb.PropertyChanged += async (s, e) =>
            {
                if (e.Id == "clipColor")
                {
                    if (e.Value == null || string.IsNullOrWhiteSpace(e.Value?.ToString()))
                    {
                        clip.ClipColor = null; // Reset to default
                    }
                    else
                    {
                        clip.ClipColor = e.Value?.ToString();
                    }
                    clip.ApplyClipColor();
                    handler?.Invoke(s, e);
                    return;
                }
                if (e.Id.StartsWith("place") || e.Id.StartsWith("resize"))
                {
                    clip.Effects ??= new Dictionary<string, IEffect>();

                    if (e.Id.StartsWith("place"))
                    {
                        // Get current values (normalized to current project resolution) from UI or Effect
                        int currentX = 0, currentY = 0;

                        PlaceEffect_ImageSharp? existingP = null;
                        if (clip.Effects.TryGetValue("__Internal_Place__", out var eff) && eff is PlaceEffect_ImageSharp pe)
                        {
                            existingP = pe;
                            currentX = pe.StartX;
                            currentY = pe.StartY;
                            if (pe.RelativeWidth > 0 && pe.RelativeWidth != page.ProjectInfo.RelativeWidth)
                            {
                                currentX = (int)(pe.StartX * ((double)page.ProjectInfo.RelativeWidth / pe.RelativeWidth));
                                currentY = (int)(pe.StartY * ((double)page.ProjectInfo.RelativeHeight / pe.RelativeHeight));
                            }
                        }

                        if (e.Id == "placeX" && int.TryParse(e.Value?.ToString(), out var vx)) currentX = vx;
                        else if (ppb.Properties.TryGetValue("placeX", out var uiX) && int.TryParse(uiX.ToString(), out var uiXInt)) currentX = uiXInt;

                        if (e.Id == "placeY" && int.TryParse(e.Value?.ToString(), out var vy)) currentY = vy;
                        else if (ppb.Properties.TryGetValue("placeY", out var uiY) && int.TryParse(uiY.ToString(), out var uiYInt)) currentY = uiYInt;

                        var newP = new PlaceEffect_ImageSharp
                        {
                            StartX = currentX,
                            StartY = currentY,
                            RelativeWidth = page.ProjectInfo.RelativeWidth,
                            RelativeHeight = page.ProjectInfo.RelativeHeight,
                            Enabled = existingP?.Enabled ?? true,
                            Name = existingP?.Name ?? "__Internal_Place__",
                            Index = existingP?.Index ?? (int.MaxValue - 100)
                        };
                        clip.Effects["__Internal_Place__"] = newP;
                    }
                    else if (e.Id.StartsWith("resize"))
                    {
                        int currentW = page.ProjectInfo.RelativeWidth, currentH = page.ProjectInfo.RelativeHeight;
                        ResizeEffect_ImageSharp? existingR = null;

                        if (clip.Effects.TryGetValue("__Internal_Resize__", out var eff) && eff is ResizeEffect_ImageSharp re)
                        {
                            existingR = re;
                            currentW = re.Width;
                            currentH = re.Height;
                            if (re.RelativeWidth > 0 && re.RelativeWidth != page.ProjectInfo.RelativeWidth)
                            {
                                currentW = (int)(re.Width * ((double)page.ProjectInfo.RelativeWidth / re.RelativeWidth));
                                currentH = (int)(re.Height * ((double)page.ProjectInfo.RelativeHeight / re.RelativeHeight));
                            }
                        }

                        if (e.Id == "resizeW" && int.TryParse(e.Value?.ToString(), out var vw)) currentW = vw;
                        else if (ppb.Properties.TryGetValue("resizeW", out var uiW) && int.TryParse(uiW.ToString(), out var uiWInt)) currentW = uiWInt;

                        if (e.Id == "resizeH" && int.TryParse(e.Value?.ToString(), out var vh)) currentH = vh;
                        else if (ppb.Properties.TryGetValue("resizeH", out var uiH) && int.TryParse(uiH.ToString(), out var uiHInt)) currentH = uiHInt;

                        var newR = new ResizeEffect_ImageSharp
                        {
                            Width = currentW,
                            Height = currentH,
                            RelativeWidth = page.ProjectInfo.RelativeWidth,
                            RelativeHeight = page.ProjectInfo.RelativeHeight,
                            Enabled = existingR?.Enabled ?? true,
                            Name = existingR?.Name ?? "__Internal_Resize__",
                            Index = existingR?.Index ?? (int.MinValue + 50),
                            PreserveAspectRatio = existingR?.PreserveAspectRatio ?? false
                        };
                        clip.Effects["__Internal_Resize__"] = newR;
                    }

                    handler?.Invoke(s, e);
                    return;
                }

                if (e.Id == "clipColor")
                {
                    if (e.Value == null || string.IsNullOrWhiteSpace(e.Value?.ToString()))
                    {
                        clip.ClipColor = null; // Reset to default
                    }
                    else
                    {
                        clip.ClipColor = e.Value?.ToString();
                    }
                    clip.ApplyClipColor();
                    handler?.Invoke(s, e);
                    return;
                }

                switch (e.Id)
                {
                    case "displayName":
                        clip.DisplayName = e.Value?.ToString() ?? clip.DisplayName;
                        break;
                    case "speedRatio":
                        {
                            if (e.Value is double ratio || double.TryParse(e.Value as string, out ratio))
                            {
                                if (ratio != 0f)
                                    clip.SecondPerFrameRatio = (float)ratio;
                            }

                            break;
                        }
                    default:
                        {

                            break;
                        }
                }

                handler?.Invoke(s, e);
            };
            return ppb.BuildWithScrollView();
        }

        #endregion

        #region text

        public static View BuildTextEntryUI(projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry e, int idx, string[] fonts,
            Action<int, projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry> onChanged,
            Action<int> onRemove,
            bool canDeleteEntry = true)
        {
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
                Text = $"Entry {idx + 1}",
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
                Placeholder = "Enter text…"
            };
            editor.Unfocused += (s, ev) => { onChanged?.Invoke(idx, e with { text = editor.Text }); };
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
            var fontPicker = new Picker { Title = PPLocalizedResources.TextOption_Font, ItemsSource = fonts, SelectedItem = fonts.Contains(e.fontFamily) ? e.fontFamily : fonts.FirstOrDefault() };
            fontPicker.SelectedIndexChanged += (s, ev) => 
            {
                if (fontPicker.SelectedItem is string sf) onChanged?.Invoke(idx, e with { fontFamily = sf });
            };
            var sizeEntry = new Entry { Text = e.fontSize.ToString(), Placeholder = "24" };
            sizeEntry.Unfocused += (s, ev) => { if (float.TryParse(sizeEntry.Text, out var ns)) onChanged?.Invoke(idx, e with { fontSize = ns }); };
            fontGrid.Add(fontPicker, 0, 0);
            fontGrid.Add(new Label { Text = PPLocalizedResources.TextOption_Size, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 1, 0);
            fontGrid.Add(sizeEntry, 2, 0);
            stack.Children.Add(fontGrid);

            var stylePicker = new Picker { Title = PPLocalizedResources.TextOption_Style, ItemsSource = new[] { PPLocalizedResources.TextOption_Style_Regular, PPLocalizedResources.TextOption_Style_Bold, PPLocalizedResources.TextOption_Style_Italic, PPLocalizedResources.TextOption_Style_BoldItalic }, SelectedItem = e.fontStyle switch { SixLabors.Fonts.FontStyle.Regular => PPLocalizedResources.TextOption_Style_Regular, SixLabors.Fonts.FontStyle.Bold => PPLocalizedResources.TextOption_Style_Bold, SixLabors.Fonts.FontStyle.Italic => PPLocalizedResources.TextOption_Style_Italic, SixLabors.Fonts.FontStyle.BoldItalic => PPLocalizedResources.TextOption_Style_BoldItalic, _ => PPLocalizedResources.TextOption_Style_Regular, } };
            stylePicker.SelectedIndexChanged += (s, ev) =>
            {
                if (stylePicker.SelectedItem is string sel)
                {
                    var fs = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_Style_Bold => SixLabors.Fonts.FontStyle.Bold,
                        var v when v == PPLocalizedResources.TextOption_Style_Italic => SixLabors.Fonts.FontStyle.Italic,
                        var v when v == PPLocalizedResources.TextOption_Style_BoldItalic => SixLabors.Fonts.FontStyle.BoldItalic,
                        _ => SixLabors.Fonts.FontStyle.Regular,
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

            // ALIGNMENT
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Alignment));
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
            var hAlignPicker = new Picker { Title = PPLocalizedResources.TextOption_HorizonOption, ItemsSource = new[] { PPLocalizedResources.TextOption_HorizonOption_Left, PPLocalizedResources.TextOption_HorizonOption_Center, PPLocalizedResources.TextOption_HorizonOption_Right }, SelectedItem = e.horizontalAlignment switch { SixLabors.Fonts.HorizontalAlignment.Left => PPLocalizedResources.TextOption_HorizonOption_Left, SixLabors.Fonts.HorizontalAlignment.Center => PPLocalizedResources.TextOption_HorizonOption_Center, SixLabors.Fonts.HorizontalAlignment.Right => PPLocalizedResources.TextOption_HorizonOption_Right, _ => PPLocalizedResources.TextOption_HorizonOption_Left, } };
            hAlignPicker.SelectedIndexChanged += (s, ev) =>
            {
                if (hAlignPicker.SelectedItem is string sel)
                {
                    SixLabors.Fonts.HorizontalAlignment ha = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_HorizonOption_Center => SixLabors.Fonts.HorizontalAlignment.Center,
                        var v when v == PPLocalizedResources.TextOption_HorizonOption_Right => SixLabors.Fonts.HorizontalAlignment.Right,
                        _ => SixLabors.Fonts.HorizontalAlignment.Left,
                    };
                    onChanged?.Invoke(idx, e with { horizontalAlignment = ha });
                }
            };
            var vAlignPicker = new Picker { Title = PPLocalizedResources.TextOption_VerticalOption, ItemsSource = new[] { PPLocalizedResources.TextOption_VerticalOption_Top, PPLocalizedResources.TextOption_VerticalOption_Center, PPLocalizedResources.TextOption_VerticalOption_Bottom }, SelectedItem = e.verticalAlignment switch { SixLabors.Fonts.VerticalAlignment.Top => PPLocalizedResources.TextOption_VerticalOption_Top, SixLabors.Fonts.VerticalAlignment.Center => PPLocalizedResources.TextOption_VerticalOption_Center, SixLabors.Fonts.VerticalAlignment.Bottom => PPLocalizedResources.TextOption_VerticalOption_Bottom, _ => PPLocalizedResources.TextOption_VerticalOption_Top, } };
            vAlignPicker.SelectedIndexChanged += (s, ev) =>
            {
                if (vAlignPicker.SelectedItem is string sel)
                {
                    SixLabors.Fonts.VerticalAlignment va = sel switch
                    {
                       var v when v == PPLocalizedResources.TextOption_VerticalOption_Center => SixLabors.Fonts.VerticalAlignment.Center,
                        var v when v == PPLocalizedResources.TextOption_VerticalOption_Bottom => SixLabors.Fonts.VerticalAlignment.Bottom,
                        _ => SixLabors.Fonts.VerticalAlignment.Top,
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
            stack.Children.Add(alignGrid);

            // TYPOGRAPHY
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Typography));
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
            stack.Children.Add(typRow);

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
            stack.Children.Add(rotDpiGrid);

            // STROKE
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Stroke));
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
            stack.Children.Add(strokeGrid);

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
            // Ensure ExtraData exists
            clip.ExtraData ??= new Dictionary<string, object>();

            // Load or normalize TextEntries from ExtraData
            List<projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry>? entries = null;
            if (clip.ExtraData.TryGetValue("TextEntries", out var entriesObj))
            {
                if (entriesObj is List<projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry> list)
                {
                    entries = list;
                }
                else if (entriesObj is JsonElement je)
                {
                    try { entries = JsonSerializer.Deserialize<List<projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry>>(je); }
                    catch { entries = null; }
                }
            }

            if (entries == null)
            {
                entries = new List<projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry>
                {
                    new projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry
                    {
                        text = "",
                        x = 0,
                        y = 0,
                        fontFamily = projectFrameCut.Render.ClipsAndTracks.TextClip.GetFont().Families.FirstOrDefault().Name ?? "Arial",
                        fontSize = 24f,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 1f
                    }
                };
                clip.ExtraData["TextEntries"] = entries;
            }

            var fonts = projectFrameCut.Render.ClipsAndTracks.TextClip.GetFont().Families.Select(f => f.Name).ToArray();

            var entriesContainer = new VerticalStackLayout { Spacing = 8 };

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
                        (id) => { entries.RemoveAt(id); UpdateStoredEntries(); RebuildEntriesUI(); });
                    entriesContainer.Children.Add(view);
                }
            }

            RebuildEntriesUI();

            // Add/Insert controls
            var addBtn = new Button
            {
                Text = PPLocalizedResources.TextOption_AddAEntry,
                HorizontalOptions = LayoutOptions.Fill,
                CornerRadius = 8,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 4, 0, 0)
            };
            addBtn.Clicked += (s, e) =>
            {
                var defFont = fonts.FirstOrDefault() ?? "Arial";
                entries.Add(new projectFrameCut.Render.ClipsAndTracks.TextClip.TextClipEntry
                {
                    text = "",
                    x = 0,
                    y = 0,
                    fontFamily = defFont,
                    fontSize = 24f,
                    r = 65535,
                    g = 65535,
                    b = 65535,
                    a = 1f
                });
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

            return grid;
        }
        #endregion

        #region effect
        public static void RebuildAllEffects(ClipElementUI clip, bool diag = false)
        {
            var newEffects = clip.Effects ?? new();
            int globalIndex = 0;
            var factories = EffectServices.GetAvailableEffectBundles();
            if (clip.EffectBundles != null)
            {
                var sortedBundles = SortEffectBundles(clip.EffectBundles);
                foreach (var bundleData in sortedBundles)
                {
                    var bundleId = bundleData.Id;
                    bundleData.Parameters ??= new();
                    var param = EffectArgsHelper.ConvertElementDictToObjectDict(bundleData.Parameters, bundleData.ParametersType);

                    var effectFactories = bundleData.Create();
                    List<IEffect> effects = new();
                    foreach (var item in effectFactories)
                    {

                        var impType = EffectHelper.DefaultImplementsType.GetValueOrDefault(item.TypeName, EffectImplementType.NotSpecified);
                        if (item is IBindableEffectFactory be)
                        {
                            effects.Add(be.Build(impType, be.ID, be.BindedInputID, be.BindedInputIDs, param));
                        }
                        else
                        {
                            effects.Add(item.Build(impType, param));
                        }
                    }

                    if (effects != null)
                    {
                        for (int i = 0; i < effects.Count; i++)
                        {
                            var effect = effects[i];
                            effect.Name = $"EffectBundle {bundleData.TypeName}({bundleData.Id}) - Subeffect #{i}";
                            effect.Enabled = bundleData.BindedOutputId != IEffectBundle.NoConnectionGUID;
                            effect.Index = globalIndex++;
                            effect.BindedEffectGroupID = bundleData.Id.ToString();
                            string key = $"{bundleData.Id}_{i}";
                            if (effect is not IBindableArgumentEffect) effect.Id = Guid.NewGuid().ToString();
                            newEffects[key] = effect;
                        }
                    }
                }
            }
            clip.Effects = newEffects
                .Where(e => string.IsNullOrWhiteSpace(e.Value.BindedEffectGroupID)
                            || (clip.EffectBundles?.ContainsKey(new(e.Value.BindedEffectGroupID)) ?? false))
                .ToDictionary();
        }

        private static List<IEffectBundle> SortEffectBundles(IReadOnlyDictionary<Guid, IEffectBundle> bundles)
        {
            var ordered = bundles.ToList();
            var adjacency = new Dictionary<Guid, List<Guid>>();
            var incoming = new Dictionary<Guid, int>();

            foreach (var kvp in ordered)
            {
                adjacency[kvp.Key] = new List<Guid>();
                incoming[kvp.Key] = 0;
            }

            foreach (var kvp in ordered)
            {
                var bundle = kvp.Value;
                var bundleId = kvp.Key;

                foreach (var inputId in GetInputDependencyIds(bundle))
                {
                    if (!bundles.ContainsKey(inputId) || inputId == bundleId) continue;
                    adjacency[inputId].Add(bundleId);
                    incoming[bundleId]++;
                }

                var outputId = bundle.BindedOutputId;
                if (IsValidOutputDependency(outputId) && bundles.ContainsKey(outputId) && outputId != bundleId)
                {
                    adjacency[bundleId].Add(outputId);
                    incoming[outputId]++;
                }
            }

            var queue = new Queue<Guid>(ordered.Where(kvp => incoming[kvp.Key] == 0).Select(kvp => kvp.Key));
            var result = new List<IEffectBundle>(ordered.Count);
            var visited = new HashSet<Guid>();

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!visited.Add(id)) continue;
                result.Add(bundles[id]);

                foreach (var next in adjacency[id])
                {
                    incoming[next]--;
                    if (incoming[next] == 0) queue.Enqueue(next);
                }
            }

            if (result.Count < ordered.Count)
            {
                var cycleIds = ordered.Where(kvp => !visited.Contains(kvp.Key)).Select(kvp => kvp.Key);
                throw new InvalidOperationException($"Effect bundle graph has a cycle. Unresolved ids: {string.Join(", ", cycleIds)}");
            }

            return result;
        }

        private static IEnumerable<Guid> GetInputDependencyIds(IEffectBundle bundle)
        {
            if (bundle.InputAnchorsDisplayName is not null)
            {
                if (bundle.BindedInputIds is null) yield break;
                foreach (var id in bundle.BindedInputIds)
                {
                    if (IsValidInputDependency(id)) yield return id;
                }
                yield break;
            }

            if (IsValidInputDependency(bundle.BindedInputId)) yield return bundle.BindedInputId;
        }

        private static bool IsValidInputDependency(Guid id)
        {
            return id != IEffectBundle.NoConnectionGUID && id != IEffectBundle.InputAnchorGUID;
        }

        private static bool IsValidOutputDependency(Guid id)
        {
            return id != IEffectBundle.NoConnectionGUID && id != IEffectBundle.OutputAnchorGUID;
        }

        public async Task<View> BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            ArgumentNullException.ThrowIfNull(clip);
            PropertyPanelBuilder ppb = new();
            ppb.AddButton(PPLocalizedResources.EffectBind_Title, (s, e) =>
            {
                var bindView = new DraftEffectBindingView();
                bindView.LoadClip(clip, page);
                if (page.UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone)
                {
                    page.Popup.Content = bindView;
                }
                else
                {
                    var v = new ApplicationAPIBase.Views.MultiWindowView.MultiWindowItem
                    {
                        Title = PPLocalizedResources.EffectBindView_Title(clip.DisplayName),
                        Content = bindView,
                        IsPopOutVisible = true
                    };
                    page.MainMultiWindowView.AddWindow(v);
                    v.Maximize();
                    v.CloseClicked += (s, e) => RebuildAllEffects(clip, false);
                }

            });
            var bundlesFactories = EffectServices.GetAvailableEffectBundles();

            if (clip.EffectBundles != null)
            {
                foreach (var bundleKvp in clip.EffectBundles)
                {
                    var bundleId = bundleKvp.Key;
                    var bundleInstance = bundleKvp.Value;
                    var locedName = EffectServices.GetLocalizedEffectBundleNames()[bundleInstance.TypeName];
                    if (string.IsNullOrWhiteSpace(bundleInstance.Name)) bundleInstance.Name = locedName;

                    string GetInputAnchorSelection(Guid id)
                    {
                        if (id == IEffectBundle.NoConnectionGUID) return PPLocalizedResources.EffectBind_NoConnection;
                        if (id == IEffectBundle.InputAnchorGUID) return PPLocalizedResources.EffectBind_SourcePicture;
                        if (clip.EffectBundles != null && clip.EffectBundles.TryGetValue(id, out var b))
                        {
                            return $"{b.Name} ({b.Id})";
                        }
                        return string.Empty;
                    }

                    string GetOutputAnchorSelection(Guid id)
                    {
                        if (id == IEffectBundle.NoConnectionGUID) return PPLocalizedResources.EffectBind_NoConnection;
                        if (id == IEffectBundle.OutputAnchorGUID) return PPLocalizedResources.EffectBind_FinalResult;
                        if (clip.EffectBundles != null && clip.EffectBundles.TryGetValue(id, out var b))
                        {
                            return $"{b.TypeName} ({b.Id})";
                        }
                        return string.Empty;
                    }

                    try
                    {

                        var bundlePpb = bundleInstance.CreateUI();

                        ppb.AddText(new TitleAndDescriptionLineLabel(bundleInstance.Name ?? bundleInstance.TypeName, bundleInstance.TypeName));
                        ppb.AddCheckbox($"Effect|{bundleId}|Enabled", PPLocalizedResources._Enabled, bundleInstance.Enabled);
                        ppb.AddEntry($"Effect|{bundleId}|Name", "Name", bundleInstance.Name ?? locedName, locedName);

                        ppb.AddSeparator();

                        ppb.AddFromAnother(bundlePpb, bundleInstance);

                        ppb.AddSeparator();

                        if (bundleInstance.InputAnchorsDisplayName is null)
                        {
                            ppb.AddPicker($"Bundle|{bundleId}|InAnchor", $"Input anchor {bundleInstance.InputAnchorDisplayName}", clip.EffectBundles.Select(b => $"{b.Value.Name} ({b.Key})").Append(PPLocalizedResources.EffectBind_SourcePicture).Append(PPLocalizedResources.EffectBind_NoConnection).ToArray(), GetInputAnchorSelection(bundleInstance.BindedInputId));
                        }
                        else
                        {
                            foreach (var item in bundleInstance.InputAnchorsDisplayName)
                            {
                                var idx = Array.IndexOf(bundleInstance.InputAnchorsDisplayName, item);
                                var currentId = (bundleInstance.BindedInputIds != null && idx >= 0 && idx < bundleInstance.BindedInputIds.Count)
                                    ? bundleInstance.BindedInputIds[idx]
                                    : IEffectBundle.NoConnectionGUID;
                                ppb.AddPicker($"Bundle|{bundleId}|InAnchors|{item}", $"Input anchor {item}", clip.EffectBundles.Select(b => $"{b.Value.Name} ({b.Key})").Append(PPLocalizedResources.EffectBind_SourcePicture).Append(PPLocalizedResources.EffectBind_NoConnection).ToArray(), GetInputAnchorSelection(currentId));

                            }
                        }

                        ppb.AddPicker($"Bundle|{bundleId}|OutAnchor", $"Output anchor {bundleInstance.OutputAnchorDisplayName}", clip.EffectBundles.Select(b => $"{b.Value.TypeName} ({b.Key})").Append(PPLocalizedResources.EffectBind_FinalResult).Append(PPLocalizedResources.EffectBind_NoConnection).ToArray(), GetOutputAnchorSelection(bundleInstance.BindedOutputId));


                        ppb.AddButton($"Bundle|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
                        ppb.AddSeparator();
                    }
                    catch (Exception ex)
                    {
                        if (Debugger.IsAttached)
                        {
                            if (Microsoft.Maui.Controls.Application.Current?.Windows?.First()?.Page is Page page)
                            {
                                if (await page.DisplayAlertAsync(Localized._Error, $"Error loading bundle {bundleInstance.TypeName}: {ex.Message}", "Throw", Localized._OK)) throw;
                            }
                        }
                        Log(ex, $"loading bundle {bundleInstance.TypeName}", this);
                        ppb.AddText(new Label { Text = $"Error loading bundle {bundleInstance.TypeName}: {ex.Message}", TextColor = Colors.Yellow });
                        ppb.AddSeparator();
                    }
                }
            }

            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            ppb.AddCustomChild(BuildAddEffectPanel(page, bundlesFactories, ppb, handler));

            static bool TryParseAnchorSelection(string? selection, string anchorLabel, Guid anchorGuid, out Guid id)
            {
                if (string.IsNullOrWhiteSpace(selection))
                {
                    id = IEffectBundle.NoConnectionGUID;
                    return false;
                }

                if (selection == PPLocalizedResources.EffectBind_NoConnection)
                {
                    id = IEffectBundle.NoConnectionGUID;
                    return true;
                }

                if (selection == anchorLabel)
                {
                    id = anchorGuid;
                    return true;
                }

                var open = selection.LastIndexOf('(');
                var close = selection.LastIndexOf(')');
                if (open >= 0 && close > open)
                {
                    var guidText = selection.Substring(open + 1, close - open - 1);
                    if (Guid.TryParse(guidText, out var parsed))
                    {
                        id = parsed;
                        return true;
                    }
                }

                id = IEffectBundle.NoConnectionGUID;
                return false;
            }

            ppb.PropertyChanged += (s, e) =>
            {
                ArgumentNullException.ThrowIfNull(clip);

                if (!ppb.Equals(s)) //from another
                {
                    if (s is IEffectBundle eb)
                    {
                        var data = eb.HandlePropertyPanelChange(e);
                        IEffectBundle? bundle = null;
                        if (data != null)
                        {
                            if (!clip?.EffectBundles?.TryGetValue(eb.Id, out bundle) ?? false) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            if (bundle is null) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            bundle.Parameters = data;

                        }
                        RebuildAllEffects(clip);
                    }
                }
                else
                {
                    if (e.Id.StartsWith("Bundle|"))
                    {
                        var parts = e.Id.Split('|');
                        if (parts.Length >= 3)
                        {
                            Guid bundleId = new(parts[1]);
                            string action = parts[2];
                            if (!clip.EffectBundles?.ContainsKey(bundleId) ?? false) return;

                            switch (action)
                            {
                                case "Remove":
                                    clip.EffectBundles?.Remove(bundleId);
                                    RebuildAllEffects(clip);
                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    break;
                                case "Name":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var nameBundle))
                                    {
                                        var locedName = EffectServices.GetLocalizedEffectBundleNames().GetValueOrDefault(nameBundle.TypeName, nameBundle.TypeName);
                                        var newName = e.Value?.ToString();
                                        nameBundle.Name = string.IsNullOrWhiteSpace(newName) ? locedName : newName;
                                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    }
                                    break;
                                case "Enabled":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var enabledBundle))
                                    {
                                        if (bool.TryParse(e.Value?.ToString(), out var enabled))
                                        {
                                            enabledBundle.Enabled = enabled;
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                                case "InAnchor":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var inBundle))
                                    {
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_SourcePicture, IEffectBundle.InputAnchorGUID, out var inId))
                                        {
                                            inBundle.BindedInputId = inId;
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                                case "InAnchors":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var insBundle))
                                    {
                                        if (parts.Length >= 4 && insBundle.InputAnchorsDisplayName is not null)
                                        {
                                            var idx = Array.IndexOf(insBundle.InputAnchorsDisplayName, parts[3]);
                                            if (idx >= 0)
                                            {
                                                if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_SourcePicture, IEffectBundle.InputAnchorGUID, out var inIds))
                                                {
                                                    if (insBundle.BindedInputIds is null || insBundle.BindedInputIds.Count != insBundle.InputAnchorsDisplayName.Length)
                                                    {
                                                        insBundle.BindedInputIds = Enumerable.Repeat(IEffectBundle.NoConnectionGUID, insBundle.InputAnchorsDisplayName.Length).ToList();
                                                    }
                                                    insBundle.BindedInputIds[idx] = inIds;
                                                    RebuildAllEffects(clip);
                                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case "OutAnchor":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var outBundle))
                                    {
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_FinalResult, IEffectBundle.OutputAnchorGUID, out var outId))
                                        {
                                            outBundle.BindedOutputId = outId;
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                    else if (e.Id == "AddBundle")
                    {
                        if (ppb.Properties.TryGetValue("NewBundleType", out var typeObj) && typeObj is string bundleTypeName)
                        {
                            if (bundlesFactories.TryGetValue(bundleTypeName, out var factory))
                            {
                                var instance = factory();
                                instance.Id = Guid.NewGuid();
                                instance.BindedInputId = IEffectBundle.NoConnectionGUID;
                                instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
                                clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                                clip.EffectBundles[instance.Id] = instance;

                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            }
                        }
                    }
                }
            };

#if DEBUG
            ppb.AddSeparator();
            ppb.AddButton("Rebuild", async (s, e) =>
            {
                try
                {
                    RebuildAllEffects(clip, true);
                    await page.DisplayAlertAsync(Localized._Info, SettingsManager.SettingLocalizedResources.Advanced_Success, Localized._OK);

                }
                catch (Exception ex)
                {
                    if (await page.DisplayAlertAsync("Error", Localized._ExceptionTemplate(ex), "Throw", Localized._OK)) throw;
                }
            });
#endif
            var panel = ppb.BuildWithScrollView();
            return panel;
        }


        private sealed class EffectBundleCardItem
        {
            public required string BundleTypeName { get; init; }
            public required string Title { get; init; }
            public required string Description { get; init; }
            public ImageSource? Thumbnail { get; init; }
            public MediaSource? VideoThumbnail { get; init; }
        }

        public static View BuildAddEffectPanel(
            Page page,
            Dictionary<string, Func<IEffectBundle>> bundlesFactories,
            PropertyPanelBuilder ppb,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            if (bundlesFactories == null || bundlesFactories.Count == 0)
            {
                return new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Padding = 12,
                    Content = new Label
                    {
                        Text = PPLocalizedResources.Add_Effect_Select,
                        Opacity = 0.7,
                        FontSize = 13
                    }
                };
            }


            void AddBundle(string bundleTypeName)
            {
                ppb.Properties["NewBundleType"] = bundleTypeName;
                PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddBundle", bundleTypeName);
                handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("AddBundle", bundleTypeName, bundleTypeName));
            }

            var cards = new List<EffectBundleCardItem>();
            foreach (var kvp in bundlesFactories.OrderBy(k => k.Key))
            {
                var bundleTypeName = kvp.Key;
                try
                {
                    var instance = kvp.Value();
                    EffectBundleDisplayItem? display = null;
                    try
                    {
                        display = instance.GetEffectBundleItem(Localized._LocaleId_);
                    }
                    catch
                    {
                        // ignore, fallback to Name/TypeName
                    }

                    cards.Add(new EffectBundleCardItem
                    {
                        BundleTypeName = bundleTypeName,
                        Title = EffectServices.GetLocalizedEffectBundleNames(Environment.NewLine).GetValueOrDefault(bundleTypeName, bundleTypeName),
                        Description = display?.Description ?? "",
                        Thumbnail = display?.Thumbnail,
                        VideoThumbnail = display?.VideoThumbnail,
                    });
                }
                catch
                {
                    cards.Add(new EffectBundleCardItem
                    {
                        BundleTypeName = bundleTypeName,
                        Title = bundleTypeName,
                        Description = "",
                        Thumbnail = null
                    });
                }
            }

            const double cardWidth = 210;
            const double cardHeight = 160;
            const double cardMargin = 6;

            var flex = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                Direction = FlexDirection.Row,
                JustifyContent = FlexJustify.Start,
                AlignItems = FlexAlignItems.Start,
                AlignContent = FlexAlignContent.Start
            };

            BindableLayout.SetItemsSource(flex, cards);
            BindableLayout.SetItemTemplate(flex, new DataTemplate(() =>
            {
                var image = new Image
                {
                    HeightRequest = 64,
                    WidthRequest = 64,
                    Aspect = Aspect.AspectFill,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Start
                };
                image.SetBinding(Image.SourceProperty, nameof(EffectBundleCardItem.Thumbnail));

                var title = new Label
                {
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    LineBreakMode = LineBreakMode.TailTruncation
                };
                title.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Title));
                title.SetBinding(ToolTipProperties.TextProperty, nameof(EffectBundleCardItem.Title));

                var desc = new Label
                {
                    FontSize = 12,
                    Opacity = 0.75,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2
                };
                desc.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Description));
                desc.SetBinding(ToolTipProperties.TextProperty, nameof(EffectBundleCardItem.Description));

                var textStack = new VerticalStackLayout
                {
                    Spacing = 2,
                    Children = { title, desc }
                };

                var row = new VerticalStackLayout
                {
                    Spacing = 10
                };
                row.Children.Add(image);
                row.Children.Add(textStack);

                var border = new Border
                {
                    WidthRequest = cardWidth,
                    HeightRequest = cardHeight,
                    Margin = new Thickness(cardMargin),
                    Padding = 10,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = row
                };



                void SelectCard(Border selected)
                {
                    foreach (var child in flex.Children)
                    {
                        if (child is Border b)
                        {
                            bool isSelected = b == selected;
                            b.Stroke = new SolidColorBrush(isSelected ? Colors.DodgerBlue : Colors.Gray.WithAlpha(0.25f));
                            b.StrokeThickness = isSelected ? 2 : 1;
                            b.Background = new SolidColorBrush(isSelected ? Colors.DodgerBlue.WithAlpha(0.1f) : Colors.Transparent);
                        }
                    }
                }

                UIServices.RegisterSelectOrContextMenu(
                    border,
                    OnSelected: () =>
                    {
                        if (border.BindingContext is EffectBundleCardItem item)
                        {
                            ppb.Properties["NewBundleType"] = item.BundleTypeName;
                            SelectCard(border);
                        }
                    },
                    OnClicked: () =>
                    {
                        if (border.BindingContext is EffectBundleCardItem item)
                            AddBundle(item.BundleTypeName);
                    },
                    OnContextMenuClick: async () =>
                    {
                        if (border.BindingContext is not EffectBundleCardItem item) return;
                        var verbs = new[] { PPLocalizedResources.Add_Effect, Localized.AssetPage_ShowPreview };
                        int action = Array.IndexOf(verbs, await page.DisplayActionSheetAsync(item.Title, Localized._Cancel, null, verbs));
                        switch (action)
                        {
                            case 0:
                                AddBundle(item.BundleTypeName);
                                break;
                            case 1:
                                await page.DisplayAlertAsync(Localized._Info, item.Description, Localized._OK);
                                break;
                        }
                    }
                );

                return border;
            }));

            return new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label
                    {
                        Text = PPLocalizedResources.Add_Effect_Select,
                        Opacity = 0.7,
                        FontSize = 13
                    },
                    flex
                }
            };
        }

        public View BuildClassicEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            PropertyPanelBuilder ppb = new();
            ppb.AddText(new Label { Text = PPLocalizedResources.EffectProp_ClassicEffectPageWarn, TextColor = Colors.Yellow });

            var localizedEffectDisplayName = EffectServices.GetLocalizedEffectNames();

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
                {
                    var effectKey = effectKvp.Key;
                    var effect = effectKvp.Value;
                    var factory = effect.GetFactory(EffectHelper.EffectsFactoriesEnum);
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.Name, localizedEffectDisplayName.TryGetValue(effect.TypeName, out var disp) ? disp : effect.TypeName));
                    ppb.AddCheckbox($"Effect|{effectKey}|Enabled", PPLocalizedResources._Enabled, effect.Enabled);
                    ppb.AddEntry($"Effect|{effectKey}|Index", PPLocalizedResources.EffectProp_Index, effect.Index.ToString(), "-1");
                    foreach (var paramName in factory.ParametersNeeded)
                    {
                        if (!factory.ParametersType.TryGetValue(paramName, out var paramType)) continue;

                        var currentVal = effect.Parameters.ContainsKey(paramName) ? effect.Parameters[paramName] : null;

                        if (currentVal is JsonElement je)
                        {
                            if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                                currentVal = je.GetBoolean();
                            else if (je.ValueKind == JsonValueKind.String)
                                currentVal = je.GetString();
                            else
                                currentVal = je.ToString();
                        }

                        string controlId = $"Effect|{effectKey}|{paramName}";

                        if (paramType == "bool")
                        {
                            bool val = false;
                            if (currentVal is bool b) val = b;
                            else if (bool.TryParse(currentVal?.ToString(), out var bParsed)) val = bParsed;
                            ppb.AddCheckbox(controlId, PluginManager.GetLocalizationItem($"_{paramName}", paramName), val);
                        }
                        else
                        {
                            string valStr = currentVal?.ToString() ?? "";
                            ppb.AddEntry(controlId, PluginManager.GetLocalizationItem($"_{paramName}", paramName), valStr, "");
                        }
                    }
                    if (effect is IBindableArgumentEffect be)
                    {
                        ppb.AddSeparator();
                        ppb.AddCustomChild("ID", new Label { Text = be.Id });
                        switch (be.EffectRole)
                        {
                            case BindableArgumentEffectType.ValueProvider:
                                ppb.AddCustomChild("Output anchor name", new Label { Text = (be as IBindableArgumentEffectValueProvider)?.OutputAnchorName ?? "none" });
                                break;
                            case BindableArgumentEffectType.OneInputValueProcessor:
                                ppb.AddCustomChild($"Input anchor {(be as IBindableArgumentEffectOneInputResultGenerator)?.InputAnchorName ?? "unknown"}", new Label { Text = $"{be.BindedArgumentProviderID} " });
                                ppb.AddCustomChild("Output anchor name", new Label { Text = (be as IBindableArgumentEffectOneInputResultGenerator)?.OutputAnchorName ?? "none" });
                                break;
                            case BindableArgumentEffectType.ManyInputValueProcessor:
                                if (be is IBindableArgumentEffectManyToOneValueProcesser mpe)
                                {
                                    foreach (var item in mpe.BindedArgumentProviderIDs)
                                    {
                                        var idx = mpe.BindedArgumentProviderIDs.IndexOf(item);
                                        string inAnchorName = "unknown";
                                        if (idx >= 0 && mpe.InputAnchorDisplayNames.Length < idx) inAnchorName = mpe.InputAnchorDisplayNames[idx];
                                        ppb.AddCustomChild($"Input anchor {inAnchorName}", new Label { Text = item });

                                    }
                                }
                                ppb.AddCustomChild("Output anchor name", new Label { Text = (be as IBindableArgumentEffectOneInputResultGenerator)?.OutputAnchorName ?? "none" });
                                break;
                            case BindableArgumentEffectType.OneInputResultGenerator:
                                ppb.AddCustomChild($"Input anchor {(be as IBindableArgumentEffectOneInputResultGenerator)?.InputAnchorName ?? "unknown"}", new Label { Text = $"{be.BindedArgumentProviderID} " });
                                break;

                            case BindableArgumentEffectType.ManyInputResultGenerator:
                                if (be is IBindableArgumentEffectManyInputResultGenerator mpg)
                                {
                                    foreach (var item in mpg.BindedArgumentProviderIDs)
                                    {
                                        var idx = mpg.BindedArgumentProviderIDs.IndexOf(item);
                                        string inAnchorName = "unknown";
                                        if (idx >= 0 && mpg.InputAnchorDisplayNames.Length < idx) inAnchorName = mpg.InputAnchorDisplayNames[idx];
                                        ppb.AddCustomChild($"Input anchor {inAnchorName}", new Label { Text = item });

                                    }
                                }
                                break;
                            default:
                                ppb.AddText($"unknown effect role.");
                                break;


                        }
                    }
                    ppb.AddButton($"Effect|{effectKey}|Remove", PPLocalizedResources.EffectProp_Remove);
                    ppb.AddSeparator();
                }
            }




            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            ppb.AddPicker("NewEffectType", PPLocalizedResources.Add_Effect_Select, localizedEffectDisplayName.Values.ToArray(), localizedEffectDisplayName.Values.FirstOrDefault());
            ppb.AddButton("AddEffect", PPLocalizedResources.Add_Effect);

            ppb.PropertyChanged += async (s, e) =>
            {
                if (e.Id.StartsWith("Effect|"))
                {
                    var parts = e.Id.Split('|');
                    if (parts.Length >= 3)
                    {
                        string effectKey = parts[1];
                        string paramName = parts[2];

                        if (paramName == "Remove")
                        {
                            if (clip.Effects != null && clip.Effects.ContainsKey(effectKey))
                            {
                                clip.Effects.Remove(effectKey);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                return;
                            }
                        }

                        if (clip.Effects != null && clip.Effects.TryGetValue(effectKey, out var effect))
                        {
                            string strVal = e.Value?.ToString() ?? "";

                            // Handle Enabled / Index specially (not part of ParametersType)
                            if (paramName == "Enabled")
                            {
                                if (bool.TryParse(strVal, out var enabledVal))
                                {
                                    clip.Effects[effectKey] = EffectServices.ReCreateEffect(effect, null, enabledVal, null, page: page);
                                }
                                handler?.Invoke(s, e);
                                return;
                            }
                            if (paramName == "Index")
                            {
                                if (int.TryParse(strVal, out var indexVal))
                                {
                                    clip.Effects[effectKey] = EffectServices.ReCreateEffect(effect, null, null, indexVal, page: page);
                                }
                                handler?.Invoke(s, e);
                                return;
                            }

                            if (effect.GetFactory(EffectHelper.EffectsFactoriesEnum).ParametersType.TryGetValue(paramName, out var paramType))
                            {
                                try
                                {
                                    object? typedValue = null;
                                    switch (paramType)
                                    {
                                        case "ushort": typedValue = ushort.Parse(strVal); break;
                                        case "int": typedValue = int.Parse(strVal); break;
                                        case "float": typedValue = float.Parse(strVal); break;
                                        case "double": typedValue = double.Parse(strVal); break;
                                        case "bool": typedValue = e.Value is bool b ? b : bool.Parse(strVal); break;
                                        case "string": typedValue = strVal; break;
                                    }

                                    if (typedValue != null)
                                    {
                                        var newParams = new Dictionary<string, object>(effect.Parameters);
                                        newParams[paramName] = typedValue;
                                        clip.Effects[effectKey] = EffectServices.ReCreateEffect(effect, newParams, null, null, page: page);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                else if (e.Id == "AddEffect")
                {
                    if (ppb.Properties.TryGetValue("NewEffectType", out var typeObj) && typeObj is string locedTypeName)
                    {
                        var typeName = localizedEffectDisplayName.FirstOrDefault(c => c.Value == locedTypeName, new("unknown", "unknown")).Key;
                        IEffect? newEffect = null;
                        if (EffectHelper.EffectsEnum.TryGetValue(typeName, out var creator))
                        {
                            try
                            {
                                newEffect = creator?.Invoke();
                            }
                            catch (Exception ex)
                            {
                                Log(ex, $"create effect of type {typeName}", this);
                            }
                        }


                        if (newEffect != null)
                        {
                            string newKey = typeName;
                            clip.Effects ??= new Dictionary<string, IEffect>();

                            int maxIndex = 0;
                            if (clip.Effects.Count > 0)
                            {
                                foreach (var item in clip.Effects.Values)
                                {
                                    if (item.Index >= maxIndex) maxIndex = item.Index + 1;
                                }
                            }
                            newEffect.Index = maxIndex;

                            clip.Effects[newKey] = newEffect;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            return;
                        }
                        else
                        {
                            Log($"Failed to create effect of type {typeName}.", "error");
                            throw new InvalidDataException($"Failed to create effect of type {typeName}.");
                        }
                    }
                }
                handler?.Invoke(s, e);
            };

            ppb.AddSeparator();
            ppb.AddText(new TitleAndDescriptionLineLabel(PPLocalizedResources.Effect_RenderOrder, PPLocalizedResources.Effect_RenderOrder_Hint));

            var orderContainer = new VerticalStackLayout { Spacing = 2, Padding = 5 };

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.Where(c => SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects") || c.Value.Name is null || !(c.Value.Name is not null && c.Value.Name.StartsWith("__Internal"))).OrderBy(c => c.Value.Index))
                {
                    orderContainer.Children.Add(BuildEffectOrderItem(effectKvp.Key, effectKvp.Value, clip, localizedEffectDisplayName, handler));
                }
            }

            ppb.AddCustomChild(orderContainer);
            var panel = ppb.BuildWithScrollView();
            return panel;

        }

        private View BuildEffectOrderItem(string effectKey, IEffect effect, ClipElementUI clip, Dictionary<string, string> localizedEffectDisplayName, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            // Drag Drop Container
            var container = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star }
                },
                Padding = new Thickness(5),
                BackgroundColor = Colors.Transparent
            };

            var dragHandle = new Label
            {
                Text = "⣿", // Grip icon
                FontSize = 20,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            var nameLabel = new Label
            {
                Text = localizedEffectDisplayName.TryGetValue(effect.TypeName, out var name) ? name : effect.Name,
                VerticalOptions = LayoutOptions.Center,
                FontSize = 16
            };

            // Add Index for clarity
            var indexLabel = new Label
            {
                Text = $"[{effect.Index}]",
                VerticalOptions = LayoutOptions.Center,
                FontSize = 12,
                TextColor = Colors.Gray,
                Margin = new Thickness(10, 0, 0, 0)
            };

            var textStack = new HorizontalStackLayout
            {
                Children = { nameLabel, indexLabel },
                VerticalOptions = LayoutOptions.Center
            };

            var dragGesture = new DragGestureRecognizer();
            dragGesture.CanDrag = true;
            dragGesture.DragStarting += (s, e) =>
            {
                e.Data.Properties.Add("EffectKey", effectKey);
            };
            dragHandle.GestureRecognizers.Add(dragGesture);

            // Add Drop to the WHOLE container (so dropping anywhere on the item works)
            var dropGesture = new DropGestureRecognizer();
            dropGesture.AllowDrop = true;
            dropGesture.Drop += (s, e) =>
            {
                if (clip.Effects == null) return;
                if (e.Data.Properties.TryGetValue("EffectKey", out var sourceKeyObj) && sourceKeyObj is string sourceKey)
                {
                    if (sourceKey == effectKey) return;

                    // Swap Request
                    if (clip.Effects.TryGetValue(sourceKey, out var sourceEffect) && clip.Effects.TryGetValue(effectKey, out var targetEffect))
                    {
                        // Swap Index
                        int tIdx = targetEffect.Index;
                        int sIdx = sourceEffect.Index;

                        clip.Effects[sourceKey] = EffectServices.ReCreateEffect(sourceEffect, null, null, tIdx, page: page);
                        clip.Effects[effectKey] = EffectServices.ReCreateEffect(targetEffect, null, null, sIdx, page: page);

                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    }
                }
            };
            container.GestureRecognizers.Add(dropGesture);

            container.Children.Add(dragHandle); // Col 0
            container.Children.Add(textStack); // Col 1
            Grid.SetColumn(textStack, 1);

            // Add visual feedback or border
            var frame = new Border
            {
                Content = container,
                Stroke = Colors.Gray,
                StrokeThickness = 0.5,
                Padding = 0,
                Margin = new Thickness(0, 2)
            };
            // Ensure gesture works on frame? Or just container? 
            // Better put Drop on Frame
            frame.GestureRecognizers.Add(dropGesture);

            return frame;
        }

        #endregion

        #region misc

        private View BuildSpeedAndRatioTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            PropertyPanelBuilder ppb = new();
            ppb.AddEntry("speedRatio", Localized.PropertyPanel_General_SpeedRatio, clip.SecondPerFrameRatio.ToString(), "1");
            ppb.AddButton("applyButton", Localized._Apply);
            ppb.ListenToChanges(e =>
            {
                if (e.Id == "speedRatio")
                {
                    clip.SecondPerFrameRatio = float.TryParse(e.Value as string, out var result) ? result : 1;
                    clip.ApplySpeedRatio();
                }
            });
            var panel = ppb.BuildWithScrollView();
            return panel;
        }

        public Func<IEnumerable<AIFunction>>? BuildToolCalls(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            List<AIFunction> toolCalls = new List<AIFunction>
            {
                AIFunctionFactory.Create(() => DraftImportAndExportHelper.ExportFromDraftPage(page, false).Clips, "get_all_clips","Get all clips inside this project."),
                AIFunctionFactory.Create(() => DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, false), "get_selected_clip_info","Get the clip selected by the user's info."),
                AIFunctionFactory.Create((string Id, ClipDraftDTO Clip) => {page.Clips[Id] = DraftImportAndExportHelper.ConvertToElement(Clip); handler.Invoke(this, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));}, "set_clip_info","Set a specific clip's information."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Select(c => c.Value.EffectProvider).FirstOrDefault(c => c.Keys.Contains(Type))?[Type]?.Invoke()?.GetInfo(), "get_effect_info","Get a specific effect's information."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().Select(c => c.EffectBundleProvider).FirstOrDefault(c => c.ContainsKey(Type))?[Type]?.Invoke()?.GetEffectBundleItem(), "get_effect_bundle_info","Get a specific effect bundle's information."),
                //AIFunctionFactory.Create((string Type) => , "get_cliptype_detail_info","Set a specific's clip information.")
            };

            return new(() => toolCalls);
        }

        private class DummyEffectBundle : IEffectBundle
        {
            public Guid Id { get; set; }
            public string TypeName => "Dummy";
            public string Name { get => "Dummy Effect Bundle"; set { } }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public bool Enabled { get; set; } = true;
            public Guid BindedInputId { get; set; }
            public Guid BindedOutputId { get; set; }

            public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

            public bool IsNormalEffect => false;

            public bool IsContinuousEffect => false;

            public bool IsBindableEffect => false;

            public string InputAnchorDisplayName => "blackhole";

            public string[]? InputAnchorsDisplayName => null;

            public string OutputAnchorDisplayName => "blackhole";

            public bool IsMultiInput => false;

            public List<Guid>? BindedInputIds { get; set; }
            public int StartPoint { get; set; }
            public int EndPoint { get; set; }

            public List<string> ParametersNeeded => [];

            public Dictionary<string, string> ParametersType => [];


            public IEffectFactory[] Create()
            {
                throw new NotImplementedException();
            }

            public PropertyPanelBuilder CreateUI()
            {
                throw new NotImplementedException();
            }

            public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
            {
                return new EffectBundleDisplayItem
                {
                    Name = Name,
                    Description = "This is a dummy effect bundle used for testing and ordering in converting EffectBundles to normal effect(s).",
                    Thumbnail = null,
                    VideoThumbnail = null
                };
            }
        }

        #endregion
    }
}
