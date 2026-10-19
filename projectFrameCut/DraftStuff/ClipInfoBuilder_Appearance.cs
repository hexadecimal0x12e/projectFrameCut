using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
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
using System.Text.Json;
using System.Text.Json.Serialization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using GridLength = Microsoft.Maui.GridLength;
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
        #region size and pos

        public View BuildSizeAndPositionTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            try
            {
                clip.Effects ??= new Dictionary<string, IEffect>();

                int valX = 0, valY = 0;
                int valW = page.ProjectInfo.RelativeWidth;
                int valH = page.ProjectInfo.RelativeHeight;
                if (clip.Effects.ContainsKey(InternalRotationID)) RebuildAllEffects(clip);
                double rotationDeg = VideoClipRotation.Normalize(clip.Rotation);
                bool allowFreeScaleResize = IsAllowFreeScaleResizeEnabled(clip);
                valX = clip.TargetX;
                valY = clip.TargetY;
                if (clip.TargetWidth > 0) valW = clip.TargetWidth;
                if (clip.TargetHeight > 0) valH = clip.TargetHeight;

                IEffectProvider BuildDefaultCropProvider()
                {
                    if (!EffectServices.GetAvailableEffectProviders().TryGetValue("Crop", out var cropProviderFactory))
                    {
                        throw new KeyNotFoundException("Crop effect bundle factory not found.");
                    }

                    var bundle = cropProviderFactory();
                    bundle.Id = InternalCropProviderGuid;
                    bundle.Name = InternalCropID;
                    bundle.Enabled = false;
                    bundle.SetMainInputSource(IEffectProvider.InputAnchorGUID);
                    bundle.SetFinalOutputSource(false);
                    var fields = bundle.Fields;
                    fields["StartX"] = new StaticEffectArgumentField(0, EffectArgumentFieldType.Integer);
                    fields["StartY"] = new StaticEffectArgumentField(0, EffectArgumentFieldType.Integer);
                    fields["Width"] = new StaticEffectArgumentField(page.ProjectInfo.RelativeWidth, EffectArgumentFieldType.Integer);
                    fields["Height"] = new StaticEffectArgumentField(page.ProjectInfo.RelativeHeight, EffectArgumentFieldType.Integer);
                    fields["Angle"] = new StaticEffectArgumentField(0f, EffectArgumentFieldType.Numeric);
                    bundle.Fields = fields;
                    return bundle;
                }

                IEffectProvider NormalizeCropProvider(IEffectProvider? source, IEffect? fallbackEffect)
                {
                    var normalized = BuildDefaultCropProvider();

                    if (source != null && string.Equals(source.TypeName, "Crop", StringComparison.Ordinal))
                    {
                        normalized.Enabled = source.Enabled;
                        normalized.AnchorsBindingState = new Dictionary<string, string>(source.AnchorsBindingState);
                        var fields = normalized.Fields;
                        fields["StartX"] = new StaticEffectArgumentField(Math.Max(0, ReadProviderFieldInt(source.Fields, "StartX", 0)), EffectArgumentFieldType.Integer);
                        fields["StartY"] = new StaticEffectArgumentField(Math.Max(0, ReadProviderFieldInt(source.Fields, "StartY", 0)), EffectArgumentFieldType.Integer);
                        fields["Width"] = new StaticEffectArgumentField(Math.Max(1, ReadProviderFieldInt(source.Fields, "Width", page.ProjectInfo.RelativeWidth)), EffectArgumentFieldType.Integer);
                        fields["Height"] = new StaticEffectArgumentField(Math.Max(1, ReadProviderFieldInt(source.Fields, "Height", page.ProjectInfo.RelativeHeight)), EffectArgumentFieldType.Integer);
                        fields["Angle"] = new StaticEffectArgumentField(ReadProviderFieldFloat(source.Fields, "Angle", 0f), EffectArgumentFieldType.Numeric);
                        normalized.Fields = fields;
                        return normalized;
                    }

                    if (fallbackEffect != null && IsCropEffect(fallbackEffect))
                    {
                        normalized.Enabled = fallbackEffect.Enabled;
                        var fields = normalized.Fields;
                        fields["StartX"] = new StaticEffectArgumentField(Math.Max(0, ReadEffectIntParameter(fallbackEffect, "StartX", 0)), EffectArgumentFieldType.Integer);
                        fields["StartY"] = new StaticEffectArgumentField(Math.Max(0, ReadEffectIntParameter(fallbackEffect, "StartY", 0)), EffectArgumentFieldType.Integer);
                        fields["Width"] = new StaticEffectArgumentField(Math.Max(1, ReadEffectIntParameter(fallbackEffect, "Width", page.ProjectInfo.RelativeWidth)), EffectArgumentFieldType.Integer);
                        fields["Height"] = new StaticEffectArgumentField(Math.Max(1, ReadEffectIntParameter(fallbackEffect, "Height", page.ProjectInfo.RelativeHeight)), EffectArgumentFieldType.Integer);
                        fields["Angle"] = new StaticEffectArgumentField(ReadEffectFloatParameter(fallbackEffect, "Angle", 0f), EffectArgumentFieldType.Numeric);
                        normalized.Fields = fields;
                    }

                    return normalized;
                }

                IEffect? existingCropEffect = TryFindInternalCropEffect(clip, out var existingCropEffectValue)
                    ? existingCropEffectValue
                    : null;

                clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
                clip.EffectProviders.TryGetValue(InternalCropProviderGuid, out var existingInternalCropProvider);
                var currentCropProvider = NormalizeCropProvider(existingInternalCropProvider, existingCropEffect);
                bool directCropEnabled = ReadBoolExtraData(clip.ExtraData, DirectCropEnabledKey, false);
                if (directCropEnabled)
                {
                    currentCropProvider.Enabled = true;
                    var directFields = currentCropProvider.Fields;
                    directFields["StartX"] = new StaticEffectArgumentField(Math.Max(0, clip.StartingX), EffectArgumentFieldType.Integer);
                    directFields["StartY"] = new StaticEffectArgumentField(Math.Max(0, clip.StartingY), EffectArgumentFieldType.Integer);
                    directFields["Width"] = new StaticEffectArgumentField(
                        Math.Max(1, ReadIntExtraData(clip.ExtraData, DirectCropWidthKey, clip.TargetWidth > 0 ? clip.TargetWidth : page.ProjectInfo.RelativeWidth)),
                        EffectArgumentFieldType.Integer);
                    directFields["Height"] = new StaticEffectArgumentField(
                        Math.Max(1, ReadIntExtraData(clip.ExtraData, DirectCropHeightKey, clip.TargetHeight > 0 ? clip.TargetHeight : page.ProjectInfo.RelativeHeight)),
                        EffectArgumentFieldType.Integer);
                    directFields["Angle"] = new StaticEffectArgumentField(0f, EffectArgumentFieldType.Numeric);
                    currentCropProvider.Fields = directFields;
                }
                else if (currentCropProvider.Enabled && SupportsDirectCrop(clip)
                    && Math.Abs(ReadProviderFieldFloat(currentCropProvider.Fields, "Angle", 0f)) < 0.0001f)
                {
                    int directWidth = Math.Max(1, ReadProviderFieldInt(currentCropProvider.Fields, "Width", page.ProjectInfo.RelativeWidth));
                    int directHeight = Math.Max(1, ReadProviderFieldInt(currentCropProvider.Fields, "Height", page.ProjectInfo.RelativeHeight));
                    SetCropGeometry(clip, new(true, Math.Max(0, ReadProviderFieldInt(currentCropProvider.Fields, "StartX", 0)),
                        Math.Max(0, ReadProviderFieldInt(currentCropProvider.Fields, "StartY", 0)), directWidth, directHeight, 0));
                    valW = directWidth;
                    valH = directHeight;
                }
                IEffectProvider previousCropPayload = currentCropProvider;

                var cropView = new ClipCropConfiguratorView
                {
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start,
                    Margin = new(8, 0, 8, 0),
                };

                cropView.LoadFromProvider(currentCropProvider, existingCropEffect);
                cropView.RelativeWidth = page.ProjectInfo.RelativeWidth;
                cropView.RelativeHeight = page.ProjectInfo.RelativeHeight;

                var transformPpb = new PropertyPanelBuilder()
                    .AddPositionTupleInputBox("place", new SingleLineLabel(PPLocalizedResources.General_LocationAndSize, 25), PositionTupleMode.XYWH, (valX, valY, valW, valH), entryWidth: 70)
                    .AddCheckbox("allowFreeScaleResize", PPLocalizedResources.General_LocationAndSize_FreeZoom, allowFreeScaleResize)
                    .AddSlider("rotationDeg", PPLocalizedResources.General_Rotation, 0, 360, rotationDeg)
                    .AddText(new SingleLineLabel(PPLocalizedResources.General_Crop, 25))
                    .AddCheckbox("cropEnable", PPLocalizedResources._Enabled, currentCropProvider.Enabled)
                    .AppendWhen(currentCropProvider.Enabled,
                    c => c.AddButton(PPLocalizedResources.Effect_ProgressPlacer_OpenEditor, async (_, _) => await page.ShowAPopup(content: cropView, mode: "dialog"))
                        .AddSeparator()
                        .AddEntry("cropStartX", PPLocalizedResources._StartX, cropView.StartX.ToString(), "0", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                        .AddEntry("cropStartY", PPLocalizedResources._StartY, cropView.StartY.ToString(), "0", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                        .AddEntry("cropWidth", PPLocalizedResources._Width, cropView.CropWidth.ToString(), "1", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                        .AddEntry("cropHeight", PPLocalizedResources._Height, cropView.CropHeight.ToString(), "1", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                        );

                cropView.ConfigurationChanged += (s, bundle) =>
                {
                    clip.Effects ??= new Dictionary<string, IEffect>();
                    clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();

                    if (!string.Equals(bundle.TypeName, "Crop", StringComparison.Ordinal))
                    {
                        return;
                    }

                    var normalized = NormalizeCropProvider(bundle, existingCropEffect);
                    currentCropProvider = normalized;
                    int width = Math.Max(1, ReadProviderFieldInt(normalized.Fields, "Width", page.ProjectInfo.RelativeWidth));
                    int height = Math.Max(1, ReadProviderFieldInt(normalized.Fields, "Height", page.ProjectInfo.RelativeHeight));
                    SetCropGeometry(clip, new(normalized.Enabled,
                        Math.Max(0, ReadProviderFieldInt(normalized.Fields, "StartX", 0)),
                        Math.Max(0, ReadProviderFieldInt(normalized.Fields, "StartY", 0)), width, height,
                        ReadProviderFieldFloat(normalized.Fields, "Angle", 0)));
                    if (normalized.Enabled)
                    {
                        SetTransformEntryText("place_W", width);
                        SetTransformEntryText("place_H", height);
                    }

                    if (TryFindInternalCropEffect(clip, out var rebuiltCrop))
                    {
                        rebuiltCrop.RelativeWidth = page.ProjectInfo.RelativeWidth;
                        rebuiltCrop.RelativeHeight = page.ProjectInfo.RelativeHeight;
                        SyncOutputSizeFromCropIfNeeded(rebuiltCrop);
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("crop", rebuiltCrop, previousCropPayload));
                    }
                    else
                    {
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("crop", normalized, previousCropPayload));
                    }

                    previousCropPayload = normalized;
                    SyncCropInputsFromView();
                };

                bool syncingCropInputs = false;

                void SetTransformEntryText(string id, int value)
                {
                    if (transformPpb.Components.TryGetValue(id, out var component) && component is Entry entry)
                    {
                        var text = value.ToString();
                        if (entry.Text != text)
                        {
                            entry.Text = text;
                        }

                        transformPpb.Properties[id] = text;
                    }
                }

                void ApplyResizeToModelWithCurrentMode(int width, int height)
                {
                    width = Math.Max(1, width);
                    height = Math.Max(1, height);

                    clip.TargetWidth = width;
                    clip.TargetHeight = height;
                }

                void SyncOutputSizeFromCropIfNeeded(IEffect crop)
                {
                    if (!crop.Enabled)
                    {
                        return;
                    }

                    if (!TryGetCropSize(crop, out var cropW, out var cropH))
                    {
                        return;
                    }

                    int croppedW = Math.Max(1, cropW);
                    int croppedH = Math.Max(1, cropH);

                    SetTransformEntryText("place_W", croppedW);
                    SetTransformEntryText("place_H", croppedH);
                    ApplyResizeToModelWithCurrentMode(croppedW, croppedH);
                }

                void SnapSizeBackToSourceAspectIfNeeded()
                {
                    if (!TryGetSourceAspectRatio(clip, [page.Assets, AssetDatabase.Assets], out var sourceAspect) || sourceAspect <= 0)
                    {
                        return;
                    }

                    int currentW = ResolvePanelInt(transformPpb, "place_W", transformPpb.Properties.GetValueOrDefault("place_W"), "place_W", clip.TargetWidth > 0 ? clip.TargetWidth : page.ProjectInfo.RelativeWidth);
                    int currentH = ResolvePanelInt(transformPpb, "place_H", transformPpb.Properties.GetValueOrDefault("place_H"), "place_H", clip.TargetHeight > 0 ? clip.TargetHeight : page.ProjectInfo.RelativeHeight);

                    currentW = Math.Max(1, currentW);
                    currentH = Math.Max(1, currentH);

                    int snappedW;
                    int snappedH;
                    if (Math.Abs(((double)currentW / currentH) - sourceAspect) < 1e-6)
                    {
                        snappedW = currentW;
                        snappedH = currentH;
                    }
                    else
                    {
                        snappedW = currentW;
                        snappedH = Math.Max(1, (int)Math.Round(currentW / sourceAspect, MidpointRounding.AwayFromZero));
                    }

                    SetTransformEntryText("place_W", snappedW);
                    SetTransformEntryText("place_H", snappedH);
                    ApplyResizeToModelWithCurrentMode(snappedW, snappedH);
                }

                transformPpb.PropertyChanged += (s, e) =>
                {
                    clip.Effects ??= new Dictionary<string, IEffect>();

                    if (e.Id == "allowFreeScaleResize")
                    {
                        bool allowFreeScale = e.Value is bool b
                            ? b
                            : bool.TryParse(e.Value?.ToString(), out var parsed) && parsed;

                        clip.ExtraData ??= new Dictionary<string, object>();
                        clip.ExtraData[AllowFreeScaleResizeKey] = allowFreeScale;

                        if (!allowFreeScale)
                        {
                            SnapSizeBackToSourceAspectIfNeeded();
                        }

                        handler?.Invoke(s, e);
                        return;
                    }

                    if (e.Id.StartsWith("place_"))
                    {
                        switch (e.Id)
                        {
                            case "place_X":
                                clip.TargetX = (int)Math.Round(Convert.ToDouble(e.Value));
                                break;
                            case "place_Y":
                                clip.TargetY = (int)Math.Round(Convert.ToDouble(e.Value));
                                break;
                            case "place_W":
                                clip.TargetWidth = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                                break;
                            case "place_H":
                                clip.TargetHeight = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                                break;
                        }

                        handler?.Invoke(s, e);
                        return;
                    }

                    if (e.Id == "rotationDeg")
                    {
                        if (e.Value is double angle)
                        {
                            clip.Rotation = VideoClipRotation.Normalize((float)angle);
                            LogDiagnostic($"Clip {clip.Id} rotation changed to {clip.Rotation} degrees.");
                        }

                        handler?.Invoke(s, e);
                        return;
                    }

                    if (!syncingCropInputs)
                    {
                        if (e.Id == "cropStartX" && int.TryParse(e.Value?.ToString(), out var sx))
                        {
                            cropView.StartX = sx;
                        }
                        else if (e.Id == "cropStartY" && int.TryParse(e.Value?.ToString(), out var sy))
                        {
                            cropView.StartY = sy;
                        }
                        else if (e.Id == "cropWidth" && int.TryParse(e.Value?.ToString(), out var w))
                        {
                            cropView.CropWidth = w;
                        }
                        else if (e.Id == "cropHeight" && int.TryParse(e.Value?.ToString(), out var h))
                        {
                            cropView.CropHeight = h;
                        }
                        else if (e.Id == "cropEnable" && e.Value is bool cropEnabled)
                        {
                            cropView.Enabled = cropEnabled;
                            handler?.Invoke(s, e);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            return;

                        }
                    }

                    handler?.Invoke(s, e);
                };



                void SetCropEntryText(string id, int value)
                {
                    if (transformPpb is null)
                    {
                        return;
                    }

                    if (transformPpb.Components.TryGetValue(id, out var component) && component is Entry entry)
                    {
                        var text = value.ToString();
                        if (entry.Text != text)
                        {
                            entry.Text = text;
                        }
                        transformPpb.Properties[id] = text;
                    }
                }

                void SetCropTextEntryText(string id, string value)
                {
                    if (transformPpb is null)
                    {
                        return;
                    }

                    if (transformPpb.Components.TryGetValue(id, out var component) && component is Entry entry)
                    {
                        if (entry.Text != value)
                        {
                            entry.Text = value;
                        }

                        transformPpb.Properties[id] = value;
                    }
                }

                void SyncCropInputsFromView()
                {
                    syncingCropInputs = true;
                    try
                    {
                        SetCropEntryText("cropStartX", cropView.StartX);
                        SetCropEntryText("cropStartY", cropView.StartY);
                        SetCropEntryText("cropWidth", cropView.CropWidth);
                        SetCropEntryText("cropHeight", cropView.CropHeight);
                    }
                    finally
                    {
                        syncingCropInputs = false;
                    }
                }

                var scrollView = transformPpb.BuildWithScrollView();

                if (TryGetProgressPlacerProvider(clip, out _, out _, false))
                {
                    var root = new Grid
                    {
                        RowDefinitions =
                    {
                        new RowDefinition(GridLength.Auto),
                        new RowDefinition(GridLength.Star)
                    },
                        RowSpacing = 8
                    };
                    root.Add(new Label
                    {
                        Text = PPLocalizedResources.KeyFrame_EditWarning,
                        TextColor = Colors.Yellow,
                        FontSize = 12,
                        HorizontalOptions = LayoutOptions.Fill,
                        VerticalOptions = LayoutOptions.Center,
                        Margin = new Thickness(10, 10, 0, 0)
                    }, 0, 0);
                    root.Add(scrollView, 0, 1);

                    SyncCropInputsFromView();
                    return root;
                }

                SyncCropInputsFromView();
                return scrollView;
            }
            catch (Exception ex)
            {
                Log(ex, "Load dSizeAndPositionTab", this);
                return new VerticalStackLayout
                {
                    Children =
                    {
                        new Label { Text = Localized._ExceptionTemplate(ex) }
                    }
                };
            }
        }

        private bool TryGetProgressPlacerProvider(ClipElementUI clip, out IKeyFramedEffectProvider provider, out IEffectProvider bundle, bool createIfMissing = false)
        {
            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();

            foreach (var eb in clip.EffectProviders.Values)
            {
                if (string.Equals(eb.TypeName, "ProgressPlacer", StringComparison.Ordinal)
                    && eb is IKeyFramedEffectProvider kfp)
                {
                    provider = kfp;
                    bundle = eb;
                    return true;
                }
            }

            if (createIfMissing)
            {
                var newProvider = new ProgressPlacerProvider();
                clip.EffectProviders[newProvider.Id] = newProvider;
                EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, newProvider, clip.GetEffectTarget());
                RebuildAllEffects(clip);
                bundle = newProvider;
                provider = newProvider;
                return true;
            }

            provider = null!;
            bundle = null!;
            return false;
        }

        #endregion

        #region audio

        public View BuildAudioTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            var track = clip.ClipType == ClipMode.VideoClip ? page.GetBoundSoundTrack(clip)
                : clip.ClipType == ClipMode.AudioClip ? clip : null;
            if (track is null) return new Grid();

            var ppb = new PropertyPanelBuilder()
                .AddCheckbox("audioEnabled", PPLocalizedResources._Enabled,
                    SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true))
                .AddSlider("volume", PPLocalizedResources.General_Audio_Volume, 0, 1,
                    SoundTrackMetadata.ReadVolume(track.ExtraData));

            if (clip.ClipType == ClipMode.VideoClip
                && SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.GeneratedFromVideoKey))
            {
                ppb.AddButton(PPLocalizedResources.General_Unbind, async (s, e) =>
                {
                    await page.UnbindClipAudioAsync(clip);
                    page.RefreshPropertyPanel(clip);
                });
            }

            ppb.PropertyChanged += (s, e) =>
            {
                if (e.Id == "audioEnabled")
                    page.SetClipAudioEnabled(clip, Convert.ToBoolean(e.Value));
                else if (e.Id == "volume")
                    page.SetClipAudioVolume(clip, Convert.ToDouble(e.Value, System.Globalization.CultureInfo.InvariantCulture));
                else
                    return;

                handler?.Invoke(s, e);
            };
            if (clip.ClipType == ClipMode.VideoClip)
            {
                ppb.AddSeparator();
                ppb.AddText(new Label { Text = Localized.Transform_Tab });
                ppb.AddCustomChild(BuildTransformTab(track));
            }
            return ppb.BuildWithScrollView();
        }

        #endregion

        #region geometry
        internal sealed record CropGeometry(bool Enabled, int X, int Y, int Width, int Height, float Rotation);

        private static bool SupportsDirectCrop(ClipElementUI clip) =>
            (clip.ClipType is ClipMode.VideoClip or ClipMode.PhotoClip) && !clip.SupportsNativeEffects
            && !EffectGroupHelper.Read(clip.ExtraData).Any(g => g.ProviderIds.Contains(InternalCropProviderGuid));

        internal static CropGeometry GetCropGeometry(ClipElementUI clip, int width, int height)
        {
            if (ReadBoolExtraData(clip.ExtraData, DirectCropEnabledKey, false))
                return new(true, clip.StartingX, clip.StartingY,
                    ReadIntExtraData(clip.ExtraData, DirectCropWidthKey, width),
                    ReadIntExtraData(clip.ExtraData, DirectCropHeightKey, height), 0);
            if (clip.EffectProviders?.GetValueOrDefault(InternalCropProviderGuid) is { } provider)
                return new(provider.Enabled, ReadProviderFieldInt(provider.Fields, "StartX", 0),
                    ReadProviderFieldInt(provider.Fields, "StartY", 0),
                    ReadProviderFieldInt(provider.Fields, "Width", width),
                    ReadProviderFieldInt(provider.Fields, "Height", height),
                    ReadProviderFieldFloat(provider.Fields, "Angle", 0));
            if (TryFindInternalCropEffect(clip, out var effect))
                return new(effect.Enabled, ReadEffectIntParameter(effect, "StartX", 0), ReadEffectIntParameter(effect, "StartY", 0),
                    ReadEffectIntParameter(effect, "Width", width), ReadEffectIntParameter(effect, "Height", height),
                    ReadEffectFloatParameter(effect, "Angle", 0));
            return new(false, 0, 0, width, height, 0);
        }

        internal static void SetCropGeometry(ClipElementUI clip, CropGeometry crop)
        {
            if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 || !float.IsFinite(crop.Rotation))
                throw new ArgumentException("Invalid crop geometry.");
            clip.EffectProviders ??= new();
            clip.ExtraData ??= new();
            if (crop.Enabled && SupportsDirectCrop(clip) && Math.Abs(VideoClipRotation.Normalize(crop.Rotation)) < 0.0001f)
            {
                clip.StartingX = crop.X;
                clip.StartingY = crop.Y;
                clip.TargetWidth = crop.Width;
                clip.TargetHeight = crop.Height;
                clip.ExtraData[DirectCropEnabledKey] = true;
                clip.ExtraData[DirectCropWidthKey] = crop.Width;
                clip.ExtraData[DirectCropHeightKey] = crop.Height;
                if (clip.EffectProviders.Remove(InternalCropProviderGuid, out var previous))
                {
                    string source = previous.GetMainInputSource();
                    bool Matches(string value) => value == previous.Id.ToString()
                        || EffectProviderOutputExtensions.TryParseOutputSourceId(value, out var providerId, out _) && providerId == previous.Id;
                    foreach (var provider in clip.EffectProviders.Values)
                    {
                        if (Matches(provider.GetMainInputSource())) provider.SetMainInputSource(source);
                        foreach (var binding in provider.EnumerateFieldBindings().Where(b => Matches(b.Value)).ToArray())
                            provider.SetFieldBinding(binding.Key, source);
                    }
                    if (previous.IsFinalOutputSource())
                        EffectBindingHelper.SetFinalOutput(clip.EffectProviders, Guid.TryParse(source, out var id)
                            && clip.EffectProviders.ContainsKey(id) ? id : null, previous.GetPipeline());
                }
            }
            else
            {
                clip.StartingX = clip.StartingY = 0;
                clip.ExtraData.Remove(DirectCropEnabledKey);
                clip.ExtraData.Remove(DirectCropWidthKey);
                clip.ExtraData.Remove(DirectCropHeightKey);
                bool add = !clip.EffectProviders.TryGetValue(InternalCropProviderGuid, out var provider);
                provider ??= EffectServices.GetAvailableEffectProviders()["Crop"]();
                provider.Id = InternalCropProviderGuid;
                provider.Name = InternalCropID;
                provider.Enabled = crop.Enabled;
                var fields = provider.Fields;
                foreach (var (key, value) in new (string, object)[]
                    { ("StartX", crop.X), ("StartY", crop.Y), ("Width", crop.Width), ("Height", crop.Height), ("Angle", crop.Rotation) })
                {
                    fields[key] = new StaticEffectArgumentField(value, key == "Angle" ? EffectArgumentFieldType.Numeric : EffectArgumentFieldType.Integer);
                    provider.ClearFieldBinding(key);
                }
                provider.Fields = fields;
                clip.EffectProviders[provider.Id] = provider;
                if (add) EffectBindingHelper.AutoConnectProviderToInput(clip.EffectProviders, provider);
                if (crop.Enabled)
                {
                    clip.TargetWidth = crop.Width;
                    clip.TargetHeight = crop.Height;
                }
            }
            RemoveInternalCropEffects(clip);
            RebuildAllEffects(clip);
        }
        #endregion

    }
}
