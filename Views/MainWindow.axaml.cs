using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using GoombaCast.Services;
using GoombaCast.ViewModels;
using System;

namespace GoombaCast.Views
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
        private double? _logLastOccupiedHeight;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ButtonClickToggleLog(object? sender, RoutedEventArgs e)
        {
            if (LogWindow.IsVisible)
            {
                var occupied = LogWindow.Bounds.Height + LogWindow.Margin.Top + LogWindow.Margin.Bottom;
                _logLastOccupiedHeight = occupied;
                LogWindow.IsVisible = false;
                Height = Math.Max(MinHeight, Height - occupied);
                LogHideShowButton.Content = "Show Log";
            }
            else
            {
                LogWindow.IsVisible = true;
                var delta = _logLastOccupiedHeight ?? 0;
                if (delta > 0)
                    Height += delta;
                LogHideShowButton.Content = "Hide Log";
            }
        }

        private async void StreamStopStartButton_Click(object? sender, RoutedEventArgs e)
        {
            var s = SettingsService.Default.Settings;
            var btn = StreamStopStartButton;

            try
            {
                btn.IsEnabled = false;

                if (!App.Audio.IsBroadcasting)
                {
                    await App.Audio.StartBroadcastAsync().ConfigureAwait(true);
                    ViewModel?.StartTimer();
                    btn.Content = "Stop Stream";
                    btn.Background = Brushes.Red;
                    Logging.Log($"Now streaming to {s.StreamName}");
                }
                else
                {
                    App.Audio.StopBroadcast();
                    ViewModel?.StopTimer();
                    btn.Content = "Start Streaming";
                    btn.Background = Brushes.Green;
                    Logging.Log($"{s.StreamName} stream stopped.");
                }
            }
            catch (Exception ex)
            {
                string startOrStop = App.Audio.IsBroadcasting ? "stopping" : "starting";
                Logging.LogError($"Error {startOrStop} stream: {ex.Message}");
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }
    }
}
