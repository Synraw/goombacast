using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using GoombaCast.Views;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GoombaCast.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService = SettingsService.Default;

        public event EventHandler<string>? StreamNameChanged;

        // Limiter Properties
        [ObservableProperty] private bool _limiterEnabled;
        [ObservableProperty] private float _limiterThreshold;

        // Server Properties
        [ObservableProperty] private string _serverAddress = string.Empty;
        [ObservableProperty] private string _username = string.Empty;
        [ObservableProperty] private string _password = string.Empty;
        [ObservableProperty] private string _streamName = string.Empty;

        // Recording Properties
        [ObservableProperty] private string _recordingDirectory = string.Empty;

        // Mixer Properties
        public ObservableCollection<AudioInputSource> InputSources { get; } = new();

        public SettingsWindowViewModel()
        {
            LoadSettings();
            LoadInputSources();
        }

        private void LoadSettings()
        {
            var settings = _settingsService.Settings;

            // Audio settings
            LimiterEnabled = settings.LimiterEnabled;
            LimiterThreshold = settings.LimiterThreshold;

            // Server settings
            ServerAddress = settings.ServerAddress ?? string.Empty;
            Username = settings.UserName ?? string.Empty;
            Password = settings.Password ?? string.Empty;
            StreamName = settings.StreamName ?? string.Empty;

            // Recording settings
            RecordingDirectory = settings.RecordingDirectory ?? string.Empty;
        }

        private void LoadInputSources()
        {
            InputSources.Clear();
            foreach (var source in App.Audio.InputSources)
            {
                InputSources.Add(source);
            }
        }

        partial void OnLimiterEnabledChanged(bool value)
        {
            var settings = _settingsService.Settings;
            settings.LimiterEnabled = value;
            _settingsService.Save();
            App.Audio.SetLimiterEnabled(value);
        }

        partial void OnLimiterThresholdChanged(float value)
        {
            var settings = _settingsService.Settings;
            settings.LimiterThreshold = value;
            _settingsService.Save();
            App.Audio.SetLimiterThreshold(value);
        }

        partial void OnServerAddressChanged(string value)
        {
            var settings = _settingsService.Settings;
            settings.ServerAddress = value;
            _settingsService.Save();
        }

        partial void OnUsernameChanged(string value)
        {
            var settings = _settingsService.Settings;
            settings.UserName = value;
            _settingsService.Save();
        }

        partial void OnPasswordChanged(string value)
        {
            var settings = _settingsService.Settings;
            settings.Password = value;
            _settingsService.Save();
        }

        partial void OnStreamNameChanged(string value)
        {
            var settings = _settingsService.Settings;
            settings.StreamName = value;
            StreamNameChanged?.Invoke(this, value);
            _settingsService.Save();
        }

        [RelayCommand]
        private async Task SelectRecordingDirectory()
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is 
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Recording Directory",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                RecordingDirectory = folders[0].Path.LocalPath;
                var settings = _settingsService.Settings;
                settings.RecordingDirectory = RecordingDirectory;
                _settingsService.Save();
            }
        }

        [RelayCommand]
        private async Task AddInputSource()
        {
            // Show device selection dialog
            var dialog = new DeviceSelectionDialog();
            var result = await dialog.ShowDialog<DeviceSelectionResult?>(GetParentWindow()!);

            if (result != null && !string.IsNullOrEmpty(result.DeviceId))
            {
                try
                {
                    var source = App.Audio.AddInputSource(result.DeviceId, result.StreamType);
                    InputSources.Add(source);
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Failed to add input source: {ex.Message}");
                }
            }
        }

        [RelayCommand]
        private void RemoveInputSource(AudioInputSource source)
        {
            if (source != null)
            {
                App.Audio.RemoveInputSource(source);
                InputSources.Remove(source);
            }
        }

        private Avalonia.Controls.Window? GetParentWindow()
        {
            return Avalonia.Application.Current?.ApplicationLifetime is 
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.Windows.FirstOrDefault(w => w is Views.SettingsWindow)
                : null;
        }
    }

    // Helper class for device selection result
    public class DeviceSelectionResult
    {
        public string DeviceId { get; set; } = string.Empty;
        public AudioEngine.AudioStreamType StreamType { get; set; }
    }
}
