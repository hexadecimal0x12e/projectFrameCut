using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
	private readonly ObservableCollection<TemplateClipItem> _clips = [];
	private static readonly IReadOnlyList<TemplateScope> ScopeValues =
	[
		TemplateScope.Any,
		TemplateScope.Project,
		TemplateScope.Clips,
		TemplateScope.Tracks
	];
	private JsonObject _projectNode = new();
	private JsonObject _draftNode = new();
	private JsonObject _draftSourceNode = new();
	private bool _isBusy;
	private bool _showNonRecommended;

	public TemplateExtractPage(ViewModels.ProjectsViewModel projectVm)
	{
		InitializeComponent();
		_projectVm = projectVm;
		FieldsCollectionView.ItemsSource = _filteredFields;
		ClipsCollectionView.ItemsSource = _clips;
		ScopePicker.ItemsSource = GetScopeOptions();
		ScopePicker.SelectedIndex = 0;
		ProjectNameLabel.Text = Localized.TemplateExtractPage_ProjectName(projectVm.Name);
		Loaded += TemplateExtractPage_Loaded;
	}

	private static List<string> GetScopeOptions() => [Localized.TemplateExtractPage_Scope_Any, Localized.TemplateExtractPage_Scope_Project, Localized.TemplateExtractPage_Scope_Clip, Localized.TemplateExtractPage_Scope_Track];


	private TemplateScope GetSelectedScope()
	{
		var index = ScopePicker.SelectedIndex;
		if (index < 0 || index >= ScopeValues.Count)
		{
			return TemplateScope.Any;
		}

		return ScopeValues[index];
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
				await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}", Localized._OK);
				await Navigation.PopAsync();
				return;
			}

			var project = JsonSerializer.Deserialize<ProjectJSONStructure>(await File.ReadAllTextAsync(projectPath), DraftPage.DraftJSONOption);
			var draft = JsonSerializer.Deserialize<DraftStructureJSON>(await File.ReadAllTextAsync(timelinePath), DraftPage.DraftJSONOption);
			if (project is null || draft is null)
			{
				await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}", Localized._OK);
				await Navigation.PopAsync();
				return;
			}

			_projectNode = JsonSerializer.SerializeToNode(project, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject();
			_draftSourceNode = JsonSerializer.SerializeToNode(draft, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject();

			InitializeClipSelections();
			RebuildExtractableFields();
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n({Localized._ExceptionTemplate(ex)})", Localized._OK);
			await Navigation.PopAsync();
		}
	}

	private void InitializeClipSelections()
	{
		foreach (var clip in _clips)
		{
			clip.PropertyChanged -= ClipItem_PropertyChanged;
		}

		_clips.Clear();
		if (_draftSourceNode["Clips"] is not JsonArray clipsArray)
		{
			return;
		}

		for (int i = 0; i < clipsArray.Count; i++)
		{
			if (clipsArray[i] is not JsonObject clipObj)
			{
				continue;
			}

			var clipId = TryGetClipId(clipObj) ?? $"clip_{i}";
			var clipName = GetClipDisplayName(clipObj, i, clipId);
			var item = new TemplateClipItem(clipId, clipName, true);
			item.PropertyChanged += ClipItem_PropertyChanged;
			_clips.Add(item);
		}
	}

	private void RebuildExtractableFields()
	{
		_draftNode = BuildDraftNodeForSelectedClips();
		var previousSelection = _allFields
			.GroupBy(f => f.PathDisplay, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				g => g.Key,
				g => g.First(),
				StringComparer.OrdinalIgnoreCase);

		foreach (var item in _allFields)
		{
			item.PropertyChanged -= FieldItem_PropertyChanged;
		}

		_allFields.Clear();
		_filteredFields.Clear();

		AddExtractableFields(_projectNode, "project", "Project", []);
		AddExtractableFields(_draftNode, "draft", "Draft", []);

		foreach (var item in _allFields)
		{
			if (previousSelection.TryGetValue(item.PathDisplay, out var prev))
			{
				item.IsSelected = prev.IsSelected;
				item.VariableType = prev.VariableType;
			}

			item.PropertyChanged += FieldItem_PropertyChanged;
		}

		ApplyFilter();
		RefreshStats();
	}

	private JsonObject BuildDraftNodeForSelectedClips()
	{
		var draftClone = JsonNode.Parse(_draftSourceNode.ToJsonString()) as JsonObject ?? new JsonObject();
		if (draftClone["Clips"] is not JsonArray clipsArray)
		{
			return draftClone;
		}

		var selectedClipIds = new HashSet<string>(
			_clips.Where(c => c.IsSelected).Select(c => c.ClipId),
			StringComparer.OrdinalIgnoreCase);

		var selectedClips = new JsonArray();
		var relatedTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var clipNode in clipsArray)
		{
			if (clipNode is not JsonObject clipObj)
			{
				continue;
			}

			var clipId = TryGetClipId(clipObj);
			if (string.IsNullOrWhiteSpace(clipId) || !selectedClipIds.Contains(clipId))
			{
				continue;
			}

			selectedClips.Add(JsonNode.Parse(clipObj.ToJsonString()));
			if (clipObj.TryGetPropertyValue("BindedSoundTrack", out var trackIdNode)
				&& trackIdNode is JsonValue trackIdValue
				&& trackIdValue.TryGetValue<string>(out var trackId)
				&& !string.IsNullOrWhiteSpace(trackId))
			{
				relatedTrackIds.Add(trackId);
			}
		}

		draftClone["Clips"] = selectedClips;
		if (draftClone["SoundTracks"] is JsonArray soundTracks && relatedTrackIds.Count > 0)
		{
			var selectedTracks = new JsonArray();
			foreach (var trackNode in soundTracks)
			{
				if (trackNode is not JsonObject trackObj)
				{
					continue;
				}

				if (!trackObj.TryGetPropertyValue("Id", out var idNode)
					|| idNode is not JsonValue idValue
					|| !idValue.TryGetValue<string>(out var id)
					|| string.IsNullOrWhiteSpace(id)
					|| !relatedTrackIds.Contains(id))
				{
					continue;
				}

				selectedTracks.Add(JsonNode.Parse(trackObj.ToJsonString()));
			}

			draftClone["SoundTracks"] = selectedTracks;
		}

		return draftClone;
	}

	private static string? TryGetClipId(JsonObject clipObj)
	{
		if (clipObj.TryGetPropertyValue("Id", out var idNode)
			&& idNode is JsonValue idValue
			&& idValue.TryGetValue<string>(out var clipId)
			&& !string.IsNullOrWhiteSpace(clipId))
		{
			return clipId;
		}

		return null;
	}

	private static string GetClipDisplayName(JsonObject clipObj, int index, string clipId)
	{
		var clipName = string.Empty;
		if (clipObj.TryGetPropertyValue("Name", out var nameNode)
			&& nameNode is JsonValue nameValue
			&& nameValue.TryGetValue<string>(out var name)
			&& !string.IsNullOrWhiteSpace(name))
		{
			clipName = name;
		}

		if (string.IsNullOrWhiteSpace(clipName))
		{
			clipName = $"Clip {index + 1}";
		}

		return clipName;
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
			var isRecommended = IsRecommendedPath(path);
			var item = new TemplateExtractFieldItem(scope, path, valuePreview, variableKey, tokens, isRecommended)
			{
				IsSelected = isRecommended,
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
		return !path.StartsWith("project")
            && (path.EndsWith(".Name", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".FilePath", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".ProjectName", StringComparison.OrdinalIgnoreCase));
	}

	private void ApplyFilter()
	{
		var keyword = (FieldSearchBar.Text ?? string.Empty).Trim();
		IEnumerable<TemplateExtractFieldItem> src = _allFields;
		if (!_showNonRecommended)
		{
			src = src.Where(s => s.IsRecommended);
		}

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

	private void ShowNonRecommendedSwitch_Toggled(object sender, ToggledEventArgs e)
	{
		_showNonRecommended = e.Value;
		ApplyFilter();
	}

	private void ClipItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(TemplateClipItem.IsSelected))
		{
			return;
		}

		RebuildExtractableFields();
	}

	private void SelectAllClips_Clicked(object sender, EventArgs e)
	{
		foreach (var clip in _clips)
		{
			clip.IsSelected = true;
		}

		RebuildExtractableFields();
	}

	private void ClearClips_Clicked(object sender, EventArgs e)
	{
		foreach (var clip in _clips)
		{
			clip.IsSelected = false;
		}

		RebuildExtractableFields();
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

		var selectedClipCount = _clips.Count(c => c.IsSelected);
		if (_clips.Count > 0 && selectedClipCount <= 0)
		{
			await DisplayAlertAsync(Localized._Info, Localized.DraftPage_SelectOneOrManyToContinue, Localized._OK);
			return;
		}

		var selected = _allFields.Where(f => f.IsSelected).ToList();
		if (selected.Count == 0)
		{
			await DisplayAlertAsync(Localized._Info, Localized.TemplateExtractPage_SelectBlankToContinue, Localized._OK);
			return;
		}

		if (selected.Any(s => string.IsNullOrWhiteSpace(s.VariableKey)))
		{
			await DisplayAlertAsync(Localized._Info, Localized.TemplateExtractPage_VarNameNotNull, Localized._OK);
			return;
		}

		if (selected.GroupBy(s => s.VariableKey.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
		{
			await DisplayAlertAsync(Localized._Info, Localized.TemplateExtractPage_VarNameNotSame, Localized._OK);
			return;
		}

		try
		{
			SetBusy(true);
			var projectClone = JsonNode.Parse(_projectNode.ToJsonString()) as JsonObject ?? new JsonObject();
			var draftClone = JsonNode.Parse(BuildDraftNodeForSelectedClips().ToJsonString()) as JsonObject ?? new JsonObject();
			var vars = new Dictionary<string, string?>();
			var variableDefinitions = new Dictionary<string, TemplateVariableDefinition>(StringComparer.OrdinalIgnoreCase);

			foreach (var field in selected)
			{
				var placeholder = $"{{{{{field.VariableKey.Trim()}}}}}";
				var root = string.Equals(field.Scope, "Project", StringComparison.OrdinalIgnoreCase) ? projectClone : draftClone;
				if (!TryReplaceNodeValue(root, field.Tokens, placeholder))
				{
					throw new InvalidOperationException($"Cannot to replace field: {field.PathDisplay}");
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
				?? throw new InvalidOperationException("Invalid draft");
			var draft = draftClone.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption)
                ?? throw new InvalidOperationException("Invalid draft");

            var projectName = await DisplayPromptAsync(Localized._Info, Localized.TemplateExtractPage_InputName, Localized._OK, null, _projectVm.Name, 1024, null, _projectVm.Name);


            var template = new JSONBasedTemplateStructure
			{
				TemplateName = projectName,
                TemplateVersion = 1,
				Scope = GetSelectedScope(),
				Project = project,
				Draft = draft,
				Variables = vars,
				VariableDefinitions = variableDefinitions
			};

			var json = JsonSerializer.Serialize(template, DraftPage.DraftJSONOption);
			using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));

			var safeName = new string(projectName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
			if (string.IsNullOrWhiteSpace(safeName))
			{
				safeName = "template";
			}

			var savePath = await FileSystemService.SaveAFile($"{safeName}_template.json", ms);
			if (string.IsNullOrWhiteSpace(savePath))
			{
				await DisplayAlertAsync(Localized._Info, Localized.DraftPage_Tasks_Status_Canceled, Localized._OK);
				return;
			}

			await DisplayAlertAsync(Localized._Info, SettingsManager.SettingLocalizedResources.Advanced_Success, Localized._OK);
			await Navigation.PopAsync();
		}
		catch (Exception ex)
		{
            await DisplayAlertAsync(Localized._Error, $"{Localized.TemplateCreatePage_InvalidTemplate}{Environment.NewLine}{Environment.NewLine}{Localized._ExceptionTemplate(ex)})", Localized._OK);
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
		ClipsCollectionView.IsEnabled = !isBusy;
		ShowNonRecommendedSwitch.IsEnabled = !isBusy;
		ScopePicker.IsEnabled = !isBusy;
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
		var selectedClipCount = _clips.Count(c => c.IsSelected);
		var count = _allFields.Count(f => f.IsSelected);
		StatsLabel.Text = Localized.TemplateExtractPage_ClipStatus(count, selectedClipCount, _clips.Count,_allFields.Count);
		//StatsLabel.Text = count <= 0
		//	? $"Clip: {selectedClipCount}/{_clips.Count}，可挖空字段: {_allFields.Count}，未选择"
		//	: $"Clip: {selectedClipCount}/{_clips.Count}，可挖空字段: {_allFields.Count}，已选择: {count}";
	}

	private sealed record PathToken(string? PropertyName, int? ArrayIndex);

	private sealed class TemplateClipItem(string clipId, string displayName, bool isSelected) : INotifyPropertyChanged
	{
		private bool _isSelected = isSelected;

		public string ClipId { get; } = clipId;
		public string DisplayName { get; } = displayName;
		public string ClipIdShort => ClipId.Length <= 8 ? ClipId : ClipId[..8];

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
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;
	}

	private sealed class TemplateExtractFieldItem(string scope, string pathDisplay, string valuePreview, string variableKey, IReadOnlyList<PathToken> tokens, bool isRecommended) : INotifyPropertyChanged
	{
		private bool _isSelected;
		private string _variableKey = variableKey;
		private TemplateVariableType _variableType = TemplateVariableType.String;

		public string Scope { get; } = scope;
		public string PathDisplay { get; } = pathDisplay;
		public string ValuePreview { get; } = valuePreview;
		public IReadOnlyList<PathToken> Tokens { get; } = tokens;
		public bool IsRecommended { get; } = isRecommended;
		public IReadOnlyList<string> VariableTypeOptions => GetVariableTypeOptions();

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

		private static IReadOnlyList<string> GetVariableTypeOptions()
		{
			var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
			return lang switch
			{
				"zh" => ["字符串", "数字", "整数", "布尔", "文件", "JSON"],
				"ja" => ["文字列", "数値", "整数", "真偽値", "ファイル", "JSON"],
				"ko" => ["문자열", "숫자", "정수", "불리언", "파일", "JSON"],
				"fr" => ["Chaine", "Nombre", "Entier", "Booleen", "Fichier", "JSON"],
				"de" => ["Zeichenfolge", "Zahl", "Ganzzahl", "Boolesch", "Datei", "JSON"],
				"es" => ["Cadena", "Numero", "Entero", "Booleano", "Archivo", "JSON"],
				"it" => ["Stringa", "Numero", "Intero", "Booleano", "File", "JSON"],
				"pl" => ["Lancuch", "Liczba", "Calkowita", "Logiczna", "Plik", "JSON"],
				"pt" => ["Texto", "Numero", "Inteiro", "Booleano", "Arquivo", "JSON"],
				"ru" => ["Stroka", "Chislo", "Tseloe", "Bulovo", "Fail", "JSON"],
				"tr" => ["Metin", "Sayi", "Tam sayi", "Mantiksal", "Dosya", "JSON"],
				"ar" => ["نص", "رقم", "عدد صحيح", "منطقي", "ملف", "JSON"],
				_ => ["String", "Number", "Integer", "Boolean", "File", "JSON"]
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