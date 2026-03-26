using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Template;

public partial class TemplateExtractPage : ContentPage
{
	private readonly ViewModels.ProjectsViewModel _projectVm;
	private readonly ObservableCollection<TemplateExtractFieldItem> _allFields = [];
	private readonly ObservableCollection<TemplateExtractFieldItem> _filteredFields = [];
	private JsonObject _projectNode = new();
	private JsonObject _draftNode = new();
	private bool _isBusy;

	public TemplateExtractPage(ViewModels.ProjectsViewModel projectVm)
	{
		InitializeComponent();
		_projectVm = projectVm;
		FieldsCollectionView.ItemsSource = _filteredFields;
		ProjectNameLabel.Text = $"项目：{projectVm.Name}";
		Loaded += TemplateExtractPage_Loaded;
	}

	private async void TemplateExtractPage_Loaded(object? sender, EventArgs e)
	{
		Loaded -= TemplateExtractPage_Loaded;
		await LoadProjectAsync();
	}

	private async Task LoadProjectAsync()
	{
		try
		{
			var projectPath = Path.Combine(_projectVm._projectPath, "project.pjfc");
			if (!File.Exists(projectPath))
			{
				projectPath = Path.Combine(_projectVm._projectPath, "project.json");
			}

			var timelinePath = Path.Combine(_projectVm._projectPath, "timeline.json");
			if (!File.Exists(projectPath) || !File.Exists(timelinePath))
			{
				await DisplayAlertAsync("错误", "项目缺少 project.pjfc/project.json 或 timeline.json，无法导出模板。", "确定");
				await Navigation.PopAsync();
				return;
			}

			var project = JsonSerializer.Deserialize<ProjectJSONStructure>(await File.ReadAllTextAsync(projectPath), DraftPage.DraftJSONOption);
			var draft = JsonSerializer.Deserialize<DraftStructureJSON>(await File.ReadAllTextAsync(timelinePath), DraftPage.DraftJSONOption);
			if (project is null || draft is null)
			{
				await DisplayAlertAsync("错误", "项目文件解析失败，无法导出模板。", "确定");
				await Navigation.PopAsync();
				return;
			}

			_projectNode = JsonSerializer.SerializeToNode(project, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject();
			_draftNode = JsonSerializer.SerializeToNode(draft, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject();

			_allFields.Clear();
			_filteredFields.Clear();

			AddExtractableFields(_projectNode, "project", "Project", []);
			AddExtractableFields(_draftNode, "draft", "Draft", []);

			foreach (var item in _allFields)
			{
				item.PropertyChanged += FieldItem_PropertyChanged;
			}

			ApplyFilter();
			RefreshStats();
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("错误", $"读取项目失败: {ex.Message}", "确定");
			await Navigation.PopAsync();
		}
	}

	private void AddExtractableFields(JsonNode? node, string path, string scope, List<PathToken> tokens)
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

				var nextTokens = new List<PathToken>(tokens) { new PathToken(kv.Key, null) };
				AddExtractableFields(kv.Value, $"{path}.{kv.Key}", scope, nextTokens);
			}
			return;
		}

		if (node is JsonArray arr)
		{
			for (int i = 0; i < arr.Count; i++)
			{
				var current = arr[i];
				if (current is null)
				{
					continue;
				}

				var nextTokens = new List<PathToken>(tokens) { new PathToken(null, i) };
				AddExtractableFields(current, $"{path}[{i}]", scope, nextTokens);
			}
			return;
		}

		if (node is JsonValue val)
		{
			var valuePreview = GetValuePreview(val);
			var variableKey = BuildUniqueVariableKey(SuggestVariableKey(path));
			var variableType = InferVariableType(path, val);
			var item = new TemplateExtractFieldItem(scope, path, valuePreview, variableKey, tokens)
			{
				IsSelected = IsRecommendedPath(path),
				VariableType = variableType
			};
			_allFields.Add(item);
		}
	}

	private static TemplateVariableType InferVariableType(string path, JsonValue value)
	{
		if (value.TryGetValue<bool>(out _))
		{
			return TemplateVariableType.Boolean;
		}

		if (value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _))
		{
			return TemplateVariableType.Integer;
		}

		if (value.TryGetValue<double>(out _))
		{
			return TemplateVariableType.Number;
		}

		if (value.TryGetValue<string>(out var str) && IsLikelyFilePath(path, str))
		{
			return TemplateVariableType.File;
		}

		return TemplateVariableType.String;
	}

	private static bool IsLikelyFilePath(string path, string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		if (path.EndsWith(".FilePath", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".Path", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".Uri", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".Url", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return (value.Contains('\\') || value.Contains('/')) && Path.HasExtension(value);
	}

	private static string GetValuePreview(JsonValue value)
	{
		if (value.TryGetValue<string>(out var str))
		{
			return str;
		}
		if (value.TryGetValue<bool>(out var b))
		{
			return b ? "true" : "false";
		}
		if (value.TryGetValue<int>(out var i))
		{
			return i.ToString();
		}
		if (value.TryGetValue<long>(out var l))
		{
			return l.ToString();
		}
		if (value.TryGetValue<double>(out var d))
		{
			return d.ToString();
		}

		return value.ToJsonString();
	}

	private string BuildUniqueVariableKey(string seed)
	{
		var key = seed;
		var i = 2;
		while (_allFields.Any(f => string.Equals(f.VariableKey, key, StringComparison.OrdinalIgnoreCase)))
		{
			key = $"{seed}_{i}";
			i++;
		}

		return key;
	}

	private static string SuggestVariableKey(string path)
	{
		var sb = new StringBuilder(path.Length);
		foreach (var ch in path)
		{
			sb.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
		}

		var normalized = sb.ToString().Trim('_');
		while (normalized.Contains("__", StringComparison.Ordinal))
		{
			normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
		}

		return string.IsNullOrWhiteSpace(normalized) ? "field" : normalized;
	}

	private static bool IsRecommendedPath(string path)
	{
		return path.EndsWith(".Name", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".FilePath", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".ProjectName", StringComparison.OrdinalIgnoreCase);
	}

	private void ApplyFilter()
	{
		var keyword = (FieldSearchBar.Text ?? string.Empty).Trim();
		IEnumerable<TemplateExtractFieldItem> src = _allFields;
		if (!string.IsNullOrWhiteSpace(keyword))
		{
			src = src.Where(s =>
				s.PathDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
				|| s.ValuePreview.Contains(keyword, StringComparison.OrdinalIgnoreCase)
				|| s.VariableKey.Contains(keyword, StringComparison.OrdinalIgnoreCase));
		}

		_filteredFields.Clear();
		foreach (var item in src)
		{
			_filteredFields.Add(item);
		}
	}

	private void FieldSearchBar_TextChanged(object sender, TextChangedEventArgs e)
	{
		ApplyFilter();
	}

	private void ClearSelection_Clicked(object sender, EventArgs e)
	{
		foreach (var item in _allFields)
		{
			item.IsSelected = false;
		}
		RefreshStats();
	}

	private void SelectRecommended_Clicked(object sender, EventArgs e)
	{
		foreach (var item in _allFields)
		{
			item.IsSelected = IsRecommendedPath(item.PathDisplay);
		}
		RefreshStats();
	}

	private async void Cancel_Clicked(object sender, EventArgs e)
	{
		await Navigation.PopAsync();
	}

	private async void Save_Clicked(object sender, EventArgs e)
	{
		if (_isBusy)
		{
			return;
		}

		var selected = _allFields.Where(f => f.IsSelected).ToList();
		if (selected.Count == 0)
		{
			await DisplayAlertAsync("提示", "请至少选择一个要挖空的字段。", "确定");
			return;
		}

		if (selected.Any(s => string.IsNullOrWhiteSpace(s.VariableKey)))
		{
			await DisplayAlertAsync("提示", "变量名不能为空。", "确定");
			return;
		}

		if (selected.GroupBy(s => s.VariableKey.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
		{
			await DisplayAlertAsync("提示", "变量名不能重复。", "确定");
			return;
		}

		try
		{
			SetBusy(true);
			var projectClone = JsonNode.Parse(_projectNode.ToJsonString()) as JsonObject ?? new JsonObject();
			var draftClone = JsonNode.Parse(_draftNode.ToJsonString()) as JsonObject ?? new JsonObject();
			var vars = new Dictionary<string, string?>();
			var variableDefinitions = new Dictionary<string, TemplateVariableDefinition>(StringComparer.OrdinalIgnoreCase);

			foreach (var field in selected)
			{
				var placeholder = $"{{{{{field.VariableKey.Trim()}}}}}";
				var root = string.Equals(field.Scope, "Project", StringComparison.OrdinalIgnoreCase) ? projectClone : draftClone;
				if (!TryReplaceNodeValue(root, field.Tokens, placeholder))
				{
					throw new InvalidOperationException($"无法替换字段: {field.PathDisplay}");
				}

				var key = field.VariableKey.Trim();
				vars[key] = field.ValuePreview;
				variableDefinitions[key] = new TemplateVariableDefinition
				{
					Type = field.VariableType,
					DefaultValue = field.ValuePreview
				};
			}

			var project = projectClone.Deserialize<ProjectJSONStructure>(DraftPage.DraftJSONOption)
				?? throw new InvalidOperationException("模板中的 Project 结构无效。");
			var draft = draftClone.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption)
				?? throw new InvalidOperationException("模板中的 Draft 结构无效。");

			var template = new JSONBasedTemplateStructure
			{
				TemplateVersion = 1,
				Project = project,
				Draft = draft,
				Variables = vars,
				VariableDefinitions = variableDefinitions
			};

			var json = JsonSerializer.Serialize(template, DraftPage.DraftJSONOption);
			using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));

			var safeName = new string(_projectVm.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
			if (string.IsNullOrWhiteSpace(safeName))
			{
				safeName = "template";
			}

			var savePath = await FileSystemService.SaveAFile($"{safeName}_template.json", ms);
			if (string.IsNullOrWhiteSpace(savePath))
			{
				await DisplayAlertAsync("提示", "已取消保存。", "确定");
				return;
			}

			await DisplayAlertAsync("成功", $"模板已保存到:\n{savePath}", "确定");
			await Navigation.PopAsync();
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("错误", $"导出模板失败: {ex.Message}", "确定");
		}
		finally
		{
			SetBusy(false);
		}
	}

	private static bool TryReplaceNodeValue(JsonNode? root, IReadOnlyList<PathToken> tokens, string replacement)
	{
		if (root is null || tokens.Count == 0)
		{
			return false;
		}

		JsonNode? current = root;
		for (int i = 0; i < tokens.Count - 1; i++)
		{
			var token = tokens[i];
			if (token.PropertyName is not null)
			{
				if (current is not JsonObject obj || !obj.TryGetPropertyValue(token.PropertyName, out current))
				{
					return false;
				}
				continue;
			}

			if (token.ArrayIndex is not null)
			{
				if (current is not JsonArray arr || token.ArrayIndex.Value < 0 || token.ArrayIndex.Value >= arr.Count)
				{
					return false;
				}
				current = arr[token.ArrayIndex.Value];
				continue;
			}

			return false;
		}

		var last = tokens[^1];
		if (last.PropertyName is not null)
		{
			if (current is not JsonObject obj)
			{
				return false;
			}

			obj[last.PropertyName] = replacement;
			return true;
		}

		if (last.ArrayIndex is not null)
		{
			if (current is not JsonArray arr || last.ArrayIndex.Value < 0 || last.ArrayIndex.Value >= arr.Count)
			{
				return false;
			}

			arr[last.ArrayIndex.Value] = replacement;
			return true;
		}

		return false;
	}

	private void SetBusy(bool isBusy)
	{
		_isBusy = isBusy;
		SaveButton.IsEnabled = !isBusy;
		FieldSearchBar.IsEnabled = !isBusy;
		FieldsCollectionView.IsEnabled = !isBusy;
	}

	private void FieldItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TemplateExtractFieldItem.IsSelected))
		{
			RefreshStats();
		}
	}

	private void RefreshStats()
	{
		var count = _allFields.Count(f => f.IsSelected);
		StatsLabel.Text = count <= 0
			? $"可挖空字段: {_allFields.Count}，未选择"
			: $"可挖空字段: {_allFields.Count}，已选择: {count}";
	}

	private sealed record PathToken(string? PropertyName, int? ArrayIndex);

	private sealed class TemplateExtractFieldItem(string scope, string pathDisplay, string valuePreview, string variableKey, IReadOnlyList<PathToken> tokens) : INotifyPropertyChanged
	{
		private bool _isSelected;
		private string _variableKey = variableKey;
		private TemplateVariableType _variableType = TemplateVariableType.String;

		private static readonly IReadOnlyList<string> _variableTypeOptions =
		[
			"字符串",
			"数字",
			"整数",
			"布尔",
			"文件",
			"JSON"
		];

		public string Scope { get; } = scope;
		public string PathDisplay { get; } = pathDisplay;
		public string ValuePreview { get; } = valuePreview;
		public IReadOnlyList<PathToken> Tokens { get; } = tokens;
		public IReadOnlyList<string> VariableTypeOptions => _variableTypeOptions;

		public TemplateVariableType VariableType
		{
			get => _variableType;
			set
			{
				if (_variableType == value)
				{
					return;
				}

				_variableType = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(VariableTypeIndex));
			}
		}

		public int VariableTypeIndex
		{
			get => ToTypeIndex(_variableType);
			set
			{
				var mapped = FromTypeIndex(value);
				if (_variableType == mapped)
				{
					return;
				}

				_variableType = mapped;
				OnPropertyChanged();
				OnPropertyChanged(nameof(VariableType));
			}
		}

		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (_isSelected == value)
				{
					return;
				}

				_isSelected = value;
				OnPropertyChanged();
			}
		}

		public string VariableKey
		{
			get => _variableKey;
			set
			{
				var normalized = NormalizeVariableKey(value);
				if (string.Equals(_variableKey, normalized, StringComparison.Ordinal))
				{
					return;
				}

				_variableKey = normalized;
				OnPropertyChanged();
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private static int ToTypeIndex(TemplateVariableType type)
		{
			return type switch
			{
				TemplateVariableType.String => 0,
				TemplateVariableType.Number => 1,
				TemplateVariableType.Integer => 2,
				TemplateVariableType.Boolean => 3,
				TemplateVariableType.File => 4,
				TemplateVariableType.Json => 5,
				_ => 0
			};
		}

		private static TemplateVariableType FromTypeIndex(int index)
		{
			return index switch
			{
				1 => TemplateVariableType.Number,
				2 => TemplateVariableType.Integer,
				3 => TemplateVariableType.Boolean,
				4 => TemplateVariableType.File,
				5 => TemplateVariableType.Json,
				_ => TemplateVariableType.String
			};
		}

		private static string NormalizeVariableKey(string? key)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				return string.Empty;
			}

			var sb = new StringBuilder(key.Length);
			foreach (var ch in key.Trim())
			{
				sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '.' ? ch : '_');
			}

			var result = sb.ToString().Trim('_');
			while (result.Contains("__", StringComparison.Ordinal))
			{
				result = result.Replace("__", "_", StringComparison.Ordinal);
			}

			return result;
		}

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}