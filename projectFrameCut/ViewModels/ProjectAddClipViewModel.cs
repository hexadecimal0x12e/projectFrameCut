using projectFrameCut.Asset;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace projectFrameCut.ViewModels;

public class ProjectAddClipViewModel : INotifyPropertyChanged
{
    private readonly DraftPage _draftPage;

    public ProjectAddClipViewModel(DraftPage draftPage)
    {
        _draftPage = draftPage;
        
        // 初始化命令
        AddTextClipCommand = new Command(async () => await AddTextClip());
        AddSolidColorClipCommand = new Command(async () => await AddSolidColorClip());
        AddSubTitleClipCommand = new Command(async () => await AddSubTitleClip());
        AddAlternativeSourceClipCommand = new Command(async () => await AddAlternativeSourceClip());
        AddAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddAssetClip(asset));
        
        // 加载资源
        LoadAssets();
    }

    // 资源列表
    public ObservableCollection<AssetItemViewModel> Assets { get; } = new();

    // 命令
    public ICommand AddTextClipCommand { get; }
    public ICommand AddSolidColorClipCommand { get; }
    public ICommand AddSubTitleClipCommand { get; }
    public ICommand AddAlternativeSourceClipCommand { get; }
    public ICommand AddAssetClipCommand { get; }

    // 事件：当需要关闭弹窗时触发
    public event EventHandler? ClipAdded;

    private void LoadAssets()
    {
        Assets.Clear();
        foreach (var asset in _draftPage.Assets.Values.OrderBy(a => a.Name))
        {
            Assets.Add(new AssetItemViewModel
            {
                Id = asset.AssetId,
                Name = asset.Name,
                Type = asset.AssetType.ToString(),
                SourcePath = asset.Path,
                ThumbPath = asset.ThumbnailPath,
                OriginalAsset = asset
            });
        }
    }

    private async Task AddTextClip()
    {
        var text = await _draftPage.DisplayPromptAsync("Add", "Input text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trackIndex = _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddASubTrack(trackIndex);
            }

            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(300),
                trackIndex: trackIndex,
                id: null,
                labelText: "Text 1",
                background: new SolidColorBrush(Colors.MediumPurple),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.TextClip;
            element.SourcePath = text;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData = new()
            {
                { "fontSize", 48 },
                { "fontColor", Colors.White.ToHex() }
            };
            
            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AddSolidColorClip()
    {
        var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(trackIndex))
        {
            _draftPage.AddATrack(trackIndex);
        }

        ushort R = 65535, G = 65535, B = 65535, A = 65535;
#if WINDOWS
        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            ColorSpectrumShape = Microsoft.UI.Xaml.Controls.ColorSpectrumShape.Ring,
            IsMoreButtonVisible = true,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            IsAlphaEnabled = true,
            IsAlphaSliderVisible = true,
            IsAlphaTextInputVisible = true,
        };
        Microsoft.UI.Xaml.Controls.ContentDialog diag = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Pick a color",
            Content = picker,
            CloseButtonText = Localized._Cancel,
            PrimaryButtonText = Localized._OK,
        };

        var services = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
        if (dialogueHelper != null)
        {
            var r = await dialogueHelper.ShowContentDialogue(diag);
            var color = picker.Color;
            R = (ushort)(color.R * 257);
            G = (ushort)(color.G * 257);
            B = (ushort)(color.B * 257);
            A = (ushort)(color.A * 257);
        }
#endif



        var element = _draftPage.CreateAndAddClip(
            startX: 0,
            width: _draftPage.FrameToPixel(90),
            trackIndex: trackIndex,
            id: null,
            labelText: $"#{R / 257:X2}{G / 257:X2}{B / 257:X2}{A / 257:X2}",
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.SolidColorClip;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = true;
        element.maxFrameCount = 0;
        element.ExtraData["R"] = R;
        element.ExtraData["G"] = G;
        element.ExtraData["B"] = B;
        element.ExtraData["A"] = A;
        element.isInfiniteLength = true;

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddSubTitleClip()
    {
        var text = await _draftPage.DisplayPromptAsync("Add", "Input subtitle text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trackIndex = _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddASubTrack(trackIndex);
            }

            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(300),
                trackIndex: trackIndex,
                id: null,
                labelText: "Subtitle 1",
                background: new SolidColorBrush(Colors.Yellow),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.SubtitleClip;
            element.SourcePath = text;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData = new()
            {
                { "fontSize", 32 },
                { "fontColor", Colors.White.ToHex() }
            };
            
            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AddAlternativeSourceClip()
    {
        var path = await _draftPage.DisplayPromptAsync("Add", "Input source path", placeholder: "#<provider>,<stream id>");
        if (string.IsNullOrWhiteSpace(path)) return;
        
        var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(trackIndex))
        {
            _draftPage.AddATrack(trackIndex);
        }

        var vidSrc = PluginManager.CreateVideoSource(path);

        var element = _draftPage.CreateAndAddClip(
            startX: 0,
            width: _draftPage.FrameToPixel((uint)vidSrc.TotalFrames),
            trackIndex: trackIndex,
            id: null,
            labelText: "Alternative source 1",
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.VideoClip;
        element.SourcePath = path;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = false;
        element.maxFrameCount = (uint)vidSrc.TotalFrames;
        element.sourceSecondPerFrame = (float)(1 / vidSrc.Fps);
        element.ExtraData = new();
        
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddAssetClip(AssetItemViewModel? assetViewModel)
    {
        if (assetViewModel?.OriginalAsset == null) return;

        var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(trackIndex))
        {
            _draftPage.AddATrack(trackIndex);
        }

        var clip = _draftPage.CreateFromAsset(
            assetViewModel.OriginalAsset,
            trackIndex,
            InternalPluginBase.InternalPluginBaseID,
            assetViewModel.SourcePath
        );

        _draftPage.RegisterClip(clip, true);
        
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AssetItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? ThumbPath { get; set; }
    public AssetItem? OriginalAsset { get; set; }
    
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbPath);
    
    public Color BackgroundColor
    {
        get
        {
            return Type?.ToLower() switch
            {
                "video" => Colors.DarkSlateBlue,
                "audio" => Colors.DarkGreen,
                "image" => Colors.DarkOrange,
                _ => Colors.Gray
            };
        }
    }
}
