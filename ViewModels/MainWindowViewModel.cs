using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using System;
using System.Configuration;
using System.Threading.Tasks;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly IDialogService? _dialogService;
        private readonly AudioEngine? _audioEngine;
        private readonly ILoggingService? _loggingService;
        private DateTime _streamStartTime;
        private System.Timers.Timer? _streamTimer;

        [ObservableProperty]
        private string _windowTitle;

        [ObservableProperty]
        private int _volumeLevel = 0;

        [ObservableProperty]
        private float _leftDb;

        [ObservableProperty]
        private float _rightDb;

        [ObservableProperty]
        private string _logLines = string.Empty;

        [ObservableProperty]
        private string _streamingTime = "00:00:00";

        [ObservableProperty]
        private string _listenerCount = "Listeners: 0";

        public IAsyncRelayCommand? OpenSettingsCommand { get; }

        public MainWindowViewModel() //Only used by Design
        {
            _volumeLevel = SettingsService.Default.Settings.VolumeLevel;
            _windowTitle = "GoombaCast: GoombaDesign";
        }

        public void WriteLineToLog(string message)
            => LogLines += message + "\n";

        public void UpdateWindowTitle(string streamName)
            => WindowTitle = $"GoombaCast: {streamName}";

        public void StartTimer()
        {
            _streamStartTime = DateTime.Now;
            _streamTimer?.Start();
        }

        public void StopTimer()
        {
            _streamTimer?.Stop();
            StreamingTime = "00:00:00";
        }

        public MainWindowViewModel(AudioEngine audio, IDialogService dialogService, ILoggingService loggingService)
        {
            _audioEngine = audio;
            _dialogService = dialogService;
            _loggingService = loggingService;

            var s = SettingsService.Default.Settings;

            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);

            _volumeLevel = s.VolumeLevel;

            _audioEngine.LevelsAvailable += (l, r) =>
            {
                LeftDb = l;
                RightDb = r;
            };

            VolumeLevel = (int)_audioEngine.GetGainLevel();

            // Subscribe to log events
            _loggingService.LogLineAdded += (_, message) =>
            {
                WriteLineToLog(message);
            };

            _streamStartTime = DateTime.Now;
            _streamTimer = new System.Timers.Timer(500); // Update every second
            _streamTimer.Elapsed += (s, e) =>
            {
                var duration = DateTime.Now - _streamStartTime;
                StreamingTime = $"{duration:hh\\:mm\\:ss}";
            };

            foreach (var item in InputDevice.GetActiveInputDevices())
            {
                Logging.Log($"Found input device: {item}");
            }

            _windowTitle = $"GoombaCast: {s.StreamName}";
        }

        public void Cleanup()
        {
            _streamTimer?.Stop();
            _streamTimer?.Dispose();
        }

        partial void OnVolumeLevelChanged(int value)
        {
            var s = SettingsService.Default.Settings;
            if (s.VolumeLevel != value)
            {
                s.VolumeLevel = value;
                SettingsService.Default.Save();
            }
            _audioEngine?.SetGainLevel(value);
        }

        private Task OpenSettingsAsync() => 
            _dialogService?.ShowSettingsDialogAsync() ?? Task.CompletedTask;
    }
}
