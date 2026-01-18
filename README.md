# GoombaCast

<div align="center">
  <img src="Assets/goomba-logo.ico" alt="GoombaCast Logo" width="128" height="128">
  <p><strong>Professional Audio Broadcasting and Recording for Windows</strong></p>
</div>

## Overview

GoombaCast is a powerful Windows desktop application for live audio broadcasting and recording. Built with Avalonia UI and .NET 8, it provides a modern, user-friendly interface for streaming audio to Icecast servers while offering advanced audio mixing, processing, and monitoring capabilities.

## ✨ Key Features

### Audio Input & Mixing
- **Multiple Input Sources**: Mix audio from multiple microphones and system audio (loopback capture)
- **Per-Source Controls**: Individual volume, mute, and solo controls for each input source
- **Master Gain Control**: Adjust overall output levels with precision

### Broadcasting
- **Icecast Streaming**: Stream directly to Icecast/SHOUTcast servers
- **Multiple Server Profiles**: Save and switch between different streaming destinations
- **Listener Monitoring**: Real-time listener count display when streaming
- **High-Quality Audio**: 320 kbps MP3 encoding at 48kHz stereo

### Audio Processing
- **Peak Limiter**: Protect against clipping with adjustable threshold (-12 dB to 0 dB)
- **Audio Metering**: Professional VU meters with peak hold indicators
- **Clipping Detection**: Visual indicators for audio overload

### Recording
- **Local Recording**: Save your broadcasts as high-quality MP3 files
- **Custom Output Directory**: Choose where to save your recordings
- **Streaming Time Display**: Track broadcast duration in real-time

### User Interface
- **Modern Design**: Clean, intuitive interface built with Avalonia UI
- **Real-Time Monitoring**: Live audio level meters with peak indicators
- **Activity Logging**: Built-in log viewer for monitoring application events
- **Settings Management**: Comprehensive settings dialog for all configuration options

## 🖥️ System Requirements

- **Operating System**: Windows 10 or later (64-bit)
- **.NET Runtime**: .NET 8.0 Runtime for Windows (included in releases)
- **Audio Hardware**: Windows-compatible audio input devices (microphones, virtual audio cables, etc.)
- **Network**: Internet connection for streaming to remote Icecast servers

## 📥 Installation

### Option 1: Download Pre-built Release
1. Download the latest release from the [Releases](https://github.com/Synraw/goombacast/releases) page
2. Extract `GoombaCast.zip` to your desired location
3. Run `GoombaCast.exe`

### Option 2: Build from Source
See the [Building from Source](#building-from-source) section below.

## 🚀 Quick Start

1. **Launch GoombaCast**
   - Run `GoombaCast.exe`

2. **Configure Settings**
   - Click the Settings button (gear icon)
   - Under "Server Profiles", click "Manage Server Profiles"
   - Add a new server profile with your Icecast server details:
     - Profile Name: A friendly name for this server
     - Server Address: Full URL (e.g., `http://your-server.com:8000/stream.mp3`)
     - Username: Usually "source"
     - Password: Your Icecast source password
   - Select the profile you just created

3. **Add Audio Sources**
   - In Settings, under "Audio Mixer", click "Add Source"
   - Select your microphone or system audio (loopback)
   - Adjust volume levels as needed

4. **Start Broadcasting**
   - Click "Start Streaming"
   - Monitor audio levels on the VU meters
   - Your stream is now live!

5. **Recording (Optional)**
   - Configure recording directory in Settings
   - Click "Start Recording" to save audio locally

## ⚙️ Configuration

### Server Profiles
GoombaCast supports multiple server profiles, allowing you to quickly switch between different streaming destinations.

**Required Settings:**
- **Profile Name**: Identifier for the server configuration
- **Server Address**: Complete Icecast mount point URL
  - Format: `http://hostname:port/mountpoint`
  - Example: `http://stream.example.com:8000/live.mp3`
- **Username**: Icecast source username (typically "source")
- **Password**: Your Icecast source password

### Audio Mixer
Add and configure multiple audio input sources:
- **Microphone**: Capture from physical microphones or line inputs
- **Loopback**: Capture system audio (desktop audio, music players, etc.)

**Per-Source Controls:**
- Volume slider (0-200%)
- Mute button
- Solo button (mute all other sources)

### Audio Processing
- **Limiter**: Enable/disable the peak limiter and set threshold
- **Threshold**: Adjust limiter threshold from -12 dB to 0 dB (default: -3 dB)

### Recording Settings
- **Recording Directory**: Choose where MP3 recordings are saved
- Default: `%USERPROFILE%\Music\GoombaCast Recordings`

## 🔧 Building from Source

### Prerequisites
- Windows 10 or later
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 or JetBrains Rider (optional, for IDE development)

### Build Steps

1. **Clone the repository**
   ```bash
   git clone https://github.com/Synraw/goombacast.git
   cd goombacast
   ```

2. **Restore dependencies**
   ```bash
   dotnet restore
   ```

3. **Build the project**
   ```bash
   dotnet build --configuration Release
   ```

4. **Run the application**
   ```bash
   dotnet run
   ```

### Publishing a Standalone Executable

To create a self-contained executable:

```bash
dotnet publish --configuration Release
```

The output will be in `bin/Release/net8.0-windows/win-x64/publish/`

## 🛠️ Technology Stack

- **UI Framework**: [Avalonia UI 11.3.6](https://avaloniaui.net/) - Cross-platform .NET UI framework
- **Language**: C# (.NET 8.0)
- **MVVM Toolkit**: [CommunityToolkit.Mvvm 8.2.1](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) - Modern MVVM implementation
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection
- **Audio Processing**: [NAudio](https://github.com/naudio/NAudio) - Audio library for .NET
  - NAudio.Core 2.2.1
  - NAudio.Wasapi 2.2.1 - Windows Audio Session API support
  - NAudio.Lame 2.1.0 - MP3 encoding
- **Target Platform**: Windows x64 (.NET 8.0)

## 📄 License

This project is licensed under the MIT License. See the [LICENSE.txt](LICENSE.txt) file for details.

Copyright (c) 2025 GoombaRadio

## 🤝 Contributing

Contributions are welcome! Here's how you can help:

1. **Fork the repository**
2. **Create a feature branch**
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make your changes**
4. **Test thoroughly**
5. **Commit your changes**
   ```bash
   git commit -m "Add some feature"
   ```
6. **Push to your fork**
   ```bash
   git push origin feature/your-feature-name
   ```
7. **Open a Pull Request**

### Development Guidelines
- Follow existing code style and conventions
- Write clear commit messages
- Test your changes on Windows 10 and Windows 11
- Update documentation as needed

## 🐛 Troubleshooting

### Common Issues

**Problem**: "No audio input devices found"
- **Solution**: Ensure your microphone is connected and enabled in Windows Sound Settings

**Problem**: "Failed to connect to Icecast server"
- **Solution**: 
  - Verify server address, username, and password
  - Check firewall settings
  - Confirm the Icecast server is running and accessible

**Problem**: "Audio is clipping/distorting"
- **Solution**:
  - Reduce input source volumes
  - Enable the limiter in Settings
  - Lower the master gain

**Problem**: "Recording not saving"
- **Solution**: 
  - Check the recording directory path in Settings
  - Ensure you have write permissions to the directory
  - Verify sufficient disk space

## 📞 Support

For bug reports and feature requests, please use the [GitHub Issues](https://github.com/Synraw/goombacast/issues) page.

## 🎯 Roadmap

Future enhancements under consideration:
- VST plugin support
- Multi-track recording
- Automated gain control (AGC)
- Audio filters and EQ
- Scheduled streaming
- Multiple simultaneous streams

---

**Made with ❤️ by GoombaRadio**