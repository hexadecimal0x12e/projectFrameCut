using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.IO;
using System.Text;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private int _mermaidImportVersion;
    private bool _mermaidImportBusy;

    private void OnImportMermaidClicked(object sender, EventArgs e)
    {
        if (_page is null || _clip is null || _closing) return;
        EndPortConnection();
        StaticInputTooltip.IsVisible = false;
        _mermaidImportVersion++;
        SetMermaidImportBusy(false);
        ClearMermaidRepair();
        MermaidImportErrors.Children.Clear();
        MermaidImportErrorsScroll.IsVisible = false;
        MermaidImportOverlay.IsVisible = true;
        MermaidImportEditor.Focus();
    }

    private void CloseMermaidImport()
    {
        _mermaidImportVersion++;
        MermaidImportOverlay.IsVisible = false;
        ClearMermaidRepair();
        SetMermaidImportBusy(false);
    }

    private void OnCancelMermaidClicked(object sender, EventArgs e) => CloseMermaidImport();

    private void SetMermaidImportBusy(bool busy)
    {
        _mermaidImportBusy = busy;
        MermaidImportToolbar.IsEnabled = MermaidImportEditor.IsEnabled = MermaidImportConfirmButton.IsEnabled = !busy;
        MermaidRepairPanel.IsEnabled = !busy;
        MermaidImportBusyIndicator.IsVisible = MermaidImportBusyIndicator.IsRunning = busy;
    }

    private async Task RunMermaidActionAsync(Func<int, Task> action)
    {
        if (_mermaidImportBusy || !MermaidImportOverlay.IsVisible) return;
        int version = _mermaidImportVersion;
        SetMermaidImportBusy(true);
        MermaidImportErrors.Children.Clear();
        MermaidImportErrorsScroll.IsVisible = false;
        try { await action(version); }
        catch (Exception ex)
        {
            Logger.Log(ex, "Import Mermaid effect graph", this);
            if (version == _mermaidImportVersion) ShowMermaidImportError(ex);
        }
        finally
        {
            if (version == _mermaidImportVersion) SetMermaidImportBusy(false);
        }
    }

    private async void OnBrowseMermaidClicked(object sender, EventArgs e) => await RunMermaidActionAsync(async version =>
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = Localized.Effect_ImportMermaid,
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".pjfcEBG", ".mmd"],
                [DevicePlatform.macOS] = ["pjfcEBG", "mmd"],
                [DevicePlatform.Android] = ["text/*", "application/octet-stream"],
                [DevicePlatform.iOS] = ["public.text", "public.data"],
            }),
        });
        if (file is null || version != _mermaidImportVersion) return;
        using var reader = new StreamReader(await file.OpenReadAsync(), new UTF8Encoding(false, true));
        var text = await reader.ReadToEndAsync();
        if (version == _mermaidImportVersion) MermaidImportEditor.Text = text;
    });

    private async void OnPasteMermaidClicked(object sender, EventArgs e) => await RunMermaidActionAsync(async version =>
    {
        var text = await Clipboard.Default.GetTextAsync() ?? "";
        if (version == _mermaidImportVersion) MermaidImportEditor.Text = text;
    });

    private async void OnConfirmMermaidClicked(object sender, EventArgs e) => await RunMermaidActionAsync(async version =>
    {
        var clip = _clip;
        var stage = _stage;
        var pipeline = _compositionPipeline;
        var text = MermaidImportEditor.Text ?? "";
        var template = _mermaidRepairTemplate ?? await Task.Run(() => EffectGroupMermaid.Parse(text, validateTemplate: false));
        if (version != _mermaidImportVersion || !MermaidImportOverlay.IsVisible) return;
        if (!ReferenceEquals(_clip, clip) || _stage != stage || _compositionPipeline != pipeline || _closing)
            throw new InvalidOperationException(Localized.Effect_IncompatiblePipeline);
        RefreshMermaidRepair(template);
        if (_mermaidRepairDiagnostics.Count != 0) return;
        AddGroupTemplate(template);
        CloseMermaidImport();
    });

    private async void OnPreviewMermaidClicked(object sender, EventArgs e) => await RunMermaidActionAsync(async version =>
    {
        var text = MermaidImportEditor.Text ?? "";
        var template = _mermaidRepairTemplate ?? await Task.Run(() => EffectGroupMermaid.Parse(text, validateTemplate: false));
        if (version == _mermaidImportVersion && !_closing) RefreshMermaidRepair(template);
    });

    private void OnMermaidTextChanged(object sender, TextChangedEventArgs e)
    {
        ClearMermaidRepair();
        MermaidImportErrors.Children.Clear();
        MermaidImportErrorsScroll.IsVisible = false;
    }

    private void ShowMermaidImportError(Exception ex)
    {
        MermaidImportErrorsScroll.IsVisible = true;
        if (ex is EffectGroupMermaid.ParseException parse)
        {
            var lines = (MermaidImportEditor.Text ?? "").Split('\n');
            int line = Math.Clamp(parse.LineNumber - 1, 0, lines.Length - 1);
            var location = new Button
            {
                Text = $"Mermaid · {line + 1}: {lines[line].Trim()}", TextColor = Colors.OrangeRed,
                FontFamily = "Font_CodeBlock", FontSize = 12, HorizontalOptions = LayoutOptions.Start,
            };
            location.Clicked += (_, _) =>
            {
                MermaidImportEditor.Focus();
                MermaidImportEditor.CursorPosition = lines.Take(line).Sum(s => s.Length + 1);
                MermaidImportEditor.SelectionLength = lines[line].TrimEnd('\r').Length;
            };
            MermaidImportErrors.Children.Add(location);
        }
        if (ex is EffectGroupValidationException validation)
        {
            foreach (var diagnostic in validation.Diagnostics)
                MermaidImportErrors.Children.Add(new VerticalStackLayout
                {
                    Spacing = 3,
                    Children =
                    {
                        MermaidDiagnosticLocation(validation, diagnostic),
                        new Label { Text = validation.Describe(diagnostic), TextColor = Colors.OrangeRed, FontSize = 12 },
                    },
                });
        }
        else MermaidImportErrors.Children.Add(new Label { Text = EffectProviderFailure.Describe(ex), TextColor = Colors.OrangeRed, FontSize = 12 });
    }
}
