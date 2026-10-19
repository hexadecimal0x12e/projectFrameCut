using CommunityToolkit.Maui.Storage;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Path = System.IO.Path;

namespace projectFrameCut.DraftStuff;

public partial class ClipInfoBuilder
{
    private const string SolidColorOutputWidthKey = "SolidColorOutputWidth";
    private const string SolidColorOutputHeightKey = "SolidColorOutputHeight";
    private const string SolidColorUseFixedOutputSizeKey = "SolidColorUseFixedOutputSize";

    private View BuildVideoSourcePropertiesTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
    {
        var targetVideoClip = page.GetLoadedClipInstance(clip.Id) as VideoClip;
        ExternalVideoSourceDescriptor? externalSource = null;
        if (RemoteRpcVideoSource.IsExternalPath(clip.SourcePath)
            && (ProjectExternalVideoSource.TryGetDescriptor(clip.SourcePath!, out var source)
                || RemoteRpcVideoSource.TryGetDescriptor(clip.SourcePath!, out source)))
            externalSource = source;
        var mediaResources = PPLocalizedResources.General_VideoCodec_SourceInfo.StartsWith("Unset localization item:", StringComparison.Ordinal)
            ? ISimpleLocalizerBase_PropertyPanel.GetMapping()["zh-CN"] : PPLocalizedResources;

        string GetVideoSourceText()
        {
            if (string.IsNullOrWhiteSpace(clip.SourcePath)) return Localized._Unknown;
            if (externalSource is { } descriptor)
            {
                var name = string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.SourceId : descriptor.Name;
                if (!string.IsNullOrWhiteSpace(descriptor.ClientName) && descriptor.ClientName != name)
                    name += $"\n{descriptor.ClientName}";
                var info = new List<string>();
                if (descriptor.Width > 0 && descriptor.Height > 0) info.Add($"{descriptor.Width}\u00D7{descriptor.Height}");
                if (double.IsFinite(descriptor.Fps) && descriptor.Fps > 0) info.Add($"{descriptor.Fps:0.###} fps");
                if (descriptor.TotalFrames >= 0) info.Add(mediaResources.General_VideoCodec_FrameCount(descriptor.TotalFrames));
                return info.Count == 0 ? name : $"{name}\n{string.Join(" \u00B7 ", info)}";
            }

            if (clip.SourcePath.StartsWith("$"))
            {
                return AssetDatabase.Assets.TryGetValue(clip.SourcePath.Substring(1), out var asset)
                    ? $"{Localized.DraftPage_CenterMenuBar_Asset}: {asset.Name}({asset.Path})"
                    : $"Unknown asset: {clip.SourcePath.Substring(1)}";
            }

            return System.IO.Path.GetFullPath(clip.SourcePath);
        }

        string currentVideoDecoderId = ReadStringValue(clip.ExtraData?.GetValueOrDefault("TargetDecoder")
            ?? clip.ExtraData?.GetValueOrDefault("targetdecoder"), "auto");
        if (string.IsNullOrWhiteSpace(currentVideoDecoderId))
        {
            currentVideoDecoderId = "auto";
        }

        var videoDecoderOptionLabelToId = new Dictionary<string, string>
        {
            [PPLocalizedResources.General_VideoCodec_TargetMode_Auto] = "auto",
            [PPLocalizedResources.General_VideoCodec_TargetMode_8bpp] = "DecoderContext8Bit",
            [PPLocalizedResources.General_VideoCodec_TargetMode_8bppHWaccel] = "DecoderContextHW",
            [PPLocalizedResources.General_VideoCodec_TargetMode_16bpp] = "DecoderContext16Bit",
            [PPLocalizedResources.General_VideoCodec_TargetMode_hdr] = "HDRDecoderContext",
        };
        var allVideoDecoderOptionLabelToId = new Dictionary<string, string>(videoDecoderOptionLabelToId)
        {
            [PPLocalizedResources.General_VideoCodec_TargetMode_http] = "HttpDecoderContext",
            [PPLocalizedResources.General_VideoCodec_TargetMode_rpsv] = "RawPictureSequenceStreamVideoDecoderContext",
            [PPLocalizedResources.General_VideoCodec_TargetMode_ffmpegDevices] = "FFmpegDeviceDecoderContext",
        };

        string GetVideoTargetFormatText()
        {
            if (externalSource is null)
            {
                var id = targetVideoClip?.DecoderName ?? (currentVideoDecoderId == "auto" ? null : currentVideoDecoderId);
                return id is null ? Localized._Unknown : allVideoDecoderOptionLabelToId.ReverseLookup(id, PPLocalizedResources.General_VideoCodec_TargetMode_Unknown(id));
            }

            var decoder = targetVideoClip?.Decoder;
            string format;
            if (decoder is IHDRVideoSource || decoder is null && externalSource.SupportsHdr)
                format = PPLocalizedResources.General_VideoCodec_TargetMode_hdr;
            else
                format = (decoder?.ResultBitPerPixel ?? (externalSource.HasKnownResultBitsPerPixel ? externalSource.ResultBitsPerPixel : 0)) switch
                {
                    8 => PPLocalizedResources.General_VideoCodec_TargetMode_8bpp,
                    16 => PPLocalizedResources.General_VideoCodec_TargetMode_16bpp,
                    _ => Localized._Unknown
                };
            return externalSource.SupportsAlpha ? $"{format} \u00B7 Alpha" : format;
        }

        if (!videoDecoderOptionLabelToId.Values.Contains(currentVideoDecoderId, StringComparer.Ordinal))
        {
            videoDecoderOptionLabelToId[PPLocalizedResources.General_VideoCodec_TargetMode_Unknown(currentVideoDecoderId)] = currentVideoDecoderId;
        }

        string selectedVideoDecoderLabel = videoDecoderOptionLabelToId
            .FirstOrDefault(kv => string.Equals(kv.Value, currentVideoDecoderId, StringComparison.Ordinal)).Key
            ?? PPLocalizedResources.General_VideoCodec_TargetMode_Auto;

        var panel = new PropertyPanelBuilder()
            .AddText(new SingleLineLabel(PPLocalizedResources.General_VideoCodec, 20))
            .AppendWhen(!(clip.SourcePath?.StartsWith("#") ?? false),
                c => c.AddPicker("videoTargetDecoderMode", PPLocalizedResources.General_VideoCodec_TargetMode,
                        videoDecoderOptionLabelToId.Keys.ToArray(), selectedVideoDecoderLabel)
                    .AppendWhen(targetVideoClip?.Decoder?.GetType() == typeof(HDRDecoderContext),
                        p => p.AddSlider("hdrBrightnessOffset", PPLocalizedResources.General_VideoCodec_HDRBrightnessOffset,
                            -1, 1, targetVideoClip?.HDRBrightnessOffset ?? 0, eventCallMode: SliderUpdateEventCallMode.OnMouseUp)),
                c => c.AddCustomChild(PPLocalizedResources.General_VideoCodec_TargetMode,
                    new Label { Text = GetVideoTargetFormatText() }))
            .AddCustomChild(externalSource is null ? PPLocalizedResources.General_VideoCodec_Source : mediaResources.General_VideoCodec_SourceInfo,
                new Label { Text = GetVideoSourceText(), LineBreakMode = LineBreakMode.WordWrap });

        panel.PropertyChanged += (s, e) =>
        {
            clip.ExtraData ??= new();
            switch (e.Id)
            {
                case "videoTargetDecoderMode":
                    currentVideoDecoderId = videoDecoderOptionLabelToId.GetValueOrDefault(e.Value?.ToString() ?? "", currentVideoDecoderId);
                    clip.ExtraData["TargetDecoder"] = currentVideoDecoderId;
                    Log($"Changed decoder for clip {clip.Id} to {currentVideoDecoderId}.");
                    break;
                case "hdrBrightnessOffset":
                    clip.ExtraData["HDRBrightnessOffset"] = Convert.ToDouble(e.Value ?? 0d);
                    break;
                default:
                    return;
            }
            handler?.Invoke(s, e);
        };
        return panel.BuildWithScrollView();
    }

    private View BuildSolidColorSourcePropertiesTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
    {
        object? GetSolidColorRawValue(string key)
        {
            if (clip.ExtraData == null)
            {
                return null;
            }

            if (clip.ExtraData.TryGetValue(key, out var value))
            {
                return value;
            }

            return clip.ExtraData.TryGetValue(key.ToLowerInvariant(), out var lowerValue)
                ? lowerValue
                : null;
        }

        Color ResolveSolidColorFromExtraData()
        {
            int r16 = Math.Clamp(ReadIntValue(GetSolidColorRawValue("R"), ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
            int g16 = Math.Clamp(ReadIntValue(GetSolidColorRawValue("G"), ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
            int b16 = Math.Clamp(ReadIntValue(GetSolidColorRawValue("B"), ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
            float a = Math.Clamp(ReadFloatValue(GetSolidColorRawValue("A"), 1f), 0f, 1f);

            return Color.FromRgba(r16 / 65535.0, g16 / 65535.0, b16 / 65535.0, a);
        }

        void SaveSolidColorToExtraData(Color color)
        {
            clip.ExtraData ??= new Dictionary<string, object>();
            clip.ExtraData["R"] = (ushort)Math.Clamp((int)Math.Round(color.Red * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
            clip.ExtraData["G"] = (ushort)Math.Clamp((int)Math.Round(color.Green * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
            clip.ExtraData["B"] = (ushort)Math.Clamp((int)Math.Round(color.Blue * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
            clip.ExtraData["A"] = (float)Math.Clamp(color.Alpha, 0f, 1f);
        }

        string currentSolidColorHex = ToArgbHex(ResolveSolidColorFromExtraData());
        int valW = clip.TargetWidth > 0 ? clip.TargetWidth
            : ReadIntExtraData(clip.ExtraData, SolidColorOutputWidthKey, page.ProjectInfo.RelativeWidth);
        int valH = clip.TargetHeight > 0 ? clip.TargetHeight
            : ReadIntExtraData(clip.ExtraData, SolidColorOutputHeightKey, page.ProjectInfo.RelativeHeight);

        var panel = new PropertyPanelBuilder()
            .AddText(new SingleLineLabel(PPLocalizedResources.General_SolidColor, 20))
            .AddCustomChild(PPLocalizedResources.General_Color,
                invoker => BuildColorPropertyView(currentSolidColorHex, Colors.White, invoker), "solidColor", currentSolidColorHex)
            .AddPositionTupleInputBox("place", new SingleLineLabel(PPLocalizedResources.General_LocationAndSize, 20), PositionTupleMode.XYWH, (clip.TargetX, clip.TargetY, valW, valH), entryWidth: 70)
            .AddSlider("rotationDeg", PPLocalizedResources.General_Rotation, 0, 360, 0);

        panel.PropertyChanged += (s, e) =>
        {
            clip.ExtraData ??= new();
            switch (e.Id)
            {
                case "solidColor":
                    SaveSolidColorToExtraData(ParseArgbOrFallback(e.Value?.ToString(), Colors.White));
                    break;
                case "place_X":
                    clip.TargetX = (int)Math.Round(Convert.ToDouble(e.Value));
                    break;
                case "place_Y":
                    clip.TargetY = (int)Math.Round(Convert.ToDouble(e.Value));
                    break;
                case "place_W":
                    clip.TargetWidth = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                    clip.ExtraData[SolidColorOutputWidthKey] = clip.TargetWidth;
                    clip.ExtraData[SolidColorUseFixedOutputSizeKey] = true;
                    break;
                case "place_H":
                    clip.TargetHeight = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                    clip.ExtraData[SolidColorOutputHeightKey] = clip.TargetHeight;
                    clip.ExtraData[SolidColorUseFixedOutputSizeKey] = true;
                    break;
                case "rotationDeg":
                    if (e.Value is not double deg) return;
                    clip.Effects ??= new();
                    clip.Effects[InternalRotationID] = new RotationEffect_IPicture
                    {
                        Angle = (float)deg,
                        Enabled = true,
                        Name = InternalRotationID,
                        Index = int.MinValue + 100,
                        RelativeWidth = page.ProjectInfo.RelativeWidth,
                        RelativeHeight = page.ProjectInfo.RelativeHeight,
                        ExpandCanvas = false,
                        Parameters = new() { ["Angle"] = (float)deg, ["ExpandCanvas"] = false },
                        Id = InternalRotationID
                    };
                    break;
                default:
                    return;
            }
            handler?.Invoke(s, e);
        };
        return panel.BuildWithScrollView();
    }

    private View BuildVectorSourcePropertiesTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> changed)
    {
        var layout = new VerticalStackLayout { Spacing = 8, Padding = 8 };
        var components = VectorComponentSerializer.Read(clip.ExtraData);
        var properties = new VerticalStackLayout { Spacing = 8 };
        var selector = new Picker { Title = Localized.VectorContentEditorView_Components, ItemsSource = components.Select(c => c.Name).ToList() };
        bool timedGroup = clip.ExtraData.ContainsKey(VectorComponentClip.ChildrenKey);
        void Save(bool bounds = false)
        {
            VectorClipServices.SetDefinition(page, clip, components, bounds && clip.ClipType == ClipMode.VectorComponentClip);
            changed.Invoke(this, new PropertyPanelPropertyChangedEventArgs("vectorComponent", null, null));
        }
        void ShowComponent()
        {
            properties.Children.Clear();
            if (selector.SelectedIndex < 0 || selector.SelectedIndex >= components.Count) return;
            var component = components[selector.SelectedIndex];
            var name = new Entry { Text = component.Name, Placeholder = Localized.VectorContentEditorView_Name };
            name.Unfocused += (_, _) => { component.Name = name.Text; Save(); };
            if (components.Count > 1) properties.Children.Add(name);
            var handler = VectorClipServices.GetHandler(component);
            if (component is ComponentGroup group)
            {
                var builder = new PropertyPanelBuilder();
                foreach (string field in timedGroup ? new[] { "Rotation" } : new[] { "Width", "Height", "Rotation" })
                    builder.AddEntry(field, field, group.Parameters.GetFloat(field, 0).ToString(CultureInfo.InvariantCulture), "");
                builder.PropertyChanged += (_, e) =>
                {
                    if (float.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    { group.Parameters[e.Id] = value; Save(true); }
                };
                properties.Children.Add(builder.Build());
                if (clip.ClipType == ClipMode.VectorCanvasClip)
                {
                    void AddChildren(ComponentGroup parent)
                    {
                        foreach (var child in parent.Children)
                        {
                            properties.Children.Add(new Label { Text = child.Name });
                            if (child is ComponentGroup nested) AddChildren(nested);
                            else if (VectorClipServices.GetHandler(child) is { } childHandler)
                            {
                                var childBuilder = childHandler.CreatePropertyUI(child);
                                childBuilder.PropertyChanged += (_, e) => { childHandler.HandlePropertyChange(child, e); Save(); };
                                properties.Children.Add(childBuilder.Build());
                            }
                        }
                    }
                    AddChildren(group);
                }
            }
            else if (handler is not null)
            {
                var builder = handler.CreatePropertyUI(component);
                builder.PropertyChanged += (_, e) => { handler.HandlePropertyChange(component, e); Save(true); };
                properties.Children.Add(builder.Build());
            }
        }
        if (components.Count > 0) selector.SelectedIndex = 0;

        if (components.Count > 1)
        {
            selector.SelectedIndexChanged += (_, _) => ShowComponent();
            layout.Children.Add(selector);
        }
        ShowComponent();
        layout.Children.Add(properties);
        if (clip.ClipType == ClipMode.VectorCanvasClip)
        {
            var add = new Button { Text = Localized.VectorContentEditorView_AddShape };
            add.Clicked += async (_, _) =>
            {
                var component = await VectorClipServices.PickComponent(page);
                if (component is null) return;
                components.Add(component);
                selector.ItemsSource = components.Select(c => c.Name).ToList();
                selector.SelectedIndex = components.Count - 1;
                Save();
            };
            var remove = new Button { Text = Localized._Remove };
            remove.Clicked += (_, _) =>
            {
                if (selector.SelectedIndex < 0) return;
                components.RemoveAt(selector.SelectedIndex);
                selector.ItemsSource = components.Select(c => c.Name).ToList();
                selector.SelectedIndex = components.Count > 0 ? 0 : -1;
                ShowComponent(); Save();
            };
            var import = new Button { Text = Localized.VectorContentEditorView_Import };
            import.Clicked += async (_, _) =>
            {
                try
                {
                    var path = await VectorClipServices.PickFile();
                    if (path is null) return;
                    components.Add(VectorClipServices.Import(path));
                    selector.ItemsSource = components.Select(c => c.Name).ToList();
                    selector.SelectedIndex = components.Count - 1;
                    Save();
                }
                catch (Exception ex) { Log(ex, "Import vector canvas component", this); page.SetStateFail(ex.Message); }
            };
            var merge = new Button { Text = Localized.VectorContentEditorView_History_Group };
            merge.Clicked += (_, _) =>
            {
                if (components.Count < 2) return;
                var group = new ComponentGroup { Name = Localized.VectorContentEditorView_Components_GroupProperties };
                group.SetChildren(components.OrderBy(c => c.Index));
                components.Clear(); components.Add(group);
                selector.ItemsSource = components.Select(c => c.Name).ToList();
                selector.SelectedIndex = 0; ShowComponent(); Save();
            };
            layout.Children.Add(import);
            layout.Children.Add(merge);
            layout.Children.Add(add);
            layout.Children.Add(remove);
        }

        if (components.Count > 1)
        {
            var exportJson = new Button { Text = Localized.VectorContentEditorView_ExportJson };
            exportJson.Clicked += async (_, _) => await ExportVectorClip(clip, false);
            var exportSvg = new Button { Text = Localized.VectorContentEditorView_ExportSVG };
            exportSvg.Clicked += async (_, _) => await ExportVectorClip(clip, true);
            layout.Children.Add(exportJson);
            layout.Children.Add(exportSvg);
        }

        layout.Children.Add(BuildVectorAntiAliasProperties(clip, changed, true).Build());
        return new ScrollView { Content = layout };
    }

    private async Task ExportVectorClip(ClipElementUI clip, bool svg)
    {
        try
        {
            string content;
            if (svg)
            {
                using var instance = projectFrameCut.Render.Plugin.PluginManager.CreateClip(JsonSerializer.SerializeToElement(DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip))) as VectorCanvasClip;
                if (instance is null) return;
                instance.ReInit(8);
                projectFrameCut.Render.Effect.EffectHelper.ResolveClipEffects(instance);
                uint frame = ((IClip)instance).TryGetRelativeFrameIndex((uint)Math.Max(0, page.CurrentFrame), null) ?? clip.relativeStartFrame;
                var source = instance.GetVectorPictureRelativeToStartPointOfSource(frame, clip.TargetWidth, clip.TargetHeight);
                var picture = clip.ClipType == ClipMode.VectorComponentClip ? new VectorPicture
                {
                    Elements = source.Elements.Select(e => (VectorCanvasElement)new VectorViewportElement(e,
                        clip.TargetWidth, clip.TargetHeight, -clip.TargetX, -clip.TargetY,
                        page.ProjectInfo.RelativeWidth, page.ProjectInfo.RelativeHeight)).ToList()
                } : source;
                content = SVGToVectorElement.ExportToSvg(picture, page.ProjectInfo.RelativeWidth, page.ProjectInfo.RelativeHeight);
            }
            else content = clip.ExtraData.ContainsKey(VectorComponentClip.ChildrenKey)
                ? JsonSerializer.Serialize(DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip))
                : VectorComponentSerializer.Serialize(VectorComponentSerializer.Read(clip.ExtraData));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            string name = string.Concat(clip.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var result = await FileSaver.Default.SaveAsync(name + (svg ? ".svg" : ".json"), stream);
            if (!result.IsSuccessful && result.Exception is not null) throw result.Exception;
        }
        catch (Exception ex) { Log(ex, $"Export vector clip {clip.Id}", this); page.SetStateFail(ex.Message); }
    }

    private PropertyPanelBuilder BuildVectorAntiAliasProperties(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler, bool preappendSperator)
    {
        string currentLabel = ReadStringValue(clip.ExtraData?.GetValueOrDefault("VectorAntiAliasMode"), "") switch
        {
            "None" => PPLocalizedResources.General_VectorClip_AAMode_None,
            "SSAA2x" => "SSAA 2x",
            "SSAA4x" => "SSAA 4x",
            "SSAA8x" => "SSAA 8x",
            _ => PPLocalizedResources.General_VectorClip_AAMode_Default
        };
        var panel = new PropertyPanelBuilder()
            .AppendWhen(preappendSperator, c => c.AddSeparator())
            .AddText(new SingleLineLabel(PPLocalizedResources.General_VectorClip, 20))
            .AddPicker("vectorAntiAliasMode", PPLocalizedResources.General_VectorClip_AAMode,
                [PPLocalizedResources.General_VectorClip_AAMode_Default, PPLocalizedResources.General_VectorClip_AAMode_None,
                    "SSAA 2x", "SSAA 4x", "SSAA 8x"], currentLabel);
        panel.PropertyChanged += (s, e) =>
        {
            clip.ExtraData ??= new();
            clip.ExtraData["VectorAntiAliasMode"] = e.Value?.ToString() switch
            {
                var t when t == PPLocalizedResources.General_VectorClip_AAMode_None => "None",
                "SSAA 2x" => "SSAA2x",
                "SSAA 4x" => "SSAA4x",
                "SSAA 8x" => "SSAA8x",
                _ => ""
            };
            handler?.Invoke(s, e);
        };
        return panel;
    }

    internal static ClipArgumentProvider EnsureClipArgumentProvider(ClipElementUI clip, DraftPage? page)
    {
        clip.EffectProviders ??= new();
        var provider = clip.EffectProviders.Values.OfType<ClipArgumentProvider>().SingleOrDefault();
        if (provider is null)
        {
            provider = new ClipArgumentProvider();
            clip.EffectProviders.Add(provider.Id, provider);
            Log($"Created clip argument node for {clip.Id}.");
        }
        IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> fields = ClipArgumentHandler.DefaultFields;
        var defaults = new Dictionary<string, object>();
        if (page is not null)
        {
            try
            {
                var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, rebuildEffects: false);
                using var owner = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto));
                fields = owner.ArgumentFields;
                foreach (var (key, field) in fields) defaults[key] = field.ReadValue(owner);
            }
            catch (Exception ex) { Log(ex, $"Read argument fields for clip {clip.Id}", nameof(ClipInfoBuilder)); }
        }
        provider.Attach(fields, key => key switch
        {
            nameof(IClip.TargetX) => clip.TargetX,
            nameof(IClip.TargetY) => clip.TargetY,
            nameof(IClip.TargetWidth) => clip.TargetWidth,
            nameof(IClip.TargetHeight) => clip.TargetHeight,
            nameof(IClip.Rotation) => clip.Rotation,
            ClipArgumentHandler.ShouldRenderKey => true,
            _ => key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)
                && TextClipStyles.Restore(clip.ExtraData) is { } style
                ? TextClipStyles.ReadValue(style, key[TextClipStyles.ArgumentPrefix.Length..])
                : defaults.GetValueOrDefault(key) ?? fields[key].DefaultValue,
        });
        if (clip.ClipType == ClipMode.TextClip && TextClipStyles.Restore(clip.ExtraData) is { } textStyle)
            SyncTextStyleClipArguments(clip, textStyle, updateValues: false);
        return provider;
    }

    internal static void SyncTextStyleClipArguments(ClipElementUI clip, ITextClipStyleProvider style, bool updateValues = true)
    {
        var provider = clip.EffectProviders?.Values.OfType<ClipArgumentProvider>().SingleOrDefault();
        if (provider is null) return;
        provider.MetaData[TextClipStyles.FromKey] = style.FromPlugin;
        provider.MetaData[TextClipStyles.TypeKey] = style.TypeName;
        var descriptors = style.SettableFields;
        var previous = provider.InFields;
        bool Compatible(string key) => descriptors.TryGetValue(key[TextClipStyles.ArgumentPrefix.Length..], out var field)
            && !field.FieldType.HasFlag(EffectArgumentFieldType.InternalUseOnly)
            && (!previous.TryGetValue(key, out var old) || old.FieldType.BaseType() == field.FieldType.BaseType());
        provider.RestoreStaticValues(provider.StaticValues
            .Where(p => !p.Key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal) || Compatible(p.Key))
            .ToDictionary(p => p.Key, p => updateValues && p.Key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)
                ? TextClipStyles.ReadValue(style, p.Key[TextClipStyles.ArgumentPrefix.Length..]) : p.Value));
        provider.AnchorsBindingState = provider.AnchorsBindingState
            .Where(p => !p.Key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)
                || Compatible(p.Key) && !descriptors[p.Key[TextClipStyles.ArgumentPrefix.Length..]].FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
            .ToDictionary();
        var fields = new Dictionary<string, ClipArgumentFieldDescriptor>(ClipArgumentHandler.DefaultFields);
        foreach (var (key, descriptor) in descriptors)
        {
            if (descriptor.FieldType.HasFlag(EffectArgumentFieldType.InternalUseOnly)) continue;
            fields[TextClipStyles.ArgumentPrefix + key] = new()
            {
                Id = TextClipStyles.ArgumentPrefix + key, FieldType = descriptor.FieldType,
                DefaultValue = descriptor.DefaultValue, MinValue = descriptor.MinValue, MaxValue = descriptor.MaxValue,
                PresetOptions = descriptor.PresetOptions, Remarks = descriptor.Remarks,
                ReadValue = _ => TextClipStyles.ReadValue(style, key),
            };
        }
        provider.Attach(fields, key => key switch
        {
            nameof(IClip.TargetX) => clip.TargetX,
            nameof(IClip.TargetY) => clip.TargetY,
            nameof(IClip.TargetWidth) => clip.TargetWidth,
            nameof(IClip.TargetHeight) => clip.TargetHeight,
            nameof(IClip.Rotation) => clip.Rotation,
            ClipArgumentHandler.ShouldRenderKey => true,
            _ => TextClipStyles.ReadValue(TextClipStyles.Restore(clip.ExtraData) ?? style,
                key[TextClipStyles.ArgumentPrefix.Length..]),
        });
    }

    internal static void SaveTextStyleClipArguments(ClipElementUI clip)
    {
        if (clip.ClipType != ClipMode.TextClip || TextClipStyles.Restore(clip.ExtraData) is not { } style) return;
        var provider = clip.EffectProviders?.Values.OfType<ClipArgumentProvider>().SingleOrDefault();
        if (provider is null) return;
        var values = new Dictionary<string, object>(provider.StaticValues);
        foreach (var (key, value) in provider.StaticValues)
        {
            if (!key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)) continue;
            string id = key[TextClipStyles.ArgumentPrefix.Length..];
            if (!style.SettableFields.TryGetValue(id, out var descriptor)) continue;
            if (!style.HandleSettableFieldsChange(descriptor, value, out var feedback))
                Log($"Text style field '{id}': {feedback}", "warn");
            values[key] = TextClipStyles.ReadValue(style, id);
        }
        provider.RestoreStaticValues(values);
        TextClipStyles.Save(style, clip.ExtraData);
        clip.ExtraData["TextEntries"] = style.BuildEntries().ToList();
    }

    private View BuildMaskSourcePropertiesTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> changed)
    {
        var provider = EnsureClipArgumentProvider(clip, page);
        var panel = new PropertyPanelBuilder();
        var host = new ClipBindingHost(clip, provider, page, () =>
        {
            RebuildAllEffects(clip);
            changed?.Invoke(this, new("__REFRESH_PANEL__", null, null));
        });
        EffectProviderUIHelper.BuildUI(provider, panel, host,
            provider.InFields.Keys.Where(k => k.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)));
        panel.PropertyChanged += (_, e) =>
        {
            var update = EffectProviderUIHelper.HandleChange(provider, e);
            if (update.newFields is not null) provider.Fields = update.newFields;
            RebuildAllEffects(clip);
            changed?.Invoke(this, e);
        };
        return panel.BuildWithScrollView();
    }
}
