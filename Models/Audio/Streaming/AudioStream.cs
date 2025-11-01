using GoombaCast.Models.Audio.AudioHandlers;
using NAudio.Wave;
using System;

namespace GoombaCast.Models.Audio.Streaming
{
    public interface IAudioStream : IDisposable
    {
        WaveFormat? WaveFormat { get; }
        bool IsRunning { get; }

        void Start();
        void Stop();
        void AddAudioHandler(IAudioHandler handler);
        bool RemoveAudioHandler(IAudioHandler handler);
        bool ChangeDevice(string? deviceId);
    }
}