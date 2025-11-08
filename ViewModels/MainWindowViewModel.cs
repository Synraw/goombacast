using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoombaCast.Extensions;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using GoombaCast.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace GoombaCast.ViewModels
{
    /// <summary>
    /// ViewModel for the main window of GoombaCast, handling audio streaming controls, metering, and UI state.
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        // Meter display and update constants
        private const float PeakFallRate = 15.0f;         // Rate at which peak indicators fall (dB/sec)
        private const float PeakUpdateInterval = 50.0f;   // Update interval for peak meters (ms)
        private const float ProgressBarWidth = 275.0f;    // Width of the VU meter progress bars

        // Timer intervals in milliseconds
        private const int PeakTimerInterval = (int)PeakUpdateInterval;
        private const int IceStatsTimerInterval = 500;    // Icecast stats refresh rate
        private const int StreamingTimerInterval = 500;   // Streaming time display update rate

        // Dependency-injected services
        private readonly AudioEngine _audioEngine;
        private readonly ILoggingService _loggingService;
        private readonly IServiceProvider _serviceProvider;

        // Streaming state
        private DateTime _streamStartTime;
        private Timer? _streamTimer;        // Updates streaming duration display
        private Timer? _iceStatsRefresh;    // Updates Icecast listener count
        private Timer? _peakResetTimer;     // Controls peak meter falloff

        // Dispose flag
        private bool _disposed;

        // Observable audio metering properties
        [ObservableProperty] private float _leftPeakDb = -90;     // Left channel peak hold
        [ObservableProperty] private float _rightPeakDb = -90;    // Right channel peak hold
        [ObservableProperty] private float _leftDb;               // Left channel current level
        [ObservableProperty] private float _rightDb;              // Right channel current level
        [ObservableProperty] private bool _isClipping;            // Audio clipping indicator
        [ObservableProperty] private int _volumeLevel;            // Input gain control
        [ObservableProperty] private bool _isLogVisible = true;   // Hide and show log window

        // Observable UI state properties
        [ObservableProperty] private string _windowTitle = "GoombaCast: (Design Time)";
        [ObservableProperty] private string _logLines = string.Empty;
        [ObservableProperty] private string _streamingTime = "00:00:00";
        [ObservableProperty] private string _listenerCount = "Listeners: N/A";

        // Observable streaming state properties
        [ObservableProperty] private bool _isStreaming;
        [ObservableProperty] private bool _isStreamButtonEnabled = true;
        [ObservableProperty] private bool _isListenerCountVisible;

        // Observable peak meter UI positions
        [ObservableProperty] private Thickness _leftPeakPosition = new(5, 0, 0, 0);
        [ObservableProperty] private Thickness _rightPeakPosition = new(5, 0, 0, 0);

        // Observable recording state properties
        [ObservableProperty] private bool _isRecording;
        [ObservableProperty] private string _recordButtonText = "Start Recording";
        [ObservableProperty] private bool _isRecordButtonEnabled = true;

        /// <summary>
        /// Default constructor for design-time use
        /// </summary>
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public MainWindowViewModel() { }
#pragma warning restore CS8618 

        /// <summary>
        /// Initializes a new instance of the MainWindowViewModel with required services
        /// </summary>
        /// <param name="audioEngine">The audio engine service for stream handling</param>
        /// <param name="loggingService">The logging service for application events</param>
        /// <param name="serviceProvider">Service provider for resolving dependencies</param>
        /// <exception cref="ArgumentNullException">Thrown if any required service is null</exception>
        public MainWindowViewModel(
            AudioEngine audioEngine,
            ILoggingService loggingService,
            IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(audioEngine, nameof(audioEngine));
            ArgumentNullException.ThrowIfNull(loggingService, nameof(loggingService));
            ArgumentNullException.ThrowIfNull(serviceProvider, nameof(serviceProvider));

            _audioEngine = audioEngine;
            _loggingService = loggingService;
            _serviceProvider = serviceProvider;

            InitializeViewModel();
            SetupEventHandlers();
            InitializeTimers();
            ScanInputDevices();
            ScanOutputDevices();
        }

        /// <summary>
        /// Initializes view model properties from settings
        /// </summary>
        private void InitializeViewModel()
        {
            var settings = SettingsService.Default.Settings;
            VolumeLevel = settings.VolumeLevel;
            WindowTitle = $"GoombaCast: {settings.StreamName}";
            IsLogVisible = !settings.HideLog;
        }

        /// <summary>
        /// Sets up event handlers for audio engine and logging events
        /// </summary>
        private void SetupEventHandlers()
        {
            _audioEngine.LevelsAvailable += OnLevelsAvailable;
            _audioEngine.ClippingDetected += OnClippingDetected;
            _loggingService.LogLineAdded += OnLogLineAdded;
            VolumeLevel = (int)_audioEngine.GetMasterGainLevel();
        }

        /// <summary>
        /// Initializes timers for UI updates
        /// </summary>
        private void InitializeTimers()
        {
            _streamTimer = CreateTimer(StreamingTimerInterval, OnStreamTimerElapsed);
            _iceStatsRefresh = CreateTimer(IceStatsTimerInterval, OnIceStatsRefreshElapsed, true);
            _peakResetTimer = CreateTimer(PeakTimerInterval, OnPeakUpdateTimerElapsed, true);
        }

        /// <summary>
        /// Creates and configures a timer with specified parameters
        /// </summary>
        private Timer CreateTimer(double interval, ElapsedEventHandler handler, bool startImmediately = false)
        {
            var timer = new Timer(interval) { AutoReset = true };
            timer.Elapsed += handler;
            if (startImmediately) timer.Start();
            return timer;
        }

        /// <summary>
        /// Updates the peak level meters with new audio levels
        /// </summary>
        private void UpdatePeakLevels(float leftDb, float rightDb)
        {
            LeftDb = leftDb;
            RightDb = rightDb;
            LeftPeakDb = leftDb > LeftPeakDb ? leftDb : LeftPeakDb;
            RightPeakDb = rightDb > RightPeakDb ? rightDb : RightPeakDb;
        }

        /// <summary>
        /// Updates the peak falloff animation
        /// </summary>
        private void UpdatePeakFalloff(float decreaseAmount)
        {
            if (LeftPeakDb > LeftDb)
                LeftPeakDb = Math.Max(LeftDb, LeftPeakDb - decreaseAmount);
            if (RightPeakDb > RightDb)
                RightPeakDb = Math.Max(RightDb, RightPeakDb - decreaseAmount);

            LeftDb = Math.Max(-90.0f, LeftDb - decreaseAmount);
            RightDb = Math.Max(-90.0f, RightDb - decreaseAmount);
        }

        /// <summary>
        /// Calculates the position of the peak indicator on the VU meter
        /// </summary>
        private float CalculatePeakPosition(float peakDb)
        {
            if (peakDb < -90.0f || float.IsNaN(peakDb))
                return 5.0f;
            return 5.0f + (peakDb + 90.0f) / 90.0f * ProgressBarWidth;
        }

        // Event handlers
        private void OnLevelsAvailable(float left, float right)
            => UpdatePeakLevels(left, right);

        private void OnPeakUpdateTimerElapsed(object? sender, ElapsedEventArgs e)
            => UpdatePeakFalloff(PeakFallRate * (PeakUpdateInterval / 1000.0f));

        private void OnStreamTimerElapsed(object? sender, ElapsedEventArgs e)
            => StreamingTime = $"{DateTime.Now - _streamStartTime:hh\\:mm\\:ss}";

        private async void OnIceStatsRefreshElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_audioEngine.IsBroadcasting) return;
            var icestats = await IcecastStats.GetStatsAsync().ConfigureAwait(false);
            ListenerCount = $"Listeners: {icestats?.GetListenerCount()}";
        }

        private void OnClippingDetected(bool isClipping)
            => IsClipping = isClipping;

        private void OnLogLineAdded(object? sender, string message)
            => WriteLineToLog(message);

        /// <summary>
        /// Adds a message to the log window and scrolls to the bottom
        /// </summary>
        public void WriteLineToLog(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            LogLines += $"{message}\n";

            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var logWindow = desktop.MainWindow?.FindControl<TextBox>("LogWindow");
                    logWindow?.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Updates the window title with the stream name
        /// </summary>
        public void UpdateWindowTitle(string? streamName)
            => WindowTitle = string.IsNullOrEmpty(streamName) ? "GoombaCast" : $"GoombaCast: {streamName}";

        /// <summary>
        /// Starts the streaming timer
        /// </summary>
        private void StartTimer()
        {
            _streamStartTime = DateTime.Now;
            _streamTimer?.Start();
        }

        /// <summary>
        /// Stops the streaming timer
        /// </summary>
        private void StopTimer()
        {
            _streamTimer?.Stop();
            StreamingTime = "00:00:00";
        }

        /// <summary>
        /// Scans and logs available input devices
        /// </summary>
        private void ScanInputDevices()
        {
            ScanDevices(InputDevice.GetActiveInputDevices());
        }

        /// <summary>
        /// Scans and logs available output devices
        /// </summary>
        private void ScanOutputDevices()
        {
            ScanDevices(OutputDevice.GetActiveOutputDevices());
        }

        /// <summary>
        /// Scans and logs devices of a given type
        /// </summary>
        /// <typeparam name="T">The type of the device.</typeparam>
        /// <param name="devices">The collection of devices to scan.</param>
        private void ScanDevices<T>(IEnumerable<T> devices)
        {
            foreach (var device in devices)
            {
                string deviceType = device switch
                {
                    InputDevice => "input",
                    OutputDevice => "output",
                    _ => "unknown"
                };
                Logging.Log($"Found {deviceType} device: {device}");
            }
        }

        /// <summary>
        /// Opens the settings dialog
        /// </summary>
        [RelayCommand]
        private async Task OpenSettingsAsync()
        {
            var viewModel = _serviceProvider.GetRequiredService<SettingsWindowViewModel>();

            var dialog = new SettingsWindow
            {
                DataContext = viewModel
            };

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Subscribe to stream name changes
                void OnStreamNameChanged(object? s, string name) => UpdateWindowTitle(name);
                viewModel.StreamNameChanged += OnStreamNameChanged;

                try
                {
                    await dialog.ShowDialog(desktop.MainWindow!).ConfigureAwait(false);
                }
                finally
                {
                    // Clean up event subscription
                    viewModel.StreamNameChanged -= OnStreamNameChanged;
                }
            }
        }

        // Generated property change handlers
        partial void OnVolumeLevelChanged(int value)
        {
            var settings = SettingsService.Default.Settings;
            if (settings.VolumeLevel != value)
            {
                settings.VolumeLevel = value;
                SettingsService.Default.Save();
            }
            _audioEngine?.SetMasterGainLevel(value);
        }

        partial void OnLeftPeakDbChanged(float value)
            => LeftPeakPosition = new Thickness(CalculatePeakPosition(value), 0, 0, 0);

        partial void OnRightPeakDbChanged(float value)
            => RightPeakPosition = new Thickness(CalculatePeakPosition(value), 0, 0, 0);

        /// <summary>
        /// Toggles the streaming state between starting and stopping
        /// </summary>
        [RelayCommand]
        private async Task ToggleStream()
        {
            var settings = SettingsService.Default.Settings;
            IsStreamButtonEnabled = false;

            try
            {
                if (!_audioEngine.IsBroadcasting)
                    await StartStreaming(settings.StreamName).ConfigureAwait(true);
                else
                    await StopStreaming(settings.StreamName).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                string startOrStop = _audioEngine.IsBroadcasting ? "stopping" : "starting";
                Logging.LogError($"Error {startOrStop} stream: {ex.Message}");
            }
            finally
            {
                IsStreamButtonEnabled = true;
            }
        }

        /// <summary>
        /// Starts the audio stream
        /// </summary>
        private async Task StartStreaming(string streamName)
        {
            if (_audioEngine.InputSources.Count == 0)
            {
                Logging.LogWarning("No input sources available to start streaming.");
                return;
            }

            await _audioEngine.StartBroadcastAsync().ConfigureAwait(true);

            StartTimer();
            IsStreaming = true;
            IsListenerCountVisible = true;
            Logging.Log($"Now streaming to {streamName}");
        }

        /// <summary>
        /// Stops the audio stream
        /// </summary>
        private async Task StopStreaming(string streamName)
        {
            await _audioEngine.StopBroadcast();
            StopTimer();
            IsStreaming = false;
            IsListenerCountVisible = false;
            Logging.Log($"{streamName} stream stopped.");
        }

        /// <summary>
        /// Toggles the recording state between starting and stopping
        /// </summary>
        [RelayCommand]
        private void ToggleRecord()
        {
            if (_audioEngine.InputSources.Count == 0)
            {
                Logging.LogWarning("No input sources available to start recording.");
                return;
            }

            var settings = SettingsService.Default.Settings;
            IsRecordButtonEnabled = false;

            try
            {
                if (!_audioEngine!.IsRecording)
                {
                    StartRecording();
                }
                else
                {
                    StopRecording();
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error {(_audioEngine!.IsRecording ? "stopping" : "starting")} recording: {ex.Message}");
            }
            finally
            {
                IsRecordButtonEnabled = true;
            }
        }

        /// <summary>
        /// Starts the recording
        /// </summary>
        private void StartRecording()
        {
            var settings = SettingsService.Default.Settings;
            _audioEngine?.StartRecording(settings.RecordingDirectory);
            IsRecording = true;
            RecordButtonText = "Stop Recording";
        }

        /// <summary>
        /// Stops the recording
        /// </summary>
        private void StopRecording()
        {
            _audioEngine?.StopRecording();
            IsRecording = false;
            RecordButtonText = "Start Recording";
        }

        /// <summary>
        /// Disposes of managed resources
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _streamTimer?.Dispose();
                _iceStatsRefresh?.Dispose();
                _peakResetTimer?.Dispose();

                _audioEngine.LevelsAvailable -= OnLevelsAvailable;
                _audioEngine.ClippingDetected -= OnClippingDetected;
                _loggingService.LogLineAdded -= OnLogLineAdded;

                _disposed = true;
            }
        }

        /// <summary>
        /// Implements IDisposable
        /// </summary>
        public new void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}