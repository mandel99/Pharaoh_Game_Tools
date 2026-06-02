using System;

namespace BinkInspector
{
    internal sealed class BitReaderLE
    {
        private readonly byte[] data;
        private int bitPosition;

        public BitReaderLE(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public int BitsRemaining => (data.Length * 8) - bitPosition;
        public int BitsRead => bitPosition;

        public bool ReadBit()
        {
            return ReadBits(1) != 0;
        }

        public uint ReadBits(int count)
        {
            if (count < 0 || count > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (BitsRemaining < count)
            {
                throw new InvalidOperationException("Not enough bits remaining.");
            }

            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                int absoluteBit = bitPosition + i;
                int byteIndex = absoluteBit >> 3;
                int bitIndex = absoluteBit & 7;
                uint bit = (uint)((data[byteIndex] >> bitIndex) & 1);
                value |= bit << i;
            }

            bitPosition += count;
            return value;
        }

        public void SkipBits(int count)
        {
            if (count < 0 || BitsRemaining < count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            bitPosition += count;
        }

        public void Align32()
        {
            int padding = (-bitPosition) & 31;
            if (padding != 0)
            {
                SkipBits(padding);
            }
        }
    }
}
