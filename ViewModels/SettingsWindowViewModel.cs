using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
        private string _username;

        [ObservableProperty]
        private string _password;

        [ObservableProperty]
        private bool _limiterEnabled;

        [ObservableProperty]
        private float _limiterThreshold;

        [ObservableProperty]
        private string _recordingDirectory;

        public SettingsWindowViewModel()
        {
            var settings = SettingsService.Default.Settings;

            ServerAddress = settings.ServerAddress ?? "http://localhost:8080/";
            StreamName = settings.StreamName ?? "My Local Icecast Stream";
            Username = settings.UserName ?? "user";
            Password = settings.Password ?? "password";

            LimiterEnabled = settings.LimiterEnabled;
            LimiterThreshold = settings.LimiterThreshold;

            var devices = InputDevice.GetActiveInputDevices();
            AvailableMicrophones = new ObservableCollection<InputDevice>(devices);

            var savedId = settings.InputDeviceId;
            SelectedMicrophone = savedId is null ? 
                AvailableMicrophones.FirstOrDefault() : 
                devices.FirstOrDefault(d => d.Id == savedId);

            RecordingDirectory = settings.RecordingDirectory;

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
                case nameof(LimiterEnabled):
                    settings.LimiterEnabled = LimiterEnabled;
                    App.Audio.SetLimiterEnabled(LimiterEnabled);
                    break;
                case nameof(LimiterThreshold):
                    settings.LimiterThreshold = LimiterThreshold;
                    App.Audio.SetLimiterThreshold(LimiterThreshold);
                    break;
                case nameof(RecordingDirectory):
                    settings.RecordingDirectory = RecordingDirectory;
                    break;
            }
            
            SettingsService.Default.Save();
        }

        [RelayCommand]
        private async Task SelectRecordingDirectory()
        {
            if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = desktop.MainWindow!;

                string? recordingDir = RecordingDirectory;
                string? dirName = recordingDir != null ? Path.GetDirectoryName(recordingDir) : null;
                string? parentPath = dirName != null ? Directory.GetParent(dirName)?.FullName : recordingDir;

                IStorageFolder? startFolder = parentPath != null
                    ? await mainWindow.StorageProvider.TryGetFolderFromPathAsync(parentPath)
                    : null;

                var folders = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Recording Directory",
                    AllowMultiple = false,
                    SuggestedStartLocation = startFolder
                });

                var folder = folders.FirstOrDefault();
                if (folder != null)
                {
                    RecordingDirectory = folder.Path.LocalPath;
                    var settings = SettingsService.Default.Settings;
                    settings.RecordingDirectory = RecordingDirectory;
                    SettingsService.Default.Save();
                }
            }
        }
    }
}
