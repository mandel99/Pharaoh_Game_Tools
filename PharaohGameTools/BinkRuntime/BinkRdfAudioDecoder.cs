using System;
using System.Collections.Generic;

namespace BinkInspector
{
    internal sealed class BinkRdfAudioDecoder
    {
        private static readonly int[] CriticalFrequencies =
        {
            100, 200, 300, 400, 510, 630, 770, 920,
            1080, 1270, 1480, 1720, 2000, 2320, 2700, 3150,
            3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500
        };

        private static readonly int[] RleLengthTable =
        {
            2, 3, 4, 5, 6, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 32, 64
        };

        private readonly int originalChannels;
        private readonly int frameLength;
        private readonly int overlapLength;
        private readonly float root;
        private readonly float[] quantTable;
        private readonly uint[] bands;
        private readonly float[] previousOverlap;
        private readonly float[] rdftCosTable;
        private readonly float[] rdftSinTable;
        private readonly int[] fftBitReverse;
        private readonly float[] fftTwiddles;
        private bool firstFrame = true;

        public BinkRdfAudioDecoder(AudioTrackInfo track)
        {
            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            originalChannels = track.IsStereo ? 2 : 1;

            int frameLengthBits;
            if (track.SampleRate < 22050)
            {
                frameLengthBits = 9;
            }
            else if (track.SampleRate < 44100)
            {
                frameLengthBits = 10;
            }
            else
            {
                frameLengthBits = 11;
            }

            frameLengthBits += IntLog2(originalChannels);

            frameLength = 1 << frameLengthBits;
            overlapLength = frameLength / 16;
            root = (float)(2.0 / (Math.Sqrt(frameLength) * 32768.0));

            quantTable = new float[96];
            for (int i = 0; i < quantTable.Length; i++)
            {
                quantTable[i] = (float)(Math.Exp(i * 0.15289164787221954) * root);
            }

            int sampleRateForBands = track.SampleRate * originalChannels;
            int sampleRateHalf = (sampleRateForBands + 1) / 2;
            int numBands = 1;
            while (numBands < 25 && sampleRateHalf > CriticalFrequencies[numBands - 1])
            {
                numBands++;
            }

            bands = new uint[numBands + 1];
            bands[0] = 2;
            for (int i = 1; i < numBands; i++)
            {
                bands[i] = (uint)((CriticalFrequencies[i - 1] * frameLength / sampleRateHalf) & ~1);
            }

            bands[numBands] = (uint)frameLength;
            previousOverlap = new float[overlapLength];

            int quarterLength = frameLength >> 2;
            rdftCosTable = new float[quarterLength];
            rdftSinTable = new float[quarterLength];
            double theta = (2.0 * Math.PI) / frameLength;
            for (int i = 0; i < quarterLength; i++)
            {
                rdftCosTable[i] = (float)Math.Cos(i * theta);
                rdftSinTable[i] = (float)Math.Sin(i * theta);
            }

            int fftSize = frameLength >> 1;
            int fftBits = IntLog2(fftSize);
            fftBitReverse = new int[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                fftBitReverse[i] = ReverseBits(i, fftBits);
            }

            fftTwiddles = new float[fftSize];
            for (int i = 0; i < (fftSize >> 1); i++)
            {
                int twiddleIndex = i << 1;
                double angle = (-2.0 * Math.PI * i) / fftSize;
                fftTwiddles[twiddleIndex] = (float)Math.Cos(angle);
                fftTwiddles[twiddleIndex + 1] = (float)Math.Sin(angle);
            }
        }

        public float[] DecodePacket(byte[] audioPayload)
        {
            if (audioPayload == null)
            {
                throw new ArgumentNullException(nameof(audioPayload));
            }

            BitReaderLE reader = new BitReaderLE(audioPayload);
            List<float> output = new List<float>(frameLength);

            while (reader.BitsRemaining > 0)
            {
                float[] coeffs = DecodeCoefficients(reader);
                float[] transformed = InversePackedRdft(coeffs);
                ApplyOverlap(transformed);
                float[] block = SlicePlayableSamples(transformed);
                output.AddRange(block);

                reader.Align32();
            }

            return output.ToArray();
        }

        private float[] DecodeCoefficients(BitReaderLE reader)
        {
            float[] coeffs = new float[frameLength];
            coeffs[0] = ReadPackedFloat(reader) * root;
            coeffs[1] = ReadPackedFloat(reader) * root;

            float[] quantizers = new float[bands.Length - 1];
            for (int i = 0; i < quantizers.Length; i++)
            {
                int index = (int)reader.ReadBits(8);
                quantizers[i] = quantTable[Math.Min(index, 95)];
            }

            int bandIndex = 0;
            float currentQuantizer = quantizers[0];
            int coefficientIndex = 2;
            while (coefficientIndex < frameLength)
            {
                bool hasRun = reader.ReadBit();
                int groupEnd;
                if (hasRun)
                {
                    int runLength = RleLengthTable[(int)reader.ReadBits(4)];
                    groupEnd = coefficientIndex + (runLength * 8);
                }
                else
                {
                    groupEnd = coefficientIndex + 8;
                }

                groupEnd = Math.Min(groupEnd, frameLength);
                int width = (int)reader.ReadBits(4);
                if (width == 0)
                {
                    while (coefficientIndex < groupEnd)
                    {
                        coeffs[coefficientIndex++] = 0f;
                        while (bandIndex + 1 < bands.Length && bands[bandIndex] < coefficientIndex)
                        {
                            currentQuantizer = quantizers[Math.Min(bandIndex, quantizers.Length - 1)];
                            bandIndex++;
                        }
                    }

                    continue;
                }

                while (coefficientIndex < groupEnd)
                {
                    if (bandIndex < bands.Length - 1 && bands[bandIndex] == coefficientIndex)
                    {
                        currentQuantizer = quantizers[bandIndex];
                        bandIndex++;
                    }

                    uint coefficient = reader.ReadBits(width);
                    if (coefficient == 0)
                    {
                        coeffs[coefficientIndex] = 0f;
                    }
                    else
                    {
                        bool negative = reader.ReadBit();
                        float value = currentQuantizer * coefficient;
                        coeffs[coefficientIndex] = negative ? -value : value;
                    }

                    coefficientIndex++;
                }
            }

            return coeffs;
        }

        private float[] InversePackedRdft(float[] packedCoefficients)
        {
            int n = frameLength;
            float[] data = new float[n];
            Array.Copy(packedCoefficients, data, n);

            float dc = data[0];
            float nyquist = data[1];
            data[0] = 0.5f * (dc + nyquist);
            data[1] = 0.5f * (dc - nyquist);

            int quarterLength = n >> 2;
            for (int i = 1; i < quarterLength; i++)
            {
                int i1 = 2 * i;
                int i2 = n - i1;

                float d01 = data[i1];
                float d02 = data[i2];
                float d11 = data[i1 + 1];
                float d12 = data[i2 + 1];

                float evenRe = 0.5f * (d01 + d02);
                float oddIm = 0.5f * (d01 - d02);
                float evenIm = 0.5f * (d11 - d12);
                float oddRe = -0.5f * (d11 + d12);

                float cosValue = rdftCosTable[i];
                float sinValue = rdftSinTable[i];

                data[i1] = evenRe + (oddRe * cosValue) - (oddIm * sinValue);
                data[i1 + 1] = evenIm + (oddIm * cosValue) + (oddRe * sinValue);
                data[i2] = evenRe - (oddRe * cosValue) + (oddIm * sinValue);
                data[i2 + 1] = -evenIm + (oddIm * cosValue) + (oddRe * sinValue);
            }

            ForwardFftInPlace(data);
            return data;
        }

        private void ApplyOverlap(float[] samples)
        {
            if (!firstFrame)
            {
                for (int i = 0; i < overlapLength; i++)
                {
                    samples[i] = ((previousOverlap[i] * (overlapLength - i)) + (samples[i] * i)) / overlapLength;
                }
            }

            Array.Copy(samples, frameLength - overlapLength, previousOverlap, 0, overlapLength);
            firstFrame = false;
        }

        private float[] SlicePlayableSamples(float[] samples)
        {
            int count = frameLength - overlapLength;
            float[] output = new float[count];
            Array.Copy(samples, output, count);
            return output;
        }

        private static float ReadPackedFloat(BitReaderLE reader)
        {
            int exponent = (int)reader.ReadBits(5);
            uint mantissa = reader.ReadBits(23);
            bool negative = reader.ReadBit();
            double value = mantissa * Math.Pow(2.0, exponent - 23);
            return negative ? (float)-value : (float)value;
        }

        private void ForwardFftInPlace(float[] data)
        {
            int n = data.Length >> 1;
            for (int i = 0; i < n; i++)
            {
                int j = fftBitReverse[i];
                if (j > i)
                {
                    int i2 = i << 1;
                    int j2 = j << 1;
                    float tr = data[i2];
                    float ti = data[i2 + 1];
                    data[i2] = data[j2];
                    data[i2 + 1] = data[j2 + 1];
                    data[j2] = tr;
                    data[j2 + 1] = ti;
                }
            }

            for (int length = 2; length <= n; length <<= 1)
            {
                int halfSize = length >> 1;
                int step = n / length;

                for (int i = 0; i < n; i += length)
                {
                    int twiddleIndex = 0;
                    for (int j = 0; j < halfSize; j++)
                    {
                        float wr = fftTwiddles[twiddleIndex << 1];
                        float wi = fftTwiddles[(twiddleIndex << 1) + 1];

                        int evenIndex = (i + j) << 1;
                        int oddIndex = (i + j + halfSize) << 1;

                        float er = data[evenIndex];
                        float ei = data[evenIndex + 1];
                        float or = data[oddIndex];
                        float oi = data[oddIndex + 1];

                        float tr = (wr * or) - (wi * oi);
                        float ti = (wr * oi) + (wi * or);

                        data[evenIndex] = er + tr;
                        data[evenIndex + 1] = ei + ti;
                        data[oddIndex] = er - tr;
                        data[oddIndex + 1] = ei - ti;

                        twiddleIndex += step;
                    }
                }
            }
        }

        private static int ReverseBits(int value, int width)
        {
            int reversed = 0;
            for (int i = 0; i < width; i++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }

            return reversed;
        }

        private static int IntLog2(int value)
        {
            int result = 0;
            while ((1 << result) < value)
            {
                result++;
            }

            return result;
        }
    }
}
