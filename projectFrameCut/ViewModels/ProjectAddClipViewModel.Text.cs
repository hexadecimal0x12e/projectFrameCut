using System.ComponentModel;
using System.Windows.Input;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Text;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;

namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel
{
    private int textStylesVersion;
    public int TextStyleFilter
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged();
            _ = FilterAssets();
        }
    }

    public ICommand ImportTextStyleCommand => new Command(async () =>
        await TextStyleServices.RunAsync(_draftPage, async () =>
        {
            await TextStyleStore.ImportAsync(_draftPage);
            await InitializeTextStyles();
        }));

    private bool MatchesTextStyleFilter(TextStyleProviderItemViewModel item) =>
        TextStyleFilter == 0 || (TextStyleFilter == 2) == item.IsUserStyle;

    public async Task InitializeTextStyles()
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(InitializeTextStyles);
            return;
        }
        int version = ++textStylesVersion;
        var items = new List<TextStyleProviderItemViewModel>();
        foreach (var plugin in PluginManager.LoadedPlugins.Values)
        foreach (var (type, factory) in plugin.TextClipStyleProvider)
        {
            if (type == "OldTextClipEntry") continue;
            try
            {
                var provider = factory();
                string name = provider.FromPlugin == InternalPluginBase.InternalPluginBaseID ? type switch
                {
                    "Basic" => Localized.TextStyles_Basic,
                    "Title" => Localized.TextStyles_Title,
                    "Pinyin" => Localized.TextStyles_Pinyin,
                    _ => type,
                } : type;
                var preset = TextStyleStore.FromProvider(provider, name);
                preset.Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provider.FromPlugin + "/" + type)).AsSpan(0, 16));
                items.Add(new(this, preset, false, null));
            }
            catch (Exception ex) { Log(ex, $"Load text style provider {type}", this); }
        }
        foreach (var item in await TextStyleStore.LoadAsync()) items.Add(new(this, item.Preset, true, item.Error));
        if (version != textStylesVersion) return;
        AvailableTextStyleProviders.Clear();
        foreach (var item in items) AvailableTextStyleProviders.Add(item);
        await FilterAssets();
    }

    private void UpdateTextStyleCommands()
    {
        foreach (var item in AvailableTextStyleProviders) item.AddTextClipWithStyleProviderCommand.ChangeCanExecute();
    }

    public async Task AddTextClipWithStyleProvider(TextStyleProviderItemViewModel item)
    {
        if (!HasTextToAdd || item.Error is not null || _draftPage.IsReadonly) return;
        await TextStyleServices.RunAsync(_draftPage, () =>
        {
            // Each placement owns its provider graph and all mutable style state.
            var preset = TextStyleStore.Instantiate(item.Preset);
            var provider = TextStyleStore.Restore(preset, TextToAdd);
            string text = TextToAdd;
            uint duration = preset.Clip.Duration > 0 ? preset.Clip.Duration
                : SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300);
            BeginTimelineClipPlacement((trackIndex, startX) =>
            {
                var element = _draftPage.CreateAndAddClip(startX: startX, width: _draftPage.FrameToPixel(duration),
                    trackIndex: trackIndex, id: preset.Clip.Id, labelText: text,
                    background: new SolidColorBrush(Colors.MediumPurple), resolveOverlap: true, relativeStart: preset.Clip.RelativeStartFrame, maxFrames: 0);
                element.ClipType = ClipMode.TextClip;
                element.FromPlugin = InternalPluginBase.InternalPluginBaseID;
                element.isInfiniteLength = true;
                element.maxFrameCount = 0;
                element.ExtraData = preset.Clip.MetaData ?? new();
                TextClipStyles.Save(provider, element.ExtraData);
                element.ExtraData["TextEntries"] = provider.BuildEntries().ToList();
                element.TargetWidth = preset.Clip.TargetWidth;
                element.TargetHeight = preset.Clip.TargetHeight;
                element.TargetX = preset.Clip.TargetX;
                element.TargetY = preset.Clip.TargetY;
                element.StartingX = preset.Clip.StartingX;
                element.StartingY = preset.Clip.StartingY;
                element.Rotation = preset.Clip.Rotation;
                element.Opacity = preset.Clip.Opacity;
                element.IsMoveable = true;
                element.IsHorizontalResizable = provider.IsHorizontalResizable;
                element.IsVerticalResizable = provider.IsVerticalResizable;
                element.CanSnapWhileResizing = provider.CanSnapWhileResizing;
                DraftImportAndExportHelper.ApplyTextStyleEffects(element, preset.Clip, _draftPage.ProjectInfo.RelativeWidth, _draftPage.ProjectInfo.RelativeHeight);
                return element;
            }, TextClipInSubTrack, name: "Text");
            ClipAdded?.Invoke(this, EventArgs.Empty);
            TextToAdd = "";
            return Task.CompletedTask;
        });
    }

    public async Task ShowTextStyleMenuAsync(TextStyleProviderItemViewModel item)
    {
        await TextStyleServices.RunAsync(_draftPage, async () =>
        {
            var options = new List<string>();
            if (item.Error is null) options.Add(Localized.TextStyles_Customize);
            if (item.IsUserStyle) options.AddRange([Localized.TextStyles_Rename, Localized.TextStyles_Export, Localized.TextStyles_Delete]);
            var selected = await _draftPage.DisplayActionSheetAsync(item.Name, Localized._Cancel, null, options.ToArray());
            if (selected == Localized.TextStyles_Customize)
                await TextStyleServices.EditAsync(_draftPage, item.Preset, item.IsUserStyle, () => _ = InitializeTextStyles());
            else if (selected == Localized.TextStyles_Export) await TextStyleStore.ExportAsync(item.Preset);
            else if (selected == Localized.TextStyles_Rename)
            {
                var name = await _draftPage.DisplayPromptAsync(Localized.TextStyles_Rename, Localized.TextStyles_Name,
                    accept: Localized._OK, cancel: Localized._Cancel, initialValue: item.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                var copy = TextStyleStore.Copy(item.Preset);
                copy.Name = name.Trim();
                await TextStyleStore.SaveAsync(copy);
                await InitializeTextStyles();
            }
            else if (selected == Localized.TextStyles_Delete
                && await _draftPage.DisplayAlertAsync(Localized.TextStyles_Delete, item.Name, Localized._OK, Localized._Cancel))
            {
                await TextStyleStore.DeleteAsync(item.Preset.Id);
                await InitializeTextStyles();
            }
        });
    }
}

public sealed class TextStyleProviderItemViewModel : INotifyPropertyChanged
{
    private readonly ProjectAddClipViewModel parent;
    public TextClipPreset Preset { get; }
    public string Id => Preset.Id.ToString();
    public string Name => Preset.Name;
    public string BasicText => Preset.SampleText;
    public bool IsUserStyle { get; }
    public string? Error { get; }
    public string Description => Error ?? (IsUserStyle ? Localized.TextStyles_User : Localized.TextStyles_BuiltIn);
    public Command AddTextClipWithStyleProviderCommand { get; }
    public Command MenuCommand { get; }
    private ImageSource? preview;
    private bool previewLoaded;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ImageSource? PreviewSource
    {
        get
        {
            if (previewLoaded || Error is not null) return preview;
            previewLoaded = true;
            _ = LoadPreviewAsync(parent.HasTextToAdd ? parent.TextToAdd : BasicText);
            return preview;
        }
    }

    private async Task LoadPreviewAsync(string text)
    {
        try
        {
            var image = await Task.Run(() => TextStyleServices.RenderPreview(Preset, text));
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                preview = image;
                PropertyChanged?.Invoke(this, new(nameof(PreviewSource)));
            });
        }
        catch (Exception ex) { Log(ex, $"Preview text style {Name}", this); }
    }

    public TextStyleProviderItemViewModel(ProjectAddClipViewModel parent, TextClipPreset preset, bool isUserStyle, string? error)
    {
        this.parent = parent;
        Preset = preset;
        IsUserStyle = isUserStyle;
        Error = error;
        AddTextClipWithStyleProviderCommand = new Command(async () => await parent.AddTextClipWithStyleProvider(this),
            () => parent.HasTextToAdd && Error is null && !parent._draftPage.IsReadonly);
        MenuCommand = new Command(async () => await parent.ShowTextStyleMenuAsync(this));
    }
}
