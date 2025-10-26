using Avalonia.Controls;
using GoombaCast.ViewModels;
using GoombaCast.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace GoombaCast.Services;

/*
 * Interface for dialog services. 
 * Add our dialog methods here.
 */

public class DialogService : IDialogService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Window _mainWindow;

    public DialogService(IServiceProvider serviceProvider, Window mainWindow)
    {
        _serviceProvider = serviceProvider;
        _mainWindow = mainWindow;
    }

    public async Task ShowSettingsDialogAsync()
    {
        var viewModel = _serviceProvider.GetRequiredService<SettingsWindowViewModel>();
        var dialog = new SettingsWindow
        {
            DataContext = viewModel
        };

        var mainVM = _mainWindow.DataContext as MainWindowViewModel;
        void OnStreamNameChanged(object? s, string name) => mainVM?.UpdateWindowTitle(name);
        viewModel.StreamNameChanged += OnStreamNameChanged;
        await dialog.ShowDialog(_mainWindow);
        viewModel.StreamNameChanged -= OnStreamNameChanged;
    }
}
