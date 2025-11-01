# Investigation: Capturing Application or Output Device Audio

**Date:** November 2024  
**Investigator:** GitHub Copilot  
**Issue Reference:** [Investigation] Capturing application or output device audio

## Executive Summary

This document investigates the feasibility of capturing output device or application-specific audio in GoombaCast. The investigation reveals that NAudio, the audio library already used by GoombaCast, provides built-in support for loopback audio capture through the `WasapiLoopbackCapture` class. This feature can be implemented with moderate effort.

## Current Implementation

GoombaCast currently uses:
- **NAudio 2.2.1** - Audio processing library
- **WasapiCapture** - Captures audio from input devices (microphones)
- **DataFlow.Capture** - Enumerates capture (input) devices

The architecture is well-structured:
```
MicrophoneStream.cs
  └── WasapiCapture (captures from microphone/input device)
      └── AudioHandlers (gain, limiter, level meter, recorder)
          └── IcecastStream (broadcasts to Icecast server)
```

## Investigation Findings

### 1. NAudio Loopback Capture Support

NAudio provides **native support** for Windows loopback audio capture through WASAPI:

#### WasapiLoopbackCapture Class
- **Purpose:** Captures audio from output devices (what you hear)
- **API:** Similar to `WasapiCapture` but uses `DataFlow.Render` instead of `DataFlow.Capture`
- **Availability:** Built into NAudio 2.2.1 (already in use)
- **Platform:** Windows Vista and later (WASAPI requirement)

#### Key Differences from Input Capture

| Feature | WasapiCapture (Input) | WasapiLoopbackCapture (Output) |
|---------|----------------------|--------------------------------|
| DataFlow | Capture | Render |
| Audio Source | Microphones, line-in | System audio output |
| Device Selection | Any capture device | Any render device |
| Share Mode | Shared or Exclusive | Shared (read-only) |
| Format Control | Can specify format | Must use device's mix format |

### 2. Implementation Complexity

**Ease of Implementation: MODERATE**

#### Advantages (Simple Parts)
1. **Same Library:** Uses NAudio, which is already a dependency
2. **Similar API:** `WasapiLoopbackCapture` has nearly identical interface to `WasapiCapture`
3. **Existing Architecture:** Audio handlers, streaming pipeline already built
4. **No Additional Dependencies:** No new packages required

#### Challenges (Complex Parts)
1. **Different Format Requirements:** Loopback must use device's native mix format (cannot force 48kHz/16-bit like microphone input)
2. **Format Conversion:** May need resampling/bit-depth conversion before encoding
3. **UI Changes:** Need to distinguish between input and output device selection
4. **Mixed Mode:** Users may want both microphone AND system audio simultaneously
5. **Zero Audio Issue:** Some devices may not provide loopback when no audio is playing

### 3. Technical Implementation Details

#### Option A: Separate Loopback Stream (Simpler)
Create a new `LoopbackStream` class parallel to `MicrophoneStream`:

```csharp
public sealed class LoopbackStream : IDisposable
{
    private WasapiLoopbackCapture? _loopback;
    
    public LoopbackStream(IcecastStream icecastStream, OutputDevice device)
    {
        _loopback = new WasapiLoopbackCapture(device.Device);
        // Note: Must use _loopback.WaveFormat (cannot override)
    }
}
```

**Pros:**
- Clear separation of concerns
- Easier to implement initially
- Less risk of breaking existing functionality

**Cons:**
- Cannot mix microphone and system audio without additional work
- More code duplication

#### Option B: Unified Stream with Device Type (More Flexible)
Modify `MicrophoneStream` to support both input and loopback:

```csharp
public enum AudioDeviceType { Input, Output }

public sealed class AudioStream : IDisposable
{
    private IWaveIn? _capture; // Can be WasapiCapture or WasapiLoopbackCapture
    
    public AudioStream(IcecastStream icecastStream, AudioDevice device)
    {
        if (device.Type == AudioDeviceType.Input)
            _capture = new WasapiCapture(device.Device);
        else
            _capture = new WasapiLoopbackCapture(device.Device);
    }
}
```

**Pros:**
- Single unified interface
- Easier to add mixing capabilities later
- Better long-term architecture

**Cons:**
- More complex initial implementation
- Requires more testing
- Changes to existing `InputDevice` model

#### Option C: Audio Mixing (Most Complex)
Support both microphone and loopback simultaneously:

```csharp
public sealed class MixedAudioStream : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveMixerStream32 _mixer;
    
    // Mix both sources in real-time
}
```

**Pros:**
- Most powerful and flexible solution
- Enables common use case (commentary + game audio)

**Cons:**
- Significantly more complex
- Requires format conversion and synchronization
- Higher CPU usage
- More potential for bugs

### 4. Device Enumeration

Output devices can be enumerated similar to input devices:

```csharp
// Current (Input devices)
var inputDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

// New (Output devices)  
var outputDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
```

### 5. Potential Issues and Mitigations

#### Issue 1: Format Mismatch
**Problem:** Loopback device may use different format than encoder expects  
**Mitigation:** Use `WaveFormatConversionStream` or `MediaFoundationResampler` from NAudio

#### Issue 2: Silent Periods
**Problem:** Some drivers don't capture when no audio is playing  
**Mitigation:** Document behavior, potentially inject silence detection

#### Issue 3: Latency
**Problem:** Loopback might have different latency than microphone  
**Mitigation:** Use smaller buffer sizes, accept minor latency in broadcast use case

#### Issue 4: User Confusion
**Problem:** Users might not understand difference between input and output devices  
**Mitigation:** Clear UI labels and help text

### 6. Alternative Solutions (Workarounds)

As mentioned in the issue, users can currently use external tools:

1. **VoiceMeeter Banana** (Free)
   - Virtual audio mixer
   - Routes audio between applications
   - Appears as both input and output device to Windows

2. **Elgato Wave Link** (Free with Elgato hardware, Limited otherwise)
   - Multi-source audio mixing
   - Designed for streamers
   - Professional interface

3. **VB-Audio Virtual Cable** (Donationware)
   - Simple virtual audio device
   - Lightweight alternative

These tools work well but require:
- Additional software installation
- Configuration knowledge
- Understanding of audio routing concepts

## Recommendations

### Phase 1: Basic Loopback Support (Recommended First Step)
**Effort:** 2-3 days  
**Approach:** Option A (Separate Loopback Stream)

1. Create `OutputDevice` model (similar to `InputDevice`)
2. Create `LoopbackStream` class using `WasapiLoopbackCapture`
3. Add format conversion if needed
4. Update UI to select between input and output mode
5. Update `AudioEngine` to support loopback mode

**Benefits:**
- Relatively simple implementation
- Low risk to existing functionality
- Covers majority of use cases
- Can be extended later

### Phase 2: Mixed Audio Support (Future Enhancement)
**Effort:** 5-7 days  
**Approach:** Option C (Audio Mixing)

1. Implement simultaneous microphone and loopback capture
2. Add mixing controls (volume balance, pan)
3. Handle format conversion and synchronization
4. Advanced UI for multi-source management

**Benefits:**
- Supports advanced streaming scenarios
- Professional-grade feature
- Differentiates from simple solutions

### Phase 3: Application-Specific Capture (Advanced)
**Effort:** 10+ days  
**Approach:** Windows Audio Session API (WASAPI Sessions)

1. Enumerate per-application audio sessions
2. Capture specific application audio
3. Mix multiple applications selectively

**Note:** This is significantly more complex and may not be worth the effort given that loopback + virtual audio cables achieve similar results.

## Conclusion

**Implementing loopback audio capture is feasible and moderately simple** due to NAudio's built-in support. The recommended approach is to:

1. **Start with basic loopback** (Phase 1) - covers most use cases with reasonable effort
2. **Document workarounds** for users who need advanced mixing now
3. **Plan for mixed audio** (Phase 2) as a future enhancement if there's user demand

The main complexity lies not in the capture itself, but in:
- Format handling (resampling/conversion)
- UI/UX design for device selection
- Mixed audio support (if desired)

Application-specific capture (Phase 3) is **not recommended** unless there's strong user demand, as the complexity is high and existing virtual audio tools provide adequate workarounds.

## Implementation Issue

A follow-up implementation issue should be created with:
- **Title:** "Feature: Add loopback audio capture support"
- **Scope:** Phase 1 (Basic Loopback) 
- **Reference:** This investigation document
- **Technical approach:** Separate LoopbackStream class
- **Estimated effort:** 2-3 days

## References

- [NAudio Documentation - WASAPI Loopback Recording](https://github.com/naudio/NAudio/blob/master/Docs/RecordingLevelsMeter.md)
- [Microsoft WASAPI Documentation](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- Current codebase: `Models/Audio/Streaming/MicrophoneStream.cs`
