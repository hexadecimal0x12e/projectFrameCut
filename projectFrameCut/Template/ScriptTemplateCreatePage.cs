#if !HEADLESS
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;

namespace projectFrameCut.Template;

public sealed class ScriptTemplateCreatePage : ContentPage
{
    private readonly Entry name = new() { Placeholder = Localized.ScriptTemplateCreatePage_NamePlaceholder };
    private readonly Editor readme = new() { Placeholder = Localized.ScriptTemplateCreatePage_ReadmePlaceholder, MinimumHeightRequest = 80 };
    private readonly ScriptTemplateEditor script = new();
    private readonly VerticalStackLayout variables = new() { Spacing = 8 };
    private readonly List<(Entry Name, Entry Value, Picker Type)> rows = [];
    private bool busy;

    public ScriptTemplateCreatePage()
    {
        Title = Localized.ScriptTemplateCreatePage_Title;
        var add = new Button { Text = Localized.ScriptTemplateCreatePage_AddVariable };
        add.Clicked += (_, _) => AddVariable();
        var save = new Button { Text = Localized.ScriptTemplateCreatePage_Save };
        save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16, Spacing = 12,
                Children = { name, readme, script, new Label { Text = Localized.ScriptTemplateCreatePage_VariableSection }, variables, add, save }
            }
        };
    }

    private void AddVariable()
    {
        var key = new Entry { Placeholder = Localized.ScriptTemplateCreatePage_VariableNamePlaceholder };
        var value = new Entry { Placeholder = Localized.ScriptTemplateCreatePage_DefaultValuePlaceholder };
        var type = new Picker { ItemsSource = Enum.GetNames<TemplateVariableType>(), SelectedIndex = (int)TemplateVariableType.String };
        var remove = new Button { Text = Localized.ScriptTemplateCreatePage_Delete };
        var row = new VerticalStackLayout { Spacing = 4, Children = { key, type, value, remove } };
        rows.Add((key, value, type));
        remove.Clicked += (_, _) => { rows.Remove((key, value, type)); variables.Children.Remove(row); };
        variables.Children.Add(row);
    }

    private async Task SaveAsync()
    {
        if (busy) return;
        string? package = null;
        try
        {
            busy = true;
            IsEnabled = false;
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException(Localized.TemplateExtractPage_InputName);
            if (string.IsNullOrWhiteSpace(script.Script)) throw new InvalidDataException(Localized.ScriptTemplateCreatePage_NoScript);
            var template = new ScriptBasedTemplateStructure
            {
                TemplateName = name.Text.Trim(), TemplateVersion = 2, Scope = TemplateScope.Any,
                CreatedInAPIVersion = IPluginBase.CurrentPluginAPIVersion, Readme = readme.Text,
                Variables = new(StringComparer.OrdinalIgnoreCase), VariableDefinitions = new(StringComparer.OrdinalIgnoreCase)
            };
            foreach (var row in rows)
            {
                var key = row.Name.Text?.Trim().Trim('{', '}').Trim();
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException(Localized.TemplateExtractPage_VarNameNotNull);
                if (template.Variables.ContainsKey(key)) throw new InvalidDataException(Localized.TemplateExtractPage_VarNameNotSame);
                var type = (TemplateVariableType)row.Type.SelectedIndex;
                if (!string.IsNullOrEmpty(row.Value.Text)) TemplateCreatePage.ConvertResolvedValue(row.Value.Text, type);
                template.Variables.Add(key, row.Value.Text);
                template.VariableDefinitions.Add(key, new TemplateVariableDefinition { Type = type, DefaultValue = row.Value.Text });
            }
            package = await TemplatePackageIO.BuildTemplatePackageAsync(template, [], new TemplateMetadataStructure
            {
                SourceTemplateID = template.TemplateID, TemplateName = template.TemplateName,
                Readme = readme.Text ?? "", CreatedAt = DateTime.Now
            }, MauiProgram.DataPath, DraftPage.DraftJSONOption, script.Script);
            using var stream = File.OpenRead(package);
            var path = await FileSystemService.SaveAFile($"{template.TemplateID:N}.pjfcTemplate", stream);
            if (string.IsNullOrWhiteSpace(path)) return;
            var imported = await TemplatePackageIO.ImportPjfcTemplateAsync(package, DraftPage.DraftJSONOption);
            TemplateStore.Templates[imported.TemplateID] = imported;
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "Save script template", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
        finally
        {
            busy = false;
            IsEnabled = true;
            if (package is not null)
            {
                try { File.Delete(package); }
                catch (Exception ex) { Log(ex, "Clean script template package", this); }
            }
        }
    }
}
#endif
