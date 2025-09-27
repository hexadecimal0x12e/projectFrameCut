using projectFrameCut.Shared;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels
{
    public class ClipViewModel : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name = "Clip";
        private double _startSeconds;
        private double _durationSeconds = 5.0;
        private bool _isSelected;
        private string? _sourcePath; // underlying asset file path
        private Dictionary<string, object> _metadata = new();
        private ClipMode _type = ClipMode.Special;

        public string Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public double StartSeconds
        {
            get => _startSeconds;
            set { if (Math.Abs(_startSeconds - value) > double.Epsilon) { _startSeconds = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged(nameof(EndSeconds)); } }
        }

        public double DurationSeconds
        {
            get => _durationSeconds;
            set { if (Math.Abs(_durationSeconds - value) > double.Epsilon) { _durationSeconds = Math.Max(0.05, value); OnPropertyChanged(); OnPropertyChanged(nameof(EndSeconds)); } }
        }

        public double EndSeconds => StartSeconds + DurationSeconds;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        // Path to original asset file (if created from asset library)
        public string? SourcePath
        {
            get => _sourcePath;
            set { if (_sourcePath != value) { _sourcePath = value; OnPropertyChanged(); } }
        }

        // Arbitrary metadata dictionary for extensibility
        public Dictionary<string, object> Metadata
        {
            get => _metadata;
            set { if (_metadata != value) { _metadata = value; OnPropertyChanged(); } }
        }

        public ClipMode Type
        {
            get => _type;
            set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
