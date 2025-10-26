using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Audio.Streaming;
using GoombaCast.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace GoombaCast.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private ObservableCollection<InputDevice> _availableMicrophones = new();

        [ObservableProperty]
        private InputDevice? _selectedMicrophone;

        [ObservableProperty]
        private int _selectedMicrophoneIndex = -1;

        [ObservableProperty]
        private string _serverAddress;

        [ObservableProperty]
        private string _streamName;

        [ObservableProperty]
        private string _streamUrl;

        [ObservableProperty]
        private string _username = "user";

        [ObservableProperty]    
        private string _password = "password";

        public SettingsWindowViewModel()
        {
            var settings = SettingsService.Default.Settings;

            ServerAddress = settings.ServerAddress ?? "http://your.icecast.server:8080/web.mp3";
            StreamName = settings.StreamName ?? "My Local Icecast Stream";
            StreamUrl = settings.StreamUrl ?? "http://localhost";
            Username = settings.UserName ?? "user";
            Password = settings.Password ?? "password";

            var devices = InputDevice.GetActiveInputDevices();
            AvailableMicrophones = new ObservableCollection<InputDevice>(devices);

            var savedId = settings.InputDeviceId;
            var match = savedId is null ? null : devices.FirstOrDefault(d => d.Id == savedId);

            if (match is not null)
                SelectedMicrophone = match;
            else if (AvailableMicrophones.Count > 0)
                SelectedMicrophone = AvailableMicrophones.First();
        }

        partial void OnSelectedMicrophoneChanged(InputDevice? value)
        {
            App.Audio.ChangeInputDevice(value?.Id ?? string.Empty);
            SettingsService.Default.Settings.InputDeviceId = value?.Id;
            SettingsService.Default.Save();
        }

        partial void OnServerAddressChanged(string value)
        {
            SettingsService.Default.Settings.ServerAddress = value;
            SettingsService.Default.Save();
        }

        partial void OnUsernameChanged(string value)
        {
            SettingsService.Default.Settings.UserName = value;
            SettingsService.Default.Save();
        }

        partial void OnPasswordChanged(string value)
        {
            SettingsService.Default.Settings.Password = value;
            SettingsService.Default.Save();
        }

        partial void OnStreamNameChanged(string value)
        {
            SettingsService.Default.Settings.StreamName = value;
            SettingsService.Default.Save();
        }

        partial void OnStreamUrlChanging(string value)
        {
            SettingsService.Default.Settings.StreamUrl = value;
            SettingsService.Default.Save();
        }
    }
}
