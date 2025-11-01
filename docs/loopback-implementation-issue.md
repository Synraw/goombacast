# Feature: Add Loopback Audio Capture Support

## Summary
Implement support for capturing audio from output devices (system audio/desktop audio) in addition to the current microphone input support. This will allow users to broadcast application audio, game audio, or any audio playing through their speakers/headphones.

## Background
Based on the investigation in `docs/audio-loopback-investigation.md`, NAudio (already used by GoombaCast) provides native support for loopback audio capture through the `WasapiLoopbackCapture` class. This feature can be implemented with moderate effort.

## Goals
- Enable users to select output devices (system audio) as audio sources
- Maintain compatibility with existing microphone input functionality  
- Provide clear UI to distinguish between input and output device types
- Handle format conversion if needed (loopback devices may use different formats)

## Non-Goals (Future Enhancements)
- Simultaneous microphone + loopback mixing (Phase 2)
- Application-specific audio capture (Phase 3)
- Advanced mixing controls

## Technical Approach

### 1. Create Output Device Model
Create `OutputDevice` class similar to existing `InputDevice`:

```csharp
public sealed class OutputDevice(MMDevice device)
{
    public MMDevice Device { get; } = device;
    public string Id => Device.ID;
    public override string ToString() => Device.FriendlyName;
    
    public static List<OutputDevice> GetActiveOutputDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices.Select(d => new OutputDevice(d)).ToList();
    }
    
    public static OutputDevice GetDefaultOutputDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new OutputDevice(device);
    }
}
```

### 2. Create LoopbackStream Class
New class in `Models/Audio/Streaming/LoopbackStream.cs`:

```csharp
public sealed class LoopbackStream : IDisposable
{
    private WasapiLoopbackCapture? _loopback;
    private LameMP3FileWriter? _mp3Writer;
    private IcecastStream? _iceStream;
    private OutputDevice? outputDevice;
    private volatile bool _running;
    
    // Similar structure to MicrophoneStream but using WasapiLoopbackCapture
    // Note: Must use _loopback.WaveFormat (cannot override format)
    // May need WaveFormatConversionStream if format doesn't match encoder
}
```

### 3. Update AudioEngine
Modify `Services/AudioEngine.cs` to support both input and output modes:

```csharp
public enum AudioSourceType { Microphone, Loopback }

public sealed class AudioEngine : IDisposable
{
    private AudioSourceType _sourceType;
    private MicrophoneStream? _micStream;
    private LoopbackStream? _loopbackStream;
    
    // Add methods to switch between input and output modes
    public void SetAudioSource(AudioSourceType type, string deviceId) { }
}
```

### 4. Update Settings Service
Add settings to persist output device selection:

```csharp
public class Settings
{
    // Existing
    public string? InputDeviceId { get; set; }
    
    // New
    public string? OutputDeviceId { get; set; }
    public AudioSourceType AudioSourceType { get; set; } = AudioSourceType.Microphone;
}
```

### 5. Update UI
Update device selection UI to:
- Show radio buttons or dropdown to select between "Microphone" and "Desktop Audio"  
- Display appropriate device list based on selection
- Clear labeling to avoid user confusion
- Help text explaining the difference

### 6. Format Conversion (If Needed)
If loopback device format doesn't match encoder requirements:

```csharp
if (_loopback.WaveFormat.SampleRate != 48000 || 
    _loopback.WaveFormat.BitsPerSample != 16 ||
    _loopback.WaveFormat.Channels != 2)
{
    // Use MediaFoundationResampler or WaveFormatConversionStream
    var resampler = new MediaFoundationResampler(
        _loopback.ToSampleProvider().ToWaveProvider(),
        new WaveFormat(48000, 16, 2)
    );
}
```

## Implementation Checklist

### Core Functionality
- [ ] Create `OutputDevice` model class
- [ ] Implement `LoopbackStream` class with `WasapiLoopbackCapture`
- [ ] Add audio format conversion/resampling logic
- [ ] Update `AudioEngine` to support loopback mode
- [ ] Add output device enumeration and selection
- [ ] Wire up audio handlers (gain, limiter, level meter, recorder) to loopback stream

### Settings & Persistence  
- [ ] Add `OutputDeviceId` to settings model
- [ ] Add `AudioSourceType` to settings model
- [ ] Save and restore output device selection
- [ ] Handle missing/disconnected output devices gracefully

### UI Updates
- [ ] Add audio source type selector (Microphone/Desktop Audio)
- [ ] Update device dropdown to show appropriate device type
- [ ] Add help text/tooltips explaining loopback capture
- [ ] Update ViewModel to handle output devices
- [ ] Test UI with device switching

### Testing & Edge Cases
- [ ] Test with various output devices (speakers, headphones, virtual cables)
- [ ] Test format conversion with different sample rates
- [ ] Test behavior when no audio is playing
- [ ] Test device disconnection/reconnection
- [ ] Test with different audio applications (games, music players, browsers)
- [ ] Verify recording and broadcasting work with loopback
- [ ] Verify audio handlers (gain, limiter) work correctly with loopback

### Documentation
- [ ] Update README with loopback feature description
- [ ] Add user guide for selecting output devices
- [ ] Document known limitations (format restrictions, driver-specific behavior)
- [ ] Add troubleshooting section for common issues

## Known Limitations

1. **Format Restrictions:** Loopback capture must use the device's native mix format (cannot force specific sample rate/bit depth like microphone input)

2. **Silent Periods:** Some audio drivers may not provide data when no audio is playing

3. **Windows Only:** WASAPI loopback is Windows Vista+ only (already a Windows app)

4. **No Per-Application Capture:** This implementation captures all system audio, not specific applications

## Testing Plan

1. **Device Enumeration:** Verify output devices are listed correctly
2. **Basic Capture:** Confirm audio is captured from output device
3. **Format Handling:** Test with devices using different native formats
4. **Broadcasting:** Verify captured audio streams to Icecast server
5. **Recording:** Verify captured audio saves to file correctly
6. **Audio Processing:** Verify gain, limiter, and level meter work with loopback
7. **Device Switching:** Test changing between input and output modes
8. **Persistence:** Verify settings are saved and restored correctly

## Success Criteria

- [ ] Users can select an output device (desktop audio) as audio source
- [ ] Loopback audio is captured and processed correctly
- [ ] Audio can be broadcast to Icecast server from loopback source
- [ ] Audio can be recorded to file from loopback source
- [ ] Audio handlers (gain, limiter, level meter) work with loopback
- [ ] Settings are persisted across application restarts
- [ ] No regression in existing microphone input functionality
- [ ] UI clearly distinguishes between input and output device types

## Future Enhancements (Out of Scope)

These can be addressed in separate issues:

1. **Mixed Audio (Phase 2):** Simultaneous microphone + loopback capture with mixing controls
2. **Application-Specific Capture (Phase 3):** Capture audio from specific applications only
3. **Advanced Features:** Per-source gain, monitoring, ducking, etc.

## Estimated Effort
**2-3 days** for a developer familiar with NAudio and the GoombaCast codebase

## References
- Investigation document: `docs/audio-loopback-investigation.md`
- NAudio WasapiLoopbackCapture: https://github.com/naudio/NAudio
- Current implementation: `Models/Audio/Streaming/MicrophoneStream.cs`
