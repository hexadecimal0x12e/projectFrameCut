using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using CommunityToolkit.Maui.Views;
using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using Microsoft.Maui.Layouts;

namespace projectFrameCut.DraftStuff;

public partial class ClipInfoBuilder
{
    private View BuildTransformTab(ClipElementUI clip)
    {
        var tabs = new CompactTabView();
        foreach (var side in Enum.GetValues<TransformSide>())
        {
            tabs.TabItems.Add(new TabbedViewItem
            {
                Header = side == TransformSide.Left ? Localized.Transform_Left : Localized.Transform_Right,
                Tag = side.ToString(),
                LazyContentFactory = () => BuildTransformSide(clip, side)
            });
        }
        tabs.SelectByTag(page.SelectedTransformSide.ToString());
        tabs.OnTabSwitched += (_, item) =>
        {
            page.SelectedTransformSide = Enum.Parse<TransformSide>(item.Tag);
            item.Content = BuildTransformSide(clip, page.SelectedTransformSide);
        };
        return tabs;
    }

    private View BuildTransformSide(ClipElementUI clip, TransformSide side)
    {
        var host = new ContentView();
        var previewQueue = new SemaphoreSlim(1);
        ITransform? selected = null;
        host.Unloaded += (_, _) =>
        {
            if (selected is IDisposable disposable) disposable.Dispose();
            selected = null;
        };
        Rebuild();
        return host;

        void Rebuild()
        {
            if (selected is IDisposable disposable) disposable.Dispose();
            selected = null;
            var infos = page.GetTransformClipInfos();
            var existing = ClipTransforms.Find(infos, clip.Id, side);
            var binding = existing?.Binding;
            var neighbor = side == TransformSide.Left ? page.FindNeighbors(clip).left : page.FindNeighbors(clip).right;
            var connected = neighbor is not null && DraftPage.SupportsPictureTransform(neighbor);
            var content = new VerticalStackLayout { Spacing = 12, Padding = 12 };
            var status = new Label
            {
                Text = binding?.InputMode == TransformInputMode.TwoInput && ClipTransforms.Resolve(infos, clip.Id, side) is null
                    ? Localized.Transform_Disconnected : connected ? Localized.Transform_Connected : Localized.Transform_Unconnected
            };
            content.Children.Add(status);
            var mode = new Picker { Title = Localized.Transform_InputMode };
            mode.Items.Add(Localized.Transform_OneInput);
            if (connected || binding?.InputMode == TransformInputMode.TwoInput) mode.Items.Add(Localized.Transform_TwoInput);
            mode.SelectedIndex = binding?.InputMode == TransformInputMode.TwoInput || (binding is null && connected) ? 1 : 0;
            content.Children.Add(mode);
            var cards = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                Direction = FlexDirection.Row,
                JustifyContent = FlexJustify.Start,
                AlignItems = FlexAlignItems.Start,
                AlignContent = FlexAlignContent.Start
            };
            content.Children.Add(new Label { Text = Localized.Transform_Type });
            content.Children.Add(cards);
            int selectedIndex = -1;
            var parameters = new ContentView();
            content.Children.Add(parameters);
            var duration = new Entry
            {
                Keyboard = Keyboard.Numeric, Placeholder = Localized.Transform_Duration,
                Text = (binding?.Duration ?? Math.Max(1u, (uint)Math.Round(page.ProjectInfo.TargetFrameRate / 2d))).ToString(CultureInfo.InvariantCulture)
            };
            content.Children.Add(new Label { Text = Localized.Transform_Duration });
            content.Children.Add(duration);
            var error = new Label { TextColor = Colors.OrangeRed };
            content.Children.Add(error);
            var apply = new Button { Text = Localized.Transform_Apply };
            var preview = new Button { Text = Localized.Transform_Preview };
            var delete = new Button { Text = Localized.Transform_Delete, IsVisible = binding is not null };
            var previewHost = new ContentView();
            content.Children.Add(preview);
            content.Children.Add(previewHost);
            content.Children.Add(apply);
            content.Children.Add(delete);
            var options = new List<(string Key, string Name, Func<Guid, Guid, ITransform> Factory, TransformDefinition Definition)>();
            Dictionary<string, object> edits = new();
            var inputMode = mode.SelectedIndex == 1 ? TransformInputMode.TwoInput : TransformInputMode.OneInput;

            void LoadTypes()
            {
                inputMode = mode.SelectedIndex == 1 ? TransformInputMode.TwoInput : TransformInputMode.OneInput;
                options.Clear();
                cards.Children.Clear();
                selectedIndex = -1;
                var flag = inputMode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
                foreach (var option in TransformServices.GetAvailableTransforms())
                {
                    try
                    {
                        var prototype = option.Value(Guid.Empty, Guid.Empty);
                        if (prototype.Definition.HasFlag(TransformDefinition.Clip) && prototype.Definition.HasFlag(flag))
                        {
                            options.Add((option.Key, TransformServices.GetTransformName(option.Key), option.Value, prototype.Definition));
                        }
                        if (prototype is IDisposable disposable) disposable.Dispose();
                    }
                    catch (Exception ex) { Log(ex, $"Discover transform {option.Key}", this); }
                }
                // AI and configured plugin transforms may not have a catalog factory.
                if (binding is not null)
                {
                    try
                    {
                        var saved = ClipTransforms.Create(binding);
                        if (saved.Definition.HasFlag(flag) && !options.Any(o => o.Key == saved.TypeName))
                        {
                            options.Add((saved.TypeName, saved.Name, (_, _) => ClipTransforms.Create(binding), saved.Definition));
                        }
                        selectedIndex = options.FindIndex(o => o.Key == saved.TypeName);
                        if (saved is IDisposable disposable) disposable.Dispose();
                    }
                    catch (Exception ex) { Log(ex, $"Load configured transform {binding.Id}", this); error.Text = ex.Message; }
                }
                if (selectedIndex < 0 && options.Count > 0) selectedIndex = 0;
                for (int i = 0; i < options.Count; i++) cards.Children.Add(BuildCard(i));
                UpdateSelection();
                apply.IsEnabled = options.Count > 0;
                preview.IsEnabled = options.Count > 0;
                if (options.Count == 0) error.Text = Localized.Transform_NoAvailable;
                LoadParameters();
            }

            void UpdateSelection()
            {
                for (int i = 0; i < cards.Children.Count; i++)
                {
                    if (cards.Children[i] is not Border card) continue;
                    card.Stroke = new SolidColorBrush(i == selectedIndex ? Colors.DodgerBlue : Colors.Gray.WithAlpha(0.25f));
                    card.StrokeThickness = i == selectedIndex ? 2 : 1;
                }
            }

            View BuildCard(int index)
            {
                var option = options[index];
                var cardMode = inputMode;
                var media = new MediaElement
                {
                    Aspect = Aspect.AspectFit,
                    ShouldAutoPlay = true,
                    ShouldLoopPlayback = true,
                    ShouldMute = true,
                    ShouldShowPlaybackControls = false
                };
                var loading = new ActivityIndicator { IsRunning = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
                var card = new Border
                {
                    WidthRequest = 210,
                    HeightRequest = 160,
                    Margin = 6,
                    Padding = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Content = new Grid
                    {
                        BackgroundColor = Colors.Black,
                        Children =
                        {
                            media, loading,
                            new Label
                            {
                                Text = option.Name,
                                FontSize = 13,
                                TextColor = Colors.White,
                                BackgroundColor = Colors.Black.WithAlpha(0.6f),
                                Padding = new Thickness(8, 6),
                                VerticalOptions = LayoutOptions.End,
                                LineBreakMode = LineBreakMode.TailTruncation
                            }
                        }
                    }
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) =>
                {
                    selectedIndex = index;
                    UpdateSelection();
                    LoadParameters();
                };
                card.GestureRecognizers.Add(tap);
                bool loaded = false, generating = false;
                string? path = null;
                card.Loaded += async (_, _) =>
                {
                    loaded = true;
                    if (path is not null)
                    {
                        media.Source = path;
                        return;
                    }
                    if (generating || page.AddClipView.BindingContext is not ProjectAddClipViewModel vm) return;
                    generating = true;
                    await previewQueue.WaitAsync();
                    try
                    {
                        if (!loaded) return;
                        var item = new TransformItemViewModel { TypeKey = option.Key, DisplayName = option.Name };
                        var transform = binding is not null && binding.TransformElement.TryGetProperty("TypeName", out var type) && type.GetString() == option.Key
                            ? ClipTransforms.Create(binding) : option.Factory(clip.Id, neighbor?.Id ?? Guid.Empty);
                        try
                        {
                            transform.Side = side;
                            await vm.GenerateTransformPreviewAsync(item, cardMode, side, transform);
                            path = item.PreviewVideoPath;
                            if (loaded && !string.IsNullOrWhiteSpace(path)) media.Source = path;
                        }
                        finally { if (transform is IDisposable disposable) disposable.Dispose(); }
                    }
                    catch (Exception ex) { Log(ex, $"Preview transform card {option.Key}/{side}", this); }
                    finally
                    {
                        previewQueue.Release();
                        generating = false;
                        loading.IsRunning = false;
                        loading.IsVisible = false;
                    }
                };
                card.Unloaded += (_, _) =>
                {
                    loaded = false;
                    media.Stop();
                    media.Source = null;
                };
                return card;
            }

            void LoadParameters()
            {
                if (selected is IDisposable disposable) disposable.Dispose();
                selected = null;
                parameters.Content = null;
                if (selectedIndex < 0 || selectedIndex >= options.Count) return;
                try
                {
                    var option = options[selectedIndex];
                    selected = binding is not null && binding.TransformElement.TryGetProperty("TypeName", out var type) && type.GetString() == option.Key
                        ? ClipTransforms.Create(binding) : option.Factory(clip.Id, neighbor?.Id ?? Guid.Empty);
                    selected.Side = side;
                    edits = new(selected.Parameters);
                    var panel = new PropertyPanelBuilder();
                    foreach (var key in selected.ParametersType.Keys.Union(selected.ParametersNeeded).Union(edits.Keys).Distinct())
                    {
                        edits.TryGetValue(key, out var value);
                        if (value is bool b) panel.AddSwitch(key, key, b);
                        else panel.AddEntry(key, key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", key);
                    }
                    panel.PropertyChanged += (_, e) => edits[e.Id] = e.Value!;
                    parameters.Content = panel.BuildWithScrollView();
                }
                catch (Exception ex) { error.Text = ex.Message; Log(ex, $"Configure transform for {clip.Id}/{side}", this); }
            }

            ITransform? Configure()
            {
                error.Text = "";
                if (selected is null) return null;
                if (!uint.TryParse(duration.Text, out var frames) || frames == 0)
                {
                    error.Text = Localized.Transform_InvalidDuration;
                    return null;
                }
                try
                {
                    selected.Parameters = edits.ToDictionary(p => p.Key, p => ConvertParameter(p.Value,
                        selected.ParametersType.GetValueOrDefault(p.Key, "string")));
                    selected.Duration = frames;
                    selected.Side = side;
                    return selected;
                }
                catch (Exception ex) { error.Text = Localized.Transform_InvalidParameters; Log(ex, "Read transform parameters", this); return null; }
            }

            mode.SelectedIndexChanged += (_, _) => LoadTypes();
            apply.Clicked += async (_, _) =>
            {
                var transform = Configure();
                if (transform is null) return;
                try
                {
                    if (binding is not null && binding.InputMode == inputMode &&
                        binding.TransformElement.GetProperty("TypeName").GetString() == transform.TypeName)
                    {
                        binding.Duration = transform.Duration;
                        transform.Side = binding.Side;
                        transform.Init();
                        binding.Capture(transform);
                        var owner = page.Clips[existing!.Value.Owner.Id];
                        TransformBinding.Write(owner.ExtraData, binding.Side, binding);
                        var resolved = ClipTransforms.Resolve(page.GetTransformClipInfos(), clip.Id, side);
                        if (resolved is { Duration: > 0 })
                        {
                            binding.Duration = resolved.Duration;
                            TransformBinding.Write(owner.ExtraData, binding.Side, binding);
                        }
                        await page.NotifyTransformChanged(owner);
                    }
                    else if (!page.SetClipTransform(clip, side, inputMode, transform, transform.Duration))
                    {
                        error.Text = Localized.Transform_Disconnected;
                        return;
                    }
                    Rebuild();
                }
                catch (Exception ex) { error.Text = ex.Message; Log(ex, $"Apply transform to {clip.Id}/{side}", this); }
            };
            preview.Clicked += async (_, _) =>
            {
                var transform = Configure();
                if (transform is null || page.AddClipView.BindingContext is not ProjectAddClipViewModel vm) return;
                var item = new TransformItemViewModel() { TypeKey = transform.TypeName, DisplayName = transform.Name };
                preview.IsEnabled = false;
                try
                {
                    await vm.GenerateTransformPreviewAsync(item, inputMode, side, transform);
                    if (!string.IsNullOrWhiteSpace(item.PreviewVideoPath))
                        previewHost.Content = new MediaElement { Source = item.PreviewVideoPath, ShouldAutoPlay = true, ShouldShowPlaybackControls = true, HeightRequest = 180 };
                }
                finally { preview.IsEnabled = true; }
            };
            delete.Clicked += async (_, _) => { await page.DeleteClipTransform(clip, side); Rebuild(); };
            if (connected && page.AddClipView.BindingContext is ProjectAddClipViewModel ai)
            {
                var prompt = new Entry { Placeholder = Localized.DraftPage_AddClipView_AIGC_InputPlaceholder };
                prompt.SetBinding(Entry.TextProperty, new Binding(nameof(ai.AITransitionPrompt), source: ai, mode: BindingMode.TwoWay));
                var aiDuration = new Picker { Title = Localized.DraftPage_AddClipView_AIGC_Duration };
                foreach (var value in new[] { "1", "2", "3", "5" }) aiDuration.Items.Add(value);
                aiDuration.SelectedIndex = ai.AITransitionDuration;
                aiDuration.SelectedIndexChanged += (_, _) => ai.AITransitionDuration = aiDuration.SelectedIndex;
                var generate = new Button { Text = Localized.DraftPage_AddClipView_AddTransform_AITransform };
                generate.Clicked += async (_, _) =>
                {
                    generate.IsEnabled = false;
                    try { await page.SelectAClip(clip.Id); await ai.GenerateAITransition(side == TransformSide.Left ? "left" : "right"); }
                    finally { generate.IsEnabled = true; }
                };
                content.Children.Add(prompt);
                content.Children.Add(aiDuration);
                content.Children.Add(generate);
            }
            host.Content = new ScrollView { Content = content };
            LoadTypes();
        }
    }

    private static object ConvertParameter(object value, string type)
    {
        if (value is JsonElement json) value = json.ToString();
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return type.ToLowerInvariant() switch
        {
            "bool" or "boolean" or "system.boolean" => bool.Parse(text),
            "int" or "int32" or "system.int32" => int.Parse(text, CultureInfo.InvariantCulture),
            "uint" or "uint32" or "system.uint32" => uint.Parse(text, CultureInfo.InvariantCulture),
            "float" or "single" or "system.single" => float.Parse(text, CultureInfo.InvariantCulture),
            "double" or "system.double" => double.Parse(text, CultureInfo.InvariantCulture),
            _ => text
        };
    }
}
