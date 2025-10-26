using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace GoombaCast.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        public event EventHandler<string>? StreamNameChanged;

        [ObservableProperty]
        private ObservableCollection<InputDevice> _availableMicrophones = [];

        [ObservableProperty]
        private InputDevice? _selectedMicrophone;

        [ObservableProperty]
        private string _serverAddress;

        [ObservableProperty]
        private string _streamName;

        [ObservableProperty]
        private string _streamUrl;

        [ObservableProperty]
        private string _username;

        [ObservableProperty]
        private string _password;

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
            SelectedMicrophone = savedId is null ? 
                AvailableMicrophones.FirstOrDefault() : 
                devices.FirstOrDefault(d => d.Id == savedId);

            PropertyChanged += (s, e) => UpdateSetting(e.PropertyName!);
        }

        private void UpdateSetting(string propertyName)
        {
            var settings = SettingsService.Default.Settings;

            switch (propertyName)
            {
                case nameof(SelectedMicrophone):
                    App.Audio.ChangeInputDevice(SelectedMicrophone?.Id ?? string.Empty);
                    settings.InputDeviceId = SelectedMicrophone?.Id;
                    break;
                case nameof(ServerAddress):
                    settings.ServerAddress = ServerAddress;
                    break;
                case nameof(Username):
                    settings.UserName = Username;
                    break;
                case nameof(Password):
                    settings.Password = Password;
                    break;
                case nameof(StreamName):
                    settings.StreamName = StreamName;
                    StreamNameChanged?.Invoke(this, StreamName);
                    break;
                case nameof(StreamUrl):
                    settings.StreamUrl = StreamUrl;
                    break;
            }
            
            SettingsService.Default.Save();
        }
    }
}
