using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GoombaCast.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void ButtonClickToggleLog(object? sender, RoutedEventArgs e)
        {
            LogWindow.IsVisible = !LogWindow.IsVisible;
            LogHideShowButton.Content = LogWindow.IsVisible ? "Hide Log" : "Show Log";
            var vm = DataContext as ViewModels.MainWindowViewModel;
            //continue with editing viewmodel stuff
           
        }
    }
}