using GoombaCast.Models.Audio.AudioProcessing;
using GoombaCast.Models.Audio.Streaming;
using GoombaCast.Services;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

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

            // Adaptive jitter buffer settings
            private const int MinBufferBytes = 7680;    // ~40ms (was 3840)
            private const int TargetBufferBytes = 15360; // ~80ms (was 7680)
            private const int MaxBufferBytes = 38400;    // ~200ms (was 19200)

            public SourceBuffer(AudioInputSource source)
            {
                Source = source;
            }

            /// <summary>
            /// Clears all buffered audio data
            /// </summary>
            public void ClearBuffer()
            {
                lock (_buffer)
                {
                    _buffer.Clear();
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
                        }
                    }

                    // Append new data
                    for (int i = 0; i < count; i++)
                    {
                        _buffer.Enqueue(buffer[offset + i]);
                    }
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
                        data = new byte[requestedBytes];

                        double ratio = (double)availableBytes / requestedBytes;
                        byte[] availableData = new byte[availableBytes];

                        for (int i = 0; i < availableBytes; i++)
                        {
                            availableData[i] = _buffer.Dequeue();
                        }

                        // Stretch by linear interpolation
                        AudioResampler.StretchAudio(availableData, data, requestedBytes);

                        return true;
                    }
                    else // send silence
                    {
                        data = new byte[requestedBytes];
                        Array.Clear(data, 0, requestedBytes);
                        return true;
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

                    if (!sourceBuffer.TryConsume(count, out var inputData))
                        continue;

                    bool shouldMix = !source.IsMuted && (!hasSolo || source.IsSolo);

                    if (shouldMix)
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
                                    float sample = s < 0 ? s / (AudioFormatConverter.MaxInt16ValueFloat + 1.0f) : s / AudioFormatConverter.MaxInt16ValueFloat;
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

                            int intSample = (int)(sample * AudioFormatConverter.MaxInt16ValueFloat);
                            shortPtr[i] = (short)Math.Clamp(intSample, short.MinValue, short.MaxValue);
                        }
                    }
                }

                Array.Copy(_outputBuffer, 0, buffer, offset, count);
            }
        }

        public void ClearInputSources()
        {
            lock (_mixLock)
            {
                _inputBuffers.Clear();
            }
        }
    }
}