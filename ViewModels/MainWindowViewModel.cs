using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, INotifyPropertyChanged
    {
        [ObservableProperty]
        private string _windowTitle;

        private float _leftDb;
        private float _rightDb;

        public float LeftDb
        {
            get => _leftDb;
            private set { if (_leftDb != value) { _leftDb = value; OnPropertyChanged(); } }
        }

        public float RightDb
        {
            get => _rightDb;
            private set { if (_rightDb != value) { _rightDb = value; OnPropertyChanged(); } }
        }

        public MainWindowViewModel(AudioEngine audio)
        {
            // These callbacks are already marshalled to UI thread via CallbackContext
            audio.LevelsAvailable += (l, r) =>
            {
                LeftDb = l;
                RightDb = r;
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
