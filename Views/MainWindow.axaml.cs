using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;

namespace GoombaCast.Views
{
    public partial class MainWindow : Window
    {
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
                    Height = Height + delta;
                LogHideShowButton.Content = "Hide Log";
            }
        }

        private async void StreamStopStartButton_Click(object? sender, RoutedEventArgs e)
        {
            var btn = StreamStopStartButton;

            try
            {
                btn.IsEnabled = false;

                if (!App.Audio.IsBroadcasting)
                {
                    await App.Audio.StartBroadcastAsync().ConfigureAwait(true);
                    btn.Content = "Stop Stream";
                    btn.Background = Brushes.Red;
                }
                else
                {
                    App.Audio.StopBroadcast();
                    btn.Content = "Start Streaming";
                    btn.Background = Brushes.Green;
                }
            }
            catch (Exception ex)
            {
                // Optional: surface to log or dialog. Keeping silent per requirement focus.
                Console.WriteLine(ex.Message);
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }
    }
}
