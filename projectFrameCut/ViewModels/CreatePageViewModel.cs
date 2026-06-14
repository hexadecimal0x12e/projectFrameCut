using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Template;

namespace projectFrameCut;

public partial class CreatePage
{
    private sealed class CreatePageViewModel : INotifyPropertyChanged
    {
        private static readonly string CustomResolutionOption = Localized.DraftPage_PrevResultion_Custom;
        private const double PreviewMaxWidth = 320d;
        private const double PreviewMaxHeight = 190d;

        private string _projectName = string.Empty;
        private string _selectedResolution = "1920 × 1080";
        private string _customRelativeWidthText = "1920";
        private string _customRelativeHeightText = "1080";
        private string _selectedFrameRate = "60";
        private bool _isCustomResolutionSelected;
        private double _previewWidth = 320d;
        private double _previewHeight = 180d;
        private string _resolutionDisplay = "1920 × 1080";
        private string _aspectRatioText = "16:9";
        private string _frameRateDisplay = "60 fps";
        private ProjectTemplateItem? _selectedTemplate;
        private bool _isBusy;
        private string _statusText = string.Empty;

        public ObservableCollection<ProjectTemplateItem> Templates { get; } = new();
        public ObservableCollection<string> ResolutionOptions { get; } =
        [
            "3840 × 2160",
            "2560 × 1440",
            "1920 × 1080",
            "1280 × 720",
            "1080 × 1920",
            "720 × 1280",
            CustomResolutionOption
        ];
        public ObservableCollection<string> FrameRateOptions { get; } =
        [
            "24",
            "25",
            "30",
            "50",
            "60",
            "120"
        ];

        public ICommand UseTemplateCommand { get; }
        public ICommand RotateResolutionCommand { get; }
        public ICommand ResolutionResetCommand { get; }
        public ICommand CreateProjectCommand { get; }
        public Func<string, int, int, uint, JSONBasedTemplateStructure?, Task>? CreateProjectRequested { get; set; }

        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        public string SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (SetProperty(ref _selectedResolution, value))
                {
                    IsCustomResolutionSelected = IsCustomResolutionValue(value);
                    UpdatePreview();
                }
            }
        }

        public string CustomRelativeWidthText
        {
            get => _customRelativeWidthText;
            set
            {
                if (SetProperty(ref _customRelativeWidthText, value) && IsCustomResolutionSelected)
                {
                    UpdatePreview();
                }
            }
        }

        public string CustomRelativeHeightText
        {
            get => _customRelativeHeightText;
            set
            {
                if (SetProperty(ref _customRelativeHeightText, value) && IsCustomResolutionSelected)
                {
                    UpdatePreview();
                }
            }
        }

        public string SelectedFrameRate
        {
            get => _selectedFrameRate;
            set
            {
                if (SetProperty(ref _selectedFrameRate, value))
                {
                    UpdatePreview();
                }
            }
        }

        public bool IsCustomResolutionSelected
        {
            get => _isCustomResolutionSelected;
            private set => SetProperty(ref _isCustomResolutionSelected, value);
        }

        public double PreviewWidth
        {
            get => _previewWidth;
            private set => SetProperty(ref _previewWidth, value);
        }

        public double PreviewHeight
        {
            get => _previewHeight;
            private set => SetProperty(ref _previewHeight, value);
        }

        public string ResolutionDisplay
        {
            get => _resolutionDisplay;
            private set => SetProperty(ref _resolutionDisplay, value);
        }

        public string AspectRatioText
        {
            get => _aspectRatioText;
            private set => SetProperty(ref _aspectRatioText, value);
        }

        public string FrameRateDisplay
        {
            get => _frameRateDisplay;
            private set => SetProperty(ref _frameRateDisplay, value);
        }

        public string TemplateCountText => Localized.CreatePage_TemplateCount(Templates.Count);

        public ProjectTemplateItem? SelectedTemplate
        {
            get => _selectedTemplate;
            private set => SetProperty(ref _selectedTemplate, value);
        }

        public bool IsNotBusy => !_isBusy;

        public bool HasStatusText => !string.IsNullOrWhiteSpace(_statusText);

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (SetProperty(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatusText));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public CreatePageViewModel()
        {
            UseTemplateCommand = new Command<ProjectTemplateItem?>(ApplyTemplate);
            RotateResolutionCommand = new Command(RotateResolution);
            ResolutionResetCommand = new Command(() =>
            {
                SelectedResolution = "1920 × 1080";
                CustomRelativeWidthText = "1920";
                CustomRelativeHeightText = "1080";
            });
            CreateProjectCommand = new Command(async () => await CreateProjectAsync());
            IsCustomResolutionSelected = IsCustomResolutionValue(SelectedResolution);
            ReloadTemplates();
            UpdatePreview();
        }

        private async Task CreateProjectAsync()
        {
            if (_isBusy)
            {
                return;
            }

            StatusText = string.Empty;
            var projectName = ProjectName?.Trim();
            if (string.IsNullOrWhiteSpace(projectName))
            {
                StatusText = Localized.CreatePage_NoProjectName;
                return;
            }

            var (width, height) = GetCurrentResolution();
            var frameRate = (uint)ParsePositiveInt(SelectedFrameRate, 60);
            var template = SelectedTemplate?.Structure;

            var handler = CreateProjectRequested;
            if (handler is null)
            {
                return;
            }

            _isBusy = true;
            OnPropertyChanged(nameof(IsNotBusy));
            try
            {
                await handler(projectName, width, height, frameRate, template);
            }
            finally
            {
                _isBusy = false;
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }

        public void ReloadTemplates()
        {
            var items = TemplateStore.Templates.Values
                .OfType<JSONBasedTemplateStructure>()
                .Where(t => t.Scope == TemplateScope.Project || t.Scope == TemplateScope.Any)
                .OrderBy(t => t.TemplateName, StringComparer.OrdinalIgnoreCase)
                .Select(t => new ProjectTemplateItem(t))
                .ToList();

            Templates.Clear();
            foreach (var item in items)
            {
                Templates.Add(item);
            }

            OnPropertyChanged(nameof(TemplateCountText));

            if (SelectedTemplate is null)
            {
                return;
            }

            SelectedTemplate = Templates.FirstOrDefault(t => t.TemplateId == SelectedTemplate.TemplateId);
        }

        private void ApplyTemplate(ProjectTemplateItem? template)
        {
            if (template is null)
            {
                return;
            }

            SelectedTemplate = template;
            ProjectName = string.IsNullOrWhiteSpace(template.Structure.Project.ProjectName)
                ? template.Name
                : template.Structure.Project.ProjectName!;
            var width = Math.Max(1, template.Structure.Project.RelativeWidth);
            var height = Math.Max(1, template.Structure.Project.RelativeHeight);
            var resolutionOption = BuildResolutionOption(width, height);
            if (ResolutionOptions.Contains(resolutionOption))
            {
                SelectedResolution = resolutionOption;
            }
            else
            {
                CustomRelativeWidthText = width.ToString();
                CustomRelativeHeightText = height.ToString();
                SelectedResolution = CustomResolutionOption;
            }

            var frameRateOption = Math.Max(1u, template.Structure.Project.TargetFrameRate).ToString();
            EnsureFrameRateOption(frameRateOption);
            SelectedFrameRate = frameRateOption;
        }

        private void UpdatePreview()
        {
            var (width, height) = GetCurrentResolution();
            var frameRate = ParsePositiveInt(SelectedFrameRate, 60);

            var scale = Math.Min(PreviewMaxWidth / width, PreviewMaxHeight / height);
            PreviewWidth = Math.Max(30d, Math.Round(width * scale, 2));
            PreviewHeight = Math.Max(30d, Math.Round(height * scale, 2));
            ResolutionDisplay = BuildResolutionOption(width, height);
            AspectRatioText = $"{BuildAspectRatio(width, height)}";
            FrameRateDisplay = $"{frameRate} fps";
        }

        private void RotateResolution()
        {
            var (width, height) = GetCurrentResolution();
            var rotatedWidth = height;
            var rotatedHeight = width;
            var rotatedOption = BuildResolutionOption(rotatedWidth, rotatedHeight);

            if (ResolutionOptions.Contains(rotatedOption))
            {
                SelectedResolution = rotatedOption;
                return;
            }

            CustomRelativeWidthText = rotatedWidth.ToString();
            CustomRelativeHeightText = rotatedHeight.ToString();
            SelectedResolution = CustomResolutionOption;
        }

        private (int width, int height) GetCurrentResolution()
        {
            if (IsCustomResolutionSelected)
            {
                var width = ParsePositiveInt(CustomRelativeWidthText, 1920);
                var height = ParsePositiveInt(CustomRelativeHeightText, 1080);
                return (width, height);
            }

            return ParseResolution(SelectedResolution, 1920, 1080);
        }

        private void EnsureFrameRateOption(string value)
        {
            if (FrameRateOptions.Contains(value))
            {
                return;
            }

            FrameRateOptions.Add(value);
        }

        private static string BuildResolutionOption(int width, int height) => $"{width} × {height}";

        private static bool IsCustomResolutionValue(string? value)
            => string.Equals(value, CustomResolutionOption, StringComparison.Ordinal);

        private static (int width, int height) ParseResolution(string? text, int fallbackWidth, int fallbackHeight)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return (fallbackWidth, fallbackHeight);
            }

            var normalized = text.Replace(" ", string.Empty).Replace("x", "×", StringComparison.OrdinalIgnoreCase);
            var parts = normalized.Split('×', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height)
                || width <= 0
                || height <= 0)
            {
                return (fallbackWidth, fallbackHeight);
            }

            return (width, height);
        }

        private static int ParsePositiveInt(string? text, int fallback)
        {
            return int.TryParse(text, out var value) && value > 0 ? value : fallback;
        }

        private static string BuildAspectRatio(int width, int height)
        {
            var divisor = GreatestCommonDivisor(width, height);
            return $"{width / divisor}:{height / divisor}";
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return Math.Abs(a);
        }

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
