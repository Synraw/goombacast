using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Audio.Streaming;
using GoombaCast.Services;
using System.Threading.Tasks;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly IDialogService? _dialogService;

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

        public void WriteLineToLog(string message)
            => LogLines += message + "\n";

        public MainWindowViewModel()
        {
            _volumeLevel = SettingsService.Default.Settings.VolumeLevel;
            _windowTitle = "GoombaCast connected to: yeah";
        }

        public MainWindowViewModel(AudioEngine audio, IDialogService dialogService)
        {
            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);

            _dialogService = dialogService;

            _volumeLevel = SettingsService.Default.Settings.VolumeLevel;

            audio.LevelsAvailable += (l, r) =>
            {
                LeftDb = l;
                RightDb = r;
            };

            foreach (var item in InputDevice.GetActiveInputDevices())
            {
                WriteLineToLog($"Found input device: {item}");
            }

            _windowTitle = "GoombaCast connected to: yeah";
        }

        // Persist when volume changes
        partial void OnVolumeLevelChanged(int value)
        {
            var s = SettingsService.Default.Settings;
            if (s.VolumeLevel != value)
            {
                s.VolumeLevel = value;
                SettingsService.Default.Save();
            }
        }

        private Task OpenSettingsAsync() => 
            _dialogService?.ShowSettingsDialogAsync() ?? Task.CompletedTask;
    }
}
