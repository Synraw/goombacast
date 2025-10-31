using Avalonia;
using Avalonia.Threading;
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
        private const float PeakFallRate = 15.0f;
        private const float UpdateInterval = 50.0f;
        private const float ProgressBarWidth = 275.0f;

        private readonly IDialogService? _dialogService;
        private readonly AudioEngine? _audioEngine;
        private readonly ILoggingService? _loggingService;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        private DateTime _streamStartTime;
        private Timer? _streamTimer;
        private Timer? _iceStatsRefresh;
        private Timer? _peakResetTimer;

        [ObservableProperty] private float _leftPeakDb = -90;
        [ObservableProperty] private float _rightPeakDb = -90;
        [ObservableProperty] private float _leftDb;
        [ObservableProperty] private float _rightDb;
        [ObservableProperty] private bool _isClipping;
        [ObservableProperty] private int _volumeLevel;
        
        [ObservableProperty] private string _windowTitle = "GoombaCast: (Design Time)";
        [ObservableProperty] private string _logLines = string.Empty;
        [ObservableProperty] private string _streamingTime = "00:00:00";
        [ObservableProperty] private string _listenerCount = "Listeners: N/A";
        
        [ObservableProperty] private bool _isStreaming;
        [ObservableProperty] private bool _isStreamButtonEnabled = true;
        [ObservableProperty] private bool _isListenerCountVisible;

        [ObservableProperty] private Thickness _leftPeakPosition = new(5, 0, 0, 0);
        [ObservableProperty] private Thickness _rightPeakPosition = new(5, 0, 0, 0);

        public IAsyncRelayCommand? OpenSettingsCommand { get; private set; }
        public IAsyncRelayCommand? ToggleStreamCommand { get; private set; }

        public MainWindowViewModel() { }

        public MainWindowViewModel(AudioEngine audioEngine, IDialogService dialogService, ILoggingService loggingService)
        {
            _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));

            InitializeViewModel();
            SetupCommands();
            SetupEventHandlers();
            InitializeTimers();
            ScanInputDevices();
        }

        private void InitializeViewModel()
        {
            var settings = SettingsService.Default.Settings;
            VolumeLevel = settings.VolumeLevel;
            WindowTitle = $"GoombaCast: {settings.StreamName}";
        }

        private void SetupCommands()
        {
            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
            ToggleStreamCommand = new AsyncRelayCommand(ToggleStream);
        }

        private void SetupEventHandlers()
        {
            App.Audio.LevelsAvailable += OnLevelsAvailable;
            App.Audio.ClippingDetected += OnClippingDetected;
            if (_loggingService != null)
            {
                _loggingService.LogLineAdded += OnLogLineAdded;
            }
            VolumeLevel = (int)App.Audio.GetGainLevel();
        }

        private void InitializeTimers()
        {
            _streamTimer = CreateTimer(500, OnStreamTimerElapsed);
            _iceStatsRefresh = CreateTimer(500, OnIceStatsRefreshElapsed, true);
            _peakResetTimer = CreateTimer(UpdateInterval, OnPeakUpdateTimerElapsed, true);
        }

        private Timer CreateTimer(double interval, ElapsedEventHandler handler, bool startImmediately = false)
        {
            var timer = new Timer(interval) { AutoReset = true };
            timer.Elapsed += handler;
            if (startImmediately) timer.Start();
            return timer;
        }

        private void UpdatePeakLevels(float leftDb, float rightDb)
        {
            Dispatcher.UIThread.Post(() =>
            {
                LeftDb = leftDb;
                RightDb = rightDb;
                LeftPeakDb = leftDb > LeftPeakDb ? leftDb : LeftPeakDb;
                RightPeakDb = rightDb > RightPeakDb ? rightDb : RightPeakDb;
            });
        }

        private void UpdatePeakFalloff(float decreaseAmount)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (LeftPeakDb > LeftDb)
                    LeftPeakDb = Math.Max(LeftDb, LeftPeakDb - decreaseAmount);
                if (RightPeakDb > RightDb)
                    RightPeakDb = Math.Max(RightDb, RightPeakDb - decreaseAmount);
            });
        }

        private double CalculatePeakPosition(double peakDb) 
            => 5 + (peakDb + 90) / 90 * ProgressBarWidth;

        private void OnLevelsAvailable(float left, float right) 
            => UpdatePeakLevels(left, right);
        
        private void OnPeakUpdateTimerElapsed(object? sender, ElapsedEventArgs e) 
            => UpdatePeakFalloff((PeakFallRate * UpdateInterval) / 1000.0f);

        private void OnStreamTimerElapsed(object? sender, ElapsedEventArgs e) 
            => StreamingTime = $"{(DateTime.Now - _streamStartTime):hh\\:mm\\:ss}";

        private async void OnIceStatsRefreshElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!App.Audio.IsBroadcasting) return;
            var icestats = await IcecastStats.GetStatsAsync().ConfigureAwait(false);
            ListenerCount = $"Listeners: {icestats?.GetListenerCount()}";
        }

        private void OnClippingDetected(bool isClipping) 
            => IsClipping = isClipping;
        
        private void OnLogLineAdded(object? sender, string message) 
            => WriteLineToLog(message);

        public void WriteLineToLog(string message)
        {
            if (!string.IsNullOrEmpty(message))
                LogLines += $"{message}\n";
        }

        public void UpdateWindowTitle(string streamName) 
            => WindowTitle = string.IsNullOrEmpty(streamName) ? "GoombaCast" : $"GoombaCast: {streamName}";

        private void StartTimer()
        {
            _streamStartTime = DateTime.Now;
            _streamTimer?.Start();
        }

        private void StopTimer()
        {
            _streamTimer?.Stop();
            StreamingTime = "00:00:00";
        }

        private void ScanInputDevices()
        {
            foreach (var device in InputDevice.GetActiveInputDevices())
                Logging.Log($"Found input device: {device}");
        }

        private async Task OpenSettingsAsync()
        {
            if (_dialogService != null)
                await _dialogService.ShowSettingsDialogAsync().ConfigureAwait(false);
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

        partial void OnLeftPeakDbChanged(float value) 
            => LeftPeakPosition = new Thickness(CalculatePeakPosition(value), 0, 0, 0);

        partial void OnRightPeakDbChanged(float value)
            => RightPeakPosition = new Thickness(CalculatePeakPosition(value), 0, 0, 0);

        private async Task ToggleStream()
        {
            var settings = SettingsService.Default.Settings;
            IsStreamButtonEnabled = false;

            try
            {
                if (!App.Audio.IsBroadcasting)
                {
                    await StartStreaming(settings.StreamName).ConfigureAwait(true);
                }
                else
                {
                    StopStreaming(settings.StreamName);
                }
            }
            catch (Exception ex)
            {
                string startOrStop = App.Audio.IsBroadcasting ? "stopping" : "starting";
                Logging.LogError($"Error {startOrStop} stream: {ex.Message}");
            }
            finally
            {
                IsStreamButtonEnabled = true;
            }
        }

        private async Task StartStreaming(string streamName)
        {
            await App.Audio.StartBroadcastAsync().ConfigureAwait(true);
            StartTimer();
            IsStreaming = true;
            IsListenerCountVisible = true;
            Logging.Log($"Now streaming to {streamName}");
        }

        private void StopStreaming(string streamName)
        {
            App.Audio.StopBroadcast();
            StopTimer();
            IsStreaming = false;
            IsListenerCountVisible = false;
            Logging.Log($"{streamName} stream stopped.");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _cts.Cancel();
                _streamTimer?.Dispose();
                _iceStatsRefresh?.Dispose();
                _peakResetTimer?.Dispose();
                _cts.Dispose();

                App.Audio.LevelsAvailable -= OnLevelsAvailable;
                App.Audio.ClippingDetected -= OnClippingDetected;
                if (_loggingService != null)
                    _loggingService.LogLineAdded -= OnLogLineAdded;

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