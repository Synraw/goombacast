using NAudio.Wave;
using System;
using System.Threading;

namespace GoombaCast.Models.Audio.AudioHandlers
{
    /// <summary>
    /// Publishes stereo dBFS levels for UI without modifying audio.
    /// Supports 16-bit PCM audio in both mono and stereo formats.
    /// </summary>
    public sealed class LevelMeterAudioHandler : AudioHandler
    {
        private const int BitsInWord = 16;
        private const int MaxInt16Value = 32768; // 2^15
        private const float DefaultClippingThreshold = 0.95f;
        private const float DefaultLevelFloorDb = -90f;

        private float _clippingThreshold = DefaultClippingThreshold;
        private int _clippingThresholdValue = (int)(MaxInt16Value * DefaultClippingThreshold);
        private readonly object _syncLock = new();

        public string FriendlyName => "Level Meter";
        public int Order { get; init; } = 100;
        public bool Enabled { get; set; } = true;
        public bool UseRmsLevels { get; set; } = true;
        public float LevelFloorDb { get; set; } = DefaultLevelFloorDb;
        
        public float ClippingThreshold
        {
            get => _clippingThreshold;
            set
            {
                if (value is <= 0f or > 1f)
                    throw new ArgumentOutOfRangeException(nameof(value), "Clipping threshold must be between 0 and 1");
                
                lock (_syncLock)
                {
                    _clippingThreshold = value;
                    _clippingThresholdValue = (int)(MaxInt16Value * value);
                }
            }
        }

        public SynchronizationContext? CallbackContext { get; set; }

        public event Action<float, float>? LevelsAvailable;
        public event Action<bool>? ClippingDetected;

        private WaveFormat? _fmt;

        public void OnStart(WaveFormat waveFormat)
        {
            ArgumentNullException.ThrowIfNull(waveFormat);
            _fmt = waveFormat;
            CallbackContext ??= SynchronizationContext.Current;
        }

        public void OnStop() => _fmt = null;

        public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            if (!Enabled || buffer == null || count <= 0) return;

            // Validate format
            if (waveFormat.Encoding != WaveFormatEncoding.Pcm || 
                waveFormat.BitsPerSample != BitsInWord)
                return;

            ProcessBufferInternal(buffer, offset, count, waveFormat);
        }

        private unsafe void ProcessBufferInternal(byte[] buffer, int offset, int count, WaveFormat waveFormat)
        {
            int channels = waveFormat.Channels;
            int frameSize = (waveFormat.BitsPerSample / 8) * channels;
            int frames = count / frameSize;

            var (leftDb, rightDb, isClipping) = UseRmsLevels ? 
                CalculateRmsLevels(buffer, offset, frames, channels) :
                CalculatePeakLevels(buffer, offset, frames, channels);

            RaiseEvents(leftDb, rightDb, isClipping);
        }

        private unsafe (float leftDb, float rightDb, bool isClipping) CalculateRmsLevels(
            byte[] buffer, int offset, int frames, int channels)
        {
            double sumL = 0, sumR = 0;
            bool isClipping = false;
            int idx = offset;
            int clippingThreshold;

            lock (_syncLock)
            {
                clippingThreshold = _clippingThresholdValue;
            }

            fixed (byte* ptr = buffer)
            {
                for (int i = 0; i < frames; i++)
                {
                    // Get left channel sample and normalize
                    short l = *(short*)(ptr + idx);
                    // Normalize to [-1.0, 1.0] range
                    double nl = l / (double)(MaxInt16Value - (l < 0 ? 1 : 0));
                    sumL += nl * nl;

                    if (l >= clippingThreshold || l <= -clippingThreshold)
                        isClipping = true;

                    if (channels > 1)
                    {
                        // Get right channel sample and normalize
                        short r = *(short*)(ptr + idx + 2);
                        // Normalize to [-1.0, 1.0] range
                        double nr = r / (double)(MaxInt16Value - (r < 0 ? 1 : 0));
                        sumR += nr * nr;

                        if (r >= clippingThreshold || r <= -clippingThreshold)
                            isClipping = true;
                    }

                    idx += channels * sizeof(short);
                }
            }

            // Calculate RMS values
            double rmsL = Math.Sqrt(sumL / frames);
            double rmsR = channels > 1 ? Math.Sqrt(sumR / frames) : rmsL;

            return (ToDb((float)rmsL, LevelFloorDb),
                   ToDb((float)rmsR, LevelFloorDb),
                   isClipping);
        }

        private unsafe (float leftDb, float rightDb, bool isClipping) CalculatePeakLevels(
            byte[] buffer, int offset, int frames, int channels)
        {
            int maxAbsL = 0, maxAbsR = 0;
            bool isClipping = false;
            int idx = offset;
            int clippingThreshold;

            lock (_syncLock)
            {
                clippingThreshold = _clippingThresholdValue;
            }

            fixed (byte* ptr = buffer)
            {
                for (int i = 0; i < frames; i++)
                {
                    // Get left channel sample
                    short l = *(short*)(ptr + idx);
                    // Handle minimum Int16 value correctly
                    int absL = l == short.MinValue ? MaxInt16Value : Math.Abs(l);
                    maxAbsL = Math.Max(maxAbsL, absL);

                    if (l >= clippingThreshold || l <= -clippingThreshold)
                        isClipping = true;

                    if (channels > 1)
                    {
                        // Get right channel sample
                        short r = *(short*)(ptr + idx + 2);
                        // Handle minimum Int16 value correctly
                        int absR = r == short.MinValue ? MaxInt16Value : Math.Abs(r);
                        maxAbsR = Math.Max(maxAbsR, absR);

                        if (r >= clippingThreshold || r <= -clippingThreshold)
                            isClipping = true;
                    }

                    idx += channels * sizeof(short);
                }
            }

            // Normalize peak values to [0.0, 1.0] range
            double peakL = maxAbsL / (double)MaxInt16Value;
            double peakR = channels > 1 ? maxAbsR / (double)MaxInt16Value : peakL;

            return (ToDb((float)peakL, LevelFloorDb),
                   ToDb((float)peakR, LevelFloorDb),
                   isClipping);
        }

        private void RaiseEvents(float leftDb, float rightDb, bool isClipping)
        {
            var ctx = CallbackContext;
            
            var levelsHandler = LevelsAvailable;
            if (levelsHandler != null)
            {
                if (ctx != null)
                    ctx.Post(_ => levelsHandler(leftDb, rightDb), null);
                else
                    levelsHandler(leftDb, rightDb);
            }

            var clippingHandler = ClippingDetected;
            if (clippingHandler != null)
            {
                if (ctx != null)
                    ctx.Post(_ => clippingHandler(isClipping), null);
                else
                    clippingHandler(isClipping);
            }
        }

        private static float ToDb(float level01, float floorDb)
        {
            if (level01 <= 0f) return float.NegativeInfinity;
            float db = 20f * MathF.Log10(level01);
            return float.IsFinite(db) ? Math.Max(db, floorDb) : float.NegativeInfinity;
        }
    }
}