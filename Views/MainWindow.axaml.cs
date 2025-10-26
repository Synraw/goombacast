using Avalonia.Controls;
using Avalonia.Interactivity;
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
                    Height += delta;
                LogHideShowButton.Content = "Hide Log";
            }
        }
    }
}
