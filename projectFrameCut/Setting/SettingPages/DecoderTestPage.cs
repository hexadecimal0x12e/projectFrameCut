using projectFrameCut.Drawing.Base;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;

namespace projectFrameCut.Setting.SettingPages;

public sealed class DecoderTestPage : ContentPage
{
    private sealed class DecoderItem
    {
        public required IPluginBase Plugin { get; init; }
        public required string Key { get; init; }
        public required IVideoSource Provider { get; init; }
        public string DisplayName { get; init; } = string.Empty;
    }

    private sealed class EffectItem
    {
        public string DisplayName { get; init; } = string.Empty;
        public Func<IEffectProvider>? Factory { get; init; }
    }

    private readonly ObservableCollection<DecoderItem> _decoders = [];
    private readonly ObservableCollection<EffectItem> _effects = [];
    private readonly CollectionView _decoderList;
    private readonly Button _pickFileButton;
    private readonly Button _initializeButton;
    private readonly Button _playButton;
    private readonly Button _back5Button;
    private readonly Button _forward5Button;
    private readonly Button _backFrameButton;
    private readonly Button _forwardFrameButton;
    private readonly Image _frameImage;
    private readonly Label _fileLabel;
    private readonly Label _decoderLabel;
    private readonly Label _frameLabel;
    private readonly Label _statusLabel;
    private readonly Slider _frameSlider;
    private readonly Picker _effectPicker;
    private readonly ContentView _effectPanelHost;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _renderCts;
    private IVideoSource? _source;
    private IEffectProvider? _effectProvider;
    private IEffect? _effect;
    private DecoderItem? _selectedDecoder;
    private string? _filePath;
    private uint _currentFrame;
    private long _renderVersion;
    private bool _updatingSlider;

    public DecoderTestPage()
    {
        Title = "解码器测试";

        foreach (var plugin in PluginManager.LoadedPlugins.Values.OrderBy(p => p.Name))
        {
            foreach (var entry in plugin.VideoSourceProvider.OrderBy(p => p.Key))
            {
                try
                {
                    var provider = entry.Value;
                    var extensions = provider.PreferredExtension.Length == 0
                        ? "无扩展名声明"
                        : string.Join(", ", provider.PreferredExtension);
                    _decoders.Add(new DecoderItem
                    {
                        Plugin = plugin,
                        Key = entry.Key,
                        Provider = provider,
                        DisplayName = $"{provider.TypeName}  [{entry.Key}]\n{plugin.Name} · {extensions}"
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"读取视频源注册项 {plugin.PluginID}/{entry.Key}", this);
                }
            }
        }

        _effects.Add(new EffectItem { DisplayName = "不使用效果" });
        foreach (var entry in EffectServices.GetAvailableEffectProviders().OrderBy(p => p.Key))
        {
            try
            {
                var provider = entry.Value();
                if (provider.TypeOfEffect != EffectType.NormalEffect || provider.RestoreInstanceWithDefaultType() is not INormalEffect)
                    continue;
                _effects.Add(new EffectItem
                {
                    DisplayName = PluginManager.GetLocalizationItem(
                        $"DisplayName_Effect_{entry.Key}",
                        string.IsNullOrWhiteSpace(provider.Name) ? entry.Key : provider.Name),
                    Factory = entry.Value
                });
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"读取效果注册项 {entry.Key}", this);
            }
        }

        _decoderList = new CollectionView
        {
            ItemsSource = _decoders,
            SelectionMode = SelectionMode.Single,
            HeightRequest = 190,
            EmptyView = new Label
            {
                Text = "没有已注册的视频源",
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            },
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { Padding = new Thickness(12, 8), LineBreakMode = LineBreakMode.WordWrap };
                label.SetBinding(Label.TextProperty, nameof(DecoderItem.DisplayName));
                return new Border
                {
                    Stroke = Colors.Gray,
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 2),
                    Content = label
                };
            })
        };
        _decoderList.SelectionChanged += DecoderList_SelectionChanged;

        _fileLabel = new Label { Text = "未选择文件", LineBreakMode = LineBreakMode.MiddleTruncation, VerticalTextAlignment = TextAlignment.Center };
        _decoderLabel = new Label { Text = "未选择解码器", TextColor = Colors.Gray };
        _frameLabel = new Label { Text = "帧: -", HorizontalTextAlignment = TextAlignment.Center };
        _statusLabel = new Label { Text = "请选择解码器和视频文件。", TextColor = Colors.Gray };
        _frameImage = new Image { Aspect = Aspect.AspectFit, BackgroundColor = Colors.Black };
        _frameSlider = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false };
        _frameSlider.ValueChanged += FrameSlider_ValueChanged;
        _effectPicker = new Picker
        {
            Title = "选择效果（可选）",
            ItemsSource = _effects,
            ItemDisplayBinding = new Binding(nameof(EffectItem.DisplayName))
        };
        _effectPicker.SelectedIndexChanged += EffectPicker_SelectedIndexChanged;
        _effectPanelHost = new ContentView
        {
            MaximumHeightRequest = 260,
            Content = new Label { Text = "未选择效果", TextColor = Colors.Gray }
        };
        _effectPicker.SelectedIndex = 0;

        _pickFileButton = new Button { Text = "选择视频文件" };
        _pickFileButton.Clicked += PickFileButton_Clicked;
        _initializeButton = new Button { Text = "初始化解码器", IsEnabled = false };
        _initializeButton.Clicked += InitializeButton_Clicked;
        _playButton = new Button { Text = "播放", IsEnabled = false };
        _playButton.Clicked += PlayButton_Clicked;
        _back5Button = new Button { Text = "后退 5 秒", IsEnabled = false };
        _back5Button.Clicked += (_, _) => SeekBySeconds(-5);
        _forward5Button = new Button { Text = "前进 5 秒", IsEnabled = false };
        _forward5Button.Clicked += (_, _) => SeekBySeconds(5);
        _backFrameButton = new Button { Text = "上一帧", IsEnabled = false };
        _backFrameButton.Clicked += (_, _) => SeekByFrames(-1);
        _forwardFrameButton = new Button { Text = "下一帧", IsEnabled = false };
        _forwardFrameButton.Clicked += (_, _) => SeekByFrames(1);

        var fileGrid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 8,
            Children =
            {
                _pickFileButton,
                _fileLabel,
                _initializeButton
            }
        };
        Grid.SetColumn(_fileLabel, 1);
        Grid.SetColumn(_initializeButton, 2);

        var transportGrid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 6,
            Children =
            {
                _back5Button,
                _backFrameButton,
                _playButton,
                _forwardFrameButton,
                _forward5Button
            }
        };
        Grid.SetColumn(_backFrameButton, 1);
        Grid.SetColumn(_playButton, 2);
        Grid.SetColumn(_forwardFrameButton, 3);
        Grid.SetColumn(_forward5Button, 4);

        var footer = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                _decoderLabel,
                _frameSlider,
                _frameLabel,
                transportGrid,
                _statusLabel,
                new Label { Text = "帧后处理效果", FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 6, 0, 0) },
                _effectPicker,
                _effectPanelHost
            }
        };
        var root = new Grid
        {
            Padding = new Thickness(16, 10),
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            ],
            RowSpacing = 8,
            Children =
            {
                new Label { Text = "已注册的视频源", FontAttributes = FontAttributes.Bold, FontSize = 17 },
                _decoderList,
                fileGrid,
                _frameImage,
                footer
            }
        };
        Grid.SetRow(_decoderList, 1);
        Grid.SetRow(fileGrid, 2);
        Grid.SetRow(_frameImage, 3);
        Grid.SetRow(footer, 4);
        Content = root;
    }

    private void DecoderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedDecoder = e.CurrentSelection.FirstOrDefault() as DecoderItem;
        _decoderLabel.Text = _selectedDecoder is null
            ? "未选择解码器"
            : $"解码器: {_selectedDecoder.Provider.TypeName} / {_selectedDecoder.Plugin.Name}";
        _initializeButton.IsEnabled = _selectedDecoder is not null && !string.IsNullOrWhiteSpace(_filePath);
    }

    private void EffectPicker_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var item = _effectPicker.SelectedItem as EffectItem;
        _effectProvider = null;
        _effect = null;
        _effectPanelHost.Content = new Label { Text = "未选择效果", TextColor = Colors.Gray };
        if (item?.Factory is not { } factory)
        {
            if (_source is not null) _ = ShowFrameAsync(_currentFrame);
            return;
        }

        try
        {
            var provider = factory();
            _effectProvider = provider;
            RebuildEffect();
            var panel = new PropertyPanelBuilder();
            EffectProviderUIHelper.BuildUI(provider, panel, null);
            panel.PropertyChanged += EffectPanel_PropertyChanged;
            _effectPanelHost.Content = new ScrollView { Content = panel.Build() };
            if (_source is not null) _ = ShowFrameAsync(_currentFrame);
        }
        catch (Exception ex)
        {
            _effectProvider = null;
            _effect = null;
            _statusLabel.Text = $"效果初始化失败: {ex.Message}";
            Logger.Log(ex, $"初始化效果 {item.DisplayName}", this);
        }
    }

    private void EffectPanel_PropertyChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        if (_effectProvider is null) return;
        try
        {
            EffectProviderUIHelper.HandleChange(_effectProvider, e);
            RebuildEffect();
            if (_source is not null) _ = ShowFrameAsync(_currentFrame);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"效果参数更新失败: {ex.Message}";
            Logger.Log(ex, $"更新效果参数 {e.Id}", this);
        }
    }

    private void RebuildEffect()
    {
        if (_effectProvider is null)
        {
            _effect = null;
            return;
        }

        var effect = _effectProvider.Build().OfType<INormalEffect>().FirstOrDefault()
            ?? throw new NotSupportedException($"效果 '{_effectProvider.TypeName}' 没有可处理视频帧的实现。");
        effect.Initialize();
        _effect = effect;
    }

    private async void PickFileButton_Clicked(object? sender, EventArgs e)
    {
        var path = await FileSystemService.PickFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        StopPlayback();
        await DisposeSourceAsync();
        _filePath = path;
        _fileLabel.Text = path;
        _initializeButton.IsEnabled = _selectedDecoder is not null;
        _statusLabel.Text = "文件已选择，请初始化解码器。";
    }

    private async void InitializeButton_Clicked(object? sender, EventArgs e)
    {
        if (_selectedDecoder is null || string.IsNullOrWhiteSpace(_filePath)) return;

        StopPlayback();
        await DisposeSourceAsync();
        SetPlaybackEnabled(false);
        _statusLabel.Text = "正在初始化解码器...";
        IVideoSource? source = null;
        try
        {
            var decoder = _selectedDecoder;
            source = decoder.Provider.CreateNew(_filePath);
            source.EnableLock = true;
            await Task.Run(source.Initialize);
            _source = source;
            source = null;

            var maxFrame = GetMaxFrame(_source);
            _frameSlider.Maximum = maxFrame ?? 1;
            _frameSlider.IsEnabled = maxFrame.GetValueOrDefault() > 0;
            _currentFrame = 0;
            SetPlaybackEnabled(true);
            _statusLabel.Text = $"{_source.Width}×{_source.Height} · {_source.Fps:0.###} FPS · 总帧数: {FormatTotalFrames(_source.TotalFrames)}";
            Logger.LogDiagnostic($"Decoder test initialized {_source.TypeName} for '{_filePath}'.");
            await ShowFrameAsync(0);
        }
        catch (Exception ex)
        {
            source?.Dispose();
            _statusLabel.Text = $"初始化失败: {ex.Message}";
            Logger.Log(ex, $"初始化解码器测试 {_selectedDecoder.Key}", this);
            await DisplayAlertAsync("解码器测试", ex.Message, "确定");
        }
    }

    private async void FrameSlider_ValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_updatingSlider || _source is null) return;
        await ShowFrameAsync((uint)Math.Clamp(Math.Round(e.NewValue), 0d, (double)uint.MaxValue));
    }

    private async void PlayButton_Clicked(object? sender, EventArgs e)
    {
        if (_source is null) return;
        if (_playCts is not null)
        {
            StopPlayback();
            return;
        }

        var cts = new CancellationTokenSource();
        _playCts = cts;
        _playButton.Text = "暂停";
        try
        {
            while (!cts.IsCancellationRequested && _source is not null)
            {
                await ShowFrameAsync(_currentFrame, cts.Token);
                if (GetMaxFrame(_source) is uint max && _currentFrame >= max) break;
                _currentFrame = _currentFrame == uint.MaxValue ? 0 : _currentFrame + 1;
                var delay = _source.Fps > 0 ? TimeSpan.FromSeconds(1 / _source.Fps) : TimeSpan.FromMilliseconds(33);
                await Task.Delay(delay, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _statusLabel.Text = $"播放失败: {ex.Message}";
            Logger.Log(ex, "播放解码器测试", this);
        }
        finally
        {
            if (ReferenceEquals(_playCts, cts))
            {
                _playCts = null;
                _playButton.Text = "播放";
            }
            cts.Dispose();
        }
    }

    private void SeekBySeconds(double seconds)
    {
        if (_source is null) return;
        var frames = (long)Math.Round((_source.Fps > 0 ? _source.Fps : 30) * seconds);
        SeekByFrames(frames);
    }

    private void SeekByFrames(long delta)
    {
        if (_source is null) return;
        if (_playCts is not null) StopPlayback();
        var target = (long)_currentFrame + delta;
        if (GetMaxFrame(_source) is uint max) target = Math.Clamp(target, 0, (long)max);
        else target = Math.Clamp(target, 0, (long)uint.MaxValue);
        _ = ShowFrameAsync((uint)target);
    }

    private async Task ShowFrameAsync(uint frameIndex, CancellationToken cancellationToken = default)
    {
        var source = _source;
        if (source is null) return;
        var effect = _effect;

        var version = Interlocked.Increment(ref _renderVersion);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _renderCts, cts);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            await _decodeGate.WaitAsync(cts.Token);
            try
            {
                var bytes = await Task.Run(() =>
                {
                    using var picture = source.GetFrame(frameIndex);
                    var output = picture;
                    if (effect is INormalEffect normal)
                    {
                        var processed = normal.Render(picture, PluginManager.CreateComputer(effect.NeedComputer), picture.Width, picture.Height);
                        if (!ReferenceEquals(processed, picture)) output = processed;
                    }
                    using var stream = new MemoryStream();
                    try
                    {
                        output.SaveToPng(stream);
                        return stream.ToArray();
                    }
                    finally
                    {
                        if (!ReferenceEquals(output, picture)) output.Dispose();
                    }
                }, cts.Token);

                if (cts.IsCancellationRequested || version != _renderVersion || !ReferenceEquals(source, _source)) return;
                _frameImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
                _currentFrame = frameIndex;
                _updatingSlider = true;
                _frameSlider.Value = Math.Min(frameIndex, _frameSlider.Maximum);
                _updatingSlider = false;
                _frameLabel.Text = $"帧: {frameIndex}";
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _renderVersion)
            {
                _statusLabel.Text = $"读取帧 {frameIndex} 失败: {ex.Message}";
                Logger.Log(ex, $"读取解码器测试帧 {frameIndex}", this);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _renderCts, null, cts);
            cts.Dispose();
        }
    }

    private void SetPlaybackEnabled(bool enabled)
    {
        _playButton.IsEnabled = enabled;
        _back5Button.IsEnabled = enabled;
        _forward5Button.IsEnabled = enabled;
        _backFrameButton.IsEnabled = enabled;
        _forwardFrameButton.IsEnabled = enabled;
    }

    private void StopPlayback()
    {
        _playCts?.Cancel();
        _playCts = null;
        _playButton.Text = "播放";
        Interlocked.Increment(ref _renderVersion);
        CancelRender();
    }

    private async Task DisposeSourceAsync()
    {
        var source = _source;
        _source = null;
        _frameImage.Source = null;
        _frameSlider.IsEnabled = false;
        SetPlaybackEnabled(false);
        if (source is null) return;

        CancelRender();
        try
        {
            await _decodeGate.WaitAsync();
            try
            {
                source.Dispose();
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "释放解码器测试视频源", this);
        }
    }

    private static uint? GetMaxFrame(IVideoSource? source)
    {
        if (source is null || source.TotalFrames < 0) return null;
        if (source.TotalFrames == 0) return 0;
        return (uint)Math.Min(source.TotalFrames - 1, uint.MaxValue);
    }

    private void CancelRender()
    {
        try { _renderCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static string FormatTotalFrames(long totalFrames)
        => totalFrames switch
        {
            long.MinValue => "无限",
            < 0 => "未知",
            _ => totalFrames.ToString()
        };

    protected override void OnDisappearing()
    {
        StopPlayback();
        _ = DisposeSourceAsync();
        base.OnDisappearing();
    }
}
