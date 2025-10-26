using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Audio.Streaming;
using GoombaCast.Services;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
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

        public void WriteLineToLog(string message)
            => LogLines += message + "\n";

        public MainWindowViewModel()
        {
            // Restore persisted volume
            _volumeLevel = SettingsService.Default.Settings.VolumeLevel;
            _windowTitle = "GoombaCast connected to: yeah";
        }

        public MainWindowViewModel(AudioEngine audio)
        {
            // Restore persisted volume
            _volumeLevel = SettingsService.Default.Settings.VolumeLevel;

            // These callbacks are already marshalled to UI thread via CallbackContext
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
    }
}
