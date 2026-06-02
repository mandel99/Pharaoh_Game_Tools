using System;

namespace BinkInspector
{
    internal static class BinkVideoTransforms
    {
        private const int DctC0 = 2896;
        private const int DctC1 = 2217;
        private const int DctC2 = 3784;
        private const int DctC3 = -5352;

        public static void IdctPut(int[] block, byte[] destination, int destinationOffset, int stride)
        {
            for (int i = 0; i < 8; i++)
            {
                Idct(block, i, null, i, true);
            }

            for (int i = 0; i < 64; i += 8)
            {
                if (destinationOffset >= destination.Length)
                {
                    break;
                }

                Idct(block, i, destination, destinationOffset, false);
                destinationOffset += stride;
            }
        }

        public static void IdctAdd(int[] block, byte[] destination, int destinationOffset, int stride)
        {
            for (int i = 0; i < 8; i++)
            {
                Idct(block, i, null, i, true);
            }

            for (int i = 0; i < 64; i += 8)
            {
                Idct(block, i, null, i, false);
            }

            AddBlock8x8(block, destination, destinationOffset, stride);
        }

        public static void AddBlock8x8(int[] block, byte[] destination, int destinationOffset, int stride)
        {
            for (int y = 0; y < 8; y++)
            {
                int rowOffset = destinationOffset + (y * stride);
                for (int x = 0; x < 8; x++)
                {
                    int destinationIndex = rowOffset + x;
                    if (destinationIndex < 0 || destinationIndex >= destination.Length)
                    {
                        continue;
                    }

                    destination[destinationIndex] = unchecked((byte)(destination[destinationIndex] + block[(y * 8) + x]));
                }
            }
        }

        private static void Idct(int[] source, int sourceOffset, byte[] destination, int destinationOffset, bool column)
        {
            int indexShift = column ? 3 : 0;
            int constantToAdd = column ? 0 : 0x7F;
            int destinationShift = column ? 0 : 8;

            int a0 = source[sourceOffset] + constantToAdd;
            int b0 = source[sourceOffset + (1 << indexShift)];
            int a2 = source[sourceOffset + (2 << indexShift)];
            int x3 = source[sourceOffset + (3 << indexShift)];
            int x4 = source[sourceOffset + (4 << indexShift)];
            int a4 = source[sourceOffset + (5 << indexShift)];
            int x6 = source[sourceOffset + (6 << indexShift)];
            int x7 = source[sourceOffset + (7 << indexShift)];

            int a1 = a0 - x4;
            int a3 = (DctC0 * (a2 - x6)) >> 11;
            int a5 = a4 - x3;
            int a7 = b0 - x7;
            a0 += x4;
            a2 += x6;
            a4 += x3;
            b0 += x7;

            int a0PlusA2 = a0 + a2;
            int a0MinusA2 = a0 - a2;
            int a1PlusA3MinusA2 = a1 + a3 - a2;
            int a1MinusA3PlusA2 = a1 - a3 + a2;

            int b1 = (DctC2 * (a5 + a7)) >> 11;
            int b3 = (DctC0 * (b0 - a4)) >> 11;
            b0 += a4;
            int b2 = ((DctC3 * a5) >> 11) - b0 + b1;
            b3 -= b2;
            int b4 = ((DctC1 * a7) >> 11) + b3 - b1;

            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 0, a0PlusA2 + b0);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 1, a1PlusA3MinusA2 + b2);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 2, a1MinusA3PlusA2 + b3);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 3, a0MinusA2 - b4);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 4, a0MinusA2 + b4);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 5, a1MinusA3PlusA2 - b3);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 6, a1PlusA3MinusA2 - b2);
            Write(destination, source, destinationOffset, sourceOffset, indexShift, destinationShift, 7, a0PlusA2 - b0);
        }

        private static void Write(byte[] destination, int[] source, int destinationOffset, int sourceOffset, int indexShift, int destinationShift, int element, int value)
        {
            int shifted = value >> destinationShift;
            if (destination == null)
            {
                source[destinationOffset + (element << indexShift)] = shifted;
            }
            else
            {
                if (destinationOffset + (element << indexShift) < 0 || destinationOffset + (element << indexShift) >= destination.Length)
                {
                    return;
                }

                destination[destinationOffset + (element << indexShift)] = unchecked((byte)shifted);
            }
        }
    }
}
