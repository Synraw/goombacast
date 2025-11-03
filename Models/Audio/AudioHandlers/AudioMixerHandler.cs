using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;

namespace GoombaCast.Models.Audio.AudioHandlers
{
    /// <summary>
    /// Mixes multiple audio input sources with individual volume control
    /// </summary>
    public sealed class AudioMixerHandler : IAudioHandler
    {
        private readonly ConcurrentDictionary<Guid, SourceBuffer> _inputBuffers = new();
        private float[] _mixBuffer = new float[4096];
        private byte[] _outputBuffer = new byte[4096];
        private readonly object _mixLock = new();
        private float _masterVolume = 1.0f;

        public string FriendlyName => "Audio Mixer";
        public int Order => -1;
        public bool Enabled { get; set; } = true;

        public float MasterVolume
        {
            get => _masterVolume;
            set => _masterVolume = Math.Clamp(value, 0f, 2f);
        }

        public List<AudioInputSource> InputSources { get; } = new();

        /// <summary>
        /// Holds accumulated audio data with adaptive buffering
        /// </summary>
        private class SourceBuffer
        {
            public AudioInputSource Source { get; }
            private readonly Queue<byte> _buffer = new();
            public long LastUpdateTime { get; set; }

            // Adaptive jitter buffer settings
            private const int MinBufferBytes = 3840;  // ~20ms @ 48kHz stereo (minimum latency)
            private const int TargetBufferBytes = 7680; // ~40ms @ 48kHz stereo (target)
            private const int MaxBufferBytes = 19200;   // ~100ms @ 48kHz stereo (maximum)

            public int UnderrunCount { get; private set; }
            public int OverflowCount { get; private set; }
            private DateTime _lastStatsLog = DateTime.UtcNow;

            public SourceBuffer(AudioInputSource source)
            {
                Source = source;
                LastUpdateTime = DateTime.UtcNow.Ticks;
            }

            /// <summary>
            /// Clears all buffered audio data
            /// </summary>
            public void ClearBuffer()
            {
                lock (_buffer)
                {
                    _buffer.Clear();
                    UnderrunCount = 0;
                    OverflowCount = 0;
                    LastUpdateTime = DateTime.UtcNow.Ticks;
                }
            }

            public void AppendData(byte[] buffer, int offset, int count)
            {
                lock (_buffer)
                {
                    // Check if we're approaching overflow
                    if (_buffer.Count + count > MaxBufferBytes)
                    {
                        // Calculate how much to drop to get back to target
                        int excessBytes = (_buffer.Count + count) - TargetBufferBytes;

                        // Only drop in 4-byte (stereo sample) increments
                        excessBytes = (excessBytes / 4) * 4;

                        if (excessBytes > 0 && excessBytes <= _buffer.Count)
                        {
                            // Drop from the front (oldest data)
                            for (int i = 0; i < excessBytes; i++)
                            {
                                _buffer.Dequeue();
                            }

                            OverflowCount++;

                            // Log stats every 2 seconds
                            if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= 2)
                            {
                                Logging.LogWarning($"[{Source.Name}] Buffer adjusted: dropped {excessBytes} bytes to prevent overflow (overflows: {OverflowCount}, underruns: {UnderrunCount})");
                                _lastStatsLog = DateTime.UtcNow;
                            }
                        }
                    }

                    // Append new data
                    for (int i = 0; i < count; i++)
                    {
                        _buffer.Enqueue(buffer[offset + i]);
                    }

                    LastUpdateTime = DateTime.UtcNow.Ticks;
                }
            }

            public bool TryConsume(int requestedBytes, out byte[] data)
            {
                lock (_buffer)
                {
                    int availableBytes = _buffer.Count;

                    // Adaptive strategy based on buffer fill level
                    if (availableBytes >= requestedBytes)
                    {
                        // We have enough data - consume exact amount
                        data = new byte[requestedBytes];
                        for (int i = 0; i < requestedBytes; i++)
                        {
                            data[i] = _buffer.Dequeue();
                        }
                        return true;
                    }
                    else if (availableBytes >= MinBufferBytes)
                    {
                        // We have minimum data - stretch what we have to fit
                        // This provides lower latency at the cost of slight quality degradation
                        data = new byte[requestedBytes];

                        // Simple sample stretching (repeat samples to fill gaps)
                        double ratio = (double)availableBytes / requestedBytes;
                        byte[] availableData = new byte[availableBytes];

                        for (int i = 0; i < availableBytes; i++)
                        {
                            availableData[i] = _buffer.Dequeue();
                        }

                        // Stretch by linear interpolation
                        StretchAudio(availableData, data, requestedBytes);

                        UnderrunCount++;

                        if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= 2)
                        {
                            Logging.LogWarning($"[{Source.Name}] Buffer underrun: stretched {availableBytes} to {requestedBytes} bytes (overflows: {OverflowCount}, underruns: {UnderrunCount})");
                            _lastStatsLog = DateTime.UtcNow;
                        }

                        return true;
                    }
                    else
                    {
                        // Not enough data at all - return silence
                        data = new byte[requestedBytes];
                        Array.Clear(data, 0, requestedBytes);

                        UnderrunCount++;

                        if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= 2)
                        {
                            Logging.LogWarning($"[{Source.Name}] Severe underrun: only {availableBytes} bytes available, returning silence");
                            _lastStatsLog = DateTime.UtcNow;
                        }

                        return true; // Return true but with silence
                    }
                }
            }

            /// <summary>
            /// Stretches audio data to fit requested size using linear interpolation
            /// </summary>
            private unsafe void StretchAudio(byte[] source, byte[] dest, int destBytes)
            {
                int sourceSamples = source.Length / 4; // Stereo 16-bit
                int destSamples = destBytes / 4;

                double ratio = (double)sourceSamples / destSamples;

                fixed (byte* srcPtr = source)
                fixed (byte* dstPtr = dest)
                {
                    short* srcShort = (short*)srcPtr;
                    short* dstShort = (short*)dstPtr;

                    for (int i = 0; i < destSamples; i++)
                    {
                        double srcPos = i * ratio;
                        int srcIndex = (int)srcPos;
                        double frac = srcPos - srcIndex;

                        if (srcIndex + 1 < sourceSamples)
                        {
                            // Linear interpolation
                            short leftA = srcShort[srcIndex * 2];
                            short leftB = srcShort[(srcIndex + 1) * 2];
                            short rightA = srcShort[srcIndex * 2 + 1];
                            short rightB = srcShort[(srcIndex + 1) * 2 + 1];

                            dstShort[i * 2] = (short)(leftA + (leftB - leftA) * frac);
                            dstShort[i * 2 + 1] = (short)(rightA + (rightB - rightA) * frac);
                        }
                        else
                        {
                            // At the end, just copy last sample
                            dstShort[i * 2] = srcShort[srcIndex * 2];
                            dstShort[i * 2 + 1] = srcShort[srcIndex * 2 + 1];
                        }
                    }
                }
            }

            public int AvailableBytes
            {
                get
                {
                    lock (_buffer)
                    {
                        return _buffer.Count;
                    }
                }
            }
        }

        public void AddInputSource(AudioInputSource source)
        {
            lock (_mixLock)
            {
                if (!InputSources.Contains(source))
                {
                    InputSources.Add(source);
                    _inputBuffers[source.Id] = new SourceBuffer(source);
                }
            }
        }

        public void RemoveInputSource(AudioInputSource source)
        {
            lock (_mixLock)
            {
                InputSources.Remove(source);
                _inputBuffers.TryRemove(source.Id, out _);
            }
        }

        public void OnStart(WaveFormat waveFormat)
        {
            // Clear any residual buffer data from previous sessions
            lock (_mixLock)
            {
                foreach (var sourceBuffer in _inputBuffers.Values)
                {
                    sourceBuffer.ClearBuffer();
                }
            }
        }

        public void OnStop()
        {
            // Clear all buffers to prevent residual audio artifacts
            lock (_mixLock)
            {
                foreach (var sourceBuffer in _inputBuffers.Values)
                {
                    sourceBuffer.ClearBuffer();
                }
            }
            
            _inputBuffers.Clear();
        }

        /// <summary>
        /// Accumulates buffer data from a specific input source
        /// </summary>
        public void ProcessInputBuffer(Guid sourceId, byte[] buffer, int offset, int count)
        {
            if (_inputBuffers.TryGetValue(sourceId, out var sourceBuffer))
            {
                sourceBuffer.AppendData(buffer, offset, count);
            }
            else
            {
                var source = InputSources.FirstOrDefault(s => s.Id == sourceId);
                if (source != null)
                {
                    var newBuffer = new SourceBuffer(source);
                    _inputBuffers[sourceId] = newBuffer;
                    newBuffer.AppendData(buffer, offset, count);
                }
            }
        }

        /// <summary>
        /// Mixes all input buffers with adaptive jitter buffer
        /// </summary>
        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            if (!Enabled)
            {
                Array.Clear(buffer, offset, count);
                return;
            }

            lock (_mixLock)
            {
                if (_inputBuffers.IsEmpty)
                {
                    Array.Clear(buffer, offset, count);
                    return;
                }

                int sampleCount = count / 2;
                if (_mixBuffer.Length < sampleCount)
                {
                    Array.Resize(ref _mixBuffer, sampleCount);
                }
                if (_outputBuffer.Length < count)
                {
                    Array.Resize(ref _outputBuffer, count);
                }

                Array.Clear(_mixBuffer, 0, sampleCount);

                bool hasSolo = InputSources.Any(s => s.IsSolo);
                int sourcesMixed = 0;

                // Mix all input sources with adaptive buffering
                foreach (var sourceBuffer in _inputBuffers.Values)
                {
                    var source = sourceBuffer.Source;

                    if (source.IsMuted || (hasSolo && !source.IsSolo))
                        continue;

                    // Always try to consume - the buffer will handle underruns gracefully
                    if (sourceBuffer.TryConsume(count, out var inputData))
                    {
                        float sourceVolume = source.Volume;

                        unsafe
                        {
                            fixed (byte* inputPtr = inputData)
                            fixed (float* mixPtr = _mixBuffer)
                            {
                                short* shortPtr = (short*)inputPtr;
                                for (int i = 0; i < sampleCount; i++)
                                {
                                    short s = shortPtr[i];
                                    float sample = s < 0 ? s / 32768f : s / 32767f;
                                    mixPtr[i] += sample * sourceVolume;
                                }
                            }
                        }

                        sourcesMixed++;
                    }
                }

                // Apply soft clipping with master volume
                unsafe
                {
                    fixed (float* mixPtr = _mixBuffer)
                    fixed (byte* outputPtr = _outputBuffer)
                    {
                        short* shortPtr = (short*)outputPtr;
                        for (int i = 0; i < sampleCount; i++)
                        {
                            float sample = mixPtr[i] * _masterVolume;

                            // Soft clipping
                            if (Math.Abs(sample) > 0.95f)
                            {
                                sample = (float)Math.Tanh(sample * 1.2f) * 0.95f;
                            }

                            int intSample = (int)(sample * 32767f);
                            shortPtr[i] = (short)Math.Clamp(intSample, short.MinValue, short.MaxValue);
                        }
                    }
                }

                Array.Copy(_outputBuffer, 0, buffer, offset, count);
            }
        }
    }
}