using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationPluginBase.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Text;
using projectFrameCut.Shared;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks.Text;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using System.Security.Cryptography;
using System.Text.Json;

namespace projectFrameCut.Services;

public static class TextStyleServices
{
    public static ITextClipStyleProvider FromLegacyEntry(TextClipEntry entry)
    {
        var provider = new OldTextClipEntryTextStyleProvider();
        provider.GetParameters()["TextEntriesJson"] = JsonSerializer.Serialize(new[] { entry });
        provider.BasicText = entry.SampleText ?? entry.text ?? "Text";
        return provider;
    }

    public static async Task RunAsync(Page page, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Logger.Log(ex, "Text style operation", typeof(TextStyleServices));
            await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    public static async Task SaveFromClipAsync(DraftPage page, DraftStuff.ClipElementUI clip, ITextClipStyleProvider style)
    {
        await RunAsync(page, async () =>
        {
            var preset = TextStyleStore.Capture(page, clip, style);
            var name = await page.DisplayPromptAsync(Localized.TextStyles_Save, Localized.TextStyles_Name,
                accept: Localized._OK, cancel: Localized._Cancel, initialValue: preset.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            preset.Name = name.Trim();
            await TextStyleStore.SaveAsync(preset);
        });
    }

    public static ImageSource? RenderPreview(TextClipPreset preset, string text)
    {
        var copy = TextStyleStore.Copy(preset);
        var provider = TextStyleStore.Restore(copy, text);
        var entries = provider.BuildEntries();
        if (entries.Length == 0) return null;
        copy.Clip.MetaData ??= new();
        TextClipStyles.Save(provider, copy.Clip.MetaData);
        var hash = Convert.ToHexString(SHA256.HashData(TextStyleStore.Serialize(copy)));
        var directory = Path.Combine(MauiProgram.CachePath, "TextStylePreviews");
        string path = Path.Combine(directory, hash + ".png");
        if (File.Exists(path)) return ImageSource.FromFile(path);
        var bounds = TextServices.MeasureBounds(entries, 1920, 1080);
        int w = copy.Clip.TargetWidth > 0 ? copy.Clip.TargetWidth : Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int h = copy.Clip.TargetHeight > 0 ? copy.Clip.TargetHeight : Math.Max(1, (int)Math.Ceiling(bounds.Height));
        double scale = Math.Min(1, Math.Min(640d / w, 240d / h));
        using var clip = new TextClip
        {
            Id = copy.Clip.Id, Name = preset.Name, Duration = Math.Max(1, copy.Clip.Duration), RelativeStartFrame = copy.Clip.RelativeStartFrame,
            TextEntries = entries.ToList(), ExtraData = copy.Clip.MetaData,
            TargetWidth = w, TargetHeight = h, Effects = copy.Clip.Effects, EffectProviders = copy.Clip.EffectProviders,
            Rotation = copy.Clip.Rotation, Opacity = copy.Clip.Opacity,
        };
        clip.ReInit(8);
        using var picture = projectFrameCut.Render.RPCProtocol.ClipPreviewRenderer.Render(clip, [clip],
            Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), w, h, clip.Duration / 2,
            CancellationToken.None, 8, initializeClips: false);
        if (picture is null) return null;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid() + ".png");
        try
        {
            picture.SaveToPng(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return ImageSource.FromFile(path);
    }

    public static async Task EditAsync(DraftPage page, TextClipPreset original, bool isUserStyle, Action refresh)
    {
        var preset = TextStyleStore.Copy(original);
        var provider = TextStyleStore.Restore(preset);
        var ui = GetUIProvider(provider);
        var preview = new Image { HeightRequest = 130, Aspect = Aspect.AspectFit };
        var error = new Label { TextColor = Colors.OrangeRed, IsVisible = false };
        var name = new Entry { Text = preset.Name, Placeholder = Localized.TextStyles_Name };
        var sample = new Editor { Text = preset.SampleText, MinimumHeightRequest = 70, AutoSize = EditorAutoSizeOption.TextChanges };
        var host = new PropertyPanelBuilder();
        void UpdatePreview(PropertyPanelPropertyChangedEventArgs? change = null)
        {
            try
            {
                provider.BasicText = sample.Text ?? "";
                if (change is not null) ui.HandlePropertyPanelChange(provider, change);
                TextStyleStore.SetStyle(preset, provider);
                preview.Source = RenderPreview(preset, provider.BasicText);
                error.IsVisible = false;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Preview custom text style", typeof(TextStyleServices));
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        }
        sample.Unfocused += (_, _) => UpdatePreview();
        var modes = new Dictionary<string, TextClipLayoutMode>
        {
            [PPLocalizedResources.TextOption_LayoutMode_FixedWidth] = TextClipLayoutMode.FixedWidth,
            [PPLocalizedResources.TextOption_LayoutMode_FillClip] = TextClipLayoutMode.FillClip,
            [PPLocalizedResources.TextOption_LayoutMode_FixedSize] = TextClipLayoutMode.FixedSize,
        };
        if (ui.ShowLayoutModePicker)
            host.AddPicker("LayoutMode", Localized.TextStyles_Layout, modes.Keys.ToArray(),
                modes.FirstOrDefault(p => p.Value == provider.LayoutMode).Key);
        if (ui.ShowFontPicker)
            host.AddDialogFontPicker("FontFamily", PPLocalizedResources.TextOption_Font, PPLocalizedResources.TextOption_Font,
                provider.GetParameters().TryGetValue("FontFamily", out var font) ? font : null, TextServices.LoadedFonts.Values.ToList(), page,
                selected =>
                {
                    if (selected.InnerFont is not null) TextClipFontRegistry.RegisterFontFace(selected.InnerFont);
                    else if (!string.IsNullOrWhiteSpace(selected.Path)) TextClipFontRegistry.AddFont(selected.Path);
                    UpdatePreview(new("FontFamily", selected.FontName, null));
                }, TextServices.RenderFontPreviewAsync);
        host.AddFromAnother(ui.CreateUI(provider), provider);
        host.PropertyChanged += (_, e) =>
        {
            if (e.Id == "LayoutMode" && modes.TryGetValue(e.Value?.ToString() ?? "", out var mode))
                UpdatePreview(new("LayoutMode", mode.ToString(), null));
            else UpdatePreview(e);
        };
        var save = new Button { Text = Localized.TextStyles_SaveCopy };
        var update = new Button { Text = Localized.TextStyles_Update, IsVisible = isUserStyle };
        var close = new Button { Text = Localized._Cancel };
        bool saving = false;
        async Task Save(bool replace)
        {
            if (saving || string.IsNullOrWhiteSpace(name.Text)) return;
            saving = true;
            await RunAsync(page, async () =>
            {
                provider.BasicText = sample.Text ?? "";
                TextStyleStore.SetStyle(preset, provider);
                preset.Name = name.Text.Trim();
                preset.SampleText = provider.BasicText;
                if (preset.Clip.Duration == 0)
                    preset.Clip.Duration = Setting.SettingManager.SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300);
                if (!replace) { preset.Id = Guid.NewGuid(); preset.LegacyKey = null; }
                TextStyleStore.StripText(preset);
                await TextStyleStore.SaveAsync(preset);
                await page.HidePopup();
                refresh();
            });
            saving = false;
        }
        save.Clicked += async (_, _) => await Save(false);
        update.Clicked += async (_, _) => await Save(true);
        close.Clicked += async (_, _) => await page.HidePopup();
        UpdatePreview();
        await page.ShowAPopup(new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12, Spacing = 8, Children =
                {
                    new Label { Text = Localized.TextStyles_Customize, FontSize = 20 }, name, sample, preview, error,
                    new Label { Text = Localized.TextStyles_AnimationHint, FontSize = 12 }, host.Build(), update, save, close,
                },
            },
        }, mode: "dialog");
    }

    public static Dictionary<string, Func<ITextClipStyleProvider>> GetAvailableTextStyleProviders()
    {
        var styles = new Dictionary<string, Func<ITextClipStyleProvider>>();
        foreach (var (plugin, instance) in PluginManager.LoadedPlugins)
        foreach (var (type, factory) in instance.TextClipStyleProvider)
        {
            if (type == "OldTextClipEntry") continue;
            styles.Add(styles.ContainsKey(type) ? $"{type} ({plugin})" : type, factory);
        }
        return styles;
    }

    public static ITextClipStyleUIProvider GetUIProvider(ITextClipStyleProvider source)
    {
        if (PluginManager.LoadedPlugins.GetValueOrDefault(source.FromPlugin) is IApplicationPluginBase plugin)
        {
            try
            {
                if (plugin.TextClipStyleUIProvider.TryGetValue(source.TypeName, out var factory)) return factory(source);
                if (plugin.GetDefaultTextClipStyleUIProvider(source) is { } ui) return ui;
            }
            catch (Exception ex) { Logger.Log(ex, $"Create text style UI for '{source.TypeName}'", typeof(TextStyleServices)); }
        }
        return new TextClipStyleUIProvider();
    }
}
