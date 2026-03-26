using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Template;

public partial class TemplateCreatePage : ContentPage
{
	private readonly ObservableCollection<TemplateVariableItem> _allVariables = [];
	private readonly ObservableCollection<TemplateVariableItem> _filteredVariables = [];
	private JSONBasedTemplateStructure? _template;
	private JsonObject _projectNode = new();
	private JsonObject _draftNode = new();
	private readonly Dictionary<string, TemplateVariableDefinition> _variableDefinitions = new(StringComparer.OrdinalIgnoreCase);
	private bool _isBusy;

	public TemplateCreatePage()
	{
		InitializeComponent();
		VariablesCollectionView.ItemsSource = _filteredVariables;
		RefreshStats();
	}

	private async void ImportTemplate_Clicked(object sender, EventArgs e)
	{
		if (_isBusy)
		{
			return;
		}

		try
		{
			var fileResult = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "选择模板 JSON 文件",
				FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
				{
					{ DevicePlatform.WinUI, [".json"] },
					{ DevicePlatform.Android, ["application/json", "text/plain"] },
					{ DevicePlatform.iOS, ["public.json", "public.plain-text"] },
					{ DevicePlatform.MacCatalyst, ["public.json", "public.plain-text"] }
				})
			});

			if (fileResult is null || string.IsNullOrWhiteSpace(fileResult.FullPath))
			{
				return;
			}

			SetBusy(true);
			await LoadTemplateFromFileAsync(fileResult.FullPath);
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("错误", $"导入模板失败: {ex.Message}", "确定");
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async Task LoadTemplateFromFileAsync(string path)
	{
		var text = await File.ReadAllTextAsync(path);
		var template = JsonSerializer.Deserialize<JSONBasedTemplateStructure>(text, DraftPage.DraftJSONOption);
		if (template is null)
		{
			throw new InvalidOperationException("模板格式无效。");
		}

		_template = template;
		_projectNode = JsonSerializer.SerializeToNode(template.Project, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject();
		_draftNode = JsonSerializer.SerializeToNode(template.Draft, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject();

		_allVariables.Clear();
		_filteredVariables.Clear();
		_variableDefinitions.Clear();

		var map = new Dictionary<string, VariableAggregate>(StringComparer.OrdinalIgnoreCase);
		CollectPlaceholderFields(_projectNode, "project", "Project", map);
		CollectPlaceholderFields(_draftNode, "draft", "Draft", map);

		if (_template.VariableDefinitions is not null)
		{
			foreach (var kv in _template.VariableDefinitions)
			{
				var key = NormalizePlaceholderKey(kv.Key);
				if (string.IsNullOrWhiteSpace(key) || kv.Value is null)
				{
					continue;
				}

				_variableDefinitions[key] = kv.Value;
				if (!map.TryGetValue(key, out var aggregate))
				{
					aggregate = new VariableAggregate
					{
						Key = key,
						Type = kv.Value.Type,
						DefaultValue = kv.Value.DefaultValue
					};
					map[key] = aggregate;
				}
				else
				{
					aggregate.Type = kv.Value.Type;
					aggregate.DefaultValue ??= kv.Value.DefaultValue;
				}
			}
		}

		if (_template.Variables is not null)
		{
			foreach (var kv in _template.Variables)
			{
				var key = NormalizePlaceholderKey(kv.Key);
				if (string.IsNullOrWhiteSpace(key))
				{
					continue;
				}

				if (!map.TryGetValue(key, out var aggregate))
				{
					aggregate = new VariableAggregate
					{
						Key = key,
						DefaultValue = kv.Value,
						Type = TemplateVariableType.Auto
					};
					map[key] = aggregate;
				}
				else if (aggregate.DefaultValue is null)
				{
					aggregate.DefaultValue = kv.Value;
				}

				if (!_variableDefinitions.ContainsKey(key))
				{
					_variableDefinitions[key] = new TemplateVariableDefinition
					{
						Type = aggregate.Type,
						DefaultValue = aggregate.DefaultValue
					};
				}
			}
		}

		foreach (var item in map.Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
		{
			var firstPath = item.Paths.FirstOrDefault() ?? "(模板变量)";
			if (!_variableDefinitions.TryGetValue(item.Key, out var def))
			{
				def = new TemplateVariableDefinition
				{
					Type = item.Type,
					DefaultValue = item.DefaultValue
				};
				_variableDefinitions[item.Key] = def;
			}

			var variable = new TemplateVariableItem(
				item.Key,
				item.Scope,
				firstPath,
				string.Join("; ", item.Paths.Take(3)),
				def.Type,
				item.DefaultValue,
				item.DefaultValue ?? string.Empty);
			_allVariables.Add(variable);
		}

		ApplyFilter();
		TemplatePathLabel.Text = $"模板文件：{path}";

		var projectName = _template.Project.ProjectName;
		if (string.IsNullOrWhiteSpace(projectName))
		{
			projectName = "Template Project";
		}
		ProjectNameEntry.Text = BuildDefaultProjectName(projectName);

		RefreshStats();
	}

	private static string BuildDefaultProjectName(string baseName)
	{
		var now = DateTime.Now;
		return $"{baseName}-{now:MMdd-HHmm}";
	}

	private static void CollectPlaceholderFields(JsonNode? node, string path, string scope, IDictionary<string, VariableAggregate> variables)
	{
		if (node is null)
		{
			return;
		}

		if (node is JsonObject obj)
		{
			foreach (var kv in obj)
			{
				if (kv.Value is null)
				{
					continue;
				}

				CollectPlaceholderFields(kv.Value, $"{path}.{kv.Key}", scope, variables);
			}
			return;
		}

		if (node is JsonArray arr)
		{
			for (int i = 0; i < arr.Count; i++)
			{
				if (arr[i] is null)
				{
					continue;
				}

				CollectPlaceholderFields(arr[i], $"{path}[{i}]", scope, variables);
			}
			return;
		}

		if (node is not JsonValue value || !TryGetPlaceholderKey(value, out var placeholder))
		{
			return;
		}

		var key = NormalizePlaceholderKey(placeholder);
		if (string.IsNullOrWhiteSpace(key))
		{
			return;
		}

		if (!variables.TryGetValue(key, out var aggregate))
		{
			aggregate = new VariableAggregate
			{
				Key = key,
				Scope = scope
			};
			variables[key] = aggregate;
		}

		aggregate.Scope = scope;
		aggregate.Paths.Add(path);
	}

	private static string NormalizePlaceholderKey(string? key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return string.Empty;
		}

		var trimmed = key.Trim();
		if (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal) && trimmed.Length > 4)
		{
			trimmed = trimmed.Substring(2, trimmed.Length - 4);
		}

		return trimmed.Trim();
	}

	private static bool TryGetPlaceholderKey(JsonValue value, out string key)
	{
		key = string.Empty;
		if (!value.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str))
		{
			return false;
		}

		str = str.Trim();
		if (!str.StartsWith("{{", StringComparison.Ordinal) || !str.EndsWith("}}", StringComparison.Ordinal))
		{
			return false;
		}

		key = str.Substring(2, str.Length - 4).Trim();
		return !string.IsNullOrWhiteSpace(key);
	}

	private void FieldSearchBar_TextChanged(object sender, TextChangedEventArgs e)
	{
		ApplyFilter();
	}

	private void ApplyFilter()
	{
		var keyword = (FieldSearchBar.Text ?? string.Empty).Trim();
		IEnumerable<TemplateVariableItem> src = _allVariables;
		if (!string.IsNullOrWhiteSpace(keyword))
		{
			src = src.Where(v =>
				v.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)
				|| v.PathsDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
				|| v.DefaultDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
				|| v.TypeDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
				|| (v.Value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
		}

		_filteredVariables.Clear();
		foreach (var item in src)
		{
			_filteredVariables.Add(item);
		}
	}

	private void ResetDefaults_Clicked(object sender, EventArgs e)
	{
		foreach (var item in _allVariables)
		{
			if (_variableDefinitions.TryGetValue(item.Key, out var def))
			{
				item.Value = def.DefaultValue ?? string.Empty;
				continue;
			}

			item.Value = item.DefaultValue ?? string.Empty;
		}
	}

	private async void PickFileForVariable_Clicked(object sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: TemplateVariableItem item })
		{
			return;
		}

		var file = await FileSystemService.PickFileAsync();
		if (!string.IsNullOrWhiteSpace(file))
		{
			item.Value = file;
		}
	}

	private async void Cancel_Clicked(object sender, EventArgs e)
	{
		await Navigation.PopAsync();
	}

	private async void CreateProject_Clicked(object sender, EventArgs e)
	{
		if (_isBusy)
		{
			return;
		}

		if (_template is null)
		{
			await DisplayAlertAsync("提示", "请先导入模板。", "确定");
			return;
		}

		var projectName = (ProjectNameEntry.Text ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(projectName))
		{
			await DisplayAlertAsync("提示", "项目名不能为空。", "确定");
			return;
		}

		if (Path.GetInvalidPathChars().Any(projectName.Contains) || Path.GetInvalidFileNameChars().Any(projectName.Contains))
		{
			await DisplayAlertAsync("提示", "项目名包含非法字符。", "确定");
			return;
		}

		try
		{
			SetBusy(true);

			var draftRoot = Path.Combine(MauiProgram.DataPath, "My Drafts");
			Directory.CreateDirectory(draftRoot);
			var projectDir = Path.Combine(draftRoot, projectName + ".pjfc");
			if (Directory.Exists(projectDir))
			{
				await DisplayAlertAsync("提示", "同名项目已存在。", "确定");
				return;
			}

			var values = BuildInputValues(_allVariables);
			var defaults = _template.Variables ?? new Dictionary<string, string?>();
			var missingKeys = CollectMissingKeys(_allVariables, values, defaults, _variableDefinitions);
			if (missingKeys.Count > 0)
			{
				await DisplayAlertAsync("提示", $"以下变量未填写且无默认值:\n{string.Join("\n", missingKeys.Take(10))}", "确定");
				return;
			}

			var projectClone = JsonNode.Parse(_projectNode.ToJsonString()) as JsonObject ?? new JsonObject();
			var draftClone = JsonNode.Parse(_draftNode.ToJsonString()) as JsonObject ?? new JsonObject();

			ReplacePlaceholders(projectClone, values, defaults, _variableDefinitions);
			ReplacePlaceholders(draftClone, values, defaults, _variableDefinitions);

			var project = projectClone.Deserialize<ProjectJSONStructure>(DraftPage.DraftJSONOption)
				?? throw new InvalidOperationException("模板中的 Project 结构无效。");
			var draft = draftClone.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption)
				?? throw new InvalidOperationException("模板中的 Draft 结构无效。");

			project.ProjectName = projectName;
			project.LastChanged = DateTime.Now;
			project.NormallyExited = true;
			project.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
			project.LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
			project.PluginUsed ??= [];
			project.TargetFrameRate = draft.TargetFrameRate;

			draft.SavedAt = DateTime.Now;

			Directory.CreateDirectory(projectDir);
			File.WriteAllText(Path.Combine(projectDir, "project.pjfc"), JsonSerializer.Serialize(project, DraftPage.DraftJSONOption));
			File.WriteAllText(Path.Combine(projectDir, "timeline.json"), JsonSerializer.Serialize(draft, DraftPage.DraftJSONOption));
			File.WriteAllText(Path.Combine(projectDir, "assets.json"), JsonSerializer.Serialize(Array.Empty<AssetItem>(), DraftPage.DraftJSONOption));

			await DisplayAlertAsync("成功", $"项目已创建:\n{projectDir}", "确定");
			await Navigation.PopAsync();
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("错误", $"创建项目失败: {ex.Message}", "确定");
		}
		finally
		{
			SetBusy(false);
		}
	}

	private static Dictionary<string, string?> BuildInputValues(IEnumerable<TemplateVariableItem> items)
	{
		var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in items)
		{
			var value = item.Value;
			if (value is not null)
			{
				dict[item.Key] = value;
			}
		}
		return dict;
	}

	private static List<string> CollectMissingKeys(
		IEnumerable<TemplateVariableItem> items,
		IReadOnlyDictionary<string, string?> values,
		IReadOnlyDictionary<string, string?> defaults,
		IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
	{
		var list = new List<string>();
		foreach (var item in items)
		{
			if (TryResolveVariable(item.Key, values, defaults, definitions, out var resolved, out _) && resolved is not null)
			{
				continue;
			}

			list.Add(item.Key);
		}

		return list;
	}

	private static void ReplacePlaceholders(
		JsonNode? node,
		IReadOnlyDictionary<string, string?> values,
		IReadOnlyDictionary<string, string?> defaults,
		IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
	{
		if (node is JsonObject obj)
		{
			var keys = obj.Select(kv => kv.Key).ToArray();
			foreach (var key in keys)
			{
				var current = obj[key];
				if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
				{
					if (!TryResolveVariable(placeholderKey, values, defaults, definitions, out var resolved, out var variableType))
					{
						throw new KeyNotFoundException($"缺少模板变量: {placeholderKey}");
					}

					obj[key] = ConvertResolvedValue(resolved, variableType);
				}
				else
				{
					ReplacePlaceholders(current, values, defaults, definitions);
				}
			}
			return;
		}

		if (node is JsonArray arr)
		{
			for (int i = 0; i < arr.Count; i++)
			{
				var current = arr[i];
				if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
				{
					if (!TryResolveVariable(placeholderKey, values, defaults, definitions, out var resolved, out var variableType))
					{
						throw new KeyNotFoundException($"缺少模板变量: {placeholderKey}");
					}

					arr[i] = ConvertResolvedValue(resolved, variableType);
				}
				else
				{
					ReplacePlaceholders(current, values, defaults, definitions);
				}
			}
		}
	}

	private static bool TryResolveVariable(
		string key,
		IReadOnlyDictionary<string, string?> values,
		IReadOnlyDictionary<string, string?> defaults,
		IReadOnlyDictionary<string, TemplateVariableDefinition> definitions,
		out string? resolved,
		out TemplateVariableType variableType)
	{
		variableType = TemplateVariableType.Auto;
		if (definitions.TryGetValue(key, out var def) && def is not null)
		{
			variableType = def.Type;
		}

		if (values.TryGetValue(key, out resolved) && resolved is not null)
		{
			return true;
		}

		if (values.TryGetValue($"{{{{{key}}}}}", out resolved) && resolved is not null)
		{
			return true;
		}

		if (def?.DefaultValue is not null)
		{
			resolved = def.DefaultValue;
			return true;
		}

		if (defaults.TryGetValue(key, out resolved))
		{
			return true;
		}

		return false;
	}

	private static JsonNode? ConvertResolvedValue(string? value, TemplateVariableType type)
	{
		if (value is null)
		{
			return null;
		}

		switch (type)
		{
			case TemplateVariableType.String:
			case TemplateVariableType.File:
				return JsonValue.Create(value);

			case TemplateVariableType.Boolean:
				if (!bool.TryParse(value, out var boolValue))
				{
					throw new FormatException($"值 '{value}' 不是有效布尔值。");
				}
				return JsonValue.Create(boolValue);

			case TemplateVariableType.Integer:
				if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
				{
					throw new FormatException($"值 '{value}' 不是有效整数。");
				}
				return JsonValue.Create(longValue);

			case TemplateVariableType.Number:
				if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
				{
					throw new FormatException($"值 '{value}' 不是有效数字。");
				}
				return JsonValue.Create(doubleValue);

			case TemplateVariableType.Json:
				return JsonNode.Parse(value);
		}

		if (value.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
		{
			return JsonNode.Parse(value.Substring(5));
		}

		if (bool.TryParse(value, out var b))
		{
			return JsonValue.Create(b);
		}

		if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
		{
			return JsonValue.Create(i);
		}

		if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
		{
			return JsonValue.Create(l);
		}

		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
		{
			return JsonValue.Create(d);
		}

		return JsonValue.Create(value);
	}

	private void SetBusy(bool isBusy)
	{
		_isBusy = isBusy;
		CreateButton.IsEnabled = !isBusy;
		FieldSearchBar.IsEnabled = !isBusy;
		VariablesCollectionView.IsEnabled = !isBusy;
		ProjectNameEntry.IsEnabled = !isBusy;
	}

	private void RefreshStats()
	{
		if (_template is null)
		{
			StatsLabel.Text = "未加载模板";
			return;
		}

		StatsLabel.Text = $"变量数: {_allVariables.Count}";
	}

	private sealed class VariableAggregate
	{
		public string Key { get; set; } = string.Empty;
		public string Scope { get; set; } = "Mixed";
		public TemplateVariableType Type { get; set; } = TemplateVariableType.Auto;
		public string? DefaultValue { get; set; }
		public List<string> Paths { get; } = [];
	}

	private sealed class TemplateVariableItem(
		string key,
		string location,
		string firstPath,
		string pathsDisplay,
		TemplateVariableType type,
		string? defaultValue,
		string value) : INotifyPropertyChanged
	{
		private string _value = value;

		public string Key { get; } = key;
		public string Location { get; } = location;
		public string PathsDisplay { get; } = string.IsNullOrWhiteSpace(pathsDisplay) ? firstPath : pathsDisplay;
		public TemplateVariableType Type { get; } = type;
		public string? DefaultValue { get; } = defaultValue;
		public string TypeDisplay => $"类型: {GetTypeDisplay(type)}";
		public bool IsFileType => type == TemplateVariableType.File;
		public Keyboard InputKeyboard => type switch
		{
			TemplateVariableType.Number => Keyboard.Numeric,
			TemplateVariableType.Integer => Keyboard.Numeric,
			_ => Keyboard.Default
		};
		public string DefaultDisplay => $"默认值: {(defaultValue is null ? "<null>" : defaultValue)}";

		public string Value
		{
			get => _value;
			set
			{
				if (string.Equals(_value, value, StringComparison.Ordinal))
				{
					return;
				}

				_value = value;
				OnPropertyChanged();
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private static string GetTypeDisplay(TemplateVariableType variableType)
		{
			return variableType switch
			{
				TemplateVariableType.String => "字符串",
				TemplateVariableType.Number => "数字",
				TemplateVariableType.Integer => "整数",
				TemplateVariableType.Boolean => "布尔",
				TemplateVariableType.File => "文件",
				TemplateVariableType.Json => "JSON",
				_ => "自动"
			};
		}

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}