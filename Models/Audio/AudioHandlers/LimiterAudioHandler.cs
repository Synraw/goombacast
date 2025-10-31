using NAudio.Wave;
using System;

namespace GoombaCast.Models.Audio.AudioHandlers
{
    public class LimiterAudioHandler : AudioHandler
    {
        private bool _enabled = true;
        private float _threshold = -3.0f; // Default threshold in dB
        private float[] _buffer = new float[4096];
        private const int MaxInt16Value = 32768; // 2^15

        public int Order => 2; // After gain, before level meter

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public float ThresholdDb
        {
            get => _threshold;
            set => _threshold = value;
        }

        public void OnStart(WaveFormat waveFormat) { }

        public void OnStop() { }

        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat format)
        {
            if (!Enabled) return;

            // Convert threshold from dB to linear amplitude
            float threshold = (float)Math.Pow(10, ThresholdDb / 20.0);

            // Convert byte array to float samples for processing
            int floatCount = count / 2; // 16-bit samples
            if (_buffer.Length < floatCount)
                Array.Resize(ref _buffer, floatCount);

            unsafe
            {
                fixed (byte* ptr = buffer)
                fixed (float* floatPtr = _buffer)
                {
                    // Convert to float samples
                    for (int i = 0; i < floatCount; i++)
                    {
                        short sample = *(short*)(ptr + offset + (i * 2));
                        // Use the same normalization as the level meter
                        floatPtr[i] = sample / (float)(MaxInt16Value - (sample < 0 ? 1 : 0));
                    }

                    // Apply limiting
                    for (int i = 0; i < floatCount; i++)
                    {
                        float abs = Math.Abs(floatPtr[i]);
                        if (abs > threshold)
                        {
                            floatPtr[i] = Math.Sign(floatPtr[i]) * threshold;
                        }
                    }

                    // Convert back to Int16 samples
                    for (int i = 0; i < floatCount; i++)
                    {
                        short sample = (short)(floatPtr[i] * MaxInt16Value);
                        *(short*)(ptr + offset + (i * 2)) = sample;
                    }
                }
            }
        }
    }
}