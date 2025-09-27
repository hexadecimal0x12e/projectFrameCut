using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels
{
    public class TimelineViewModel : INotifyPropertyChanged
    {
        private double _pixelsPerSecond = 100; // zoom level
        private double _playheadSeconds;
        private bool _isPlaying;
        private double _totalSeconds = 60; // base timeline length

        // Snapping settings
        private bool _snapEnabled = true;
        private double _snapGridSeconds = 0.5; // grid interval in seconds
        private double _snapThresholdSeconds = 0.1; // snap when within this range
        private bool _snapToPlayhead = true;
        private bool _snapToClips = true;

        public double PixelsPerSecond
        {
            get => _pixelsPerSecond;
            set { if (Math.Abs(_pixelsPerSecond - value) > double.Epsilon) { _pixelsPerSecond = Math.Clamp(value, 10, 1000); OnPropertyChanged(); OnPropertyChanged(nameof(TimelineWidth)); } }
        }

        public double PlayheadSeconds
        {
            get => _playheadSeconds;
            set { if (Math.Abs(_playheadSeconds - value) > double.Epsilon) { _playheadSeconds = Math.Max(0, value); OnPropertyChanged(); } }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
        }

        public bool SnapEnabled
        {
            get => _snapEnabled;
            set { if (_snapEnabled != value) { _snapEnabled = value; OnPropertyChanged(); } }
        }

        public double SnapGridSeconds
        {
            get => _snapGridSeconds;
            set { if (Math.Abs(_snapGridSeconds - value) > double.Epsilon) { _snapGridSeconds = Math.Max(0.01, value); OnPropertyChanged(); } }
        }

        public double SnapThresholdSeconds
        {
            get => _snapThresholdSeconds;
            set { if (Math.Abs(_snapThresholdSeconds - value) > double.Epsilon) { _snapThresholdSeconds = Math.Max(0.001, value); OnPropertyChanged(); } }
        }

        public bool SnapToPlayhead
        {
            get => _snapToPlayhead;
            set { if (_snapToPlayhead != value) { _snapToPlayhead = value; OnPropertyChanged(); } }
        }

        public bool SnapToClips
        {
            get => _snapToClips;
            set { if (_snapToClips != value) { _snapToClips = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<TrackViewModel> Tracks { get; } = new();

        public double TotalSeconds
        {
            get => _totalSeconds;
            set { if (Math.Abs(_totalSeconds - value) > double.Epsilon) { _totalSeconds = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged(nameof(TimelineWidth)); } }
        }

        public double TimelineWidth => TotalSeconds * PixelsPerSecond; // 60 seconds virtual length by default

        public void AddTrack(string? name = null)
        {
            Tracks.Add(new TrackViewModel { Name = name ?? $"Track #{Tracks.Count + 1}" });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
