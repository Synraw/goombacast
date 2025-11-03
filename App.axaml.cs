using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GoombaCast.Services;
using GoombaCast.ViewModels;
using GoombaCast.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast
{
    public partial class App : Application, IAsyncDisposable
    {
        private ServiceProvider? _serviceProvider;
        private static AudioEngine? _audio;
        
        public static AudioEngine Audio => _audio ?? 
            throw new InvalidOperationException("AudioEngine not initialized");

        public override void Initialize() 
            => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                return;
            }

            try
            {
                ConfigureServices(desktop);
                ConfigureMainWindow(desktop);
                InitializeAudioEngine();
            }
            catch (Exception ex)
            {
                Logging.LogError($"Application initialization failed: {ex}");
                throw;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ConfigureServices(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();

            // Register services
            services.AddSingleton<ILoggingService, LoggingService>();
            services.AddSingleton<AudioEngine>(sp => 
            {
                var uiCtx = SynchronizationContext.Current ?? 
                    throw new InvalidOperationException("UI SynchronizationContext not available");
                return new AudioEngine(uiCtx);
            });
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<SettingsWindowViewModel>();
            services.AddSingleton<IDialogService>(sp =>
            {
                var mainWindow = desktop.MainWindow ?? 
                    throw new InvalidOperationException("MainWindow not initialized");
                return new DialogService(sp, mainWindow);
            });

            _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            var loggingService = _serviceProvider.GetRequiredService<ILoggingService>();
            Logging.Initialize(loggingService);

            _audio = _serviceProvider.GetRequiredService<AudioEngine>();

            DisableAvaloniaDataAnnotationValidation();
        }

        private void ConfigureMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_serviceProvider == null)
                throw new InvalidOperationException("ServiceProvider not initialized");

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            mainWindow.DataContext = viewModel;

            desktop.Exit += OnApplicationExit;
        }

        private async void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error during application shutdown: {ex}");
            }
        }

        private void InitializeAudioEngine()
        {
            if (_audio == null)
                throw new InvalidOperationException("AudioEngine not initialized");

            try
            {
                var settings = SettingsService.Default.Settings;

                // Configure limiter
                _audio.SetLimiterEnabled(settings.LimiterEnabled);
                _audio.SetLimiterThreshold(settings.LimiterThreshold);

                // Load saved input sources from settings
                if (settings.InputSources != null && settings.InputSources.Count > 0)
                {
                    foreach (var sourceConfig in settings.InputSources)
                    {
                        try
                        {
                            var source = _audio.AddInputSource(sourceConfig.DeviceId, sourceConfig.StreamType);
                            source.Volume = sourceConfig.Volume;
                            Logging.Log($"Restored input source: {source.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logging.LogWarning($"Failed to restore input source {sourceConfig.DeviceId}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // No saved sources, add default device
                    AddDefaultInputSource(settings);
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to initialize audio engine: {ex}");
                throw;
            }
        }

        private void AddDefaultInputSource(Services.AppSettings settings)
        {
            try
            {
                if (settings.AudioStreamType == AudioEngine.AudioStreamType.Microphone)
                {
                    var deviceId = settings.MicrophoneDeviceId;
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        var defaultDevice = Models.Audio.Streaming.InputDevice.GetDefaultInputDevice();
                        deviceId = defaultDevice.Id;
                    }
                    _audio!.AddInputSource(deviceId, AudioEngine.AudioStreamType.Microphone);
                    Logging.Log("Added default microphone input source");
                }
                else
                {
                    var deviceId = settings.LoopbackDeviceId;
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        var defaultDevice = Models.Audio.Streaming.OutputDevice.GetDefaultOutputDevice();
                        deviceId = defaultDevice.Id;
                    }
                    _audio!.AddInputSource(deviceId, AudioEngine.AudioStreamType.Loopback);
                    Logging.Log("Added default loopback input source");
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to add default input source: {ex.Message}");
                throw;
            }
        }

        private static void DisableAvaloniaDataAnnotationValidation()
        {
            var dataValidationPluginsToRemove = BindingPlugins.DataValidators
                .OfType<DataAnnotationsValidationPlugin>()
                .ToArray();

            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_serviceProvider == null) return;

            try
            {
                await Task.Run(() => _audio?.Dispose());

                if (_serviceProvider is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    _serviceProvider.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error disposing application resources: {ex}");
                throw;
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}