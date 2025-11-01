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
                StartAudioEngine();
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

            // Initialize logging
            var loggingService = _serviceProvider.GetRequiredService<ILoggingService>();
            Logging.Initialize(loggingService);

            // Store AudioEngine instance and configure from settings
            _audio = _serviceProvider.GetRequiredService<AudioEngine>();
            ConfigureAudioEngineFromSettings(_audio);

            DisableAvaloniaDataAnnotationValidation();
        }

        private void ConfigureAudioEngineFromSettings(AudioEngine audio)
        {
            var settings = SettingsService.Default.Settings;
            
            audio.RecreateAudioStream(settings.AudioStreamType);

            if (settings.AudioStreamType == AudioEngine.AudioStreamType.Microphone && 
                !string.IsNullOrEmpty(settings.MicrophoneDeviceId))
            {
                audio.ChangeDevice(settings.MicrophoneDeviceId);
            }
            else if (settings.AudioStreamType == AudioEngine.AudioStreamType.Loopback && 
                     !string.IsNullOrEmpty(settings.LoopbackDeviceId))
            {
                audio.ChangeDevice(settings.LoopbackDeviceId);
            }

            audio.SetLimiterEnabled(settings.LimiterEnabled);
            audio.SetLimiterThreshold(settings.LimiterThreshold);
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

        private void StartAudioEngine()
        {
            try
            {
                _audio?.Start();
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to start audio engine: {ex}");
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