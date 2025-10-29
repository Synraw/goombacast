using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace GoombaCast.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly IDialogService? _dialogService;
        private readonly AudioEngine? _audioEngine;
        private readonly ILoggingService? _loggingService;
        private readonly CancellationTokenSource _cts = new();
        private DateTime _streamStartTime;
        private Timer? _streamTimer;
        private Timer? _iceStatsRefresh;
        private bool _disposed;

        [ObservableProperty]
        private string _windowTitle = "GoombaCast (Design Time)";

        [ObservableProperty]
        private int _volumeLevel;

        [ObservableProperty]
        private float _leftDb;

        [ObservableProperty]
        private float _rightDb;
        
        [ObservableProperty]
        private bool _isClipping;

        [ObservableProperty]
        private string _logLines = string.Empty;

        [ObservableProperty]
        private string _streamingTime = "00:00:00";

        [ObservableProperty]
        private string _listenerCount = "Listeners: N/A";

        public IAsyncRelayCommand? OpenSettingsCommand { get; }

        // Design time constructor
        public MainWindowViewModel()
        {

        }

        public MainWindowViewModel(
            AudioEngine audioEngine,
            IDialogService dialogService,
            ILoggingService loggingService)
        {
            _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));

            var settings = SettingsService.Default.Settings;
            _volumeLevel = settings.VolumeLevel;
            _windowTitle = $"GoombaCast: {settings.StreamName}";

            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);

            InitializeAudioHandlers();
            InitializeLogging();
            InitializeTimers();
            ScanInputDevices();
        }

        private void InitializeAudioHandlers()
        {
            App.Audio.LevelsAvailable += OnLevelsAvailable;
            App.Audio.ClippingDetected += OnClippingDetected;
            VolumeLevel = (int)App.Audio.GetGainLevel();
        }

        private void InitializeLogging()
        {
            if (_loggingService != null)
            {
                _loggingService.LogLineAdded += OnLogLineAdded;
            }
        }

        private void InitializeTimers()
        {
            _streamTimer = new Timer(500)
            {
                AutoReset = true
            };
            _streamTimer.Elapsed += OnStreamTimerElapsed;

            _iceStatsRefresh = new Timer(500)
            {
                AutoReset = true
            };
            _iceStatsRefresh.Elapsed += OnIceStatsRefreshElapsed;
            _iceStatsRefresh.Start();
        }

        private void ScanInputDevices()
        {
            foreach (var device in InputDevice.GetActiveInputDevices())
            {
                Logging.Log($"Found input device: {device}");
            }
        }

        private void OnLevelsAvailable(float left, float right)
        {
            LeftDb = left;
            RightDb = right;
        }
        
        private void OnClippingDetected(bool isClipping)
        {
            IsClipping = isClipping;
        }

        private void OnLogLineAdded(object? sender, string message)
        {
            WriteLineToLog(message);
        }

        private void OnStreamTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            var duration = DateTime.Now - _streamStartTime;
            StreamingTime = $"{duration:hh\\:mm\\:ss}";
        }

        private async void OnIceStatsRefreshElapsed(object? sender, ElapsedEventArgs e)
        {
            var icestats = await IcecastStats.GetStatsAsync().ConfigureAwait(false);

            if (App.Audio.IsBroadcasting)
            {
                ListenerCount = $"Listeners: {icestats?.GetListenerCount()}";
            }
        }

        public void WriteLineToLog(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            LogLines += $"{message}\n";
        }

        public void UpdateWindowTitle(string streamName)
        {
            WindowTitle = string.IsNullOrEmpty(streamName)
                ? "GoombaCast"
                : $"GoombaCast: {streamName}";
        }

        public void StartTimer()
        {
            _streamStartTime = DateTime.Now;
            _streamTimer?.Start();
        }

        public void StopTimer()
        {
            _streamTimer?.Stop();
            StreamingTime = "00:00:00";
        }

        partial void OnVolumeLevelChanged(int value)
        {
            var settings = SettingsService.Default.Settings;
            if (settings.VolumeLevel != value)
            {
                settings.VolumeLevel = value;
                SettingsService.Default.Save();
            }
            _audioEngine?.SetGainLevel(value);
        }

        private async Task OpenSettingsAsync()
        {
            if (_dialogService is null) return;
            await _dialogService.ShowSettingsDialogAsync()
                .ConfigureAwait(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cts.Cancel();
                    _streamTimer?.Dispose();
                    _iceStatsRefresh?.Dispose();
                    _cts.Dispose();

                    // Unsubscribe from events
                    if (_audioEngine != null)
                    {
                        _audioEngine.LevelsAvailable -= OnLevelsAvailable;
                        _audioEngine.ClippingDetected -= OnClippingDetected;
                    }

                    if (_loggingService != null)
                        _loggingService.LogLineAdded -= OnLogLineAdded;
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}