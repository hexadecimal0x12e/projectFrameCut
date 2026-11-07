#if !HEADLESS
using projectFrameCut.Services;

namespace projectFrameCut.Template;

public sealed class ScriptTemplateEditor : VerticalStackLayout
{
    private readonly Editor script = new()
    {
        AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 240,
        Placeholder = "$title = $TemplateParameters['Title']\nGet-ProjectInfo"
    };

    public string Script => script.Text ?? "";

    public ScriptTemplateEditor()
    {
        Spacing = 8;
        Children.Add(new Label { Text = Localized.ScriptTemplateCreatePage_ScriptPreview, FontAttributes = FontAttributes.Bold });
        Children.Add(new Label { Text = Localized.ScriptTemplate_ParameterHint, FontSize = 12 });
        var select = new Button { Text = Localized.ScriptTemplateCreatePage_SelectScript, HorizontalOptions = LayoutOptions.Start };
        select.Clicked += async (_, _) =>
        {
            try
            {
                var file = await FilePicker.Default.PickAsync(new PickOptions
                {
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    { [DevicePlatform.WinUI] = [".ps1"], [DevicePlatform.Android] = ["text/plain"], [DevicePlatform.MacCatalyst] = ["public.plain-text"] })
                });
                if (file is null) return;
                using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                script.Text = await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                Log(ex, "Read template script", this);
                Element? parent = Parent;
                while (parent is not null && parent is not Page) parent = parent.Parent;
                if (parent is Page page) await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            }
        };
        Children.Add(select);
        Children.Add(script);
    }
}
#endif
