using System;
using System.Runtime.CompilerServices;

namespace GoombaCast.Models.Audio.AudioProcessing
{
    /// <summary>
    /// Provides high-quality audio resampling using windowed Sinc interpolation
    /// </summary>
    public static class AudioResampler
    {
        public const int TargetSampleRate = 48000; //48khz

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
        /// Stretches audio data to fit requested size using cubic interpolation for better quality
        /// </summary>
        public static unsafe void StretchAudio(byte[] source, byte[] dest, int destBytes)
        {
            int sourceSamples = source.Length / 4; // Stereo 16-bit
            int destSamples = destBytes / 4;

            // Edge case: if stretching by less than 1%, just copy
            if (Math.Abs(sourceSamples - destSamples) < sourceSamples * 0.01)
            {
                Buffer.BlockCopy(source, 0, dest, 0, Math.Min(source.Length, destBytes));
                return;
            }

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

                    if (srcIndex + 2 < sourceSamples && srcIndex > 0)
                    {
                        // Cubic (Catmull-Rom) interpolation for smoother results
                        // Uses 4 points: p0, p1, p2, p3 where we interpolate between p1 and p2
                        
                        // LEFT channel
                        short leftP0 = srcShort[(srcIndex - 1) * 2];
                        short leftP1 = srcShort[srcIndex * 2];
                        short leftP2 = srcShort[(srcIndex + 1) * 2];
                        short leftP3 = srcShort[(srcIndex + 2) * 2];
                        
                        dstShort[i * 2] = (short)CubicInterpolate(leftP0, leftP1, leftP2, leftP3, frac);

                        // RIGHT channel
                        short rightP0 = srcShort[(srcIndex - 1) * 2 + 1];
                        short rightP1 = srcShort[srcIndex * 2 + 1];
                        short rightP2 = srcShort[(srcIndex + 1) * 2 + 1];
                        short rightP3 = srcShort[(srcIndex + 2) * 2 + 1];
                        
                        dstShort[i * 2 + 1] = (short)CubicInterpolate(rightP0, rightP1, rightP2, rightP3, frac);
                    }
                    else if (srcIndex + 1 < sourceSamples)
                    {
                        // Fallback to linear interpolation at boundaries
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
                        int lastIdx = Math.Min(srcIndex, sourceSamples - 1);
                        dstShort[i * 2] = srcShort[lastIdx * 2];
                        dstShort[i * 2 + 1] = srcShort[lastIdx * 2 + 1];
                    }
                }
            }
        }

        /// <summary>
        /// Performs Catmull-Rom cubic interpolation for smoother audio stretching
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CubicInterpolate(short p0, short p1, short p2, short p3, double t)
        {
            double a0 = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;
            double a1 = p0 - 2.5 * p1 + 2.0 * p2 - 0.5 * p3;
            double a2 = -0.5 * p0 + 0.5 * p2;
            double a3 = p1;

            double result = a0 * t * t * t + a1 * t * t + a2 * t + a3;
            return Math.Clamp((int)result, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Applies anti-aliasing filter before resampling to prevent aliasing artifacts
        /// </summary>
        public static unsafe void ApplyAntiAliasingFilter(
            byte[] buffer, 
            int length, 
            int sourceSampleRate,
            ref float[] filterState)
        {
            // Calculate appropriate filter order based on sample rate ratio
            // Higher decimation = need stronger filter
            double ratio = (double)sourceSampleRate / TargetSampleRate;
            int filterOrder = ratio > 1.0 
                ? Math.Clamp((int)(ratio * 16), 16, 64)  // Downsampling: stronger filter
                : 16;  // Upsampling: lighter filter
            
            int filterStateSize = filterOrder * 2; // Stereo channels

            // Initialize or resize filter state if needed
            if (filterState == null || filterState.Length != filterStateSize)
            {
                filterState = new float[filterStateSize];
                Array.Clear(filterState, 0, filterState.Length);
            }

            // Calculate cutoff frequency (Nyquist of target rate)
            double cutoffFreq = Math.Min(TargetSampleRate / 2.0, sourceSampleRate / 2.0) / sourceSampleRate;
            
            int samples = length / 4; // Stereo 16-bit frames

            fixed (byte* bufPtr = buffer)
            fixed (float* statePtr = filterState)
            {
                short* shortPtr = (short*)bufPtr;

                float* leftState = statePtr;
                float* rightState = statePtr + filterOrder;

                for (int i = 0; i < samples; i++)
                {
                    // Process LEFT channel
                    float leftSample = shortPtr[i * 2];
                    float leftFiltered = 0f;

                    // Apply FIR filter with windowed sinc coefficients
                    for (int j = 0; j < filterOrder - 1; j++)
                    {
                        int tapIndex = j - filterOrder / 2;
                        float coefficient = (float)CalculateFilterCoefficient(tapIndex, cutoffFreq, filterOrder);
                        leftFiltered += leftState[j] * coefficient;
                        leftState[j] = leftState[j + 1];
                    }
                    
                    leftState[filterOrder - 1] = leftSample;
                    leftFiltered += leftSample * (float)CalculateFilterCoefficient(filterOrder / 2, cutoffFreq, filterOrder);

                    shortPtr[i * 2] = (short)Math.Clamp((int)leftFiltered, short.MinValue, short.MaxValue);

                    // Process RIGHT channel
                    float rightSample = shortPtr[i * 2 + 1];
                    float rightFiltered = 0f;

                    for (int j = 0; j < filterOrder - 1; j++)
                    {
                        int tapIndex = j - filterOrder / 2;
                        float coefficient = (float)CalculateFilterCoefficient(tapIndex, cutoffFreq, filterOrder);
                        rightFiltered += rightState[j] * coefficient;
                        rightState[j] = rightState[j + 1];
                    }
                    
                    rightState[filterOrder - 1] = rightSample;
                    rightFiltered += rightSample * (float)CalculateFilterCoefficient(filterOrder / 2, cutoffFreq, filterOrder);

                    shortPtr[i * 2 + 1] = (short)Math.Clamp((int)rightFiltered, short.MinValue, short.MaxValue);
                }
            }
        }

        /// <summary>
        /// Calculates FIR filter coefficient using windowed sinc
        /// </summary>
        private static double CalculateFilterCoefficient(int tap, double cutoffFreq, int filterOrder)
        {
            if (tap == 0)
                return 2.0 * cutoffFreq;

            // Sinc function
            double x = tap * Math.PI * 2.0 * cutoffFreq;
            double sinc = Math.Sin(x) / x;

            // Hamming window
            double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * (tap + filterOrder / 2.0) / filterOrder);

            return sinc * window * 2.0 * cutoffFreq;
        }

        /// <summary>
        /// Stretches audio data to fit requested size using high-quality Sinc interpolation
        /// </summary>
        public static unsafe void StretchAudioHighQuality(byte[] source, byte[] dest, int destBytes)
        {
            int sourceSamples = source.Length / 4;
            int destSamples = destBytes / 4;
            
            double ratio = (double)sourceSamples / destSamples;
            const int sincRadius = 3; // Smaller radius than resampling since we're just stretching

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
        }
    }
}