using System;

namespace GoombaCast.Models.Audio.AudioProcessing
{
    /// <summary>
    /// Provides high-quality audio resampling using windowed Sinc interpolation
    /// </summary>
    public static class AudioResampler
    {
        const int TargetSampleRate = 48000; //48khz

        /// <summary>
        /// Resamples 16-bit stereo PCM to 48kHz using Sinc interpolation
        /// </summary>
        public static unsafe int ResampleTo48kHz(
            byte[] source, 
            int sourceBytes, 
            int sourceSampleRate, 
            byte[] dest,
            ref byte[] resampleBuffer)
        {
            int sourceSamples = sourceBytes / 4;
            double ratio = (double)sourceSampleRate / TargetSampleRate;
            int targetSamples = (int)(sourceSamples / ratio);
            int targetBytes = targetSamples * 4;

            if (dest.Length < targetBytes)
            {
                Array.Resize(ref resampleBuffer, targetBytes);
                dest = resampleBuffer;
            }

            fixed (byte* srcPtr = source)
            fixed (byte* dstPtr = dest)
            {
                short* srcShort = (short*)srcPtr;
                short* dstShort = (short*)dstPtr;

                const int sincRadius = 4;

                for (int i = 0; i < targetSamples; i++)
                {
                    double srcPos = i * ratio;
                    int srcIndex = (int)srcPos;
                    double frac = srcPos - srcIndex;

                    float leftSum = 0;
                    float rightSum = 0;
                    float weightSum = 0;

                    for (int j = -sincRadius; j <= sincRadius; j++)
                    {
                        int sampleIdx = srcIndex + j;
                        if (sampleIdx < 0 || sampleIdx >= sourceSamples)
                            continue;

                        double x = frac - j;
                        float weight = (float)SincWindow(x, sincRadius);

                        leftSum += srcShort[sampleIdx * 2] * weight;
                        rightSum += srcShort[sampleIdx * 2 + 1] * weight;
                        weightSum += weight;
                    }

                    if (weightSum > 0)
                    {
                        leftSum /= weightSum;
                        rightSum /= weightSum;
                    }

                    dstShort[i * 2] = (short)Math.Clamp((int)leftSum, short.MinValue, short.MaxValue);
                    dstShort[i * 2 + 1] = (short)Math.Clamp((int)rightSum, short.MinValue, short.MaxValue);
                }
            }

            return targetBytes;
        }

        /// <summary>
        /// Fast resampling method - lower quality, no filtering, just drops samples to match target rate
        /// </summary>
        public static unsafe int ResampleTo48kHzFast(
            byte[] source, 
            int sourceBytes, 
            int sourceSampleRate, 
            byte[] dest)
        {
            int sourceSamples = sourceBytes / 4;
            double ratio = (double)sourceSampleRate / TargetSampleRate;
            int targetSamples = (int)(sourceSamples / ratio);

            fixed (byte* srcPtr = source, dstPtr = dest)
            {
                short* srcShort = (short*)srcPtr;
                short* dstShort = (short*)dstPtr;

                for (int i = 0; i < targetSamples; i++)
                {
                    int srcIndex = (int)(i * ratio);
                    if (srcIndex < sourceSamples)
                    {
                        dstShort[i * 2] = srcShort[srcIndex * 2];
                        dstShort[i * 2 + 1] = srcShort[srcIndex * 2 + 1];
                    }
                }
            }

            return targetSamples * 4;
        }

        /// <summary>
        /// Windowed Sinc function for high-quality resampling (Blackman window)
        /// </summary>
        public static double SincWindow(double x, int radius)
        {
            if (Math.Abs(x) < 0.0001)
                return 1.0;

            double pix = Math.PI * x;
            double sinc = Math.Sin(pix) / pix;

            // Blackman window for smoothing
            double window = 0.42 - 0.5 * Math.Cos(Math.PI * (x + radius) / radius)
                                 + 0.08 * Math.Cos(2 * Math.PI * (x + radius) / radius);

            return sinc * window;
        }

        /// <summary>
        /// Stretches audio data to fit requested size using linear interpolation
        /// </summary>
        public static unsafe void StretchAudio(byte[] source, byte[] dest, int destBytes)
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

        /// <summary>
        /// Applies anti-aliasing filter before resampling to prevent aliasing artifacts
        /// </summary>
        public static unsafe void ApplyAntiAliasingFilter(
            byte[] buffer, 
            int length, 
            int sampleRate,
            ref float[] filterState)
        {
            const int filterOrder = 8;

            // Separate filter states for left and right channels
            if (filterState == null || filterState.Length != filterOrder * 2)
            {
                filterState = new float[filterOrder * 2];
                Array.Clear(filterState, 0, filterState.Length);
            }

            int samples = length / 4; // Stereo 16-bit frames

            fixed (byte* bufPtr = buffer)
            fixed (float* statePtr = filterState)
            {
                short* shortPtr = (short*)bufPtr;

                // Split filter state between channels
                float* leftState = statePtr;
                float* rightState = statePtr + filterOrder;

                // Process each stereo frame independently
                for (int i = 0; i < samples; i++)
                {
                    // Process LEFT channel
                    float leftSample = shortPtr[i * 2];
                    float leftFiltered = leftSample * 0.5f;

                    for (int j = 0; j < filterOrder - 1; j++)
                    {
                        leftFiltered += leftState[j] * (0.5f / filterOrder);
                        leftState[j] = leftState[j + 1];
                    }
                    leftState[filterOrder - 1] = leftSample;

                    shortPtr[i * 2] = (short)Math.Clamp((int)leftFiltered, short.MinValue, short.MaxValue);

                    // Process RIGHT channel independently
                    float rightSample = shortPtr[i * 2 + 1];
                    float rightFiltered = rightSample * 0.5f;

                    for (int j = 0; j < filterOrder - 1; j++)
                    {
                        rightFiltered += rightState[j] * (0.5f / filterOrder);
                        rightState[j] = rightState[j + 1];
                    }
                    rightState[filterOrder - 1] = rightSample;

                    shortPtr[i * 2 + 1] = (short)Math.Clamp((int)rightFiltered, short.MinValue, short.MaxValue);
                }
            }
        }
    }
}