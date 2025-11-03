using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GoombaCast.Models.Audio.Streaming;

namespace GoombaCast.Models.Audio.AudioHandlers
{
    /// <summary>
    /// Mixes multiple audio input sources with individual volume control
    /// </summary>
    public sealed class AudioMixerHandler : IAudioHandler
    {
        private readonly ConcurrentDictionary<Guid, (AudioInputSource Source, byte[] Buffer, int Count)> _inputBuffers = new();
        private float[] _mixBuffer = new float[4096];
        private byte[] _outputBuffer = new byte[4096];
        private readonly object _mixLock = new();
        private float _masterVolume = 1.0f;

        public string FriendlyName => "Audio Mixer";
        public int Order => -1; // Process before other handlers
        public bool Enabled { get; set; } = true;

        public float MasterVolume
        {
            get => _masterVolume;
            set => _masterVolume = Math.Clamp(value, 0f, 2f);
        }

        public List<AudioInputSource> InputSources { get; } = new();

        public void AddInputSource(AudioInputSource source)
        {
            lock (_mixLock)
            {
                if (!InputSources.Contains(source))
                {
                    InputSources.Add(source);
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
            // Initialize buffers based on format
        }

        public void OnStop()
        {
            _inputBuffers.Clear();
        }

        /// <summary>
        /// Stores buffer from a specific input source
        /// </summary>
        public void ProcessInputBuffer(Guid sourceId, byte[] buffer, int offset, int count)
        {
            var tempBuffer = new byte[count];
            Array.Copy(buffer, offset, tempBuffer, 0, count);
            
            var source = InputSources.FirstOrDefault(s => s.Id == sourceId);
            if (source != null)
            {
                _inputBuffers[sourceId] = (source, tempBuffer, count);
            }
        }

        /// <summary>
        /// Mixes all input buffers into the output buffer
        /// </summary>
        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            if (!Enabled || _inputBuffers.IsEmpty) return;

            lock (_mixLock)
            {
                // Ensure buffers are large enough
                int sampleCount = count / 2; // 16-bit samples
                if (_mixBuffer.Length < sampleCount)
                {
                    Array.Resize(ref _mixBuffer, sampleCount);
                }
                if (_outputBuffer.Length < count)
                {
                    Array.Resize(ref _outputBuffer, count);
                }

                Array.Clear(_mixBuffer, 0, sampleCount);

                // Check for solo sources
                bool hasSolo = InputSources.Any(s => s.IsSolo);

                // Mix all input sources
                foreach (var (source, inputBuffer, inputCount) in _inputBuffers.Values)
                {
                    // Skip if muted or if another source is soloed
                    if (source.IsMuted || (hasSolo && !source.IsSolo))
                        continue;

                    float sourceVolume = source.Volume;
                    int samplesToMix = Math.Min(sampleCount, inputCount / 2);

                    unsafe
                    {
                        fixed (byte* inputPtr = inputBuffer)
                        fixed (float* mixPtr = _mixBuffer)
                        {
                            short* shortPtr = (short*)inputPtr;
                            for (int i = 0; i < samplesToMix; i++)
                            {
                                float sample = shortPtr[i] / 32768f;
                                mixPtr[i] += sample * sourceVolume;
                            }
                        }
                    }
                }

                // Apply master volume and convert back to Int16
                unsafe
                {
                    fixed (float* mixPtr = _mixBuffer)
                    fixed (byte* outputPtr = _outputBuffer)
                    {
                        short* shortPtr = (short*)outputPtr;
                        for (int i = 0; i < sampleCount; i++)
                        {
                            float sample = mixPtr[i] * _masterVolume;
                            sample = Math.Clamp(sample, -1f, 1f);
                            shortPtr[i] = (short)(sample * 32767f);
                        }
                    }
                }

                // Copy mixed output to the buffer
                Array.Copy(_outputBuffer, 0, buffer, offset, count);

                // Clear processed buffers
                _inputBuffers.Clear();
            }
        }
    }
}