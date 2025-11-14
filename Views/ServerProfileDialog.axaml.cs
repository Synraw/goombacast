using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Services;
using System;
using System.Linq;
using static GoombaCast.Services.AppSettings;

namespace GoombaCast.Views
{
    public partial class ServerProfileDialog : Window
    {
        private readonly SettingsService _settingsService = SettingsService.Default;
        private ServerProfileConfig? _currentEditingProfile;
        private bool _isNewProfile;

        // Design-time constructor
        public ServerProfileDialog()
        {
            InitializeComponent();
        }

        // Runtime constructor
        public ServerProfileDialog(SettingsService settingsService)
        {
            ArgumentNullException.ThrowIfNull(settingsService);
            InitializeComponent();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            InitializeControls();
            LoadProfiles();
        }

        private void InitializeControls() 
            => ServerAddressTextBox.TextChanged += OnServerAddressTextChanged;
        

        private void LoadProfiles()
        {
            var settings = _settingsService.Settings;
            ProfileListBox.ItemsSource = settings.ServerProfiles;

            if (settings.CurrentServer != null)
            {
                var activeProfile = settings.ServerProfiles.FirstOrDefault(p => 
                    p.ServerAddress == settings.CurrentServer.ServerAddress &&
                    p.UserName == settings.CurrentServer.UserName);
                
                if (activeProfile != null)
                {
                    ProfileListBox.SelectedItem = activeProfile;
                }
            }
        }

        private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ProfileListBox?.SelectedItem is ServerProfileConfig profile)
            {
                LoadProfileIntoEditor(profile);
                _isNewProfile = false;
            }
        }

        private void LoadProfileIntoEditor(ServerProfileConfig profile)
        {
            _currentEditingProfile = profile;

            EditorPanel.IsEnabled = true;
            ProfileNameTextBox.Text = profile.ProfileName;
            ServerAddressTextBox.Text = profile.ServerAddress;
            UserNameTextBox.Text = profile.UserName;
            PasswordTextBox.Text = profile.Password;

            UpdateActiveButton(profile);
            ValidateServerAddress();
        }

        private void UpdateActiveButton(ServerProfileConfig profile)
        {
            var settings = _settingsService.Settings;
            bool isActive = settings.CurrentServer != null &&
                          settings.CurrentServer.ServerAddress == profile.ServerAddress &&
                          settings.CurrentServer.UserName == profile.UserName;

            SetActiveButton.Content = isActive ? "✓ Active" : "Set Active";
            SetActiveButton.IsEnabled = !isActive;
        }

        private void OnNewProfileClick(object? sender, RoutedEventArgs e)
        {
            _currentEditingProfile = new ServerProfileConfig
            {
                ProfileName = "New Profile",
                ServerAddress = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };

            _isNewProfile = true;
            LoadProfileIntoEditor(_currentEditingProfile);

            ProfileListBox.SelectedItem = null;
        }

        private void OnSaveProfileClick(object? sender, RoutedEventArgs e)
        {
            if (_currentEditingProfile == null) return;

            // Validate inputs
            if (string.IsNullOrWhiteSpace(ProfileNameTextBox.Text))
            {
                ShowValidationError("Profile name is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ServerAddressTextBox.Text))
            {
                ShowValidationError("Server address is required.");
                return;
            }

            // Update the profile with form values
            _currentEditingProfile.ProfileName = ProfileNameTextBox.Text;
            _currentEditingProfile.ServerAddress = ServerAddressTextBox.Text;
            _currentEditingProfile.UserName = UserNameTextBox.Text ?? string.Empty;
            _currentEditingProfile.Password = PasswordTextBox.Text ?? string.Empty;

            // Validate server address
            var settings = _settingsService.Settings;
            if (!settings.IsServerAddressValid(_currentEditingProfile))
            {
                ShowValidationError("Invalid server address. Must be a valid HTTP or HTTPS URL.");
                return;
            }

            try
            {
                if (_isNewProfile)
                {
                    // Add new profile
                    settings.ServerProfiles.Add(_currentEditingProfile);
                    _isNewProfile = false;
                }

                _settingsService.Save();
                
                // Force refresh the ListBox
                    var tempSource = ProfileListBox.ItemsSource;
                    ProfileListBox.ItemsSource = null;
                    ProfileListBox.ItemsSource = tempSource;
                    ProfileListBox.SelectedItem = _currentEditingProfile;

                Logging.Log($"Saved profile: {_currentEditingProfile.ProfileName}");
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to save profile: {ex.Message}");
                ShowValidationError($"Failed to save profile: {ex.Message}");
            }
        }

        private void OnSetActiveClick(object? sender, RoutedEventArgs e)
        {
            if (_currentEditingProfile == null) return;

            try
            {
                var settings = _settingsService.Settings;
                settings.CurrentServer = _currentEditingProfile;
                _settingsService.Save();

                UpdateActiveButton(_currentEditingProfile);
                Logging.Log($"Set active profile: {_currentEditingProfile.ProfileName}");
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to set active profile: {ex.Message}");
                ShowValidationError($"Failed to set active profile: {ex.Message}");
            }
        }

        private void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
        {
            if (_currentEditingProfile == null || _isNewProfile) return;

            try
            {
                var settings = _settingsService.Settings;
                
                // Check if this is the active profile
                bool isActive = settings.CurrentServer != null &&
                              settings.CurrentServer.ServerAddress == _currentEditingProfile.ServerAddress;

                if (isActive)
                    settings.CurrentServer = null;

                ProfileListBox.SelectedItem = null;

                settings.ServerProfiles.Remove(_currentEditingProfile);
                _settingsService.Save();

                _currentEditingProfile = null;
                _isNewProfile = false;
                EditorPanel.IsEnabled = false;
                ProfileNameTextBox.Text = string.Empty;
                ServerAddressTextBox.Text = string.Empty;
                UserNameTextBox.Text = string.Empty;
                PasswordTextBox.Text = string.Empty;
                ServerAddressValidation.IsVisible = false;

                var tempSource = ProfileListBox.ItemsSource;
                ProfileListBox.ItemsSource = null;
                ProfileListBox.ItemsSource = tempSource;

                Logging.Log("Profile deleted");
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to delete profile: {ex.Message}");
                ShowValidationError($"Failed to delete profile: {ex.Message}");
            }
        }

        private void OnServerAddressTextChanged(object? sender, TextChangedEventArgs e)
        {
            ValidateServerAddress();
        }

        private void ValidateServerAddress()
        {
            var address = ServerAddressTextBox.Text;
            
            if (string.IsNullOrWhiteSpace(address))
            {
                ServerAddressValidation.IsVisible = false;
                return;
            }

            bool isValid = Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
                          (uri.Scheme == "http" || uri.Scheme == "https");

            ServerAddressValidation.IsVisible = !isValid;
        }

        private void ShowValidationError(string message)
        {
            Logging.LogWarning(message);
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}