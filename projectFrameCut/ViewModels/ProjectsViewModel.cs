using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels
{
#if WINDOWS
    [WinRT.GeneratedBindableCustomProperty()]
#endif
    public partial class ProjectsViewModel : INotifyPropertyChanged
    {
        public string _name = "unknown";
        public DateTime? _lastChanged;
        public string _thumbPath = string.Empty;
        public string _projectPath = string.Empty;

        public ProjectsViewModel(string name, DateTime? lastChanged, string thumbPath)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _lastChanged = lastChanged;
            _thumbPath = thumbPath ?? throw new ArgumentNullException(nameof(thumbPath));
        }

        public ProjectsViewModel()
        {
        }

        public string Name
        {
            get => _name == "!!CreateButton!!" ? Localized.HomePage_CreateAProject : _name;
        }

        public string LastChangedDisplay
        {
            get =>
                _name == "!!CreateButton!!" ?
#if WINDOWS || MACCATALYST
                Localized.HomePage_CreateAProject_Hint 
#else
                Localized.HomePage_CreateAProject_Hint_Tap
#endif
                : _lastChanged is null ? Localized.HomePage_GoDraft_DraftBroken_InvaildInfoTitle :
                DateTime.Now.Ticks - _lastChanged.Value.Ticks >= 0 ?
                TimeSpan.FromTicks(DateTime.Now.Ticks - _lastChanged.Value.Ticks) switch
                {
                    var t when t.TotalHours < 2 => Localized.HomePage_LastChangedOnMinutes(t.Minutes),
                    var t when t.TotalHours < 48 => Localized.HomePage_LastChangedOnHours((int)t.TotalHours),
                    var t when t.TotalDays < 14 => Localized.HomePage_LastChangedOnDays((int)t.TotalDays),
                    _ => Localized.HomePage_LastChangedOnExactTimeSpan(_lastChanged.Value)
                }
                : Localized.HomePage_LastChangedOnFuture;
        }


        public ImageSource? ThumbImage
        {
            get
            {
                try
                {
                    if (_thumbPath == "!!CreateButton!!")
                    {
                        return ImageSource.FromFile("icon_add_png");
                    }
                    if (!File.Exists(_thumbPath) && new FileInfo(_thumbPath).Length <= 16)
                    {
                        return ImageSource.FromFile("icon_unknown_png");
                    }
                    return ImageSource.FromFile(_thumbPath);
                }
                catch (Exception ex)
                {
                    //Log(ex, $"Get thumb for {_thumbPath}", this); //this is okay for not logging
                    return ImageSource.FromFile("icon_unknown_png");
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T backingField, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(backingField, value)) return false;
            backingField = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
