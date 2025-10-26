using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace GoombaCast.Views
{
    public partial class MainWindow : Window
    {
        // Stores the last visible height occupied by the log (including margins)
        private double? _logLastOccupiedHeight;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ButtonClickToggleLog(object? sender, RoutedEventArgs e)
        {
            if (LogWindow.IsVisible)
            {
                // Capture how much vertical space the log currently occupies
                var occupied = LogWindow.Bounds.Height + LogWindow.Margin.Top + LogWindow.Margin.Bottom;
                _logLastOccupiedHeight = occupied;

                // Hide the log and shrink the window by the same amount
                LogWindow.IsVisible = false;
                Height = Math.Max(MinHeight, Height - occupied);
                LogHideShowButton.Content = "Show Log";
            }
            else
            {
                // Show the log and restore the previous height by adding back the captured delta
                LogWindow.IsVisible = true;

                var delta = _logLastOccupiedHeight ?? 0;
                if (delta > 0)
                    Height = Height + delta;

                LogHideShowButton.Content = "Hide Log";
            }
        }

        private async void ButtonClickOpenSettings(object? sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow();
            await dlg.ShowDialog(this);
        }
    }
}
