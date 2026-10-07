using CommunityToolkit.Maui.Views;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Globalization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.DraftStuff;

public partial class ClipInfoBuilder
{
    private View BuildTransformTab(ClipElementUI clip)
    {
        var tabs = new CompactTabView();
        foreach (var side in Enum.GetValues<TransformSide>())
            tabs.TabItems.Add(new TabbedViewItem
            {
                Header = side == TransformSide.Left ? Localized.Transform_Left : Localized.Transform_Right,
                Tag = side.ToString(), LazyContentFactory = () => BuildTransformSide(clip, side)
            });
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
        bool audio = clip.ClipType == ClipMode.AudioClip;
        var host = new ContentView();
        var previewQueue = new SemaphoreSlim(1);
        Rebuild();
        return host;

        void Rebuild()
        {
            var infos = page.GetTransformClipInfos(audio);
            var existing = TransformProcessing.Find(infos, clip.Id, side);
            var saved = existing?.Provider;
            var owner = existing is { } found ? page.Clips[found.Owner.Id] : clip;
            var neighbors = page.FindTransformNeighbors(clip, audio);
            var neighbor = side == TransformSide.Left ? neighbors.left : neighbors.right;
            bool connected = neighbor is not null && (audio ? DraftPage.SupportsAudioTransform(neighbor) : DraftPage.SupportsPictureTransform(neighbor));
            bool savedDual = saved is not null && TransformProcessing.ReadEnum<TransformInputMode>(saved.MetaData, TransformProcessing.ModeKey) == TransformInputMode.TwoInput;
            bool sharedConnected = existing is { } shared && infos.Any(c => c.Id == TransformProcessing.ReadNextClip(shared.Provider.MetaData)
                && c.Duration > 0 && c.Start == shared.Owner.End && c.Layer == shared.Owner.Layer && c.SubLayer == shared.Owner.SubLayer);
            var ppb = new PropertyPanelBuilder();
            if (savedDual && !sharedConnected)
                ppb.AddText(new Label { Text = Localized.Transform_Disconnected });
            Picker? mode = null;
            var selector = new ContentView();
            if (saved is null)
            {
                ppb.AddPicker("InputMode", Localized.Transform_InputMode,
                    connected ? [Localized.Transform_OneInput, Localized.Transform_TwoInput] : [Localized.Transform_OneInput],
                    connected ? Localized.Transform_TwoInput : Localized.Transform_OneInput, c => mode = c);
                ppb.AddCustomChild(selector);
            }
            var parameters = new ContentView();
            if (saved is not null)
            {
                ppb.AddEntry("Duration", Localized.Transform_Duration,
                    TransformProcessing.ReadDuration(saved.MetaData).ToString(CultureInfo.InvariantCulture), "", c => c.Keyboard = Keyboard.Numeric);
                ppb.AddCheckbox("PreRender", Localized.Transform_PreRender,
                    TransformProcessing.ReadEnum<TransformRenderOrder>(saved.MetaData, TransformProcessing.OrderKey) == TransformRenderOrder.BeforeEffects);
                ppb.AddCheckbox("Enabled", PPLocalizedResources._Enabled, saved.Enabled);
                ppb.AddSeparator();
                ppb.AddCustomChild(parameters);
            }
            var error = new Label { TextColor = Colors.OrangeRed };
            ppb.AddText(error);
            var options = new Dictionary<string, Func<IEffectProvider>>();
            IEffectProvider? selected = null;

            TransformInputMode InputMode() => saved is not null
                ? TransformProcessing.ReadEnum<TransformInputMode>(saved.MetaData, TransformProcessing.ModeKey)
                : mode?.SelectedIndex == 1 ? TransformInputMode.TwoInput : TransformInputMode.OneInput;
            TransformRenderOrder RenderOrder() => saved is null ? TransformServices.DefaultRenderOrder :
                ((CheckBox)ppb.Components["PreRender"]).IsChecked ? TransformRenderOrder.BeforeEffects : TransformRenderOrder.AfterEffects;

            void LoadParameters()
            {
                parameters.Content = null;
                if (selected is null) return;
                var ui = EffectServices.GetUIProvider(selected);
                if (ui is IBindingHostHolder bindingHost)
                    bindingHost.BindingHost = new ClipBindingHost(owner, selected, page, LoadParameters);
                var panel = ui.CreateUI(selected);
                panel.PropertyChanged += (_, args) =>
                {
                    try
                    {
                        var update = ui.HandlePropertyPanelChange(selected, args);
                        if (update.newFields is not null) selected.Fields = update.newFields;
                        else if (update.newParams is not null)
                        {
                            var fields = selected.Fields;
                            foreach (var p in update.newParams)
                                fields[p.Key] = new StaticEffectArgumentField(p.Value,
                                    fields.GetValueOrDefault(p.Key)?.FieldType ?? EffectArgumentFieldType.Unknown) { Id = p.Key };
                            selected.Fields = fields;
                        }
                    }
                    catch (Exception ex) { error.Text = ex.Message; Log(ex, "Edit transform parameters", this); }
                };
                parameters.Content = panel.Build();
            }

            void LoadTypes()
            {
                options.Clear();
                selected = null;
                error.Text = "";
                var cards = new List<EffectProviderCardItem>();
                var inputMode = InputMode();
                var flag = inputMode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
                foreach (var option in TransformServices.GetAvailableTransforms(audio).OrderBy(o => o.Key))
                {
                    try
                    {
                        var provider = option.Value();
                        var prototype = TransformServices.Create(provider);
                        try
                        {
                            if (!prototype.Definition.HasFlag(audio ? TransformDefinition.Audio : TransformDefinition.Clip) || !prototype.Definition.HasFlag(flag)) continue;
                        }
                        finally { (prototype as IDisposable)?.Dispose(); }
                        options.Add(option.Key, option.Value);
                        var ui = EffectServices.GetUIProvider(provider);
                        var display = ui.GetDisplayItem(provider);
                        cards.Add(new EffectProviderCardItem
                        {
                            ProviderTypeName = option.Key, Title = TransformServices.GetTransformName(option.Key),
                            Description = ui.GetLocalizedEffectDescription(provider, PluginManager.CurrentLocale),
                            Thumbnail = display.Thumbnail,
                            EffectTypeName = inputMode == TransformInputMode.OneInput ? Localized.Transform_OneInput : Localized.Transform_TwoInput
                        });
                    }
                    catch (Exception ex) { Log(ex, $"Discover transform {option.Key}", this); }
                }
                var factories = options.ToDictionary();
                var previewProviders = new Dictionary<string, IEffectProvider>();
                selector.Content = BuildProviderPickerPanel(cards, page, null, async typeName =>
                {
                    try
                    {
                        selected = options[typeName]();
                        await Apply();
                    }
                    catch (Exception ex) { error.Text = ex.Message; Log(ex, $"Add transform {typeName} to {clip.Id}/{side}", this); }
                }, Localized.Transform_Type, Localized.Transform_Apply, Localized.Transform_NoAvailable, audio ? null : GeneratePreview);

                async Task<MediaSource?> GeneratePreview(EffectProviderCardItem item, CancellationToken token)
                {
                    await previewQueue.WaitAsync(token);
                    try
                    {
                        if (!previewProviders.TryGetValue(item.ProviderTypeName, out var provider))
                            previewProviders[item.ProviderTypeName] = provider = factories[item.ProviderTypeName]();
                        var path = await page.RenderTransformPreviewAsync(clip, side, inputMode, provider, token);
                        return string.IsNullOrWhiteSpace(path) ? null : MediaSource.FromFile(path);
                    }
                    finally { previewQueue.Release(); }
                }
            }

            IEffectProvider? Configure()
            {
                error.Text = "";
                if (selected is null) return null;
                if (saved is null)
                {
                    selected.Enabled = true;
                    selected.MetaData[TransformProcessing.DurationKey] = Math.Max(1u, (uint)Math.Round(page.ProjectInfo.TargetFrameRate / 2d));
                    selected.MetaData[TransformProcessing.OrderKey] = TransformServices.DefaultRenderOrder;
                    return selected;
                }
                if (!uint.TryParse(((Entry)ppb.Components["Duration"]).Text, out var frames) || frames == 0)
                {
                    error.Text = Localized.Transform_InvalidDuration;
                    return null;
                }
                selected.Enabled = ((CheckBox)ppb.Components["Enabled"]).IsChecked;
                selected.MetaData[TransformProcessing.DurationKey] = frames;
                selected.MetaData[TransformProcessing.OrderKey] = RenderOrder();
                return selected;
            }

            async Task Apply()
            {
                var provider = Configure();
                if (provider is null) return;
                try
                {
                    if (saved is not null)
                    {
                        owner.EffectProviders![saved.Id] = provider;
                        var resolved = TransformProcessing.Resolve(page.GetTransformClipInfos(audio), clip.Id, side);
                        if (resolved is { Duration: > 0 }) provider.MetaData[TransformProcessing.DurationKey] = resolved.Duration;
                        await page.NotifyTransformChanged(owner);
                        Log($"Updated transform {provider.Id} on {owner.Id}/{side}, {TransformProcessing.ReadDuration(provider.MetaData)} frames, {RenderOrder()}, enabled={provider.Enabled}.");
                    }
                    else if (!page.SetClipTransform(clip, side, InputMode(), provider, TransformProcessing.ReadDuration(provider.MetaData),
                        order: RenderOrder()))
                    {
                        error.Text = Localized.Transform_Disconnected;
                        return;
                    }
                    Rebuild();
                }
                catch (Exception ex) { error.Text = ex.Message; Log(ex, $"Apply transform to {clip.Id}/{side}", this); }
            }
            if (mode is not null) mode.SelectedIndexChanged += (_, _) => LoadTypes();
            if (saved is not null)
            {
                ppb.AddButton(Localized.Transform_Apply, async (_, _) => await Apply());
                ppb.AddButton(Localized.Transform_Delete, async (_, _) =>
                {
                    try { await page.DeleteClipTransform(clip, side); Rebuild(); }
                    catch (Exception ex) { error.Text = ex.Message; Log(ex, $"Delete transform from {clip.Id}/{side}", this); }
                });
            }
            if (!audio && saved is null && connected && page.AddClipView.BindingContext is ProjectAddClipViewModel ai)
            {
                ppb.AddSeparator();
                ppb.AddEntry("AIPrompt", Localized.DraftPage_AddClipView_AddTransform_AITransform, ai.AITransitionPrompt ?? "",
                    Localized.DraftPage_AddClipView_AIGC_InputPlaceholder,
                    c => c.SetBinding(Entry.TextProperty, new Binding(nameof(ai.AITransitionPrompt), source: ai, mode: BindingMode.TwoWay)));
                ppb.AddPicker("AIDuration", Localized.DraftPage_AddClipView_AIGC_Duration, ["1", "2", "3", "5"],
                    PickerSetter: c =>
                    {
                        c.SelectedIndex = ai.AITransitionDuration;
                        c.SelectedIndexChanged += (_, _) => ai.AITransitionDuration = c.SelectedIndex;
                    });
                ppb.AddButton("GenerateAI", Localized.DraftPage_AddClipView_AddTransform_AITransform);
                var generate = (Button)ppb.Components["GenerateAI"];
                generate.Clicked += async (_, _) =>
                {
                    generate.IsEnabled = false;
                    try
                    {
                        await page.SelectAClip(clip.Id);
                        await ai.GenerateAITransition(side == TransformSide.Left ? "left" : "right", RenderOrder());
                        Rebuild();
                    }
                    catch (Exception ex) { error.Text = ex.Message; Log(ex, $"Generate AI transform for {clip.Id}/{side}", this); }
                    finally { generate.IsEnabled = true; }
                };
            }
            host.Content = ppb.BuildWithScrollView();
            if (saved is null) LoadTypes();
            else
            {
                selected = EffectBindingHelper.MigrateToEffectProviders(
                    owner.EffectProviders!.Values.Select(EffectBindingHelper.SerializeProvider).ToArray(), null)[saved.Id];
                LoadParameters();
            }
        }
    }
}
