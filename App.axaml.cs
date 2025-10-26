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

namespace GoombaCast
{
    public partial class App : Application
    {
        public static AudioEngine Audio { get; private set; } = null!;
        private ServiceProvider? _serviceProvider;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            // Capture UI SynchronizationContext for safe UI updates
            var uiCtx = SynchronizationContext.Current;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var services = new ServiceCollection();

                // First register and create the logging service
                services.AddSingleton<ILoggingService, LoggingService>();
                
                var tempProvider = services.BuildServiceProvider();
                var loggingService = tempProvider.GetRequiredService<ILoggingService>();
                Logging.Initialize(loggingService);

                Audio = new AudioEngine(uiCtx);

                services.AddSingleton(Audio);
                services.AddTransient<SettingsWindowViewModel>();
                services.AddSingleton<IDialogService>(sp => new DialogService(sp, desktop.MainWindow ?? throw new InvalidOperationException("MainWindow not initialized")));
                _serviceProvider = services.BuildServiceProvider();

                DisableAvaloniaDataAnnotationValidation();

                // Create main window and set up dialog service
                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;

                // Create dialog service with main window reference
                var dialogService = new DialogService(_serviceProvider, mainWindow);

                // Create view model with all required dependencies
                var viewModel = new MainWindowViewModel(Audio, dialogService, loggingService);
                mainWindow.DataContext = viewModel;

                desktop.Exit += (_, __) =>
                {
                    Audio.Dispose();
                    _serviceProvider?.Dispose();
                    viewModel?.Cleanup();
                };

                // Start audio once UI is ready
                Audio.Start();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }
    }
}