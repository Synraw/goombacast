using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using GoombaCast.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GoombaCast.Audio.Streaming;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, INotifyPropertyChanged
    {
        [ObservableProperty]
        private string _windowTitle;

        [ObservableProperty]
        private int _volumeLevel = 0;

        [ObservableProperty]
        private float _leftDb;

        [ObservableProperty]
        private float _rightDb;

        //public float LeftDb
        //{
        //    get => _leftDb;
        //    private set { if (_leftDb != value) { _leftDb = value; OnPropertyChanged(); } }
        //}

        //public float RightDb
        //{
        //    get => _rightDb;
        //    private set { if (_rightDb != value) { _rightDb = value; OnPropertyChanged(); } }
        //}

        [ObservableProperty]
        private string _logLines = string.Empty;
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
                _logLines += $"Found input device: {item}\n";
            }

            _windowTitle = "GoombaCast connected to: yeah";
        }
     
        //public event PropertyChangedEventHandler? PropertyChanged;
        //private void OnPropertyChanged([CallerMemberName] string? name = null)
        //    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
