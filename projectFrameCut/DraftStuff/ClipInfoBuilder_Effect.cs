using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics.Text;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platform;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Converters;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;
using ContentView = Microsoft.Maui.Controls.ContentView;
using CornerRadius = Microsoft.Maui.CornerRadius;
using DataTemplate = Microsoft.Maui.Controls.DataTemplate;
using Environment = System.Environment;
using GridLength = Microsoft.Maui.GridLength;
using GridUnitType = Microsoft.Maui.GridUnitType;
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
        #region effect
        public static void RebuildAllEffects(ClipElementUI clip, bool diag = false)
        {
            clip.EffectProviders ??= new();
            RenderOutputProvider.Ensure(clip.EffectProviders);
            EffectGroupBindingGuard.Synchronize(clip);
            if (clip.Effects?.TryGetValue(InternalRotationID, out var rotation) == true && rotation.TypeName == "Rotation")
            {
                if (rotation.Enabled) clip.Rotation += ReadEffectFloatParameter(rotation, "Angle", 0);
                clip.Rotation = VideoClipRotation.Normalize(clip.Rotation);
                clip.Effects.Remove(InternalRotationID);
                Log($"Migrated rotation for clip {clip.Id}: {clip.Rotation} degrees.");
            }
            var effects = EffectBindingHelper.RebuildAllEffects(clip.EffectProviders, clip.Effects, extraData: clip.ExtraData);
            if (effects != null) clip.Effects = effects;
        }

        public Task<View> BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
            => BuildEffectStageTab(clip, handler, null);

        public async Task<View> BuildEffectStageTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler, EffectStage? selectedStage)
        {
            ArgumentNullException.ThrowIfNull(clip);
            EffectStageUI.Resolve(clip, page);
            if (selectedStage is null)
            {
                var items = new List<TabbedViewItem>();
                foreach (var stage in clip.SupportedStages.Where(s => s != EffectStage.SourceReplacement))
                    items.Add(new TabbedViewItem
                    {
                        Header = EffectStageUI.Name(stage),
                        Tag = stage.ToString(),
                        LazyAsyncContentFactory = () => BuildEffectStageTab(clip, handler, stage)
                    });
                int selectedIndex = Math.Max(0, items.FindIndex(item => item.Tag == clip.ExtraData.GetValueOrDefault("__EffectStageTab__")?.ToString()));
                if (page.IsGuiRpcRequest && items.Count > 0) await LoadTabContentAsync(items[selectedIndex]);
                var tabs = new CompactTabView
                {
                    SelectedIndex = selectedIndex,
                    TabItems = new(items),
                };
                tabs.OnTabSwitched += (_, item) => RunPanelAction(() => clip.ExtraData["__EffectStageTab__"] = item.Tag);
                return tabs;
            }
            return (await BuildEffectStagePanel(clip, handler, selectedStage.Value)).BuildWithScrollView();
        }

        private async Task<PropertyPanelBuilder> BuildEffectStagePanel(ClipElementUI clip,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler, EffectStage stage)
        {
            var sw = Stopwatch.StartNew();
            var selectionTarget = clip.GetEffectSelectionTarget(stage);
            PropertyPanelBuilder ppb = new();
            var providerSnapshot = new Dictionary<Guid, IEffectProvider>(clip.EffectProviders ?? []);
            var localizedProviderNames = EffectServices.GetLocalizedEffectProviderNames("", false);
            foreach (var p in providerSnapshot.Values)
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = localizedProviderNames.GetValueOrDefault(p.TypeName, p.TypeName);
            var types = providerSnapshot.Count > 0 ? await EffectFieldTypeResolution.ResolveAsync(providerSnapshot) : null;
            var bindingDiagnostics = await Task.Run(() => EffectBindingHelper.ValidateBindings(providerSnapshot, types, DynamicEffectCalculation.Enabled(clip.ExtraData)));
            var diagnosticsByProvider = bindingDiagnostics.Where(d => d.ProviderId.HasValue).ToLookup(d => d.ProviderId!.Value);
            var mixturePlaceholder = stage == EffectStage.Composition && clip.ClipType != ClipMode.AudioClip
                && !providerSnapshot.Values.Any(p => p.TypeOfEffect == EffectType.MixtureProvider)
                ? new ClassicOverlayMixtureProvider() : null;

            if (bindingDiagnostics.Count > 0)
            {
                ppb.AddText(new Label
                {
                    Text = string.Join(Environment.NewLine, bindingDiagnostics.Select(d => $"⚠ [{d.Code}] {d.Message}")),
                    TextColor = Colors.OrangeRed,
                    FontAttributes = FontAttributes.Bold,
                    LineBreakMode = LineBreakMode.WordWrap
                });
                ppb.AddSeparator();
            }

            ppb.AddButton(PPLocalizedResources.EffectBind_Title, async (s, e) => await RunPanelActionAsync(async () =>
            {
                try
                {
                    var bindView = new DraftEffectBindingView();
                    bindView.LoadStage(clip, page, showAllEffect, stage);
                    bindView.EffectProvidersChanged += () =>
                        RunPanelAction(() => handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__EFFECT_BINDING_CHANGED__", clip, null)));
                    bool completed = false;
                    async Task CompleteEditing()
                    {
                        if (completed) return;
                        completed = true;
                        try
                        {
                            await bindView.CompleteEditingAsync();
                        }
                        catch (Exception ex) { Log(ex, $"Close effect binding editor for {clip.Id}", this); }
                    }
                    if (page.IsMobileForm)
                    {
                        page.Popup.Content = bindView;
                        void OnPopupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => RunPanelAction(() =>
                        {
                            if (args.PropertyName != Border.ContentProperty.PropertyName || ReferenceEquals(page.Popup.Content, bindView)) return;
                            page.Popup.PropertyChanged -= OnPopupChanged;
                            _ = CompleteEditing();
                        });
                        page.Popup.PropertyChanged += OnPopupChanged;
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
                        v.CloseClicked += async (s, e) => await RunPanelActionAsync(async () =>
                        {
                            if (!e.Cancel) await CompleteEditing();
                        });
                        bindView.Unloaded += (_, _) => RunPanelAction(() => bindView.Dispatcher.Dispatch(async () => await RunPanelActionAsync(async () =>
                        {
                            if (v.Parent is null && !v.IsInStandaloneWindowMode) await CompleteEditing();
                        })));
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"Show effect binding view for {clip.DisplayName}", this);
                    await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                    if (Debugger.IsAttached) throw;
                }

            }));
            var bundlesFactories = EffectServices.GetAvailableEffectProviders(provider =>
                provider.SupportedStages.Contains(stage)
                && (stage != EffectStage.SourceReplacement || provider.TypeOfEffect == EffectType.SourceReplacement));
            var inputSources = new Dictionary<(Guid ProviderId, string Display), string>();
            var inputDisplayNames = new Dictionary<string, string>();
            var inputEditVersions = new Dictionary<Guid, int>();
            var haveManySpeedVarianceProvider = (clip.EffectProviders?.Count(c => c.Value.TypeOfEffect.HasFlag(EffectType.SpeedVarianceProvider)) ?? 0) >= 2;
            if (providerSnapshot.Count > 0 || mixturePlaceholder is not null)
            {
                var filteredProviders = providerSnapshot
                     .Where(c => c.Value.Stage == stage
                         && (stage != EffectStage.SourceReplacement || c.Value.TypeOfEffect == EffectType.SourceReplacement)
                         && c.Value.TypeOfEffect != EffectType.Transform && (
                         showAllEffect
                         || (!c.Value.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)
                              && (c.Value.Target.HasFlag(EffectTarget.ValueProvider)
                                  || EffectBindingHelper.AreTargetsCompatible(c.Value.Target,
                                      stage == EffectStage.SourceReplacement ? selectionTarget : clip.GetEffectTarget())))
                         || (c.Value.Target == EffectTarget.SpeedVariance && haveManySpeedVarianceProvider)
                         || (c.Value.Target == EffectTarget.Mixture && stage == EffectStage.Composition)))
                     .ToList();
                if (mixturePlaceholder is not null) filteredProviders.Add(new(mixturePlaceholder.Id, mixturePlaceholder));

                // Sort providers by their single stored picture input. Fan-out is allowed.
                var sortedProviders = new List<KeyValuePair<Guid, IEffectProvider>>();
                var providersById = filteredProviders.ToDictionary(p => p.Key);
                var consumers = filteredProviders.ToLookup(p => EffectProviderOutputExtensions.TryParseOutputSourceId(p.Value.GetMainInputSource(), out var id, out _)
                    ? id : IEffectProvider.NoConnectionGUID);
                var visitedIds = new HashSet<Guid>();
                var traverseQueue = new Queue<Guid>();
                foreach (var b in filteredProviders)
                {
                    if (b.Value.GetMainInputSource() == IEffectProvider.InputAnchorGUID.ToString())
                    {
                        traverseQueue.Enqueue(b.Key);
                    }
                }
                while (traverseQueue.Count > 0)
                {
                    var id = traverseQueue.Dequeue();
                    if (!visitedIds.Add(id)) continue;
                    sortedProviders.Add(providersById[id]);
                    foreach (var b in consumers[id])
                    {
                        if (!visitedIds.Contains(b.Key))
                        {
                            traverseQueue.Enqueue(b.Key);
                        }
                    }
                }
                // Append any remaining bundles not connected to the main chain
                foreach (var b in filteredProviders)
                {
                    if (!visitedIds.Contains(b.Key))
                        sortedProviders.Add(b);
                }

                var inputOptions = types is null ? new Dictionary<Guid, List<string>>()
                    : await Task.Run(() => BuildInputCandidateSources(sortedProviders.Select(p => p.Value).ToArray(), providerSnapshot, types, showAllEffect, DynamicEffectCalculation.Enabled(clip.ExtraData)));
                var outputTargets = new Dictionary<string, List<string>>();
                foreach (var p in providerSnapshot.Values)
                {
                    AddOutputTarget(p.GetMainInputSource(), $"{p.Name} ({p.Id}) [{PPLocalizedResources.EffectBind_InputAnchor}]");
                    foreach (var binding in p.EnumerateFieldBindings())
                        AddOutputTarget(binding.Value, $"{p.Name} ({p.Id}) [{binding.Key}]");
                }
                void AddOutputTarget(string source, string display)
                {
                    if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _) || !providerSnapshot.ContainsKey(id)) return;
                    if (!outputTargets.TryGetValue(source, out var targets)) outputTargets[source] = targets = [];
                    targets.Add(display);
                }
                Log($"Prepared effect panel for {clip.Id}, stage {stage}, providers {providerSnapshot.Count}, candidates {inputOptions.Values.Sum(o => o.Count)}, elapsed {sw.ElapsedMilliseconds} ms.", "debug");

                foreach (var bundleKvp in sortedProviders)
                {
                    if (bundleKvp.Key != sortedProviders[0].Key) ppb.AddSeparator();
                    var bundleId = bundleKvp.Key;
                    var bundleInstance = bundleKvp.Value;
                    var locedName = localizedProviderNames.GetValueOrDefault(bundleInstance.TypeName, bundleInstance.TypeName);
                    var locedType = bundleInstance.TypeOfEffect switch
                    {
                        Shared.EffectType.ContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                        Shared.EffectType.AudioContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                        // Deprecated effect types are not used in the new system, but we still provide a localized name for them.
                        (EffectType)2 => PPLocalizedResources.Effect_BindableArgsEffect,
                        (EffectType)5 => PPLocalizedResources.Effect_BindableArgsEffect,
                        Shared.EffectType.VectorPictureEffect => Localized.Effect_VectorPictureEffect,
                        Shared.EffectType.VectorComponentEffect => Localized.Effect_VectorComponentEffect,
                        Shared.EffectType.TextEffect => PPLocalizedResources.Effect_TextEffect,
                        Shared.EffectType.ContinuousTextEffect => PPLocalizedResources.Effect_ContinuousTextEffect,
                        Shared.EffectType.ClipPositionProvider => Localized.EffectType_ClipPosition,
                        _ => PPLocalizedResources.Effect_GeneralEffect,
                    };
                    if (string.IsNullOrWhiteSpace(bundleInstance.Name)) bundleInstance.Name = locedName;

                    string GetInputAnchorSelection(string id)
                    {
                        HashSet<Guid>? visited = null;
                        while (EffectProviderOutputExtensions.TryParseOutputSourceId(id, out var hiddenId, out _)
                            && providerSnapshot.TryGetValue(hiddenId, out var hidden) && !showAllEffect
                            && hidden.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor))
                        {
                            visited ??= [];
                            if (!visited.Add(hiddenId)) return PPLocalizedResources.EffectBind_NoConnection;
                            id = hidden.GetMainInputSource();
                        }
                        if (id == IEffectProvider.NoConnectionGUID.ToString()) return PPLocalizedResources.EffectBind_NoConnection;
                        if (id == IEffectProvider.InputAnchorGUID.ToString()) return PPLocalizedResources.EffectBind_SourcePicture;
                        if (id == ValueProviderFrameContext.BuiltInFrameProviderId) return PPLocalizedResources.EffectBindView_BoundSource_FrameIndex;
                        if (id == ValueProviderFrameContext.BuiltInProgressProviderId) return PPLocalizedResources.EffectBindView_BoundSource_Progress;
                        if (id == ValueProviderFrameContext.ClipAudioProviderId) return Localized.Effect_RenderSourceAudio;
                        if (id == ValueProviderFrameContext.ClipPositionProviderId) return Localized.Effect_RenderSourcePosition;
                        if (id == ValueProviderFrameContext.BuiltInClipFrameProviderId) return Localized.Effect_ClipCurrentFrame;
                        if (id == ValueProviderFrameContext.BuiltInProjectProgressProviderId) return Localized.Effect_ProjectProgress;
                        if (id == ValueProviderFrameContext.BuiltInAudioSampleProviderId) return Localized.Effect_GlobalMix;
                        if (EffectProviderOutputExtensions.TryParseOutputSourceId(id, out var providerId, out var outputId)
                            && providerSnapshot.TryGetValue(providerId, out var b))
                        {
                            if (!inputDisplayNames.TryGetValue(id, out var display))
                                inputDisplayNames[id] = display = outputId is null ? $"{b.Name} ({b.Id})" : $"{b.Name} [{outputId}] ({b.Id})";
                            inputSources[(bundleId, display)] = id;
                            return display;
                        }
                        return string.Empty;
                    }

                    try
                    {
                        // Inject the binding host so each field can offer a bind action
                        // even outside the node binding view.
                        var bundleUI = EffectServices.GetUIProvider(bundleInstance);
                        if (bundleUI is IBindingHostHolder bundleBindingHostHolder)
                        {
                            bundleBindingHostHolder.BindingHost = new ClipBindingHost(clip, bundleInstance, page,
                                onChanged: () =>
                                {
                                    if (ReferenceEquals(bundleInstance, mixturePlaceholder)
                                        && !TryMaterializeMixturePlaceholder(clip, bundleInstance))
                                    {
                                        handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        return;
                                    }
                                    RebuildAllEffects(clip, false);
                                    handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                });
                        }

                        var bundlePpb = bundleUI.CreateUI(bundleInstance);

                        ppb.AddText(new TitleAndDescriptionLineLabel(locedName, locedType));
                        var providerDiagnostics = diagnosticsByProvider[bundleId];
                        if (providerDiagnostics.Any())
                        {
                            ppb.AddText(new Label
                            {
                                Text = string.Join(Environment.NewLine, providerDiagnostics.Select(d => $"⚠ [{d.Code}] {d.Message}")),
                                TextColor = Colors.OrangeRed,
                                FontAttributes = FontAttributes.Bold,
                                LineBreakMode = LineBreakMode.WordWrap
                            });
                        }
                        ppb.AddCheckbox($"Provider|{bundleId}|Enabled", PPLocalizedResources._Enabled, bundleInstance.Enabled);
                        ppb.AddEntry($"Provider|{bundleId}|Name", Localized.VectorContentEditorView_Name, bundleInstance.Name ?? locedName, locedName);

                        if (!bundleInstance.Target.HasFlag(EffectTarget.IsKeyFramed))
                        {
                            ppb.AddSeparator();

                            ppb.AddFromAnother(bundlePpb, bundleInstance);
                        }

                        AddContinuousEffectRangeUI(ppb, bundleInstance, clip, page);

                        //they can't be reordered
                        if (bundleInstance.TypeOfEffect is EffectType.Transform or EffectType.SpeedVarianceProvider or EffectType.MixtureProvider or EffectType.SourceReplacement) goto remove_btn;

                        ppb.AddSeparator();

                        var resolvedInAnchorId = bundleInstance.GetMainInputSource();
                        var inAnchorProviderOptions = inputOptions.GetValueOrDefault(bundleId, []).Select(GetInputAnchorSelection).ToList();
                        var curIn = GetInputAnchorSelection(resolvedInAnchorId);
                        if (curIn is not null && !inAnchorProviderOptions.Contains(curIn))
                            inAnchorProviderOptions.Add(curIn);

                        if (bundleInstance.InFields.ContainsKey(EffectProviderAnchorExtensions.InputKey))
                        {
                            ppb.AddPicker($"Provider|{bundleId}|InAnchor", PPLocalizedResources.EffectBind_InputAnchor,
                                inAnchorProviderOptions.Append(PPLocalizedResources.EffectBind_NoConnection).Distinct().ToArray(), curIn);
                        }
                        if (bundleInstance is IMultipleOutputEffectProvider multiple)
                        {
                            foreach (var output in multiple.OutFields.Values)
                            {
                                string source = EffectProviderOutputExtensions.CreateOutputSourceId(bundleId, output.Id);
                                var targets = new List<string>();
                                if (bundleInstance.IsFinalOutputSource() && bundleInstance.GetFinalOutputFieldId() == output.Id)
                                    targets.Add(PPLocalizedResources.EffectBind_FinalResult);
                                if (outputTargets.TryGetValue(source, out var boundTargets)) targets.AddRange(boundTargets);
                                string current = targets.Count > 0 ? string.Join("; ", targets) : PPLocalizedResources.EffectBind_NoConnection;
                                var options = new List<string> { current };
                                if ((types?.GetOutputType(bundleInstance, output.Id) ?? output.FieldType).IsContent()) options.Add(PPLocalizedResources.EffectBind_FinalResult);
                                options.Add(PPLocalizedResources.EffectBind_NoConnection);
                                ppb.AddPicker($"Provider|{bundleId}|OutAnchor|{Uri.EscapeDataString(output.Id)}",
                                    $"{PPLocalizedResources.EffectBind_OutputAnchor} ({output.Id})", options.Distinct().ToArray(), current);
                            }
                        }
                        else if ((types?.GetOutputType(bundleInstance) ?? bundleInstance.OutField.FieldType).IsContent())
                            ppb.AddPicker($"Provider|{bundleId}|OutAnchor", PPLocalizedResources.EffectBind_OutputAnchor,
                                [PPLocalizedResources.EffectBind_FinalResult, PPLocalizedResources.EffectBind_NoConnection],
                                bundleInstance.IsFinalOutputSource() ? PPLocalizedResources.EffectBind_FinalResult : PPLocalizedResources.EffectBind_NoConnection);

                    remove_btn:
                        if (stage != EffectStage.Composition || bundleInstance.TypeOfEffect is not (EffectType.Transform or EffectType.MixtureProvider))
                            ppb.AddButton($"Provider|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
                    }
                    catch (Exception ex)
                    {
                        if (page.IsGuiRpcRequest) throw;
                        if (Debugger.IsAttached)
                        {
                            if (Microsoft.Maui.Controls.Application.Current?.Windows?.First()?.Page is Page page)
                            {
                                if (await page.DisplayAlertAsync(Localized._Error, $"Error loading bundle {bundleInstance.TypeName}: {ex.Message}", "Throw", Localized._OK)) throw;
                            }
                        }
                        Log(ex, $"loading bundle {bundleInstance.TypeName}", this);
                        ppb.AddText(new Label { Text = $"Error loading bundle {bundleInstance.TypeName}: {ex.Message}", TextColor = Colors.Yellow });
                    }
                }
            }

            if (stage != EffectStage.Composition)
            {
                ppb.AddSeparator();
                ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
                ppb.AddCustomChild(BuildAddEffectPanel(selectionTarget, page, bundlesFactories, ppb, handler, hideKeyFramedProviders: true));
            }

            static bool TryParseAnchorSelection(string? selection, string anchorLabel, Guid anchorGuid, out Guid id)
            {
                if (string.IsNullOrWhiteSpace(selection))
                {
                    id = IEffectProvider.NoConnectionGUID;
                    return false;
                }

                if (selection == PPLocalizedResources.EffectBind_NoConnection)
                {
                    id = IEffectProvider.NoConnectionGUID;
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

                id = IEffectProvider.NoConnectionGUID;
                return false;
            }

            ppb.PropertyChanged += GuardPanelChangeAsync(async (s, e) =>
            {
                ArgumentNullException.ThrowIfNull(clip);
                clip.EffectProviders ??= new();

                if (e.Id.StartsWith(ContinuousEffectRangePropertyPrefix, StringComparison.Ordinal))
                {
                    RebuildAllEffects(clip);
                    handler?.Invoke(s, e);
                    return;
                }

                if (!ppb.Equals(s)) //from another
                {
                    if (s is IEffectProvider eb)
                    {
                        bool materializing = ReferenceEquals(eb, mixturePlaceholder) && !clip.EffectProviders.ContainsKey(eb.Id);
                        if (materializing && Equals(e.Value, e.OriginValue)) return;
                        if (materializing && !TryMaterializeMixturePlaceholder(clip, eb))
                        {
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            return;
                        }
                        var fieldUpdate = EffectServices.GetUIProvider(eb).HandlePropertyPanelChange(eb, e);
                        var data = fieldUpdate.newParams;
                        var newFields = fieldUpdate.newFields;
                        IEffectProvider? bundle = null;
                        if (data != null || newFields != null)
                        {
                            if (!clip?.EffectProviders?.TryGetValue(eb.Id, out bundle) ?? false) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            if (bundle is null) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            if (newFields is not null)
                            {
                                bundle.Fields = newFields;
                            }
                            else if (data is { Count: > 0 })
                            {
                                var fields = new Dictionary<string, IEffectArgumentField>();
                                foreach (var kvp in data)
                                {
                                    fields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                                }
                                bundle.Fields = fields;
                            }

                        }
                        RebuildAllEffects(clip);
                        handler?.Invoke(s, materializing
                            ? new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null) : e);
                    }
                }
                else
                {
                    if (e.Id.StartsWith("Provider|"))
                    {
                        var parts = e.Id.Split('|');
                        if (parts.Length >= 3)
                        {
                            Guid bundleId = new(parts[1]);
                            string action = parts[2];
                            if (mixturePlaceholder?.Id == bundleId && Equals(e.Value, e.OriginValue)) return;
                            if (mixturePlaceholder is not null && mixturePlaceholder.Id == bundleId
                                && !TryMaterializeMixturePlaceholder(clip, mixturePlaceholder))
                            {
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                return;
                            }
                            if (!clip.EffectProviders?.ContainsKey(bundleId) ?? false) return;

                            switch (action)
                            {
                                case "Remove":
                                    if (clip.EffectProviders is { } providersToEdit)
                                        EffectBindingHelper.RemoveProvider(providersToEdit, bundleId);
                                    RebuildAllEffects(clip);
                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    break;
                                case "Name":
                                    if (clip.EffectProviders.TryGetValue(bundleId, out var nameProvider))
                                    {
                                        var locedName = localizedProviderNames.GetValueOrDefault(nameProvider.TypeName, nameProvider.TypeName);
                                        var newName = e.Value?.ToString();
                                        nameProvider.Name = string.IsNullOrWhiteSpace(newName) ? locedName : newName;
                                        if (mixturePlaceholder?.Id == bundleId) RebuildAllEffects(clip);
                                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    }
                                    break;
                                case "Enabled":
                                    if (clip.EffectProviders.TryGetValue(bundleId, out var enabledProvider))
                                    {
                                        if (bool.TryParse(e.Value?.ToString(), out var enabled))
                                        {
                                            clip.EffectProviders[bundleId].Enabled = enabled;
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                                case "InAnchor":
                                    if (clip.EffectProviders.TryGetValue(bundleId, out var inProvider))
                                    {
                                        string? source = e.Value?.ToString() switch
                                        {
                                            var v when v == PPLocalizedResources.EffectBindView_BoundSource_FrameIndex => ValueProviderFrameContext.BuiltInFrameProviderId,
                                            var v when v == PPLocalizedResources.EffectBindView_BoundSource_Progress => ValueProviderFrameContext.BuiltInProgressProviderId,
                                            var v when v == Localized.Effect_RenderSourceAudio => ValueProviderFrameContext.ClipAudioProviderId,
                                            var v when v == Localized.Effect_RenderSourcePosition => ValueProviderFrameContext.ClipPositionProviderId,
                                            var v when v == Localized.Effect_ClipCurrentFrame => ValueProviderFrameContext.BuiltInClipFrameProviderId,
                                            var v when v == Localized.Effect_ProjectProgress => ValueProviderFrameContext.BuiltInProjectProgressProviderId,
                                            var v when v == Localized.Effect_GlobalMix => ValueProviderFrameContext.BuiltInAudioSampleProviderId,
                                            _ => null,
                                        };
                                        Guid newSourceId = IEffectProvider.NoConnectionGUID;
                                        if (inputSources.TryGetValue((bundleId, e.Value?.ToString() ?? ""), out var selectedSource)) source = selectedSource;
                                        if (source is not null || TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_SourcePicture, IEffectProvider.InputAnchorGUID, out newSourceId))
                                        {
                                            int version = inputEditVersions[bundleId] = inputEditVersions.GetValueOrDefault(bundleId) + 1;
                                            source ??= newSourceId.ToString();
                                            if (source != IEffectProvider.NoConnectionGUID.ToString())
                                            {
                                                try
                                                {
                                                    var candidate = await EffectFieldTypeResolution.ResolveAsync(clip.EffectProviders, bindingOverrides: new Dictionary<Guid, IReadOnlyDictionary<string, string>>
                                                    {
                                                        [bundleId] = new Dictionary<string, string>(inProvider.AnchorsBindingState) { [EffectProviderAnchorExtensions.InputKey] = source },
                                                    });
                                                    if (version != inputEditVersions[bundleId] || !ReferenceEquals(clip.EffectProviders?.GetValueOrDefault(bundleId), inProvider)) return;
                                                    if (!candidate.AreInputsCompatible(inProvider) || DynamicEffectCalculation.HasTypeErrors(candidate, clip.ExtraData))
                                                    {
                                                        Log($"Rejected effect input {source} for provider {bundleId} on clip {clip.Id}.", "warning");
                                                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                                        return;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Log(ex, $"Resolve effect input for provider {bundleId} on clip {clip.Id}", this);
                                                    page.SetStatusText(ex.Message);
                                                    return;
                                                }
                                            }
                                            inProvider.SetMainInputSource(source);
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                                case "OutAnchor":
                                    if (clip.EffectProviders.TryGetValue(bundleId, out var outProvider))
                                    {
                                        if (outProvider is IMultipleOutputEffectProvider multiple)
                                        {
                                            if (parts.Length != 4 || !multiple.OutFields.TryGetValue(Uri.UnescapeDataString(parts[3]), out var output)) break;
                                            if (e.Value?.ToString() == PPLocalizedResources.EffectBind_FinalResult)
                                            {
                                                EffectBindingHelper.SetFinalOutput(clip.EffectProviders, bundleId,
                                                    outProvider.Stage, output.Id, types);
                                            }
                                            else if (e.Value?.ToString() == PPLocalizedResources.EffectBind_NoConnection)
                                            {
                                                EffectBindingHelper.RemoveReferencesTo(clip.EffectProviders.Values,
                                                    EffectProviderOutputExtensions.CreateOutputSourceId(bundleId, output.Id));
                                                if (outProvider.IsFinalOutputSource() && outProvider.GetFinalOutputFieldId() == output.Id)
                                                    outProvider.SetFinalOutputSource(false);
                                            }
                                            else break;
                                            Log($"Changed output binding for clip {clip.Id}, provider {bundleId}, output {output.Id}: {e.Value}.");
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                            break;
                                        }
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_FinalResult, IEffectProvider.OutputAnchorGUID, out var newTargetId))
                                        {
                                            EffectBindingHelper.SetFinalOutput(clip.EffectProviders,
                                                newTargetId == IEffectProvider.OutputAnchorGUID ? outProvider.Id : null, outProvider.Stage);
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                    else if (e.Id == "AddProvider")
                    {
                        if (ppb.Properties.TryGetValue("NewProviderType", out var typeObj) && typeObj is string bundleTypeName)
                        {
                            if (bundlesFactories.TryGetValue(bundleTypeName, out var factory))
                            {
                                var previousProviders = new Dictionary<Guid, IEffectProvider>(clip.EffectProviders);
                                var previousBindings = previousProviders.ToDictionary(p => p.Key,
                                    p => new Dictionary<string, string>(p.Value.AnchorsBindingState));
                                try
                                {
                                    var instance = factory();
                                    instance.Stage = stage;
                                    if ((stage == EffectStage.SourceReplacement && instance.TypeOfEffect != EffectType.SourceReplacement)
                                        || !CanSelectEffectProvider(instance, selectionTarget, hideKeyFramedProviders: true))
                                    {
                                        Log($"Rejected effect provider {bundleTypeName} for clip {clip.Id} ({clip.ClipType}).", "warning");
                                        return;
                                    }
                                    instance.Id = Guid.NewGuid();
                                    instance.DisconnectMainInput();
                                    instance.SetFinalOutputSource(false);
                                    clip.EffectProviders[instance.Id] = instance;
                                    EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, instance, clip.GetEffectTarget());
                                    RebuildAllEffects(clip);
                                }
                                catch (Exception ex)
                                {
                                    clip.EffectProviders = previousProviders;
                                    foreach (var p in previousProviders) p.Value.AnchorsBindingState = previousBindings[p.Key];
                                    Log(ex, $"Add effect provider {bundleTypeName} to clip {clip.Id}", this);
                                    await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                                }
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            }
                        }
                    }
                }
            });
            ppb.AppendWhen(SettingsManager.IsBoolSettingTrue("DeveloperMode"),
                  p => p.AddSeparator()
                        .AddButton(PPLocalizedResources.EffectTab_ShowAll, (s, e) => RunPanelAction(() =>
                        {
                            showAllEffect = !showAllEffect;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        }))
                        .AddButton(PPLocalizedResources.EffectTab_Rebuild, async (s, e) => await RunPanelActionAsync(async () =>
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
                        }))
                        .AddButton("Generate bind graph (AnchorsBindingState)", async (s, e) => await RunPanelActionAsync(async () =>
                        {
                            var graph = EffectBindingHelper.GenerateStoredMermaidDiagram(clip.EffectProviders, EffectGroupHelper.Read(clip.ExtraData));
                            await page.ShowPopupAsync(new MermaidCodeBlockRenderer().Render(graph), new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = true });
                        }))
                        .AddButton("Generate bind graph (Fields)", async (s, e) => await RunPanelActionAsync(async () =>
                        {
                            var graph = EffectBindingHelper.GenerateRenderTimeMermaidDiagram(clip.EffectProviders, EffectGroupHelper.Read(clip.ExtraData));
                            await page.ShowPopupAsync(new MermaidCodeBlockRenderer().Render(graph), new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = true });
                        })));

            Log($"Built effect panel for {clip.Id}, stage {stage}, providers {providerSnapshot.Count}, elapsed {sw.ElapsedMilliseconds} ms.", "debug");
            return ppb;
        }

        private static Dictionary<Guid, List<string>> BuildInputCandidateSources(IReadOnlyList<IEffectProvider> targets,
            IReadOnlyDictionary<Guid, IEffectProvider> providers, EffectFieldTypeResolution types, bool showInternal, bool forward)
        {
            var outputs = providers.Values.Where(p => showInternal || !p.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor))
                .SelectMany(p => types.GetContext(p.Id).State.Outputs.Keys.Select(key =>
                {
                    string? outputId = p is IMultipleOutputEffectProvider ? key : null;
                    return (Provider: p, OutputId: outputId, Source: outputId is null ? p.Id.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(p.Id, outputId));
                })).ToArray();
            var options = new Dictionary<Guid, List<string>>();
            foreach (var target in targets)
            {
                if (target.TypeOfEffect is EffectType.Transform or EffectType.SpeedVarianceProvider or EffectType.MixtureProvider or EffectType.SourceReplacement
                    || !target.InFields.ContainsKey(EffectProviderAnchorExtensions.InputKey)) continue;
                var sources = options[target.Id] = [];
                foreach (var output in outputs)
                {
                    if ((!forward && output.Provider.Id == target.Id) || output.Provider.Stage != target.Stage
                        || output.Provider is ClipArgumentProvider || output.Provider.TypeOfEffect == EffectType.Transform) continue;
                    var candidate = types.ResolveBindingCandidate(target, EffectProviderAnchorExtensions.InputKey, output.Source, forward);
                    if (candidate is not null && output.Provider.CanConnectContent(target, output.OutputId, candidate)
                        && candidate.AreInputsCompatible(target)) sources.Add(output.Source);
                }
                foreach (var source in new[] { IEffectProvider.InputAnchorGUID.ToString(), ValueProviderFrameContext.BuiltInFrameProviderId,
                    ValueProviderFrameContext.BuiltInProgressProviderId, ValueProviderFrameContext.BuiltInClipFrameProviderId,
                    ValueProviderFrameContext.BuiltInProjectProgressProviderId, ValueProviderFrameContext.BuiltInAudioSampleProviderId,
                    ValueProviderFrameContext.ClipAudioProviderId, ValueProviderFrameContext.ClipPositionProviderId })
                {
                    var candidate = types.ResolveBindingCandidate(target, EffectProviderAnchorExtensions.InputKey, source, forward);
                    var sourceType = source == IEffectProvider.InputAnchorGUID.ToString() ? target.Stage.GetContentType()
                        : ValueProviderFrameContext.SourceType(source);
                    if (candidate is not null && EffectFieldTypes.AreCompatible(sourceType, candidate.GetInputType(target, EffectProviderAnchorExtensions.InputKey))
                        && candidate.AreInputsCompatible(target)) sources.Add(source);
                }
            }
            return options;
        }

        #endregion

        #region color adjustment
        private View BuildColorAdjustmentTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.Effects ??= new Dictionary<string, IEffect>();
            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();

            var colorAdjustProviderFactories = EffectServices.GetAvailableEffectProviders().Select(c => (c, c.Value())).Where(c => c.Item2.Target == EffectTarget.ColorAdjustment && c.Item2.TypeName != "ColorAdjustment").Select(C => C.c).ToDictionary(c => c.Key, c => c.Value);
            var localizedProviderNames = EffectServices.GetLocalizedEffectProviderNames("", false);
            ColorAdjustmentEffectProvider bundle = null!;
            if (!clip.EffectProviders.TryGetValue(InternalColorAdjustmentProviderGuid, out var b) || b is not ColorAdjustmentEffectProvider cb)
            {
                bundle = new ColorAdjustmentEffectProvider() { Id = InternalColorAdjustmentProviderGuid };
            }
            else
            {
                bundle = cb;
            }

            var ppb = new PropertyPanelBuilder();
            var bundleUI = EffectServices.GetUIProvider(bundle);
            if (bundleUI is IBindingHostHolder colorProviderBindingHostHolder)
            {
                colorProviderBindingHostHolder.BindingHost = new ClipBindingHost(clip, bundle, page,
                    onChanged: () => { RebuildAllEffects(clip, false); handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null)); });
            }
            ppb.AddFromAnother(bundleUI.CreateUI(bundle), bundle);
            foreach (var item in clip.EffectProviders.Where(c => c.Value.Target == EffectTarget.ColorAdjustment && c.Value.Id != InternalColorAdjustmentProviderGuid))
            {
                var bundleId = item.Key;
                var bundleInstance = item.Value;
                var locedName = localizedProviderNames.TryGetValue(item.Value.Name, out var locName) ? locName : item.Value.TypeName;
                ppb.AddSeparator();
                ppb.AddText(new SingleLineLabel(locedName, 25));
                ppb.AddCheckbox($"Effect|{bundleId}|Enabled", PPLocalizedResources._Enabled, bundleInstance.Enabled);
                var itemUI = EffectServices.GetUIProvider(bundleInstance);
                if (itemUI is IBindingHostHolder itemBindingHostHolder)
                {
                    itemBindingHostHolder.BindingHost = new ClipBindingHost(clip, bundleInstance, page,
                        onChanged: () => { RebuildAllEffects(clip, false); handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null)); });
                }
                ppb.AddFromAnother(itemUI.CreateUI(bundleInstance), bundleInstance);
                ppb.AddButton($"Provider|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
            }
            ppb.AppendWhen(colorAdjustProviderFactories.Any(), c => c.AddSeparator());
            foreach (var item in colorAdjustProviderFactories)
            {
                ppb.AddCustomChild(localizedProviderNames.TryGetValue(item.Key, out var value) ? value : item.Key, new Button
                {
                    Text = Localized.DraftPage_CenterMenuBar_AddClip,
                    Command = new Command(
                        () => RunPanelAction(() =>
                        {
                            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddProvider", item.Key);
                        }))
                });
            }
            ppb.PropertyChanged += GuardPanelChange((s, e) =>
            {
                if (s is IEffectProvider senderProvider)
                {
                    if (clip.EffectProviders.TryGetValue(senderProvider.Id, out var editingProvider))
                    {
                        var fieldUpdate = EffectServices.GetUIProvider(senderProvider).HandlePropertyPanelChange(senderProvider, e);
                        var updated = fieldUpdate.newParams;
                        if (fieldUpdate.newFields is not null)
                        {
                            editingProvider.Fields = fieldUpdate.newFields;
                        }
                        else if (updated is { Count: > 0 })
                        {
                            var fields = new Dictionary<string, IEffectArgumentField>();
                            foreach (var kvp in updated)
                            {
                                fields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                            }
                            editingProvider.Fields = fields;
                        }
                        RebuildAllEffects(clip);
                        clip.ApplySpeedRatio();
                        handler?.Invoke(s, e);
                    }
                    else
                    {
                        clip.EffectProviders[InternalColorAdjustmentProviderGuid] =
                            new ColorAdjustmentEffectProvider()
                            {
                                Id = InternalColorAdjustmentProviderGuid,
                            };
                        var cap = clip.EffectProviders[InternalColorAdjustmentProviderGuid];
                        EffectBindingHelper.AutoConnectProviderToInput(clip.EffectProviders, cap);
                        var fieldUpdate = EffectServices.GetUIProvider(senderProvider).HandlePropertyPanelChange(senderProvider, e);
                        var newParams = fieldUpdate.newParams;
                        if (fieldUpdate.newFields is not null)
                        {
                            cap.Fields = fieldUpdate.newFields;
                        }
                        else if (newParams is { Count: > 0 })
                        {
                            var fields = new Dictionary<string, IEffectArgumentField>();
                            foreach (var kvp in newParams)
                            {
                                fields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                            }
                            cap.Fields = fields;
                        }

                        RebuildAllEffects(clip);
                        clip.ApplySpeedRatio();
                        handler?.Invoke(s, e);
                    }
                    return;
                }
                else if (e.Id.StartsWith("Provider|"))
                {
                    var parts = e.Id.Split('|');
                    if (parts.Length >= 3)
                    {
                        Guid bundleId = new(parts[1]);
                        string action = parts[2];
                        if (!clip.EffectProviders?.ContainsKey(bundleId) ?? false) return;

                        switch (action)
                        {
                            case "Remove":
                                if (clip.EffectProviders is { } providers)
                                    EffectBindingHelper.RemoveProvider(providers, bundleId);
                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                break;
                            case "Enabled":
                                if (clip.EffectProviders.TryGetValue(bundleId, out var enabledProvider))
                                {
                                    if (bool.TryParse(e.Value?.ToString(), out var enabled))
                                    {
                                        enabledProvider.Enabled = enabled;
                                        RebuildAllEffects(clip);
                                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    }
                                }
                                break;
                        }
                    }
                }
                else if (e.Id == "AddProvider")
                {
                    if (e.Value is string bundleTypeName)
                    {
                        if (colorAdjustProviderFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            instance.Id = Guid.NewGuid();
                            instance.DisconnectMainInput();
                            instance.SetFinalOutputSource(false);
                            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
                            clip.EffectProviders[instance.Id] = instance;
                            EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, instance, clip.GetEffectTarget());

                            RebuildAllEffects(clip);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        }
                    }
                }
                handler?.Invoke(s, e);
            });

            ppb.AddButton(PPLocalizedResources.ColorAdjustment_Reset, (_, _) => RunPanelAction(() =>
            {
                if (clip.EffectProviders is { } providers)
                    EffectBindingHelper.RemoveProvider(providers, InternalColorAdjustmentProviderGuid);
                RebuildAllEffects(clip);
                handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
            }), (c) => c.TextColor = Color.FromArgb("#FF8080"));

            return ppb.BuildWithScrollView();
        }
        #endregion

        #region kf

        private View BuildKeyFrameTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();

            var root = new VerticalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(12, 10)
            };
            var title = new Label
            {
                Text = Localized.InteractableEditor_KeyFrame,
                FontSize = 20,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#E8EEF8")
            };
            root.Children.Add(title);
            ClipPositionTuple GetCurrentClipPosition()
            {
                int width = clip.TargetWidth > 0 ? clip.TargetWidth : (int)Math.Max(1, page.ProjectInfo.RelativeWidth);
                int height = clip.TargetHeight > 0 ? clip.TargetHeight : (int)Math.Max(1, page.ProjectInfo.RelativeHeight);
                return new ClipPositionTuple(clip.TargetX, clip.TargetY, width, height, false, clip.Rotation, clip.Opacity);
            }

            bool hasAnyProvider = false;

            foreach (var kvp in clip.EffectProviders)
            {
                if (kvp.Value is not IKeyFramedEffectProvider provider)
                    continue;

                hasAnyProvider = true;
                var section = BuildKeyframeProviderSectionUI(provider, clip, GetCurrentClipPosition, handler);
                root.Children.Add(section);
            }

            if (!hasAnyProvider)
            {

                root.Children.Add(new Label
                {
                    Text = PPLocalizedResources.KeyFrame_NoSupport,
                    TextColor = Color.FromArgb("#A8B8CC"),
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.WordWrap
                });
            }

            // 列出可用的支持关键帧的 Effect，供用户添加
            var allProviderFactories = EffectServices.GetAvailableEffectProviders();
            if (allProviderFactories.Count > 0)
            {
                root.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = Colors.White.WithAlpha(0.1f),
                    Margin = new Thickness(0, 8)
                });

                root.Children.Add(new Label
                {
                    Text = PPLocalizedResources.KeyFrame_AddTitle,
                    FontSize = 16,
                    TextColor = Color.FromArgb("#C8C8CC"),
                    Margin = new Thickness(0, 4, 0, 8)
                });

                var addPpb = new PropertyPanelBuilder();
                root.Children.Add(BuildAddEffectPanel(
                    EffectTarget.IsKeyFramed | clip.GetEffectSelectionTarget(),
                    page,
                    allProviderFactories,
                    addPpb,
                    (s, e) =>
                    {
                        if (e.Id == "AddProvider" &&
                            addPpb.Properties.TryGetValue("NewProviderType", out var typeObj) &&
                            typeObj is string bundleTypeName &&
                            allProviderFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            if (!CanSelectEffectProvider(instance, EffectTarget.IsKeyFramed | clip.GetEffectSelectionTarget(), true))
                            {
                                Log($"Rejected keyframe provider {bundleTypeName} for clip {clip.Id} ({clip.ClipType}).", "warning");
                                return;
                            }
                            instance.Id = Guid.NewGuid();
                            instance.DisconnectMainInput();
                            instance.SetFinalOutputSource(false);
                            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
                            clip.EffectProviders[instance.Id] = instance;
                            EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, instance, clip.GetEffectTarget());
                            RebuildAllEffects(clip);
                            handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("ProgressList", null, null));
                            // 重建标签页
                            var rebuiltTab = BuildKeyFrameTab(clip, handler);
                            if (root.Parent is ScrollView sv)
                            {
                                sv.Content = rebuiltTab;
                            }
                        }
                    },
                    showSubfix: false,
                    ignoreIsNotVisibleInNewEffectSelector: true //keyframed effect may not have visible UI but should still be addable from this panel
                ));
            }

            return new ScrollView { Content = root };
        }

        private View BuildKeyframeProviderSectionUI(
            IKeyFramedEffectProvider provider,
            ClipElementUI clip,
            Func<ClipPositionTuple> getDefaultPosition,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            var section = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };

            string displayName = EffectProviderHelper.L(provider.TypeName, provider.TypeName);

            var actionsRow = new HorizontalStackLayout { Spacing = 8 };
            var collapseButton = new Label
            {
                Text = "▼",
                TextColor = Color.FromArgb("#A8B8CC"),
                FontSize = 14,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Start
            };
            var title = new Label
            {
                Text = displayName,
                FontSize = 20,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#E8EEF8"),
                HorizontalOptions = LayoutOptions.Start
            };
            var addButton = new Button
            {
                Text = "+",
                CornerRadius = 8,
                FontAttributes = FontAttributes.Bold,
                HorizontalOptions = LayoutOptions.End
            };
            actionsRow.Children.Add(collapseButton);
            actionsRow.Children.Add(title);
            actionsRow.Children.Add(addButton);
            section.Children.Add(actionsRow);

            var listHost = new VerticalStackLayout { Spacing = 8 };
            section.Children.Add(listHost);
            var stepUI = EffectServices.GetStepUIProvider(provider);

            bool isCollapsed = false;
            var collapseTap = new TapGestureRecognizer();
            var titleTap = new TapGestureRecognizer();
            void SwitchCollpaseMode()
            {
                isCollapsed = !isCollapsed;
                collapseButton.Text = isCollapsed ? "▶" : "▼";
                listHost.IsVisible = !isCollapsed;
            }
            collapseTap.Tapped += (_, _) => RunPanelAction(() => SwitchCollpaseMode());
            titleTap.Tapped += (_, _) => RunPanelAction(() => SwitchCollpaseMode());
            collapseButton.GestureRecognizers.Add(collapseTap);
            title.GestureRecognizers.Add(titleTap);

            // Store a stable reference to the "CropList" parameter name for the notification event
            string listParamName = $"{provider.TypeName}List";

            void RebuildList()
            {
                listHost.Children.Clear();

                var steps = provider.Steps;
                if (steps.Count == 0)
                {
                    listHost.Children.Add(new Label
                    {
                        Text = PPLocalizedResources.KeyFrame_Empty,
                        TextColor = Color.FromArgb("#A8B8CC"),
                        FontSize = 12
                    });
                    listHost.Children.Add(new Button
                    {
                        Text = PPLocalizedResources.EffectProp_Remove,
                        Command = new Command(() => RunPanelAction(() =>
                        {
                            if (clip.EffectProviders is { } providers)
                                EffectBindingHelper.RemoveProvider(providers, provider.Id);
                            handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                            RebuildList();
                        })),
                        TextColor = Color.FromArgb("#FF8080")
                    });
                    return;
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    int idx = i;
                    var stepInfo = steps[idx];

                    var card = new Border
                    {
                        Stroke = Colors.White.WithAlpha(0.10f),
                        StrokeShape = new RoundRectangle { CornerRadius = 10 },
                        Background = new SolidColorBrush(Color.FromArgb("#0FFFFFFF")),
                        Padding = new Thickness(10)
                    };

                    var stack = new VerticalStackLayout { Spacing = 8 };

                    var header = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Auto)
                        }
                    };
                    header.Add(new Label
                    {
                        Text = PPLocalizedResources.KeyFrame_Progress(i, stepInfo.Progress),
                        FontAttributes = FontAttributes.Bold,
                        FontSize = 13,
                        TextColor = Color.FromArgb("#DDE7F3")
                    }, 0, 0);

                    var deleteButton = new Button
                    {
                        Text = "\ue9d5",
                        FontFamily = "Icons",
                        CornerRadius = 8,
                        Padding = new Thickness(10, 4),
                        TextColor = Color.FromArgb("#FF8080")
                    };
                    deleteButton.Clicked += (_, _) => RunPanelAction(() =>
                    {
                        provider.RemoveStep(idx);
                        RebuildAllEffects(clip);
                        handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                        RebuildList();
                        handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    });
                    header.Add(deleteButton, 1, 0);
                    stack.Children.Add(header);

                    var stepPpb = stepUI.CreateStepUI(stepInfo);
                    var stepView = stepPpb.Build();
                    stepPpb.PropertyChanged += GuardPanelChange((s, args) =>
                    {
                        try
                        {
                            if (clip.EffectProviders?.GetValueOrDefault(provider.Id) != provider) return;
                            if (stepUI.HandleStepUIChange(stepInfo, args))
                            {
                                provider.UpdateStep(idx, stepInfo);
                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                                RebuildList();
                            }
                        }
                        catch (Exception ex) { Log(ex, $"Edit {provider.TypeName} keyframe", this); }
                    });
                    stack.Children.Add(stepView);
                    card.Content = stack;
                    listHost.Children.Add(card);
                }
            }

            addButton.Clicked += (_, _) => RunPanelAction(() =>
            {
                if (isCollapsed)
                {
                    isCollapsed = !isCollapsed;
                    collapseButton.Text = isCollapsed ? "▶" : "▼";
                    listHost.IsVisible = !isCollapsed;
                }
                var steps = provider.Steps;
                var step = provider.CreateStep(steps.Count == 0 ? 0 : Math.Min(1, steps.Max(s => s.Progress) + 0.1));
                if (provider.TypeName == "ProgressPlacer")
                {
                    var position = getDefaultPosition();
                    step.SetValues(new Dictionary<string, object>
                    {
                        ["TargetX"] = position.TargetX,
                        ["TargetY"] = position.TargetY,
                        ["TargetWidth"] = Math.Max(1, position.TargetWidth),
                        ["TargetHeight"] = Math.Max(1, position.TargetHeight),
                        ["Rotation"] = position.Rotation,
                        ["Opacity"] = position.Opacity,
                    });
                }
                provider.AddStep(step);
                RebuildAllEffects(clip);
                handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                RebuildList();
            });

            RebuildList();
            return section;
        }

        #endregion

        #region mixture

        private static bool TryMaterializeMixturePlaceholder(ClipElementUI clip, IEffectProvider placeholder)
        {
            clip.EffectProviders ??= new();
            if (clip.EffectProviders.TryGetValue(placeholder.Id, out var provider)) return ReferenceEquals(provider, placeholder);
            if (clip.EffectProviders.Values.Any(p => p.TypeOfEffect == EffectType.MixtureProvider)) return false;
            clip.EffectProviders[placeholder.Id] = placeholder;
            EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, placeholder, clip.GetEffectTarget());
            return true;
        }

        private View BuildMixtureTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            static bool IsMixtureProvider(IEffectProvider bundle) => bundle.TypeOfEffect == EffectType.MixtureProvider && bundle.Target == EffectTarget.Mixture;

            clip.Effects ??= new Dictionary<string, IEffect>();
            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();

            var allProviderFactories = EffectServices.GetAvailableEffectProviders();
            var localizedProviderNames = EffectServices.GetLocalizedEffectProviderNames("", false);

            var mixtureProviderFactoryItems = allProviderFactories
                .Where(kvp => kvp.Value().Target == EffectTarget.Mixture)
                .Select(kvp => new
                {
                    TypeName = kvp.Key,
                    Factory = kvp.Value,
                    DisplayName = localizedProviderNames.GetValueOrDefault(kvp.Key, kvp.Key)
                })
                .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
                .ToList();

            var mixtureProviders = clip.EffectProviders
                ?.Where(kvp => kvp.Value.Target == EffectTarget.Mixture)
                ?.Select(c => c.Value)
                ?.ToList() ?? [];

            var ppb = new PropertyPanelBuilder();

            if (mixtureProviders.Count > 1)
            {
                ppb.AddText(new Label
                {
                    Text = PPLocalizedResources.Mixture_ErrMultiplePvd,
                    TextColor = Colors.Orange
                });
            }

            var mixturePlaceholder = mixtureProviders.Count == 0 ? new ClassicOverlayMixtureProvider() : null;
            var bundle = mixtureProviders.FirstOrDefault() ?? mixturePlaceholder;
            if (bundle is not null)
            {
                var bundleId = bundle.Id;
                string localizedName = localizedProviderNames.GetValueOrDefault(bundle.TypeName, bundle.TypeName);

                ppb.AddText(new SingleLineLabel(localizedName ?? bundle.Name, 20));
                ppb.AddText(new Label
                {
                    Text = PPLocalizedResources.Mixture_UnsupportWarn,
                    TextColor = Colors.Yellow
                });
                try
                {
                    var bundlePpb = EffectServices.GetUIProvider(bundle).CreateUI(bundle);
                    ppb.AddFromAnother(bundlePpb, bundle);
                }
                catch (Exception ex)
                {
                    Log(ex, $"loading mixture bundle {bundle.TypeName}", this);
                    ppb.AddText(new Label
                    {
                        Text = $"Error loading bundle UI: {ex.Message}",
                        TextColor = Colors.Yellow
                    });
                }

                if (mixturePlaceholder is null)
                {
                    ppb.AddButton(PPLocalizedResources.EffectProp_Remove, (s, e) => RunPanelAction(() =>
                    {
                        if (clip.EffectProviders is { } providers)
                            EffectBindingHelper.RemoveProvider(providers, bundleId);
                        RebuildAllEffects(clip);
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    }));
                }
                ppb.AddSeparator();
            }
            if (mixturePlaceholder is not null)
            {
                ppb.AppendWhen(mixtureProviderFactoryItems.Count > 0, c => c.AddCustomChild(BuildAddEffectPanel(EffectTarget.Mixture, page, allProviderFactories, ppb, handler, false)));
            }

            ppb.PropertyChanged += GuardPanelChange((s, e) =>
            {
                if (s is IEffectProvider senderProvider)
                {
                    bool materializing = ReferenceEquals(senderProvider, mixturePlaceholder)
                        && !clip.EffectProviders.ContainsKey(senderProvider.Id);
                    if (materializing && Equals(e.Value, e.OriginValue)) return;
                    if (materializing && !TryMaterializeMixturePlaceholder(clip, senderProvider))
                    {
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        return;
                    }
                    if (clip.EffectProviders.TryGetValue(senderProvider.Id, out var editingProvider))
                    {
                        var fieldUpdate = EffectServices.GetUIProvider(senderProvider).HandlePropertyPanelChange(senderProvider, e);
                        var updated = fieldUpdate.newParams;
                        if (fieldUpdate.newFields is not null)
                        {
                            editingProvider.Fields = fieldUpdate.newFields;
                        }
                        else if (updated is { Count: > 0 })
                        {
                            var fields = new Dictionary<string, IEffectArgumentField>();
                            foreach (var kvp in updated)
                            {
                                fields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                            }
                            editingProvider.Fields = fields;
                        }
                        RebuildAllEffects(clip);
                        handler?.Invoke(s, materializing
                            ? new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null) : e);
                    }
                    return;
                }
                else if (e.Id == "AddProvider")
                {
                    int currentCount = clip.EffectProviders.Values.Count(IsMixtureProvider);
                    if (currentCount >= 1)
                    {
                        page.Dispatcher.Dispatch(async () => await RunPanelActionAsync(async () =>
                        {
                            await page.DisplayAlertAsync(Localized._Info, PPLocalizedResources.Mixture_ErrSingle, Localized._OK);
                        }));
                        return;
                    }
                    if (ppb.Properties.TryGetValue("NewProviderType", out var typeObj) && typeObj is string bundleTypeName)
                    {
                        if (allProviderFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            if (!CanSelectEffectProvider(instance, EffectTarget.Mixture))
                            {
                                Log($"Rejected mixture provider {bundleTypeName} for clip {clip.Id}.", "warning");
                                return;
                            }
                            instance.Id = Guid.NewGuid();
                            instance.DisconnectMainInput();
                            instance.SetFinalOutputSource(false);
                            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
                            clip.EffectProviders[instance.Id] = instance;
                            EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, instance, clip.GetEffectTarget());

                            RebuildAllEffects(clip);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        }
                    }
                }

                handler?.Invoke(s, e);
            });

            return ppb.BuildWithScrollView();
        }

        #endregion

        #region effect misc

        private sealed class EffectProviderCardItem
        {
            public required string ProviderTypeName { get; init; }
            public required string Title { get; init; }
            public required string Description { get; init; }
            public ImageSource? Thumbnail { get; init; }
            public MediaSource? VideoThumbnail { get; init; }
            public required string EffectTypeName { get; init; }
        }

        private sealed class EffectProviderCardLayout(double cardWidth, double cardHeight) : Microsoft.Maui.Controls.Layout
        {
            protected override ILayoutManager CreateLayoutManager() => new CardLayoutManager(this, cardWidth, cardHeight);

            private sealed class CardLayoutManager(EffectProviderCardLayout layout, double cardWidth, double cardHeight) : LayoutManager(layout)
            {
                private int columns = 1;

                public override Size Measure(double widthConstraint, double heightConstraint)
                {
                    double width = double.IsFinite(widthConstraint) ? Math.Max(0, widthConstraint) : Layout.Count * cardWidth;
                    columns = (int)Math.Max(1, Math.Min(Layout.Count, Math.Floor(width / cardWidth)));
                    foreach (var child in Layout)
                        child.Measure(Math.Min(cardWidth, width), cardHeight);

                    // Arrangement reuses these columns so fractional width rounding cannot change the row count.
                    return new Size(
                        ResolveConstraints(widthConstraint, Layout.Width, width, Layout.MinimumWidth, Layout.MaximumWidth),
                        ResolveConstraints(heightConstraint, Layout.Height, (Layout.Count + columns - 1) / columns * cardHeight,
                            Layout.MinimumHeight, Layout.MaximumHeight));
                }

                public override Size ArrangeChildren(Rect bounds)
                {
                    for (int i = 0; i < Layout.Count; i++)
                        Layout[i].Arrange(new Rect(bounds.X + i % columns * cardWidth, bounds.Y + i / columns * cardHeight,
                            Math.Min(cardWidth, Math.Max(0, bounds.Width)), cardHeight));
                    return bounds.Size;
                }
            }
        }

        private static string GetEffectTypeName(EffectTarget target)
        {
            if (target.HasFlag(EffectTarget.Pipeline)) return Localized.EffectType_Pipeline;
            StringBuilder result = new();
            if (target.HasFlag(EffectTarget.Video)) result.Append(Localized.EffectType_Video);
            if (target.HasFlag(EffectTarget.Audio)) result.Append(Localized.EffectType_Audio);
            if (target.HasFlag(EffectTarget.SpeedVariance)) result.Append(Localized.EffectType_SpeedVariance);
            if (target.HasFlag(EffectTarget.Mixture)) result.Append(Localized.EffectType_Mixture);
            if (target.HasFlag(EffectTarget.ColorAdjustment)) result.Append(Localized.EffectType_ColorAdjustment);
            if (target.HasFlag(EffectTarget.ClipPosition)) result.Append(Localized.EffectType_ClipPosition);
            if (target.HasFlag(EffectTarget.Text)) result.Append(PPLocalizedResources.Effect_TextEffect);
            if (target.HasFlag(EffectTarget.VectorComponent)) result.Append(Localized.Effect_VectorComponentEffect);
            if (target.HasFlag(EffectTarget.VectorPicture)) result.Append(Localized.Effect_VectorPictureEffect);
            if (target.HasFlag(EffectTarget.IsKeyFramed)) result.Append(Localized.EffectType_KeyFramed);
            return result.ToString();
        }

        internal static bool CanSelectEffectProvider(
            IEffectProvider provider,
            EffectTarget target,
            bool ignoreIsNotVisibleInNewEffectSelector = false,
            bool hideKeyFramedProviders = false,
            bool includePipelineProviders = false)
        {
            const EffectTarget targetKinds = EffectTarget.Video | EffectTarget.Audio | EffectTarget.Text
                | EffectTarget.VectorComponent | EffectTarget.VectorPicture | EffectTarget.SpeedVariance | EffectTarget.Mixture
                | EffectTarget.ColorAdjustment | EffectTarget.SourceReplacement | EffectTarget.ValueProvider | EffectTarget.ClipPosition;
            bool pipeline = provider.Target.HasFlag(EffectTarget.Pipeline);
            if (provider.TypeOfEffect == EffectType.Transform || pipeline && !includePipelineProviders)
                return false;
            if (!pipeline && target != EffectTarget.NotSpecified && (provider.Target & target & targetKinds) == 0)
                return false;
            if (!pipeline && target != EffectTarget.NotSpecified && target.HasFlag(EffectTarget.Text)
                && provider.Target.HasFlag(EffectTarget.SourceReplacement))
                return false;
            bool keyFramed = provider.Target.HasFlag(EffectTarget.IsKeyFramed) || provider is IKeyFramedEffectProvider;
            if (target != EffectTarget.NotSpecified && target.HasFlag(EffectTarget.IsKeyFramed) && !keyFramed)
                return false;
            return (!hideKeyFramedProviders || !keyFramed)
                && (ignoreIsNotVisibleInNewEffectSelector || !provider.Target.HasFlag(EffectTarget.IsNotVisibleInNewEffectSelector));
        }

        public static View BuildAddEffectPanel(
            EffectTarget target,
            Page page,
           Dictionary<string, Func<IEffectProvider>> bundlesFactories,
            PropertyPanelBuilder ppb,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler,
            bool showSubfix = true,
            bool ignoreIsNotVisibleInNewEffectSelector = false,
            bool hideKeyFramedProviders = false,
            bool includePipelineProviders = false)
        {
            View EmptyPanel()
            {
                return new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Padding = 12,
                    Content = new Label
                    {
                        Text = PPLocalizedResources.Add_Effect_None,
                        Opacity = 0.7,
                        FontSize = 13
                    }
                };
            }

            if (bundlesFactories == null || bundlesFactories.Count == 0) return EmptyPanel();


            void AddProvider(string bundleTypeName)
            {
                if (!bundlesFactories.TryGetValue(bundleTypeName, out var factory)
                    || !CanSelectEffectProvider(factory(), target, ignoreIsNotVisibleInNewEffectSelector, hideKeyFramedProviders, includePipelineProviders))
                {
                    Log($"Rejected effect provider {bundleTypeName} for target {target}.", "warning");
                    return;
                }
                ppb.Properties["NewProviderType"] = bundleTypeName;
                PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddProvider", bundleTypeName);
                handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("AddProvider", bundleTypeName, bundleTypeName));
            }

            var cards = new List<EffectProviderCardItem>();
            var localizedNames = EffectServices.GetLocalizedEffectProviderNames(Environment.NewLine, showSubfix);
            foreach (var kvp in bundlesFactories.OrderBy(k => k.Key))
            {
                var bundleTypeName = kvp.Key;
                IEffectProvider instance;
                try
                {
                    instance = kvp.Value();
                }
                catch (Exception ex)
                {
                    Log(ex, $"Create effect provider {bundleTypeName} for selector", typeof(ClipInfoBuilder));
                    continue;
                }
                if (!CanSelectEffectProvider(instance, target, ignoreIsNotVisibleInNewEffectSelector, hideKeyFramedProviders, includePipelineProviders))
                    continue;
                try
                {
                    var uiProvider = EffectServices.GetUIProvider(instance);
                    var displayItem = uiProvider.GetDisplayItem(instance);
                    var description = uiProvider.GetLocalizedEffectDescription(instance, PluginManager.CurrentLocale);

                    cards.Add(new EffectProviderCardItem
                    {
                        ProviderTypeName = bundleTypeName,
                        Title = localizedNames.GetValueOrDefault(bundleTypeName, bundleTypeName),
                        Description = description,
                        Thumbnail = displayItem.Thumbnail,
                        VideoThumbnail = displayItem.VideoThumbnail,
                        EffectTypeName = GetEffectTypeName(instance.Target),
                    });
                }
                catch
                {
                    cards.Add(new EffectProviderCardItem
                    {
                        ProviderTypeName = bundleTypeName,
                        Title = bundleTypeName,
                        Description = "",
                        Thumbnail = null,
                        EffectTypeName = GetEffectTypeName(instance.Target)
                    });
                }
            }

            if (cards.Count == 0) return EmptyPanel();

            return BuildProviderPickerPanel(cards, page,
                typeName => ppb.Properties["NewProviderType"] = typeName, AddProvider);
        }

        private static View BuildProviderPickerPanel(
            List<EffectProviderCardItem> cards, Page page, Action<string>? onSelected, Action<string> onAdd,
            string? selectionText = null, string? addText = null, string? emptyText = null,
            Func<EffectProviderCardItem, CancellationToken, Task<MediaSource?>>? loadPreview = null)
        {
            const double cardWidth = 210;
            const double cardHeight = 160;
            const double cardMargin = 6;

            // ─── Collect unique categories for filter ───
            var allCategories = cards
                .Select(c => c.EffectTypeName)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var cardLayout = new EffectProviderCardLayout(cardWidth + cardMargin * 2, cardHeight + cardMargin * 2);
            var emptyLabel = new Label
            {
                Text = emptyText ?? PPLocalizedResources.Add_Effect_None,
                FontSize = 18,
                IsVisible = cards.Count == 0
            };
            cardLayout.IsVisible = cards.Count > 0;

#if WINDOWS
            cardLayout.Loaded += (_, _) => RunPanelAction(() =>
            {
                for (Element? e = cardLayout.Parent; e is not null; e = e.Parent)
                    if (e is ScrollView scroll && scroll.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
                        sv.BringIntoViewOnFocusChange = false;
            }, page);
#endif

            // Filter state
            string? filterSearchText = null;
            string? filterCategory = null;
            Border? selectedCard = null;

            void ApplyFilter()
            {
                IEnumerable<EffectProviderCardItem> filtered = cards;

                if (!string.IsNullOrWhiteSpace(filterSearchText))
                {
                    var lower = filterSearchText.ToLowerInvariant();
                    filtered = filtered.Where(c =>
                        c.Title.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        c.Description.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        c.ProviderTypeName.Contains(lower, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(filterCategory))
                {
                    filtered = filtered.Where(c => c.EffectTypeName == filterCategory);
                }

                selectedCard = null;
                var items = filtered.ToList();
                emptyLabel.IsVisible = items.Count == 0;
                cardLayout.IsVisible = items.Count > 0;
                BindableLayout.SetItemsSource(cardLayout, items);
            }

            // ─── Search bar ───
            var searchBar = new SearchBar
            {
                Placeholder = PPLocalizedResources.Effect_Add_Search
            };
            searchBar.TextChanged += (_, e) => RunPanelAction(() =>
            {
                filterSearchText = e.NewTextValue;
                ApplyFilter();
            }, page);

            // ─── Category filter picker ───
            var categoryPicker = new Picker
            {
                WidthRequest = 130
            };
            categoryPicker.Items.Add(PPLocalizedResources.Effect_Add_Search_Any);
            foreach (var cat in allCategories)
            {
                categoryPicker.Items.Add(cat);
            }
            categoryPicker.SelectedIndexChanged += (_, _) => RunPanelAction(() =>
            {
                filterCategory = categoryPicker.SelectedIndex <= 0 ? null : categoryPicker.Items[categoryPicker.SelectedIndex];
                ApplyFilter();
            }, page);

            // ─── Filter bar ───
            var filterBar = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Margin = new Thickness(0, 0, 0, 8)
            };
            filterBar.Add(new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Margin = new Thickness(0, 0, 8, 0),
                Content = searchBar
            }, 0, 0);
            filterBar.Add(categoryPicker, 1, 0);

            BindableLayout.SetItemsSource(cardLayout, cards);
            BindableLayout.SetItemTemplate(cardLayout, new DataTemplate(() =>
            {
                // ─── Preview image ───
                var image = new Image
                {
                    Aspect = Aspect.AspectFill,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };
                image.SetBinding(Image.SourceProperty, nameof(EffectProviderCardItem.Thumbnail));

                // ─── Video preview (hidden by default) ───
                var mediaPlayer = new MediaElement
                {
                    Aspect = loadPreview is null ? Aspect.AspectFill : Aspect.AspectFit,
                    ShouldAutoPlay = true,
                    ShouldLoopPlayback = true,
                    ShouldMute = true,
                    ShouldShowPlaybackControls = false,
                    IsVisible = false,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };

                // ─── Effect type label (bottom-left overlay on thumbnail) ───
                var typeLabel = new Label
                {
                    FontSize = 11,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.End,
                    HorizontalOptions = LayoutOptions.Start,
                    Margin = new Thickness(6, 0, 0, 6),
                    Padding = new Thickness(4, 2),
                    Background = new SolidColorBrush(Colors.Black.WithAlpha(0.5f)),
                };
                typeLabel.SetBinding(Label.TextProperty, nameof(EffectProviderCardItem.EffectTypeName));

                // ─── Effect name label (bottom-right overlay on thumbnail) ───
                var nameLabel = new Label
                {
                    FontSize = 11,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.End,
                    HorizontalOptions = LayoutOptions.End,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(4, 2),
                    Background = new SolidColorBrush(Colors.Black.WithAlpha(0.5f)),
                    LineBreakMode = LineBreakMode.TailTruncation,
                };
                nameLabel.SetBinding(Label.TextProperty, nameof(EffectProviderCardItem.Title));

                // ─── Preview area (fills card) ───
                var previewGrid = new Grid
                {
                    Children = { image, mediaPlayer, typeLabel, nameLabel }
                };

                // ─── Hover overlay (hidden by default, slides up from bottom) ───
                var hoverTitle = new Label
                {
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2
                };
                hoverTitle.SetBinding(Label.TextProperty, nameof(EffectProviderCardItem.Title));

                var hoverDesc = new Label
                {
                    FontSize = 11,
                    TextColor = Colors.White.WithAlpha(0.85f),
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2
                };
                hoverDesc.SetBinding(Label.TextProperty, nameof(EffectProviderCardItem.Description));

                var hoverOverlay = new Border
                {
                    IsVisible = false,
                    VerticalOptions = LayoutOptions.End,
                    Background = new SolidColorBrush(Colors.Gray.WithAlpha(0.85f)),
                    Padding = new Thickness(8, 6),
                    StrokeThickness = 0,
                    Content = new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children = { hoverTitle, hoverDesc }
                    }
                };

                // ─── Main card container ───
                var cardContent = new Grid
                {
                    Children = { previewGrid, hoverOverlay }
                };

                var border = new Border
                {
                    Padding = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    StrokeThickness = 2,
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = cardContent
                };
                var card = new Grid
                {
                    Padding = new Thickness(cardMargin),
                    Children = { border }
                };
                bool hovered = false, loaded = false;
                var visibilityParents = new List<VisualElement>();
                CancellationTokenSource? previewCts = null;
                var loading = new ActivityIndicator
                {
                    IsVisible = false,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };
                async Task ShowPreview()
                {
                    if (loadPreview is null || !loaded || border.BindingContext is not EffectProviderCardItem item) return;
                    previewCts?.Cancel();
                    var cts = new CancellationTokenSource();
                    previewCts = cts;
                    loading.IsRunning = loading.IsVisible = true;
                    try
                    {
                        var source = await loadPreview(item, cts.Token);
                        if (cts.IsCancellationRequested || !loaded || !hovered ||
                            !ReferenceEquals(border.BindingContext, item) || source is null) return;
                        mediaPlayer.Source = source;
                        mediaPlayer.IsVisible = true;
                        image.IsVisible = false;
                        mediaPlayer.Play();
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Log(ex, $"Load provider preview {item.ProviderTypeName}", typeof(ClipInfoBuilder));
                    }
                    finally
                    {
                        if (ReferenceEquals(previewCts, cts))
                        {
                            previewCts = null;
                            loading.IsRunning = loading.IsVisible = false;
                        }
                        cts.Dispose();
                    }
                }
                void StopPreview()
                {
                    previewCts?.Cancel();
                    loading.IsRunning = loading.IsVisible = false;
                    try
                    {
                        if (mediaPlayer.Source is not null) mediaPlayer.Stop();
                        mediaPlayer.Source = null;
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Stop provider preview", typeof(ClipInfoBuilder));
                    }
                    mediaPlayer.IsVisible = false;
                    image.IsVisible = true;
                }

                bool IsCardVisible()
                {
                    if (card.Width <= 0 || card.Height <= 0) return false;
                    var bounds = new Rect(0, 0, card.Width, card.Height);
                    for (Element? e = card; e is not null; e = e.Parent)
                    {
                        if (e is not VisualElement v) continue;
                        if (!v.IsVisible || v.Opacity <= 0 || v.Width <= 0 || v.Height <= 0) return false;
                        if (v is ScrollView scroll)
                        {
                            bounds.X -= scroll.ScrollX;
                            bounds.Y -= scroll.ScrollY;
                        }
                        double left = Math.Max(0, bounds.Left), top = Math.Max(0, bounds.Top);
                        double right = Math.Min(v.Width, bounds.Right), bottom = Math.Min(v.Height, bounds.Bottom);
                        if (right <= left || bottom <= top) return false;
                        bounds = new Rect(left, top, right - left, bottom - top);
                        bounds.X += v.X + v.TranslationX;
                        bounds.Y += v.Y + v.TranslationY;
                    }
                    return true;
                }

                void UpdateVideoPreview(object? sender = null, EventArgs? args = null) => RunPanelAction(() =>
                {
                    if (border.BindingContext is not EffectProviderCardItem item || item.VideoThumbnail is null) return;
                    if (!loaded || !IsCardVisible())
                    {
                        if (mediaPlayer.Source is not null) StopPreview();
                        return;
                    }
                    if (ReferenceEquals(mediaPlayer.Source, item.VideoThumbnail)) return;
                    try
                    {
                        mediaPlayer.IsVisible = true;
                        image.IsVisible = false;
                        mediaPlayer.Source = item.VideoThumbnail;
                        mediaPlayer.Play();
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Play provider preview {item.ProviderTypeName}", typeof(ClipInfoBuilder));
                        StopPreview();
                    }
                }, page);

                void OnPreviewLayoutChanged(object? sender, PropertyChangedEventArgs args) => RunPanelAction(() =>
                {
                    if (args.PropertyName is "IsVisible" or "Opacity" or "Bounds" or "X" or "Y" or "Width" or "Height" or "TranslationX" or "TranslationY")
                        UpdateVideoPreview();
                }, page);

                if (loadPreview is not null) previewGrid.Children.Add(loading);
                card.Loaded += (_, _) => RunPanelAction(() =>
                {
                    if (loaded) return;
                    loaded = true;
                    for (Element? e = card; e is not null; e = e.Parent)
                    {
                        if (e is not VisualElement v) continue;
                        visibilityParents.Add(v);
                        v.PropertyChanged += OnPreviewLayoutChanged;
                        if (v is ScrollView scroll) scroll.Scrolled += UpdateVideoPreview;
                    }
                    UpdateVideoPreview();
                }, page);
                border.BindingContextChanged += async (_, _) => await RunPanelActionAsync(async () =>
                {
                    StopPreview();
                    UpdateVideoPreview();
                    if (loaded && hovered && border.BindingContext is EffectProviderCardItem { VideoThumbnail: null })
                        await ShowPreview();
                }, page);
                card.Unloaded += (_, _) => RunPanelAction(() =>
                {
                    loaded = false;
                    hovered = false;
                    foreach (var v in visibilityParents)
                    {
                        v.PropertyChanged -= OnPreviewLayoutChanged;
                        if (v is ScrollView scroll) scroll.Scrolled -= UpdateVideoPreview;
                    }
                    visibilityParents.Clear();
                    hoverOverlay.IsVisible = false;
                    StopPreview();
                }, page);

                // ─── Hover handling ───
                async void OnHover(bool isHovered) => await RunPanelActionAsync(async () =>
                {
                    hovered = isHovered;
                    hoverOverlay.IsVisible = isHovered;
                    if (loadPreview is null || border.BindingContext is not EffectProviderCardItem { VideoThumbnail: null }) return;
                    if (isHovered) await ShowPreview();
                    else StopPreview();
                }, page);

#if MACCATALYST || WINDOWS
                var pointerGesture = new PointerGestureRecognizer();
                pointerGesture.PointerEntered += (_, _) => OnHover(true);
                pointerGesture.PointerExited += (_, _) => OnHover(false);
                cardContent.GestureRecognizers.Add(pointerGesture);
#endif

                void SelectCard(Border selected)
                {
                    if (selectedCard == selected) return;
                    if (selectedCard is { } previous)
                    {
                        previous.Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f));
                        previous.Background = new SolidColorBrush(Colors.Transparent);
                    }
                    selected.Stroke = new SolidColorBrush(Colors.DodgerBlue);
                    selected.Background = new SolidColorBrush(Colors.DodgerBlue.WithAlpha(0.1f));
                    selectedCard = selected;
                }

                UIServices.RegisterSelectOrContextMenu(
                    border,
                    OnSelected: () => RunPanelAction(() =>
                    {
                        if (border.BindingContext is EffectProviderCardItem item)
                        {
                            onSelected?.Invoke(item.ProviderTypeName);
                            SelectCard(border);
                            Log($"Selected effect provider {item.ProviderTypeName}.", "debug");
                        }
                    }, page),
                    OnClicked: () => RunPanelAction(() =>
                    {
                        if (border.BindingContext is EffectProviderCardItem item)
                            onAdd(item.ProviderTypeName);
                    }, page),
                    OnContextMenuClick: async () => await RunPanelActionAsync(async () =>
                    {
                        if (border.BindingContext is not EffectProviderCardItem item) return;
                        var verbs = new[] { addText ?? PPLocalizedResources.Add_Effect, Localized.AssetPage_ShowPreview };
                        int action = Array.IndexOf(verbs, await page.DisplayActionSheetAsync(item.Title, Localized._Cancel, null, verbs));
                        switch (action)
                        {
                            case 0:
                                onAdd(item.ProviderTypeName);
                                break;
                            case 1:
                                await page.DisplayAlertAsync(Localized._Info, item.Description, Localized._OK);
                                break;
                        }
                    }, page)
                );

                return card;
            }));

            return new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label
                    {
                        Text = selectionText ?? PPLocalizedResources.Add_Effect_Select,
                        Opacity = 0.7,
                        FontSize = 13
                    },
                    filterBar,
                    cardLayout,
                    emptyLabel
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
                    var effectProviderInstance = EffectHelper.EffectsProviderEnum.TryGetValue(effect.TypeName, out var providerFactory) ? providerFactory() : null;
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.Name, localizedEffectDisplayName.TryGetValue(effect.TypeName, out var disp) ? disp : effect.TypeName));
                    ppb.AddCheckbox($"Effect|{effectKey}|Enabled", PPLocalizedResources._Enabled, effect.Enabled);
                    ppb.AddEntry($"Effect|{effectKey}|Index", PPLocalizedResources.EffectProp_Index, effect.Index.ToString(), "-1");
                    foreach (var paramName in effectProviderInstance?.ParametersNeeded ?? new List<string>())
                    {
                        if (effectProviderInstance is null || !effectProviderInstance.ParametersType.TryGetValue(paramName, out var paramType)) continue;

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
                    ppb.AddSeparator();
                    IEffectProvider? eb = null;
                    ppb.AddCustomChild("IEffect.ID", new Label { Text = effect.Id });
                    ppb.AddCustomChild("IEffect.TypeName", new Label { Text = effect.TypeName });
                    ppb.AddCustomChild("IEffect.TypeOfEffect", new Label { Text = effect.TypeOfEffect.ToString() });
                    ppb.AddCustomChild("IEffect.ImplementType", new Label { Text = effect.ImplementType.ToString() });
                    ppb.AddCustomChild("IEffect.IsDeterministic", new Label { Text = effect.IsDeterministic.ToString() });
                    ppb.AppendWhen(
                       condition: (Guid.TryParse(effect.BindedEffectProvidingSystemID, out var g) && (clip.EffectProviders?.TryGetValue(g, out eb) ?? false) && eb is not null),
                       onTrue: c => c.AddCustomChild("Binded IEffectProvider", new Label { Text = $"{eb?.Name ?? "unknown"} ({effect.BindedEffectProvidingSystemID})" })
                                     .AddCustomChild("IEffectProvider.EffectTarget", new Label { Text = eb?.Target is not null ? eb.Target.ToString() : "No bundle" })
                                     .AddCustomChild("IEffectProvider.Stage", new Label { Text = eb?.Stage is not null && Enum.IsDefined(eb.Stage) ? eb.Stage.ToString() : "unknown stage" }),
                       onFalse: c => c.AddCustomChild("Binded IEffectProvider", new Label { Text = $"Unknown bundle '{effect.BindedEffectProvidingSystemID}'" }));
                    ppb.AddButton($"Effect|{effectKey}|Remove", PPLocalizedResources.EffectProp_Remove);
                    ppb.AddSeparator();
                }
            }




            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            var availableEffectNames = localizedEffectDisplayName
                .Where(c => EffectHelper.EffectsProviderEnum.TryGetValue(c.Key, out var factory)
                    && CanSelectEffectProvider(factory(), clip.GetEffectSelectionTarget()))
                .ToDictionary(c => c.Key, c => c.Value);
            ppb.AddPicker("NewEffectType", PPLocalizedResources.Add_Effect_Select, availableEffectNames.Values.ToArray(), availableEffectNames.Values.FirstOrDefault());
            ppb.AddButton("AddEffect", PPLocalizedResources.Add_Effect);

            ppb.PropertyChanged += GuardPanelChangeAsync(async (s, e) =>
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

                            if (EffectHelper.EffectsProviderEnum.TryGetValue(effect.TypeName, out var providerFactory)
                                && providerFactory().ParametersType.TryGetValue(paramName, out var paramType))
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
                        var typeName = availableEffectNames.FirstOrDefault(c => c.Value == locedTypeName, new("unknown", "unknown")).Key;
                        IEffect? newEffect = null;
                        if (EffectHelper.EffectsProviderEnum.TryGetValue(typeName, out var creator))
                        {
                            try
                            {
                                var provider = creator();
                                if (!CanSelectEffectProvider(provider, clip.GetEffectSelectionTarget()))
                                {
                                    Log($"Rejected classic effect {typeName} for clip {clip.Id} ({clip.ClipType}).", "warning");
                                    return;
                                }
                                newEffect = provider.RestoreInstanceWithDefaultType();
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
            });

            ppb.AddSeparator();
            ppb.AddText(new TitleAndDescriptionLineLabel(PPLocalizedResources.Effect_RenderOrder, PPLocalizedResources.Effect_RenderOrder_Hint));

            var orderContainer = new VerticalStackLayout { Spacing = 2, Padding = 5 };

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
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
            dragGesture.DragStarting += (s, e) => RunPanelAction(() =>
            {
                e.Data.Properties.Add("EffectKey", effectKey);
            });
            dragHandle.GestureRecognizers.Add(dragGesture);

            // Add Drop to the WHOLE container (so dropping anywhere on the item works)
            var dropGesture = new DropGestureRecognizer();
            dropGesture.AllowDrop = true;
            dropGesture.Drop += (s, e) => RunPanelAction(() =>
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
            });
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

    }
}
