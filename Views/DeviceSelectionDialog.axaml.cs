using Avalonia.Controls;
using Avalonia.Interactivity;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using GoombaCast.ViewModels;
using System;
using System.Linq;

namespace GoombaCast.Views
{
    public partial class DeviceSelectionDialog : Window
    {
        private RadioButton? _microphoneRadio;
        private RadioButton? _loopbackRadio;
        private ComboBox? _deviceComboBox;
        private Border? _deviceInfoPanel;
        private TextBlock? _deviceInfoText;

        private readonly AudioEngine? _audioEngine;

        // Design-time constructor
        public DeviceSelectionDialog() 
        {
            InitializeComponent();
        }

        // Runtime constructor
        public DeviceSelectionDialog(AudioEngine audioEngine)
        {
            ArgumentNullException.ThrowIfNull(audioEngine);
            _audioEngine = audioEngine;
            InitializeComponent();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            InitializeControls();
            LoadDevices();
        }

        private void InitializeControls()
        {
            // Safe to find controls now - name scope is ready
            _microphoneRadio = this.FindControl<RadioButton>("MicrophoneRadio");
            _loopbackRadio = this.FindControl<RadioButton>("LoopbackRadio");
            _deviceComboBox = this.FindControl<ComboBox>("DeviceComboBox");
            _deviceInfoPanel = this.FindControl<Border>("DeviceInfoPanel");
            _deviceInfoText = this.FindControl<TextBlock>("DeviceInfoText");

            // Wire up events in code instead of XAML
            if (_microphoneRadio != null)
                _microphoneRadio.IsCheckedChanged += OnDeviceTypeChanged;
            if (_loopbackRadio != null)
                _loopbackRadio.IsCheckedChanged += OnDeviceTypeChanged;
            if (_deviceComboBox != null)
                _deviceComboBox.SelectionChanged += OnDeviceSelectionChanged;
        }

        private void OnDeviceTypeChanged(object? sender, RoutedEventArgs e)
        {
            LoadDevices();
        }

        private void OnDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateDeviceInfo();
        }

        private void LoadDevices()
        {
            if (_deviceComboBox == null || _microphoneRadio == null || _audioEngine == null) 
                return;

            try
            {
                if (_microphoneRadio.IsChecked == true)
                {
                    var devices = InputDevice.GetActiveInputDevices();
                    _deviceComboBox.ItemsSource = devices;
                    
                    if (devices.Count == 0)
                    {
                        ShowNoDevicesWarning("No microphone devices found. Please check your audio settings.");
                    }
                }
                else
                {
                    var devices = OutputDevice.GetActiveOutputDevices();
                    _deviceComboBox.ItemsSource = devices;
                    
                    if (devices.Count == 0)
                    {
                        ShowNoDevicesWarning("No audio output devices found. Please check your audio settings.");
                    }
                }

                if (_deviceComboBox.ItemCount > 0)
                {
                    _deviceComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error loading devices: {ex.Message}");
                ShowNoDevicesWarning($"Error loading devices: {ex.Message}");
            }
        }

        private void ShowNoDevicesWarning(string message)
        {
            if (_deviceInfoPanel != null && _deviceInfoText != null)
            {
                _deviceInfoPanel.IsVisible = true;
                _deviceInfoPanel.Background = Avalonia.Media.Brushes.DarkRed;
                _deviceInfoText.Text = message;
            }
        }

        private void UpdateDeviceInfo()
        {
            if (_deviceComboBox?.SelectedItem == null || _deviceInfoPanel == null || _deviceInfoText == null)
                return;

            try
            {
                _deviceInfoPanel.IsVisible = true;
                _deviceInfoPanel.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

                var deviceName = _deviceComboBox.SelectedItem.ToString();
                var deviceType = _microphoneRadio?.IsChecked == true ? "Microphone" : "System Audio";
                
                _deviceInfoText.Text = $"Device: {deviceName}\nType: {deviceType}";
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error updating device info: {ex.Message}");
            }
        }

        private void OnAddClick(object? sender, RoutedEventArgs e)
        {
            if (_deviceComboBox?.SelectedItem == null)
            {
                ShowNoDevicesWarning("Please select a device first.");
                return;
            }

            if (_audioEngine == null)
            {
                ShowNoDevicesWarning("Audio engine not initialized.");
                return;
            }

            try
            {
                var streamType = _microphoneRadio?.IsChecked == true
                    ? AudioEngine.AudioStreamType.Microphone
                    : AudioEngine.AudioStreamType.Loopback;

                string deviceId = _microphoneRadio?.IsChecked == true
                    ? ((InputDevice)_deviceComboBox.SelectedItem).Id
                    : ((OutputDevice)_deviceComboBox.SelectedItem).Id;

                // Check if device is already added
                var existingSource = _audioEngine!.InputSources.FirstOrDefault(s => s.DeviceId == deviceId);
                if (existingSource != null)
                {
                    ShowNoDevicesWarning("This device is already added to the mixer.");
                    return;
                }

                Close(new DeviceSelectionResult
                {
                    DeviceId = deviceId,
                    StreamType = streamType
                });
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error adding device: {ex.Message}");
                ShowNoDevicesWarning($"Error: {ex.Message}");
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }
    }
}