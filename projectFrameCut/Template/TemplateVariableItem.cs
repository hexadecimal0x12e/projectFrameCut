using System.ComponentModel;
using System.Runtime.CompilerServices;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.Template;

public class TemplateVariableItem : INotifyPropertyChanged
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
