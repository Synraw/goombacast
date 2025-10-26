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
            Audio = new AudioEngine(uiCtx);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Configure services
                var services = new ServiceCollection();
                ConfigureServices(services, desktop);
                _serviceProvider = services.BuildServiceProvider();

                // Avoid duplicate validations from both Avalonia and the CommunityToolkit
                DisableAvaloniaDataAnnotationValidation();

                // Create main window and set up dialog service
                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;

                // Create dialog service with main window reference
                var dialogService = new DialogService(_serviceProvider, mainWindow);

                // Create view model with dependencies
                var viewModel = new MainWindowViewModel(Audio, dialogService);
                mainWindow.DataContext = viewModel;

                desktop.Exit += (_, __) =>
                {
                    Audio.Dispose();
                    _serviceProvider?.Dispose();
                };

                // Start audio once UI is ready
                Audio.Start();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ConfigureServices(IServiceCollection services, IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Register services
            services.AddSingleton(Audio);
            services.AddTransient<SettingsWindowViewModel>();
            
            // Register dialog service (will be created manually since it needs the main window)
            services.AddSingleton<IDialogService>(sp => 
                new DialogService(sp, desktop.MainWindow ?? 
                    throw new InvalidOperationException("MainWindow not initialized")));
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