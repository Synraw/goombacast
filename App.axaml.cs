using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using GoombaCast.ViewModels;
using GoombaCast.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast
{
    public partial class App : Application, IAsyncDisposable
    {
        private ServiceProvider? _serviceProvider;

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
            services.AddTransient<SettingsWindowViewModel>();
            services.AddSingleton<MainWindowViewModel>();

            _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            var loggingService = _serviceProvider.GetRequiredService<ILoggingService>();
            Logging.Initialize(loggingService);

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

            var audioEngine = _serviceProvider.GetRequiredService<AudioEngine>();
            InitializeAudioEngine(audioEngine);

            desktop.Exit += OnApplicationExit;
        }

        private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
        {
            try
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error during application shutdown: {ex}");
            }
        }

        private void InitializeAudioEngine(AudioEngine audioEngine)
        {
            ArgumentNullException.ThrowIfNull(audioEngine);

            try
            {
                var settings = SettingsService.Default.Settings;

                // Configure limiter
                audioEngine.SetLimiterEnabled(settings.LimiterEnabled);
                audioEngine.SetLimiterThreshold(settings.LimiterThreshold);

                // Restore or initialize input sources
                RestoreInputSources(settings, audioEngine);
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to initialize audio engine: {ex}");
                throw;
            }
        }

        private void RestoreInputSources(AppSettings settings, AudioEngine audioEngine)
        {
            if (settings.InputSources != null && settings.InputSources.Count > 0)
            {
                RestoreSavedInputSources(settings.InputSources, audioEngine);
            }
            else
            {
                AddDefaultInputSource(audioEngine);
            }
        }

        private void RestoreSavedInputSources(List<AppSettings.InputSourceConfig> sourceConfigs, AudioEngine audioEngine)
        {
            foreach (var sourceConfig in sourceConfigs.ToList())
            {
                try
                {
                    var source = audioEngine.AddInputSource(sourceConfig.DeviceId, sourceConfig.StreamType);
                    source.Volume = sourceConfig.Volume;
                    source.IsMuted = sourceConfig.IsMuted;
                    source.IsSolo = sourceConfig.IsSolo;
                }
                catch (Exception ex)
                {
                    Logging.LogWarning($"Failed to restore input source {sourceConfig.DeviceId}: {ex.Message}");
                }
            }
        }

        private void AddDefaultInputSource(AudioEngine audioEngine)
        {
            try
            {
                var defaultMic = InputDevice.GetDefaultInputDevice();
                if (defaultMic != null)
                {
                    audioEngine.AddInputSource(defaultMic.Id, AudioEngine.AudioStreamType.Microphone);
                }
            }
            catch (Exception ex)
            {
                Logging.LogWarning($"Could not add default input device: {ex.Message}");
                Logging.Log("Please add audio sources in Settings.");
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
                var audioEngine = _serviceProvider.GetService<AudioEngine>();
                if (audioEngine != null)
                {
                    await audioEngine.DisposeAsync().ConfigureAwait(false);
                }

                if (_serviceProvider is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    _serviceProvider.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error disposing application resources: {ex}");
            }
            finally
            {
                _serviceProvider = null;
                GC.SuppressFinalize(this);
            }
        }
    }
}