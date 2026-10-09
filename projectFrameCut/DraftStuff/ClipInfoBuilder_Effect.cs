using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.AI;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics.Text;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platform;
using projectFrameCut.AIAssistance;
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
            if (clip.Effects?.TryGetValue(InternalRotationID, out var rotation) == true && rotation.TypeName == "Rotation")
            {
                if (rotation.Enabled) clip.Rotation += ReadEffectFloatParameter(rotation, "Angle", rotation is RotationEffect_IPicture r ? r.Angle : 0);
                clip.Rotation = VideoClipRotation.Normalize(clip.Rotation);
                clip.Effects.Remove(InternalRotationID);
                Log($"Migrated rotation for clip {clip.Id}: {clip.Rotation} degrees.");
            }
            var effects = EffectBindingHelper.RebuildAllEffects(clip.EffectProviders, clip.Effects);
            if (effects != null) clip.Effects = effects;
        }

        public Task<View> BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
            => BuildEffectTab(clip, handler, null);

        public async Task<View> BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler, EffectPipeline? pipeline)
        {
            ArgumentNullException.ThrowIfNull(clip);
            if (pipeline is null && clip.SupportsNativeEffects)
            {
                var tabs = new CompactTabView();
                foreach (var stage in new[] { EffectPipeline.NativeContent, EffectPipeline.Picture })
                    tabs.TabItems.Add(new TabbedViewItem
                    {
                        Header = stage == EffectPipeline.NativeContent ? Localized.Effect_NativePipeline : Localized.Effect_PicturePipeline,
                        Tag = stage.ToString(),
                        Content = await BuildEffectTab(clip, handler, stage)
                    });
                tabs.OnTabSwitched += (_, item) => clip.ExtraData["__EffectPipelineTab__"] = item.Tag;
                tabs.SelectByTag(clip.ExtraData.GetValueOrDefault("__EffectPipelineTab__")?.ToString() ?? EffectPipeline.NativeContent.ToString());
                return tabs;
            }
            var selectionTarget = pipeline is { } stageTarget ? clip.GetEffectSelectionTarget(stageTarget) : clip.GetEffectSelectionTarget();
            PropertyPanelBuilder ppb = new();
            var bindingDiagnostics = EffectBindingHelper.ValidateBindings(clip.EffectProviders);

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

            ppb.AddButton(PPLocalizedResources.EffectBind_Title, async (s, e) =>
            {
                try
                {
                    var bindView = new DraftEffectBindingView();
                    bindView.LoadClip(clip, page, showAllEffect, pipeline);
                    // Sync the completed graph rebuild to the draft preview.
                    bindView.EffectProvidersChanged += () =>
                    {
                        // DraftEffectBindingView has already rebuilt the provider graph. Do not build it a
                        // second time or rebuild the entire property/timeline UI for a single connection.
                        handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__EFFECT_BINDING_CHANGED__", clip, null));
                    };
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
                        v.CloseClicked += (s, e) =>
                        {
                            RebuildAllEffects(clip, false);
                            // 与 EffectProvidersChanged 一致：触发 __REFRESH_PANEL__ 让 DraftPage 重建并刷新预览。
                            handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        };
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"Show effect binding view for {clip.DisplayName}", this);
                    await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                    if (Debugger.IsAttached) throw;
                }

            });
            var bundlesFactories = EffectServices.GetAvailableEffectProviders()
                .Where(p => pipeline is null || p.Value().Target.HasFlag(EffectTarget.ValueProvider) || p.Value().TypeOfEffect.GetPipeline() == pipeline)
                .ToDictionary(p => p.Key, p => p.Value);
            var haveManySpeedVarianceProvider = (clip.EffectProviders?.Count(c => c.Value.TypeOfEffect.HasFlag(EffectType.SpeedVarianceProvider)) ?? 0) >= 2;
            var haveManyMixtureProvider = (clip.EffectProviders?.Count(c => c.Value.TypeOfEffect.HasFlag(EffectType.MixtureProvider)) ?? 0) >= 2;
            var haveManySourceReplacementEffect = (clip.EffectProviders?.Count(c => c.Value.TypeOfEffect.HasFlag(EffectType.SourceReplacement)) ?? 0) >= 2;
            if (clip.EffectProviders != null)
            {
                var filteredProviders = clip.EffectProviders
                     .Where(c => (pipeline is null || c.Value.Target.HasFlag(EffectTarget.ValueProvider) || c.Value.TypeOfEffect.GetPipeline() == pipeline)
                         && c.Value.TypeOfEffect != EffectType.Transform && (
                         showAllEffect
                         || (!c.Value.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)
                              && (c.Value.Target.HasFlag(EffectTarget.ValueProvider)
                                  || EffectBindingHelper.AreTargetsCompatible(c.Value.Target, clip.GetEffectTarget())))
                         || (c.Value.Target == EffectTarget.SpeedVariance && haveManySpeedVarianceProvider)
                         || (c.Value.Target == EffectTarget.Mixture && haveManyMixtureProvider)
                         || (c.Value.Target == EffectTarget.SourceReplacement && haveManySourceReplacementEffect)))
                     .ToList();

                // Sort providers by their single stored picture input. Fan-out is allowed.
                var sortedProviders = new List<KeyValuePair<Guid, IEffectProvider>>();
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
                    var bundleKvp = filteredProviders.First(b => b.Key == id);
                    sortedProviders.Add(bundleKvp);
                    foreach (var b in filteredProviders)
                    {
                        if (b.Value.GetMainInputSource() == id.ToString() && !visitedIds.Contains(b.Key))
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

                foreach (var bundleKvp in sortedProviders)
                {
                    var bundleId = bundleKvp.Key;
                    var bundleInstance = bundleKvp.Value;
                    var locedName = EffectServices.GetLocalizedEffectProviderNames("", false).GetValueOrDefault(bundleInstance.TypeName, bundleInstance.TypeName);
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
                        _ => PPLocalizedResources.Effect_GeneralEffect,
                    };
                    if (string.IsNullOrWhiteSpace(bundleInstance.Name)) bundleInstance.Name = locedName;

                    string GetInputAnchorSelection(string id)
                    {
                        if (id == IEffectProvider.NoConnectionGUID.ToString()) return PPLocalizedResources.EffectBind_NoConnection;
                        if (id == IEffectProvider.InputAnchorGUID.ToString()) return PPLocalizedResources.EffectBind_SourcePicture;
                        if (Guid.TryParse(id, out var providerId) && clip.EffectProviders != null && clip.EffectProviders.TryGetValue(providerId, out var b))
                        {
                            if (!showAllEffect && b.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor))
                                return GetInputAnchorSelection(b.GetMainInputSource());
                            return $"{b.Name} ({b.Id})";
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
                                    RebuildAllEffects(clip, false);
                                    handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                });
                        }

                        var bundlePpb = bundleUI.CreateUI(bundleInstance);

                        ppb.AddText(new TitleAndDescriptionLineLabel(locedName, locedType));
                        var providerDiagnostics = bindingDiagnostics.Where(d => d.ProviderId == bundleId).ToList();
                        if (providerDiagnostics.Count > 0)
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

                        ppb.AddSeparator();

                        //they can't be reordered
                        if (bundleInstance.TypeOfEffect is EffectType.Transform or EffectType.SpeedVarianceProvider or EffectType.MixtureProvider or EffectType.SourceReplacement or EffectType.NonIPictureOutputValueProvider) goto remove_btn;


                        var resolvedInAnchorId = bundleInstance.GetMainInputSource();

                        // 构建过滤后的 InAnchor 下拉选项：排除自身、类型不兼容和（showAllEffect=false 时）内部 bundle
                        var inAnchorProviderOptions = clip.EffectProviders
                             .Where(b => b.Key != bundleId
                                 && b.Value.CanConnectContent(bundleInstance)
                                 && (showAllEffect || !b.Value.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)))
                             .Select(b => $"{b.Value.Name} ({b.Key})")
                             .ToList();
                        var curIn = GetInputAnchorSelection(resolvedInAnchorId);
                        if (curIn is not null && !inAnchorProviderOptions.Contains(curIn))
                            inAnchorProviderOptions.Add(curIn);

                        ppb.AddPicker($"Provider|{bundleId}|InAnchor", PPLocalizedResources.EffectBind_InputAnchor,
                            inAnchorProviderOptions.Append(PPLocalizedResources.EffectBind_SourcePicture).Append(PPLocalizedResources.EffectBind_NoConnection).ToArray(),
                            GetInputAnchorSelection(resolvedInAnchorId));
                        ppb.AddPicker($"Provider|{bundleId}|OutAnchor", PPLocalizedResources.EffectBind_OutputAnchor,
                            [PPLocalizedResources.EffectBind_FinalResult, PPLocalizedResources.EffectBind_NoConnection],
                            bundleInstance.IsFinalOutputSource() ? PPLocalizedResources.EffectBind_FinalResult : PPLocalizedResources.EffectBind_NoConnection);

                    remove_btn:
                        ppb.AddButton($"Provider|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
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
            ppb.AddCustomChild(BuildAddEffectPanel(selectionTarget, page, bundlesFactories, ppb, handler, hideKeyFramedProviders: true));

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

            ppb.PropertyChanged += (s, e) =>
            {
                ArgumentNullException.ThrowIfNull(clip);
                clip.EffectProviders ??= new();

                if (!ppb.Equals(s)) //from another
                {
                    if (s is IEffectProvider eb)
                    {
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
                        handler?.Invoke(s, e);
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
                                        var locedName = EffectServices.GetLocalizedEffectProviderNames().GetValueOrDefault(nameProvider.TypeName, nameProvider.TypeName);
                                        var newName = e.Value?.ToString();
                                        nameProvider.Name = string.IsNullOrWhiteSpace(newName) ? locedName : newName;
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
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_SourcePicture, IEffectProvider.InputAnchorGUID, out var newSourceId))
                                        {
                                            inProvider.SetMainInputSource(newSourceId);
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                                case "OutAnchor":
                                    if (clip.EffectProviders.TryGetValue(bundleId, out var outProvider))
                                    {
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_FinalResult, IEffectProvider.OutputAnchorGUID, out var newTargetId))
                                        {
                                            EffectBindingHelper.SetFinalOutput(clip.EffectProviders,
                                                newTargetId == IEffectProvider.OutputAnchorGUID ? outProvider.Id : null, outProvider.TypeOfEffect.GetPipeline());
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
                                var instance = factory();
                                if (!CanSelectEffectProvider(instance, selectionTarget, hideKeyFramedProviders: true))
                                {
                                    Log($"Rejected effect provider {bundleTypeName} for clip {clip.Id} ({clip.ClipType}).", "warning");
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
                }
            };
            ppb.AppendWhen(SettingsManager.IsBoolSettingTrue("DeveloperMode"),
                  p => p.AddSeparator()
                        .AddButton(PPLocalizedResources.EffectTab_ShowAll, (s, e) =>
                        {
                            showAllEffect = !showAllEffect;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        })
                        .AddButton(PPLocalizedResources.EffectTab_Rebuild, async (s, e) =>
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
                        })
                        .AddButton("Generate bind graph (AnchorsBindingState)", async (s, e) =>
                        {
                            var graph = EffectBindingHelper.GenerateStoredMermaidDiagram(clip.EffectProviders);
                            await page.ShowPopupAsync(new MermaidCodeBlockRenderer().Render(graph), new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = true });
                        })
                        .AddButton("Generate bind graph (Fields)", async (s, e) =>
                        {
                            var graph = EffectBindingHelper.GenerateRenderTimeMermaidDiagram(clip.EffectProviders);
                            await page.ShowPopupAsync(new MermaidCodeBlockRenderer().Render(graph), new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = true });
                        }));

            var panel = ppb.BuildWithScrollView();
            return panel;
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
                        () =>
                        {
                            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddProvider", item.Key);
                        })
                });
            }
            ppb.PropertyChanged += (s, e) =>
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
            };

            ppb.AddButton(PPLocalizedResources.ColorAdjustment_Reset, (_, _) =>
            {
                if (clip.EffectProviders is { } providers)
                    EffectBindingHelper.RemoveProvider(providers, InternalColorAdjustmentProviderGuid);
                RebuildAllEffects(clip);
                handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
            }, (c) => c.TextColor = Color.FromArgb("#FF8080"));

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
                return new ClipPositionTuple(clip.TargetX, clip.TargetY, width, height, false, clip.Rotation);
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

            // Also check ProgressPlacer which may exist as its own bundle
            if (TryGetProgressPlacerProvider(clip, out var placerProvider, out _, false) && !hasAnyProvider)
            {
                hasAnyProvider = true;
                var section = BuildKeyframeProviderSectionUI(placerProvider, clip, GetCurrentClipPosition, handler);
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

            bool isCollapsed = false;
            var collapseTap = new TapGestureRecognizer();
            var titleTap = new TapGestureRecognizer();
            void SwitchCollpaseMode()
            {
                isCollapsed = !isCollapsed;
                collapseButton.Text = isCollapsed ? "▶" : "▼";
                listHost.IsVisible = !isCollapsed;
            }
            collapseTap.Tapped += (_, _) => SwitchCollpaseMode();
            titleTap.Tapped += (_, _) => SwitchCollpaseMode();
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
                        Command = new Command(() =>
                        {
                            if (provider is IEffectProvider bud && clip.EffectProviders is { } providers)
                                EffectBindingHelper.RemoveProvider(providers, bud.Id);
                            handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                            RebuildList();
                        }),
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
                    deleteButton.Clicked += (_, _) =>
                    {
                        provider.RemoveStep(idx);
                        RebuildAllEffects(clip);
                        handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                        RebuildList();
                    };
                    header.Add(deleteButton, 1, 0);
                    stack.Children.Add(header);

                    var stepPpb = provider.CreateStepUI(idx);
                    var stepView = stepPpb.Build();
                    stepPpb.PropertyChanged += (s, args) =>
                    {
                        if (provider.HandleStepUIChange(idx, args))
                        {
                            RebuildAllEffects(clip);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                            RebuildList();
                        }
                    };
                    stack.Children.Add(stepView);
                    card.Content = stack;
                    listHost.Children.Add(card);
                }
            }

            addButton.Clicked += (_, _) =>
            {
                if (isCollapsed)
                {
                    isCollapsed = !isCollapsed;
                    collapseButton.Text = isCollapsed ? "▶" : "▼";
                    listHost.IsVisible = !isCollapsed;
                }
                provider.AddStep(getDefaultPosition());
                RebuildAllEffects(clip);
                handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                RebuildList();
            };

            RebuildList();
            return section;
        }

        #endregion

        #region mixture

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

            if (mixtureProviders.Count == 0)
            {
                ppb.AddText(new SingleLineLabel(PPLocalizedResources.Mixture_None));
            }

            var bundle = mixtureProviders.FirstOrDefault();
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

                ppb.AddButton(PPLocalizedResources.EffectProp_Remove, (s, e) =>
                {
                    if (clip.EffectProviders is { } providers)
                        EffectBindingHelper.RemoveProvider(providers, bundleId);
                    RebuildAllEffects(clip);
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return;
                });
                ppb.AddSeparator();
            }
            else
            {
                ppb.AppendWhen(mixtureProviderFactoryItems.Count > 0, c => c.AddCustomChild(BuildAddEffectPanel(EffectTarget.Mixture, page, allProviderFactories, ppb, handler, false)));
            }

            ppb.PropertyChanged += (s, e) =>
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
                        handler?.Invoke(s, e);
                    }
                    return;
                }
                else if (e.Id == "AddProvider")
                {
                    int currentCount = clip.EffectProviders.Values.Count(IsMixtureProvider);
                    if (currentCount >= 1)
                    {
                        page.Dispatcher.Dispatch(async () =>
                        {
                            await page.DisplayAlertAsync(Localized._Info, PPLocalizedResources.Mixture_ErrSingle, Localized._OK);
                        });
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
            };

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

        private static string GetEffectTypeName(EffectTarget target)
        {
            StringBuilder result = new();
            if (target.HasFlag(EffectTarget.Video)) result.Append(Localized.EffectType_Video);
            if (target.HasFlag(EffectTarget.Audio)) result.Append(Localized.EffectType_Audio);
            if (target.HasFlag(EffectTarget.SpeedVariance)) result.Append(Localized.EffectType_SpeedVariance);
            if (target.HasFlag(EffectTarget.Mixture)) result.Append(Localized.EffectType_Mixture);
            if (target.HasFlag(EffectTarget.ColorAdjustment)) result.Append(Localized.EffectType_ColorAdjustment);
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
            bool hideKeyFramedProviders = false)
        {
            const EffectTarget targetKinds = EffectTarget.Video | EffectTarget.Audio | EffectTarget.Text
                | EffectTarget.VectorComponent | EffectTarget.VectorPicture | EffectTarget.SpeedVariance | EffectTarget.Mixture
                | EffectTarget.ColorAdjustment | EffectTarget.SourceReplacement | EffectTarget.ValueProvider;
            if (provider.TypeOfEffect == EffectType.Transform)
                return false;
            if (target != EffectTarget.NotSpecified && (provider.Target & target & targetKinds) == 0)
                return false;
            if (target != EffectTarget.NotSpecified && target.HasFlag(EffectTarget.Text)
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
            bool hideKeyFramedProviders = false)
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
                    || !CanSelectEffectProvider(factory(), target, ignoreIsNotVisibleInNewEffectSelector, hideKeyFramedProviders))
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
                if (!CanSelectEffectProvider(instance, target, ignoreIsNotVisibleInNewEffectSelector, hideKeyFramedProviders))
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

            var flex = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                Direction = FlexDirection.Row,
                JustifyContent = FlexJustify.Start,
                AlignItems = FlexAlignItems.Start,
                AlignContent = FlexAlignContent.Start
            };

            // Filter state
            string? filterSearchText = null;
            string? filterCategory = null;

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

                BindableLayout.SetItemsSource(flex, filtered.ToList());
            }

            // ─── Search bar ───
            var searchBar = new SearchBar
            {
                Placeholder = PPLocalizedResources.Effect_Add_Search
            };
            searchBar.TextChanged += (_, e) =>
            {
                filterSearchText = e.NewTextValue;
                ApplyFilter();
            };

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
            categoryPicker.SelectedIndexChanged += (_, _) =>
            {
                filterCategory = categoryPicker.SelectedIndex <= 0 ? null : categoryPicker.Items[categoryPicker.SelectedIndex];
                ApplyFilter();
            };

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

            BindableLayout.SetItemsSource(flex, cards);
            BindableLayout.SetEmptyView(flex, new Label
            {
                Text = emptyText ?? PPLocalizedResources.Add_Effect_None,
                FontSize = 18,
            });
            BindableLayout.SetItemTemplate(flex, new DataTemplate(() =>
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
                    LineBreakMode = LineBreakMode.TailTruncation
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
                    WidthRequest = cardWidth,
                    HeightRequest = cardHeight,
                    Margin = new Thickness(cardMargin),
                    Padding = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    StrokeThickness = 1,
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = cardContent
                };
                bool hovered = false, loaded = false;
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
                if (loadPreview is not null)
                {
                    previewGrid.Children.Add(loading);
                    border.Loaded += (_, _) => loaded = true;
                    border.BindingContextChanged += async (_, _) =>
                    {
                        previewCts?.Cancel();
                        loading.IsRunning = loading.IsVisible = false;
                        mediaPlayer.Source = null;
                        mediaPlayer.IsVisible = false;
                        image.IsVisible = true;
                        if (loaded && hovered) await ShowPreview();
                    };
                    border.Unloaded += (_, _) =>
                    {
                        loaded = false;
                        hovered = false;
                        previewCts?.Cancel();
                        loading.IsRunning = loading.IsVisible = false;
                        mediaPlayer.Stop();
                        mediaPlayer.Source = null;
                    };
                }

                // ─── Hover handling ───
                async void OnHover(bool isHovered)
                {
                    hovered = isHovered;
                    if (border.BindingContext is EffectProviderCardItem item)
                    {
                        if (isHovered)
                        {
                            hoverOverlay.IsVisible = true;
                            if (loadPreview is not null) await ShowPreview();
                            else if (item.VideoThumbnail is not null)
                            {
                                mediaPlayer.Source = item.VideoThumbnail;
                                mediaPlayer.IsVisible = true;
                                image.IsVisible = false;
                                mediaPlayer.Play();
                            }
                        }
                        else
                        {
                            hoverOverlay.IsVisible = false;
                            previewCts?.Cancel();
                            loading.IsRunning = loading.IsVisible = false;
                            mediaPlayer.Pause();
                            mediaPlayer.Source = null;
                            mediaPlayer.IsVisible = false;
                            image.IsVisible = true;
                        }
                    }
                }

#if MACCATALYST || WINDOWS
                var pointerGesture = new PointerGestureRecognizer();
                pointerGesture.PointerEntered += (_, _) => OnHover(true);
                pointerGesture.PointerExited += (_, _) => OnHover(false);
                cardContent.GestureRecognizers.Add(pointerGesture);
#endif

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
                        if (border.BindingContext is EffectProviderCardItem item)
                        {
                            onSelected?.Invoke(item.ProviderTypeName);
                            SelectCard(border);
                        }
                    },
                    OnClicked: () =>
                    {
                        if (border.BindingContext is EffectProviderCardItem item)
                            onAdd(item.ProviderTypeName);
                    },
                    OnContextMenuClick: async () =>
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
                        Text = selectionText ?? PPLocalizedResources.Add_Effect_Select,
                        Opacity = 0.7,
                        FontSize = 13
                    },
                    filterBar,
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
                    ppb.AppendWhen(
                       condition: (Guid.TryParse(effect.BindedEffectProvidingSystemID, out var g) && (clip.EffectProviders?.TryGetValue(g, out eb) ?? false) && eb is not null),
                       onTrue: c => c.AddCustomChild("Binded IEffectProvider", new Label { Text = $"{eb.Name} ({effect.BindedEffectProvidingSystemID})" })
                                     .AddCustomChild("IEffectProvider.EffectTarget", new Label { Text = eb?.Target is not null ? eb.Target.ToString() : "No bundle" }),
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
            };

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

    }
}
