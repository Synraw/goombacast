using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using GoombaCast.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GoombaCast.Audio.Streaming;

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
            _windowTitle = "GoombaCast connected to: yeah";
        }

        public MainWindowViewModel(AudioEngine audio)
        {
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
    }
}
