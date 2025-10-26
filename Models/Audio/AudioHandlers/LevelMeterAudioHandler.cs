using NAudio.Wave;
using System;
using System.Threading;

namespace GoombaCast.Models.Audio.AudioHandlers
{
    // Publishes stereo dBFS levels for UI without modifying audio.
    public sealed class LevelMeterAudioHandler : AudioHandler
    {
        public int Order { get; init; } = 100; // default after processing like gain
        public bool Enabled { get; set; } = true;

        public bool UseRmsLevels { get; set; } = true;
        public float LevelFloorDb { get; set; } = -90f;

        // If set (e.g., to SynchronizationContext.Current on the UI thread), events are marshalled there.
        public SynchronizationContext? CallbackContext { get; set; }

        public event Action<float, float>? LevelsAvailable;

        private WaveFormat? _fmt;

        public void OnStart(WaveFormat waveFormat)
        {
            _fmt = waveFormat;
            CallbackContext ??= SynchronizationContext.Current;
        }

        public void OnStop() { }

        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            if (!Enabled) return;

            // Support stereo 16-bit PCM; if mono, report same value on both channels.
            if (waveFormat.Encoding != WaveFormatEncoding.Pcm || waveFormat.BitsPerSample != 16)
                return;

            int channels = waveFormat.Channels;
            int bps = waveFormat.BitsPerSample / 8; // 2
            int frameSize = bps * channels;
            int frames = count / frameSize;
            if (frames <= 0) return;

            float leftDb, rightDb;

            if (UseRmsLevels)
            {
                double sumL = 0, sumR = 0;
                int idx = offset;

                for (int i = 0; i < frames; i++)
                {
                    short l = (short)(buffer[idx] | buffer[idx + 1] << 8);
                    double nl = l / 32768.0;
                    sumL += nl * nl;

                    if (channels > 1)
                    {
                        short r = (short)(buffer[idx + 2] | buffer[idx + 3] << 8);
                        double nr = r / 32768.0;
                        sumR += nr * nr;
                    }

                    idx += frameSize;
                }

                double rmsL = Math.Sqrt(sumL / frames);
                double rmsR = channels > 1 ? Math.Sqrt(sumR / frames) : rmsL;

                leftDb = ToDb((float)rmsL, LevelFloorDb);
                rightDb = ToDb((float)rmsR, LevelFloorDb);
            }
            else
            {
                int maxAbsL = 0, maxAbsR = 0;
                int idx = offset;

                for (int i = 0; i < frames; i++)
                {
                    short l = (short)(buffer[idx] | buffer[idx + 1] << 8);
                    int al = Math.Abs(l);
                    if (al > maxAbsL) maxAbsL = al;

                    if (channels > 1)
                    {
                        short r = (short)(buffer[idx + 2] | buffer[idx + 3] << 8);
                        int ar = Math.Abs(r);
                        if (ar > maxAbsR) maxAbsR = ar;
                    }

                    idx += frameSize;
                }

                double peakL = maxAbsL / 32768.0;
                double peakR = channels > 1 ? maxAbsR / 32768.0 : peakL;

                leftDb = ToDb((float)peakL, LevelFloorDb);
                rightDb = ToDb((float)peakR, LevelFloorDb);
            }

            var handler = LevelsAvailable;
            if (handler != null)
            {
                var ctx = CallbackContext;
                if (ctx != null)
                    ctx.Post(_ => handler(leftDb, rightDb), null);
                else
                    handler(leftDb, rightDb);
            }
        }

        private static float ToDb(float level01, float floorDb)
        {
            if (level01 <= 0f) return float.NegativeInfinity;
            float db = 20f * (float)Math.Log10(level01);
            if (float.IsNaN(db) || float.IsInfinity(db)) return float.NegativeInfinity;
            return Math.Max(db, floorDb);
        }
    }
}