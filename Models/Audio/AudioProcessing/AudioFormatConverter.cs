using System;

namespace GoombaCast.Models.Audio.AudioProcessing
{
    /// <summary>
    /// Provides static utility methods for audio format conversion and processing
    /// </summary>
    public static class AudioFormatConverter
    {
        /// <summary>
        /// Maximum value for signed 16-bit audio samples
        /// </summary>
        public const float MaxInt16ValueFloat = 32767.0f; //Int16.MaxValue;

        /// <summary>
        /// Minimum value for signed 16-bit audio samples
        /// </summary>
        private const float MinInt16ValueFloat = -32768f;

        /// <summary>
        /// Converts 32-bit float audio to 16-bit stereo integer format with TPDF dithering
        /// </summary>
        public static unsafe int ConvertFloat32ToStereoInt16(
            byte[] source, 
            byte[] dest, 
            int sourceBytes, 
            int sourceChannels,
            Random ditherRng)
        {
            int samplesPerChannel = sourceBytes / (4 * sourceChannels);
            int outputSamples = samplesPerChannel * 2;

            fixed (byte* sourcePtr = source)
            fixed (byte* destPtr = dest)
            {
                float* floatPtr = (float*)sourcePtr;
                short* shortPtr = (short*)destPtr;

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    float leftSum = 0;
                    float rightSum = 0;

                    if (sourceChannels == 1)
                    {
                        // Mono to stereo
                        leftSum = rightSum = floatPtr[i];
                    }
                    else
                    {
                        // Multi-channel to stereo
                        for (int ch = 0; ch < sourceChannels; ch++)
                        {
                            float sample = floatPtr[i * sourceChannels + ch];
                            if (ch % 2 == 0)
                                leftSum += sample;
                            else
                                rightSum += sample;
                        }

                        leftSum /= (sourceChannels + 1) / 2;
                        rightSum /= sourceChannels / 2;
                    }

                    leftSum = Math.Clamp(leftSum, -1.0f, 1.0f);
                    rightSum = Math.Clamp(rightSum, -1.0f, 1.0f);

                    // Add TPDF dithering
                    float ditherL = ((float)ditherRng.NextDouble() + (float)ditherRng.NextDouble() - 1.0f) * 0.5f;
                    float ditherR = ((float)ditherRng.NextDouble() + (float)ditherRng.NextDouble() - 1.0f) * 0.5f;

                    shortPtr[i * 2] = (short)((leftSum * MaxInt16ValueFloat) + ditherL);
                    shortPtr[i * 2 + 1] = (short)((rightSum * MaxInt16ValueFloat) + ditherR);
                }
            }

            return outputSamples * 2;
        }

        /// <summary>
        /// Converts 16-bit audio to stereo format
        /// </summary>
        public static unsafe int ConvertInt16ToStereo(
            byte[] source, 
            byte[] dest, 
            int sourceBytes, 
            int sourceChannels)
        {
            int samplesPerChannel = sourceBytes / (2 * sourceChannels);
            int outputBytes = samplesPerChannel * 4; // Stereo 16-bit

            fixed (byte* sourcePtr = source)
            fixed (byte* destPtr = dest)
            {
                short* srcShort = (short*)sourcePtr;
                short* dstShort = (short*)destPtr;

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    if (sourceChannels == 1)
                    {
                        // Mono to stereo
                        short sample = srcShort[i];
                        dstShort[i * 2] = sample;
                        dstShort[i * 2 + 1] = sample;
                    }
                    else if (sourceChannels == 2)
                    {
                        // Already stereo
                        dstShort[i * 2] = srcShort[i * 2];
                        dstShort[i * 2 + 1] = srcShort[i * 2 + 1];
                    }
                    else
                    {
                        // Multi-channel to stereo - average channels
                        int leftSum = 0;
                        int rightSum = 0;
                        int leftCount = 0;
                        int rightCount = 0;

                        for (int ch = 0; ch < sourceChannels; ch++)
                        {
                            int sample = srcShort[i * sourceChannels + ch];
                            if (ch % 2 == 0)
                            {
                                leftSum += sample;
                                leftCount++;
                            }
                            else
                            {
                                rightSum += sample;
                                rightCount++;
                            }
                        }

                        dstShort[i * 2] = (short)(leftSum / Math.Max(1, leftCount));
                        dstShort[i * 2 + 1] = (short)(rightSum / Math.Max(1, rightCount));
                    }
                }
            }

            return outputBytes;
        }

        /// <summary>
        /// Converts 24-bit integer audio to 16-bit stereo format
        /// </summary>
        public static unsafe int ConvertInt24ToStereoInt16(
            byte[] source, 
            byte[] dest, 
            int sourceBytes, 
            int sourceChannels)
        {
            int samplesPerChannel = sourceBytes / (3 * sourceChannels);
            int outputBytes = samplesPerChannel * 4;

            fixed (byte* sourcePtr = source)
            fixed (byte* destPtr = dest)
            {
                short* shortPtr = (short*)destPtr;

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    int leftSum = 0;
                    int rightSum = 0;
                    int leftCount = 0;
                    int rightCount = 0;

                    for (int ch = 0; ch < sourceChannels; ch++)
                    {
                        int byteOffset = (i * sourceChannels + ch) * 3;

                        // Read 24-bit sample (little-endian)
                        int sample24 = sourcePtr[byteOffset] |
                                      (sourcePtr[byteOffset + 1] << 8) |
                                      (sourcePtr[byteOffset + 2] << 16);

                        // Sign extend from 24-bit to 32-bit
                        if ((sample24 & 0x800000) != 0)
                            sample24 |= unchecked((int)0xFF000000);

                        // Convert to 16-bit (shift right by 8)
                        int sample16 = sample24 >> 8;

                        if (ch % 2 == 0 || sourceChannels == 1)
                        {
                            leftSum += sample16;
                            leftCount++;
                        }
                        if (ch % 2 == 1 || sourceChannels == 1)
                        {
                            rightSum += sample16;
                            rightCount++;
                        }
                    }

                    shortPtr[i * 2] = (short)(leftSum / Math.Max(1, leftCount));
                    shortPtr[i * 2 + 1] = (short)(rightSum / Math.Max(1, rightCount));
                }
            }

            return outputBytes;
        }
    }
}