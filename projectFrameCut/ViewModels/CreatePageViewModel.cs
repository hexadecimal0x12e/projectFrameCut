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
        public sealed record ResolutionOption(string AspectRatio, int Width, int Height);

        private static readonly ResolutionOption DefaultResolution = new("16:9", 1920, 1080);
        private const double PreviewMaxWidth = 320d;
        private const double PreviewMaxHeight = 190d;

        private string _projectName = string.Empty;
        private ResolutionOption _selectedResolution = DefaultResolution;
        private string _selectedFrameRate = "60";
        private double _previewWidth = 320d;
        private double _previewHeight = 180d;
        private string _resolutionDisplay = "1920 × 1080";
        private string _aspectRatioText = "16:9";
        private string _frameRateDisplay = "60 fps";
        private ProjectTemplateItem? _selectedTemplate;
        private bool _isBusy;
        private string _statusText = string.Empty;

        public ObservableCollection<ProjectTemplateItem> Templates { get; } = new();
        public ObservableCollection<ResolutionOption> ResolutionOptions { get; } =
        [
            DefaultResolution,
            new("9:16", 1080, 1920),
            new("1:1", 1080, 1080),
            new("4:3", 1440, 1080),
            new("3:4", 1080, 1440),
            new("3:2", 1620, 1080),
            new("2:3", 1080, 1620),
            new("4:5", 1080, 1350),
            new("5:4", 1350, 1080),
            new("21:9", 2520, 1080)
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
        public ICommand CreateProjectCommand { get; }
        public Func<string, int, int, uint, JSONBasedTemplateStructure?, Task>? CreateProjectRequested { get; set; }

        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        public ResolutionOption SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (value is not null && SetProperty(ref _selectedResolution, value))
                {
                    LogDiagnostic($"CreatePage: selected {value.AspectRatio} ({value.Width} × {value.Height}).");
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
            CreateProjectCommand = new Command(async () => await CreateProjectAsync());
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
                Log($"Creating project '{projectName}' at {width} × {height} ({SelectedResolution.AspectRatio}), {frameRate} fps.");
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
            var resolutionOption = ResolutionOptions.FirstOrDefault(r => r.Width == width && r.Height == height);
            if (resolutionOption is null)
            {
                resolutionOption = new ResolutionOption(BuildAspectRatio(width, height), width, height);
                ResolutionOptions.Add(resolutionOption);
            }
            SelectedResolution = resolutionOption;

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
            ResolutionDisplay = $"{width} × {height}";
            AspectRatioText = SelectedResolution.AspectRatio;
            FrameRateDisplay = $"{frameRate} fps";
        }

        private (int width, int height) GetCurrentResolution()
            => (SelectedResolution.Width, SelectedResolution.Height);

        private void EnsureFrameRateOption(string value)
        {
            if (FrameRateOptions.Contains(value))
            {
                return;
            }

            FrameRateOptions.Add(value);
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
