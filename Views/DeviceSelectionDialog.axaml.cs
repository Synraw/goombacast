using Avalonia.Controls;
using Avalonia.Interactivity;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using GoombaCast.ViewModels;
using System.Linq;

namespace GoombaCast.Views
{
    public partial class DeviceSelectionDialog : Window
    {
        private RadioButton? _microphoneRadio;
        private RadioButton? _loopbackRadio;
        private ComboBox? _deviceComboBox;

        public DeviceSelectionDialog()
        {
            InitializeComponent();
            LoadDevices();
        }

        protected override void OnOpened(System.EventArgs e)
        {
            base.OnOpened(e);
            
            _microphoneRadio = this.FindControl<RadioButton>("MicrophoneRadio");
            _loopbackRadio = this.FindControl<RadioButton>("LoopbackRadio");
            _deviceComboBox = this.FindControl<ComboBox>("DeviceComboBox");

            if (_microphoneRadio != null)
                _microphoneRadio.IsCheckedChanged += OnDeviceTypeChanged;
            if (_loopbackRadio != null)
                _loopbackRadio.IsCheckedChanged += OnDeviceTypeChanged;

            LoadDevices();
        }

        private void OnDeviceTypeChanged(object? sender, RoutedEventArgs e)
        {
            LoadDevices();
        }

        private void LoadDevices()
        {
            if (_deviceComboBox == null || _microphoneRadio == null) return;

            if (_microphoneRadio.IsChecked == true)
            {
                _deviceComboBox.ItemsSource = InputDevice.GetActiveInputDevices();
            }
            else
            {
                _deviceComboBox.ItemsSource = OutputDevice.GetActiveOutputDevices();
            }

            if (_deviceComboBox.ItemCount > 0)
                _deviceComboBox.SelectedIndex = 0;
        }

        private void OnAddClick(object? sender, RoutedEventArgs e)
        {
            if (_deviceComboBox?.SelectedItem == null) return;

            var streamType = _microphoneRadio?.IsChecked == true
                ? AudioEngine.AudioStreamType.Microphone
                : AudioEngine.AudioStreamType.Loopback;

            string deviceId = _microphoneRadio?.IsChecked == true
                ? ((InputDevice)_deviceComboBox.SelectedItem).Id
                : ((OutputDevice)_deviceComboBox.SelectedItem).Id;

            Close(new DeviceSelectionResult
            {
                DeviceId = deviceId,
                StreamType = streamType
            });
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }
    }
}