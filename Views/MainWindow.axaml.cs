using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GoombaCast.Extensions;
using GoombaCast.Services;
using GoombaCast.ViewModels;

namespace GoombaCast.Views
{
    public partial class MainWindow : Window
    {
        private double? _savedHeightWithLog;
        private double? _savedHeightWithoutLog;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += async (s, e) =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    _savedHeightWithLog = Height;

                    if (!vm.IsLogVisible)
                    {
                        LogWindow.IsVisible = true;
                        UpdateLayout();
                        
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                        
                        var logHeight = LogWindow.Bounds.Height + LogWindow.Margin.Top + LogWindow.Margin.Bottom;

                        LogWindow.IsVisible = false;
                        LogHideShowButton.Content = "Show Log";
                        _savedHeightWithoutLog = Height - logHeight;
                        Height = _savedHeightWithoutLog.Value;
                    }
                    else
                    {
                        LogWindow.IsVisible = true;
                        LogHideShowButton.Content = "Hide Log";
                    }
                }
            };
        }

        private void ButtonClickToggleLog(object? sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;

            if (LogWindow.IsVisible)
            {
                _savedHeightWithLog = Height;

                var logHeight = LogWindow.Bounds.Height + LogWindow.Margin.Top + LogWindow.Margin.Bottom;
                _savedHeightWithoutLog = Height - logHeight;

                LogWindow.IsVisible = false;
                Height = _savedHeightWithoutLog.Value;
                LogHideShowButton.Content = "Show Log";
                viewModel.IsLogVisible = false;

                var settings = SettingsService.Default.Settings;
                settings.HideLog = true;
                SettingsService.Default.Save();
            }
            else
            {
                LogWindow.IsVisible = true;
                Height = _savedHeightWithLog ?? 550;
                LogHideShowButton.Content = "Hide Log";
                viewModel.IsLogVisible = true;
                LogWindow.ScrollToEnd();

                var settings = SettingsService.Default.Settings;
                settings.HideLog = false;
                SettingsService.Default.Save();
            }
        }
    }
}
