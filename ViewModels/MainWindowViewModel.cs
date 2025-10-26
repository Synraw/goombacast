using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using System.Threading.Tasks;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly IDialogService? _dialogService;
        private readonly AudioEngine? _audioEngine;
        private readonly ILoggingService? _loggingService;

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

            foreach (var item in InputDevice.GetActiveInputDevices())
            {
                Logging.Log($"Found input device: {item}");
            }

            _windowTitle = $"GoombaCast: {s.StreamName}";
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
