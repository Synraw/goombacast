using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using GoombaCast.Views;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace GoombaCast.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService = SettingsService.Default;
        private readonly AudioEngine _audioEngine;

        // Limiter Properties
        [ObservableProperty] private bool _limiterEnabled;
        [ObservableProperty] private float _limiterThreshold;

        // Server Properties
        [ObservableProperty] private string _currentProfileName = "No Profile Selected";

        // Recording Properties
        [ObservableProperty] private string _recordingDirectory = string.Empty;

        // Mixer Properties
        public ObservableCollection<AudioInputSource> InputSources { get; } = new();

        public SettingsWindowViewModel() 
        {
            // Design-time constructor
            _audioEngine = null!;
            
            var inputDevice = InputDevice.GetDefaultInputDevice();
            var outputDevice = OutputDevice.GetDefaultOutputDevice();
            InputSources.Add(new AudioInputSource(inputDevice.ToString(), inputDevice.Id, AudioEngine.AudioStreamType.Microphone));
            InputSources.Add(new AudioInputSource(outputDevice.ToString(), outputDevice.Id, AudioEngine.AudioStreamType.Loopback));
        }

        public SettingsWindowViewModel(AudioEngine audioEngine)
        {
            ArgumentNullException.ThrowIfNull(audioEngine);

            _audioEngine = audioEngine;

            LoadSettings();
            LoadInputSources();
        }

        private void LoadSettings()
        {
            var settings = _settingsService.Settings;

            // Audio settings
            LimiterEnabled = settings.LimiterEnabled;
            LimiterThreshold = settings.LimiterThreshold;

            if (settings.CurrentServer != null)
            {
                CurrentProfileName = settings.CurrentServer.ProfileName;
            } else {
                CurrentProfileName = "No Profile Selected";
            }

            // Recording settings
            RecordingDirectory = settings.RecordingDirectory ?? string.Empty;
        }

        private void LoadInputSources()
        {
            InputSources.CollectionChanged += InputSources_CollectionChanged;
            InputSources.Clear();
            foreach (var source in _audioEngine.InputSources)
            {
                InputSources.Add(source);
            }
        }

        private void InputSources_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Hook up property changed events for newly added items
            if (e.NewItems != null)
            {
                foreach (AudioInputSource source in e.NewItems)
                {
                    source.PropertyChanged += InputSource_PropertyChanged;
                }
            }

            // Unhook property changed events for removed items
            if (e.OldItems != null)
            {
                foreach (AudioInputSource source in e.OldItems)
                {
                    source.PropertyChanged -= InputSource_PropertyChanged;
                }
            }
        }

        private void InputSource_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AudioInputSource.IsSolo) && sender is AudioInputSource soloedSource)
            {
                // If this source was just set to solo, unsolo all others
                if (soloedSource.IsSolo)
                {
                    foreach (var source in InputSources)
                    {
                        if (source != soloedSource && source.IsSolo)
                        {
                            source.IsSolo = false;
                        }
                    }
                }
            }
        }

        partial void OnLimiterEnabledChanged(bool value)
        {
            var settings = _settingsService.Settings;
            settings.LimiterEnabled = value;
            _settingsService.Save();
            _audioEngine.SetLimiterEnabled(value);
        }

        partial void OnLimiterThresholdChanged(float value)
        {
            var settings = _settingsService.Settings;
            settings.LimiterThreshold = value;
            _settingsService.Save();
            _audioEngine.SetLimiterThreshold(value);
        }

        [RelayCommand]
        private async Task ManageServerProfiles()
        {
            var dialog = new ServerProfileDialog(_settingsService);
            await dialog.ShowDialog(GetParentWindow()!);

            LoadSettings();
            
            // Update the main window title with the new profile
            UpdateMainWindowTitle();
        }

        private void UpdateMainWindowTitle()
        {
            var settings = _settingsService.Settings;
            var profileName = settings.CurrentServer?.ProfileName;
            
            // Find the main window and update its title
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = desktop.MainWindow;
                if (mainWindow?.DataContext is MainWindowViewModel mainViewModel)
                {
                    mainViewModel.UpdateWindowTitle(profileName);
                }
            }
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
            var dialog = new DeviceSelectionDialog(_audioEngine);
            var result = await dialog.ShowDialog<DeviceSelectionResult?>(GetParentWindow()!);

            if (result != null && !string.IsNullOrEmpty(result.DeviceId))
            {
                try
                {
                    var source = _audioEngine.AddInputSource(result.DeviceId, result.StreamType);
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
                _audioEngine.RemoveInputSource(source);
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

    public class DeviceSelectionResult
    {
        public string DeviceId { get; set; } = string.Empty;
        public AudioEngine.AudioStreamType StreamType { get; set; }
    }
}
