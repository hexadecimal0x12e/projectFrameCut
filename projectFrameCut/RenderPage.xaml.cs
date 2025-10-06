using projectFrameCut.Shared;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace projectFrameCut;

public partial class RenderPage : ContentPage
{
    public string _workingPath;
    string _draft;
    Shared.ProjectJSONStructure _project;

    public RenderPage()
    {
        InitializeComponent();
        BindingContext = new RenderPageViewModel();
    }

    public RenderPage(string path, string draftStructureJSON, Shared.ProjectJSONStructure projectInfo)
    {
        InitializeComponent();
        _workingPath = path;
        _draft = draftStructureJSON;
        _project = projectInfo;
        Title = Localized.RenderPage_ExportTitle(projectInfo.projectName);

        BindingContext = new RenderPageViewModel();
    }

    private async void ContentPage_Loaded(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workingPath))
        {
            await DisplayAlert(Localized._Info, Localized.RenderPage_NoDraft, Localized._OK);
        }
    }

    private async void StartRender_Clicked(object sender, EventArgs e)
    {
        if (BindingContext is RenderPageViewModel vm)
        {
            Log("Output options:\r\n" + vm.BuildSummary());
#if WINDOWS
            var outTempFile = Path.Combine(_workingPath, "export", $"output-{Guid.NewGuid()}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(outTempFile) ?? throw new NullReferenceException());
            var args = $"render " +
                $"\"-draft={Path.Combine(_workingPath, "timeline.json")}\" " +
                $"-range=0-{_project.Duration} " +
                $"\"-output={outTempFile}\" " +
                $"-output_options={vm.Width},{vm.Height},{vm.Framerate},AV_PIX_FMT_YUV444P,libx264 " +
                $"-maxParallelThreads={(int)MaxParallelThreadsCount.Value}";

            Log($"Args to render:{args}");

            LoggingEntry.Text = "";

            var render = new projectFrameCut.Platforms.Windows.RenderHelper();

            var renderOutput = "";

            bool running = true;

            render.OnLog += (log) =>
            {
                renderOutput += log + "\r\n";

                
            };  

            new Thread(() =>
            {
                while (running)
                {
                    //renderOutput += "Render started.\r\n";
                    Dispatcher.Dispatch(() =>
                    {
                        LoggingEntry.Text = renderOutput;
                    });
                    Thread.Sleep(2500);
                }
            }).Start();

            render.OnProgressChanged += (p) =>
            {
                Dispatcher.Dispatch(async () =>
                {
                    await RenderProgress.ProgressTo(p, 250, Easing.Linear);
                });
            };  

           await render.StartRender(args);

            running = false;
#endif
        }
    }

    private void MaxParallelThreadsCount_ValueChanged(object sender, ValueChangedEventArgs e)
    {
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)e.NewValue);
    }
}
public class RenderPageViewModel : INotifyPropertyChanged
{
    public string[] ExportOptions_Resolution { get; } = [
        "1280x720",
        "1920x1080",
        "2560x1440",
        "3840x2160",
        "7680x4320",
        Localized.RenderPage_CustomOption
    ];

    public string[] ExportOptions_Framerate { get; } = [
        "24", "30", "45", "60", "90", "120",
        Localized.RenderPage_CustomOption
    ];

    public string[] ExportOptions_Encoding { get; } = [
        "h264", "h265/hevc", "av1",
        Localized.RenderPage_CustomOption
    ];

    string _resoultion = "1920x1080";
    public string Resoultion
    {
        get => _resoultion;
        set
        {
            if (SetProperty(ref _resoultion, value))
            {
                OnPropertyChanged(nameof(IsCustomResolutionVisible));
                if (!string.IsNullOrWhiteSpace(value) &&
                    value != Localized.RenderPage_CustomOption &&
                    value.Contains('x'))
                {
                    var parts = value.Split('x', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        _width = parts[0];
                        _height = parts[1];
                        OnPropertyChanged(nameof(Width));   // ÐÞÕý
                        OnPropertyChanged(nameof(Height));  // ÐÞÕý
                    }
                }
            }
        }
    }

    string _framerate = "30";
    public string Framerate
    {
        get
        {
            if (_framerate == Localized.RenderPage_CustomOption) return "";
            else return _framerate;
        }
        set
        {
            if (SetProperty(ref _framerate, value))
            {
                OnPropertyChanged(nameof(IsCustomFramerateVisible));
            }
        }
    }

    public string FramerateDisplay
    {
        get
        {
            if (ExportOptions_Framerate.Any((x) => x == Framerate)) return Framerate;
            else return Localized.RenderPage_CustomOption;
        }
        set
        {
            Framerate = value;
        }
    }

    string _encoding = "h264";
    public string Encoding
    {
        get
        {
            if (_encoding == Localized.RenderPage_CustomOption) return "";
            else return _encoding;
        }
        set
        {
            if (SetProperty(ref _encoding, value))
            {
                OnPropertyChanged(nameof(IsCustomEncodingVisible));
            }
        }
    }

    public string EncodingDisplay
    {
        get
        {
            if (ExportOptions_Encoding.Any((x) => x == Encoding)) return Encoding;
            else return Localized.RenderPage_CustomOption;
        }
        set
        {
            Encoding = value;
        }
    }

    string _width = "1920";
    public string Width
    {
        get => _width;
        set
        {
            SetProperty(ref _width, value);

        }
    }

    string _height = "1080";
    public string Height
    {
        get => _height;
        set
        {
            SetProperty(ref _height, value);
        }
    }


    public bool IsCustomResolutionVisible => _resoultion == Localized.RenderPage_CustomOption;
    public bool IsCustomFramerateVisible => !ExportOptions_Framerate.Where((x) => x != Localized.RenderPage_CustomOption).Any((x) => x == _framerate);
    public bool IsCustomEncodingVisible =>  !ExportOptions_Encoding.Where((x) => x != Localized.RenderPage_CustomOption).Any((x) => x == _encoding);  

    public string BuildSummary() =>
        $"{_width}x{_height} @ {_framerate} fps\nEncoding: {_encoding}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value!;
        OnPropertyChanged(name);
        return true;
    }
    
}