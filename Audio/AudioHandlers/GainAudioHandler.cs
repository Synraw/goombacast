using NAudio.Wave;
using System;
using GoombaCast.Audio.AudioHandlers;

namespace GoombaCast.Audio.AudioHandlers
{
    // In-place gain (pre-encoder). Assumes 16-bit PCM.
    public sealed class GainAudioHandler : AudioHandler
    {
        public int Order { get; init; } = 0;
        public bool Enabled { get; set; } = true;

        // dB gain applied to samples. 0 = unity.
        private double _gainDb;
        private double _gainLinear = 1.0;

        public double GainDb
        {
            get => _gainDb;
            set
            {
                _gainDb = value;
                _gainLinear = Math.Pow(10.0, _gainDb / 20.0);
            }
        }

        private WaveFormat? _fmt;

        public void OnStart(WaveFormat waveFormat)
        {
            _fmt = waveFormat;
        }

        public void OnStop() { }

        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            if (!Enabled) return;
            // Only 16-bit PCM supported for in-place fast path
            if (waveFormat.Encoding != WaveFormatEncoding.Pcm || waveFormat.BitsPerSample != 16)
                return;

            int end = offset + count;
            // Little-endian 16-bit samples
            for (int i = offset; i < end; i += 2)
            {
                short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                int amplified = (int)Math.Round(s * _gainLinear);

                if (amplified > short.MaxValue) amplified = short.MaxValue;
                else if (amplified < short.MinValue) amplified = short.MinValue;

                buffer[i] = (byte)(amplified & 0xFF);
                buffer[i + 1] = (byte)((amplified >> 8) & 0xFF);
            }
        }
    }
}