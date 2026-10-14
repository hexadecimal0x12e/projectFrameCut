using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using GridLength = Microsoft.Maui.GridLength;
using Switch = Microsoft.Maui.Controls.Switch;
using Thickness = Microsoft.Maui.Thickness;

namespace projectFrameCut.DraftStuff;

public partial class ClipInfoBuilder
{
    internal const string ContinuousEffectRangePropertyPrefix = "ContinuousEffectRange|";

    internal static void AddContinuousEffectRangeUI(PropertyPanelBuilder panel, IEffectProvider provider, ClipElementUI clip, DraftPage page)
    {
        var fields = provider.Fields;
        if (!fields.ContainsKey(nameof(IContinuousEffect.IsScoped))
            || !fields.ContainsKey(nameof(IContinuousEffect.StartPoint))
            || !fields.ContainsKey(nameof(IContinuousEffect.EndPoint))) return;
        var range = page.GetClipFrameRange(clip);
        if (range.Duration == 0 || range.Start >= int.MaxValue) return;
        int min = (int)range.Start;
        int max = (int)Math.Min((ulong)range.Start + range.Duration, (ulong)int.MaxValue);
        var saved = ReadScope();
        bool updating = false;
        var stack = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(8) };
        var scopeSwitch = new Switch { IsToggled = saved.Scoped, VerticalOptions = LayoutOptions.Center };
        var row = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8
        };
        row.Add(new Label { Text = Localized.EffectScope_Enable, VerticalOptions = LayoutOptions.Center }, 0);
        row.Add(scopeSwitch, 1);
        stack.Children.Add(row);

        var editors = new VerticalStackLayout { Spacing = 8, IsEnabled = saved.Scoped };
        var startEntry = new Entry { Keyboard = Keyboard.Numeric };
        var endEntry = new Entry { Keyboard = Keyboard.Numeric };
        editors.Children.Add(new Label { Text = Localized.EffectScope_StartFrame, FontSize = 13 });
        editors.Children.Add(startEntry);
        editors.Children.Add(new Label { Text = Localized.EffectScope_EndFrame, FontSize = 13 });
        editors.Children.Add(endEntry);

        string? thumbnail = clip.SourcePath;
        if (thumbnail?.StartsWith("$") == true)
            thumbnail = page.Assets.TryGetValue(thumbnail[1..], out var asset) ? asset.ThumbnailPath : null;
        var slider = new ClipRangeSlider
        {
            Maximum = max - min,
            ThumbnailPath = thumbnail,
            Margin = new Thickness(10, 20, 10, 20)
        };
        editors.Children.Add(slider);
        var infoLabel = new Label { FontSize = 12, TextColor = Colors.Gray };
        var errorLabel = new Label { Text = Localized.EffectScope_InvalidRange, FontSize = 12, TextColor = Colors.Orange, IsVisible = false };
        var applyButton = new Button { Text = Localized._Apply, HorizontalOptions = LayoutOptions.End };
        editors.Children.Add(infoLabel);
        editors.Children.Add(errorLabel);
        editors.Children.Add(applyButton);
        stack.Children.Add(editors);

        bool TryReadRange(out int start, out int end)
        {
            end = 0;
            return int.TryParse(startEntry.Text, out start) && int.TryParse(endEntry.Text, out end)
                && start >= min && start < end && end <= max;
        }

        void UpdatePreview()
        {
            if (updating) return;
            bool valid = TryReadRange(out int start, out int end);
            applyButton.IsEnabled = valid;
            errorLabel.IsVisible = !valid;
            infoLabel.Text = valid && page.ProjectInfo.TargetFrameRate > 0
                ? PPLocalizedResources.Timing_LengthInfo_Start((uint)start, (uint)(end - start), page.ProjectInfo.TargetFrameRate)
                : string.Empty;
            if (valid)
            {
                slider.LowerValue = start - min;
                slider.UpperValue = end - min;
            }
        }

        (bool Scoped, int Start, int End) ReadScope()
        {
            var values = provider.Fields;
            EffectParamConvert.TryConvertToBool(values[nameof(IContinuousEffect.IsScoped)].GetGetter()(), out bool scoped);
            EffectParamConvert.TryConvertToInt(values[nameof(IContinuousEffect.StartPoint)].GetGetter()(), out int start);
            EffectParamConvert.TryConvertToInt(values[nameof(IContinuousEffect.EndPoint)].GetGetter()(), out int end);
            return (scoped, start, end);
        }

        void ApplyRange(bool scoped)
        {
            var old = ReadScope();
            int start = old.Start;
            int end = old.End;
            if (scoped && !TryReadRange(out start, out end)) return;
            if (old == (scoped, start, end)) return;
            var values = provider.Fields;
            SetValue(nameof(IContinuousEffect.IsScoped), scoped);
            SetValue(nameof(IContinuousEffect.StartPoint), start);
            SetValue(nameof(IContinuousEffect.EndPoint), end);
            provider.Fields = values;
            Log($"Updated continuous effect {provider.TypeName} ({provider.Id}) range on clip {clip.Id}: {old} -> {(scoped, start, end)}.");
            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(panel, ContinuousEffectRangePropertyPrefix + provider.Id, (scoped, start, end));

            void SetValue(string id, object value)
            {
                if (values[id] is StaticEffectArgumentField field) field.Value = value;
                else values[id] = new StaticEffectArgumentField { Id = id, FieldType = values[id].FieldType, Value = value };
            }
        }

        void SyncFields(int start, int end)
        {
            updating = true;
            start = Math.Clamp(start, min, max - 1);
            startEntry.Text = start.ToString();
            endEntry.Text = (end > start ? Math.Min(end, max) : max).ToString();
            updating = false;
            UpdatePreview();
        }

        startEntry.TextChanged += (_, _) => UpdatePreview();
        endEntry.TextChanged += (_, _) => UpdatePreview();
        startEntry.Completed += (_, _) => ApplyRange(scopeSwitch.IsToggled);
        endEntry.Completed += (_, _) => ApplyRange(scopeSwitch.IsToggled);
        applyButton.Clicked += (_, _) => ApplyRange(scopeSwitch.IsToggled);
        slider.ValuesChanged += (_, _) =>
        {
            updating = true;
            int start = min + (int)Math.Round(slider.LowerValue);
            startEntry.Text = start.ToString();
            endEntry.Text = Math.Clamp(min + (int)Math.Round(slider.UpperValue), start + 1, max).ToString();
            updating = false;
            UpdatePreview();
        };
        slider.DragCompleted += (_, _) => ApplyRange(scopeSwitch.IsToggled);
        scopeSwitch.Toggled += (_, e) =>
        {
            if (!e.Value)
            {
                var old = ReadScope();
                SyncFields(old.Start, old.End);
            }
            editors.IsEnabled = e.Value;
            ApplyRange(e.Value);
        };

        SyncFields(saved.Start, saved.End);
        panel.AddCustomChildWithID(ContinuousEffectRangePropertyPrefix + provider.Id, stack);
        panel.Properties[ContinuousEffectRangePropertyPrefix + provider.Id] = saved;
    }
}
