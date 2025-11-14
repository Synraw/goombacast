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
            MicrophoneRadio.IsCheckedChanged += OnDeviceTypeChanged;
            LoopbackRadio.IsCheckedChanged += OnDeviceTypeChanged;
            DeviceComboBox.SelectionChanged += OnDeviceSelectionChanged;
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
            if (_audioEngine == null) 
                return;

            try
            {
                if (MicrophoneRadio.IsChecked == true)
                {
                    var devices = InputDevice.GetActiveInputDevices();
                    DeviceComboBox.ItemsSource = devices;
                    
                    if (devices.Count == 0)
                    {
                        ShowNoDevicesWarning("No microphone devices found. Please check your audio settings.");
                    }
                }
                else
                {
                    var devices = OutputDevice.GetActiveOutputDevices();
                    DeviceComboBox.ItemsSource = devices;
                    
                    if (devices.Count == 0)
                    {
                        ShowNoDevicesWarning("No audio output devices found. Please check your audio settings.");
                    }
                }

                if (DeviceComboBox.ItemCount > 0)
                {
                    DeviceComboBox.SelectedIndex = 0;
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
                DeviceInfoPanel.IsVisible = true;
                DeviceInfoPanel.Background = Avalonia.Media.Brushes.DarkRed;
                DeviceInfoText.Text = message;
            
        }

        private void UpdateDeviceInfo()
        {
            if (DeviceComboBox.SelectedItem == null)
                return;

            try
            {
                DeviceInfoPanel.IsVisible = true;
                DeviceInfoPanel.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

                var deviceName = DeviceComboBox.SelectedItem.ToString();
                var deviceType = MicrophoneRadio?.IsChecked == true ? "Microphone" : "System Audio";
                
                DeviceInfoText.Text = $"Device: {deviceName}\nType: {deviceType}";
            }
            catch (Exception ex)
            {
                Logging.LogError($"Error updating device info: {ex.Message}");
            }
        }

        private void OnAddClick(object? sender, RoutedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem == null)
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
                var streamType = MicrophoneRadio.IsChecked == true
                    ? AudioEngine.AudioStreamType.Microphone
                    : AudioEngine.AudioStreamType.Loopback;

                string deviceId = MicrophoneRadio?.IsChecked == true
                    ? ((InputDevice)DeviceComboBox.SelectedItem).Id
                    : ((OutputDevice)DeviceComboBox.SelectedItem).Id;

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