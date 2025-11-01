using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static GoombaCast.Services.AudioEngine;

namespace GoombaCast.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        public event EventHandler<string>? StreamNameChanged;

        [ObservableProperty]
        private ObservableCollection<InputDevice> _availableMicrophones = [];

        [ObservableProperty]
        private ObservableCollection<OutputDevice> _availableAudioOutputs = [];

        [ObservableProperty]
        private InputDevice? _selectedMicrophone;

        [ObservableProperty]
        private OutputDevice? _selectedAudioLoopback;

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

        private bool _useLoopback;

        public bool UseLoopback
        {
            get => _useLoopback;
            set
            {
                if (SetProperty(ref _useLoopback, value))
                {
                    var settings = SettingsService.Default.Settings;
                    var newType = value ? AudioStreamType.Loopback : AudioStreamType.Microphone;
                    
                    settings.AudioStreamType = newType;

                    App.Audio.RecreateAudioStream(newType);

                    if (value)
                    {
                        App.Audio.ChangeDevice(SelectedAudioLoopback?.Id ?? string.Empty);
                        settings.LoopbackDeviceId = SelectedAudioLoopback?.Id;
                    }
                    else
                    {
                        App.Audio.ChangeDevice(SelectedMicrophone?.Id ?? string.Empty);
                        settings.MicrophoneDeviceId = SelectedMicrophone?.Id;
                    }
                    
                    SettingsService.Default.Save();
                }
            }
        }

        public SettingsWindowViewModel()
        {
            var settings = SettingsService.Default.Settings;

            ServerAddress = settings.ServerAddress ?? "http://localhost:8080/";
            StreamName = settings.StreamName ?? "My Local Icecast Stream";
            Username = settings.UserName ?? "user";
            Password = settings.Password ?? "password";
            LimiterEnabled = settings.LimiterEnabled;
            LimiterThreshold = settings.LimiterThreshold;
            RecordingDirectory = settings.RecordingDirectory;

            InitializeDevices(settings);

            PropertyChanged += (s, e) => UpdateSetting(e.PropertyName!);
        }

        private void InitializeDevices(AppSettings settings)
        {
            var inputDevices = InputDevice.GetActiveInputDevices();
            var outputDevices = OutputDevice.GetActiveOutputDevices();

            AvailableMicrophones = new ObservableCollection<InputDevice>(inputDevices);
            AvailableAudioOutputs = new ObservableCollection<OutputDevice>(outputDevices);

            SelectedMicrophone = settings.MicrophoneDeviceId != null
                ? inputDevices.FirstOrDefault(d => d.Id == settings.MicrophoneDeviceId)
                : inputDevices.FirstOrDefault();

            SelectedAudioLoopback = settings.LoopbackDeviceId != null
                ? outputDevices.FirstOrDefault(d => d.Id == settings.LoopbackDeviceId)
                : outputDevices.FirstOrDefault();

            UseLoopback = settings.AudioStreamType == AudioStreamType.Loopback;
        }

        private void UpdateSetting(string propertyName)
        {
            var settings = SettingsService.Default.Settings;

            switch (propertyName)
            {
                case nameof(SelectedMicrophone):
                    if (!UseLoopback && SelectedMicrophone != null)
                    {
                        settings.MicrophoneDeviceId = SelectedMicrophone.Id;
                        App.Audio.ChangeDevice(SelectedMicrophone.Id);
                    }
                    break;
                case nameof(SelectedAudioLoopback):
                    if (UseLoopback && SelectedAudioLoopback != null)
                    {
                        settings.LoopbackDeviceId = SelectedAudioLoopback.Id;
                        App.Audio.ChangeDevice(SelectedAudioLoopback.Id);
                    }
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
