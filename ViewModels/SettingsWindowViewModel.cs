using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Audio.Streaming;
using GoombaCast.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace GoombaCast.ViewModels
{
    public partial class SettingsWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private ObservableCollection<InputDevice> _availableMicrophones = new();

        [ObservableProperty]
        private InputDevice? _selectedMicrophone;

        [ObservableProperty]
        private int _selectedMicrophoneIndex = -1;

        public SettingsWindowViewModel()
        {
            var devices = InputDevice.GetActiveInputDevices();
            AvailableMicrophones = new ObservableCollection<InputDevice>(devices);

            var savedId = SettingsService.Default.Settings.SelectedMicrophoneId;
            var match = savedId is null ? null : devices.FirstOrDefault(d => d.Id == savedId);

            if (match is not null)
                SelectedMicrophone = match;
            else if (AvailableMicrophones.Count > 0)
                SelectedMicrophone = AvailableMicrophones[0];
        }

        partial void OnSelectedMicrophoneChanged(InputDevice? value)
        {
            App.Audio.ChangeInputDevice(value?.Id ?? string.Empty);
            SettingsService.Default.Settings.SelectedMicrophoneId = value?.Id;
            SettingsService.Default.Save();
        }
    }
}
