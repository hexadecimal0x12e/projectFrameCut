using projectFrameCut.Asset;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace projectFrameCut.Template;

public partial class ScriptTemplateCreatePage : ContentPage
{
    private ScriptBasedTemplateStructure _template = new();
    private readonly ObservableCollection<ScriptVariableItem> _variables = [];
    private bool _isBusy;
    private string _scriptContent = "";
    private readonly ObservableCollection<string> _tags = [];

    public ScriptTemplateCreatePage()
    {
        InitializeComponent();
        VariablesCollectionView.ItemsSource = _variables;
        TagsContainer.BindingContext = _tags;
    }

    private async void OnSelectScriptClicked(object? sender, EventArgs e)
    {
        if (_isBusy)
            return;

        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = new[] { ".ps1" },
                    [DevicePlatform.Android] = new[] { "application/ps1", "text/plain" }
                })
            });

            if (result is null || string.IsNullOrWhiteSpace(result.FullPath))
                return;

            var scriptContent = await File.ReadAllTextAsync(result.FullPath);
            _scriptContent = scriptContent;
            ScriptFileLabel.Text = result.FileName;
            ScriptEditor.Text = scriptContent;

            ValidateForm();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private void ValidateForm()
    {
        var hasScript = !string.IsNullOrWhiteSpace(_scriptContent);
        var hasName = !string.IsNullOrWhiteSpace(TemplateNameEntry.Text?.Trim());
        SaveButton.IsEnabled = hasScript && hasName && !_isBusy;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_isBusy)
            return;

        var templateName = TemplateNameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(templateName))
        {
            await DisplayAlertAsync(Localized._Error, HomePage.GetInvalidFileNameWarn(), Localized._OK);
            return;
        }

        if (string.IsNullOrWhiteSpace(_scriptContent))
        {
            await DisplayAlertAsync(Localized._Error, Localized.ScriptTemplateCreatePage_NoScript, Localized._OK);
            return;
        }

        try
        {
            _isBusy = true;
            SaveButton.IsEnabled = false;
            StatusLabel.Text = Localized._Processing;

            // ---- 从交互式列表构建变量定义 ----
            var variableDefinitions = new Dictionary<string, TemplateVariableDefinition>();
            var variableValues = new Dictionary<string, string?>();

            foreach (var item in _variables)
            {
                var key = item.VariableName?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                variableDefinitions[key] = new TemplateVariableDefinition
                {
                    Type = item.Type,
                    DefaultValue = item.DefaultValue,
                    UserFriendlyName = item.DisplayName,
                    Description = $"Script variable: {key}"
                };

                variableValues[key] = item.DefaultValue;
            }

            _template = new ScriptBasedTemplateStructure
            {
                TemplateName = templateName,
                Scope = TemplateScope.Any,
                Variables = variableValues,
                VariableDefinitions = variableDefinitions,
                Project = _template.Project,
                Draft = _template.Draft,
                CreatedInAPIVersion = IPluginBase.CurrentPluginAPIVersion
            };

            // ---- 构建元数据 ----
            var metadata = new TemplateMetadataStructure
            {
                SourceTemplateID = _template.TemplateID,
                Scope = _template.Scope,
                TemplateName = _template.TemplateName,
                Subtitle = SubtitleEntry.Text?.Trim() ?? "",
                CreatedAt = DateTime.UtcNow,
                Revision = 1,
                Tags = [.._tags],
                Readme = ReadmeEditor.Text?.Trim()
            };

            // ---- 打包模板 ----
            var packageZipPath = await TemplatePackageIO.BuildTemplatePackageAsync(
                _template,
                Array.Empty<AssetItem>(),
                metadata,
                string.Empty,
                JsonSerializerOptions.Default,
                scriptContent: _scriptContent);

            // ---- 保存文件 ----
            await using var packageStream = File.OpenRead(packageZipPath);
            var safeName = SanitizeFileName(templateName);
            var savePath = await FileSystemService.SaveAFile(
                $"{safeName}_{_template.TemplateID:N}.pjfcTemplate",
                packageStream);

            try { File.Delete(packageZipPath); } catch { }

            await FileSystemService.ShowFileInFolderAsync(savePath);
            await DisplayAlertAsync(Localized._Info, SettingsManager.SettingLocalizedResources.Advanced_Success, Localized._OK);
            await Navigation.PopAsync();

        }
        catch (Exception ex)
        {
            StatusLabel.Text = Localized._ExceptionTemplate(ex);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            _isBusy = true;
        }
        finally
        {
            ValidateForm();
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (_isBusy)
            return;

        await Navigation.PopAsync();
    }

    private void OnAddVariableClicked(object? sender, EventArgs e)
    {
        // 查找一个不重复的默认变量名
        var baseName = "variable";
        var index = 1;
        while (_variables.Any(v => string.Equals(v.VariableName, baseName + index, StringComparison.OrdinalIgnoreCase)))
            index++;

        _variables.Add(new ScriptVariableItem
        {
            VariableName = baseName + index,
            DisplayName = "Variable " + index,
            DefaultValue = "",
            SelectedType = "String"
        });

        ValidateForm();
    }

    private void OnDeleteVariableClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ScriptVariableItem item })
            return;

        _variables.Remove(item);
        ValidateForm();
    }

    private void AddTag_Clicked(object sender, EventArgs e)
    {
        AddCurrentTag();
    }

    private void TagInputEntry_Completed(object sender, EventArgs e)
    {
        AddCurrentTag();
    }

    private void RemoveTag_Clicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: string tag })
        {
            return;
        }

        _tags.Remove(tag);
    }

    private void AddCurrentTag()
    {
        var tagText = TagInputEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(tagText))
        {
            return;
        }

        if (!_tags.Contains(tagText, StringComparer.OrdinalIgnoreCase))
        {
            _tags.Add(tagText);
        }

        TagInputEntry.Text = string.Empty;
        TagInputEntry.Focus();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Select(c => invalid.Contains(c) ? '_' : c))
            .TrimEnd('.')
            .Truncate(100);
    }

    private async Task DisplayAlertAsync(string title, string message, string cancel)
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Page is Page page)
            await page.DisplayAlertAsync(title, message, cancel);
    }

    private void ScriptEditor_Focused(object sender, FocusEventArgs e)
    {
        ScriptEditor.MaximumHeightRequest = 500;
    }

    private void ScriptEditor_Unfocused(object sender, FocusEventArgs e)
    {
        ScriptEditor.MaximumHeightRequest = 120;
        _scriptContent = ScriptEditor.Text.Trim();
        ValidateForm();
    }

    private void TemplateNameEntry_Unfocused(object sender, FocusEventArgs e)
    {
        ValidateForm();
    }
}

/// <summary>
/// 变量定义列表中的单个变量项，支持数据绑定。
/// </summary>
public class ScriptVariableItem : INotifyPropertyChanged
{
    private string _variableName = "";
    private string _displayName = "";
    private string _defaultValue = "";
    private TemplateVariableType _type = TemplateVariableType.String;
    private string _selectedType = "String";

    /// <summary>
    /// 可选的变量类型列表（用于 Picker 数据源）。
    /// 实例属性以确保 MAUI 数据绑定能正确解析。
    /// </summary>
    public List<string> TypeOptions { get; } =
    [
        "String", "Number", "Integer", "Boolean", "File", "Json"
    ];

    public string VariableName
    {
        get => _variableName;
        set
        {
            if (_variableName != value)
            {
                _variableName = value;
                OnPropertyChanged();
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    public string DefaultValue
    {
        get => _defaultValue;
        set
        {
            if (_defaultValue != value)
            {
                _defaultValue = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 解析后的变量类型（由 <see cref="SelectedType"/> 驱动）。
    /// </summary>
    public TemplateVariableType Type => _type;

    /// <summary>
    /// Picker 绑定的字符串类型值。更改时会同步解析 <see cref="Type"/>。
    /// </summary>
    public string SelectedType
    {
        get => _selectedType;
        set
        {
            if (_selectedType != value)
            {
                _selectedType = value;
                Enum.TryParse<TemplateVariableType>(value, ignoreCase: true, out var parsed);
                _type = parsed;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 简单字符串扩展，用于文件名截断。
/// </summary>
file static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
