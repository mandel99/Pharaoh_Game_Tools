using System;
using System.Collections.Generic;

namespace BinkInspector
{
    internal sealed class BinkVideoDecoder
    {
        private const int ParamBlockTypes = 0;
        private const int ParamSubBlockTypes = 1;
        private const int ParamColors = 2;
        private const int ParamPattern = 3;
        private const int ParamXOff = 4;
        private const int ParamYOff = 5;
        private const int ParamIntraDc = 6;
        private const int ParamInterDc = 7;
        private const int ParamRun = 8;
        private const int ParamCount = 9;

        private const int SkipBlock = 0;
        private const int ScaledBlock = 1;
        private const int MotionBlock = 2;
        private const int RunBlock = 3;
        private const int ResidueBlock = 4;
        private const int IntraBlock = 5;
        private const int FillBlock = 6;
        private const int InterBlock = 7;
        private const int PatternBlock = 8;
        private const int RawBlock = 9;

        private const int HuffmanTreeCount = 16;
        private const int HuffmanSymbolCount = 16;
        private const int HuffmanMaxCodeLength = 7;
        private const int HuffmanLookupCodeCount = 1 << HuffmanMaxCodeLength;

        private static readonly int[] BlockTypeRleLengths = { 4, 8, 12, 32 };
        // Static Bink codebooks. Codes are stored LSB-first because the bitstream is read least-significant bit first.
        private static readonly byte[,] HuffmanCodeBits =
        {
            { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F },
            { 0x00, 0x01, 0x03, 0x05, 0x07, 0x09, 0x0B, 0x0D, 0x0F, 0x13, 0x15, 0x17, 0x19, 0x1B, 0x1D, 0x1F },
            { 0x00, 0x02, 0x01, 0x09, 0x05, 0x15, 0x0D, 0x1D, 0x03, 0x13, 0x0B, 0x1B, 0x07, 0x17, 0x0F, 0x1F },
            { 0x00, 0x02, 0x06, 0x01, 0x09, 0x05, 0x0D, 0x1D, 0x03, 0x13, 0x0B, 0x1B, 0x07, 0x17, 0x0F, 0x1F },
            { 0x00, 0x04, 0x02, 0x06, 0x01, 0x09, 0x05, 0x0D, 0x03, 0x13, 0x0B, 0x1B, 0x07, 0x17, 0x0F, 0x1F },
            { 0x00, 0x04, 0x02, 0x0A, 0x06, 0x0E, 0x01, 0x09, 0x05, 0x0D, 0x03, 0x0B, 0x07, 0x17, 0x0F, 0x1F },
            { 0x00, 0x02, 0x0A, 0x06, 0x0E, 0x01, 0x09, 0x05, 0x0D, 0x03, 0x0B, 0x1B, 0x07, 0x17, 0x0F, 0x1F },
            { 0x00, 0x01, 0x05, 0x03, 0x13, 0x0B, 0x1B, 0x3B, 0x07, 0x27, 0x17, 0x37, 0x0F, 0x2F, 0x1F, 0x3F },
            { 0x00, 0x01, 0x03, 0x13, 0x0B, 0x2B, 0x1B, 0x3B, 0x07, 0x27, 0x17, 0x37, 0x0F, 0x2F, 0x1F, 0x3F },
            { 0x00, 0x01, 0x05, 0x0D, 0x03, 0x13, 0x0B, 0x1B, 0x07, 0x27, 0x17, 0x37, 0x0F, 0x2F, 0x1F, 0x3F },
            { 0x00, 0x02, 0x01, 0x05, 0x0D, 0x03, 0x13, 0x0B, 0x1B, 0x07, 0x17, 0x37, 0x0F, 0x2F, 0x1F, 0x3F },
            { 0x00, 0x01, 0x09, 0x05, 0x0D, 0x03, 0x13, 0x0B, 0x1B, 0x07, 0x17, 0x37, 0x0F, 0x2F, 0x1F, 0x3F },
            { 0x00, 0x02, 0x01, 0x03, 0x13, 0x0B, 0x1B, 0x3B, 0x07, 0x27, 0x17, 0x37, 0x0F, 0x2F, 0x1F, 0x3F },
            { 0x00, 0x01, 0x05, 0x03, 0x07, 0x27, 0x17, 0x37, 0x0F, 0x4F, 0x2F, 0x6F, 0x1F, 0x5F, 0x3F, 0x7F },
            { 0x00, 0x01, 0x05, 0x03, 0x07, 0x17, 0x37, 0x77, 0x0F, 0x4F, 0x2F, 0x6F, 0x1F, 0x5F, 0x3F, 0x7F },
            { 0x00, 0x02, 0x01, 0x05, 0x03, 0x07, 0x27, 0x17, 0x37, 0x0F, 0x2F, 0x6F, 0x1F, 0x5F, 0x3F, 0x7F }
        };
        private static readonly byte[,] HuffmanCodeLengths =
        {
            { 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
            { 1, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 },
            { 2, 2, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 },
            { 2, 3, 3, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 },
            { 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5 },
            { 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5 },
            { 2, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5 },
            { 1, 3, 3, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6 },
            { 1, 2, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6 },
            { 1, 3, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6 },
            { 2, 2, 3, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6 },
            { 1, 4, 4, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6 },
            { 2, 2, 2, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6 },
            { 1, 3, 3, 3, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7 },
            { 1, 3, 3, 3, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7 },
            { 2, 2, 3, 3, 3, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7 }
        };
        private static readonly byte[,,] HuffmanSymbolLookup = BuildHuffmanSymbolLookup();

        private readonly int width;
        private readonly int height;
        private readonly int numPixels;
        private readonly int uvSize;
        private readonly byte[] previousFrameData;
        private readonly Bundle[] bundles;
        private readonly Tree[] colorHighTrees;
        private readonly int[] coeffIndex;
        private readonly int[] coeffList;
        private readonly byte[] modeList;
        private readonly byte[] tempScalingBuffer;
        private readonly int[] tempDctBuffer;
        private readonly BinkReferenceData referenceData;

        private byte[] planeData;
        private byte[] previousPlaneData;
        private int planeDataOffset;
        private int planeEndOffset;
        private int stride;
        private int colorLastValue;
        private int currentPlaneWidth;
        private int currentPlaneHeight;

        public BinkVideoDecoder(BinkFile file)
            : this(file, null)
        {
        }

        public BinkVideoDecoder(BinkFile file, byte[] initialFrameData = null)
        {
            width = (int)file.Width;
            height = (int)file.Height;
            numPixels = width * height;
            uvSize = ((width + 1) >> 1) * ((height + 1) >> 1);
            previousFrameData = new byte[numPixels + (uvSize * 2)];
            if (initialFrameData != null)
            {
                if (initialFrameData.Length != previousFrameData.Length)
                {
                    throw new ArgumentException("Initial frame state size does not match the decoder state size.", nameof(initialFrameData));
                }

                Buffer.BlockCopy(initialFrameData, 0, previousFrameData, 0, previousFrameData.Length);
            }

            bundles = new Bundle[ParamCount];
            for (int i = 0; i < bundles.Length; i++)
            {
                bundles[i] = new Bundle();
            }

            colorHighTrees = new Tree[16];
            for (int i = 0; i < colorHighTrees.Length; i++)
            {
                colorHighTrees[i] = new Tree();
            }

            coeffIndex = new int[64];
            coeffList = new int[128];
            modeList = new byte[128];
            tempScalingBuffer = new byte[64];
            tempDctBuffer = new int[64];
            referenceData = BinkReferenceData.Load();
        }

        public byte[] CaptureReferenceFrameData()
        {
            byte[] snapshot = new byte[previousFrameData.Length];
            Buffer.BlockCopy(previousFrameData, 0, snapshot, 0, previousFrameData.Length);
            return snapshot;
        }

        public BinkDecodedVideoFrame Decode(FramePacket packet)
        {
            int chromaWidth = (width + 1) >> 1;
            int chromaHeight = (height + 1) >> 1;
            int frameSize = previousFrameData.Length;
            byte[] yuv = new byte[frameSize];
            Buffer.BlockCopy(previousFrameData, 0, yuv, 0, frameSize);

            BitReaderLE reader = new BitReaderLE(packet.VideoPayload);

            DecodePlane(reader, yuv, 0, width, height, 0);
            if (reader.BitsRemaining >= 1)
            {
                DecodePlane(reader, yuv, 1, chromaWidth, chromaHeight, numPixels);
            }

            if (reader.BitsRemaining >= 1)
            {
                DecodePlane(reader, yuv, 2, chromaWidth, chromaHeight, numPixels + uvSize);
            }

            Buffer.BlockCopy(yuv, 0, previousFrameData, 0, frameSize);
            return new BinkDecodedVideoFrame(width, height, yuv);
        }

        private void DecodePlane(BitReaderLE reader, byte[] frameData, int planeIndex, int currentWidth, int currentHeight, int planeOffset)
        {
            int blockWidth = (currentWidth + 7) >> 3;
            int blockHeight = (currentHeight + 7) >> 3;
            int planeSize = currentWidth * currentHeight;

            planeData = new byte[planeSize];
            previousPlaneData = new byte[planeSize];
            Buffer.BlockCopy(frameData, planeOffset, planeData, 0, planeSize);
            Buffer.BlockCopy(previousFrameData, planeOffset, previousPlaneData, 0, planeSize);
            planeDataOffset = 0;
            planeEndOffset = planeSize;
            stride = currentWidth;
            currentPlaneWidth = currentWidth;
            currentPlaneHeight = currentHeight;

            InitLengths(Math.Max(currentWidth, 8), blockWidth);
            ReadPlaneTrees(reader);

            int blockLineIncrement = stride * 7;
            int currentBlockY = 0;
            int currentPlanePtr = 0;

            while (currentBlockY++ < blockHeight)
            {
                ReadBlockTypes(reader, bundles[ParamBlockTypes]);
                ReadBlockTypes(reader, bundles[ParamSubBlockTypes]);
                ReadColors(reader, bundles[ParamColors]);
                ReadPatterns(reader, bundles[ParamPattern]);
                ReadMotionValues(reader, bundles[ParamXOff]);
                ReadMotionValues(reader, bundles[ParamYOff]);
                ReadDcs(reader, bundles[ParamIntraDc], false);
                ReadDcs(reader, bundles[ParamInterDc], true);
                ReadRuns(reader, bundles[ParamRun]);

                int currentBlockX = 0;
                while (currentBlockX++ < blockWidth)
                {
                    int blockType = GetValue(ParamBlockTypes);
                    switch (blockType)
                    {
                        case SkipBlock:
                            break;
                        case ScaledBlock:
                            if ((currentBlockY & 1) != 0)
                            {
                                DecodeScaledBlock(reader, currentPlanePtr);
                            }
                            currentBlockX++;
                            currentPlanePtr += 16;
                            continue;
                        case MotionBlock:
                            DecodeMotionBlock(currentPlanePtr);
                            break;
                        case RunBlock:
                            DecodeRunBlock(reader, planeData, currentPlanePtr, stride);
                            break;
                        case ResidueBlock:
                            DecodeResidueBlock(reader, currentPlanePtr);
                            break;
                        case IntraBlock:
                            DecodeIntraBlock(reader, planeData, currentPlanePtr, stride);
                            break;
                        case FillBlock:
                            DecodeFillBlock(currentPlanePtr, 8);
                            break;
                        case InterBlock:
                            DecodeInterBlock(reader, currentPlanePtr);
                            break;
                        case PatternBlock:
                            DecodePatternBlock(planeData, currentPlanePtr, stride);
                            break;
                        case RawBlock:
                            DecodeRawBlock(currentPlanePtr);
                            break;
                        default:
                        throw new InvalidOperationException("Invalid block type " + blockType.ToString());
                    }

                    currentPlanePtr += 8;
                }

                currentPlanePtr += blockLineIncrement;
            }

            reader.Align32();
            Buffer.BlockCopy(planeData, 0, frameData, planeOffset, planeSize);
        }

        private void DecodeMotionBlock(int destinationOffset)
        {
            int xOff = GetValue(ParamXOff);
            int yOff = GetValue(ParamYOff);
            int sourceOffset = destinationOffset + xOff + (yOff * stride);
            CopyBlock(sourceOffset, destinationOffset);
        }

        private void DecodeRunBlock(BitReaderLE reader, byte[] block, int offset, int blockStride)
        {
            int i = 0;
            int scanIndex = (int)reader.ReadBits(4) << 6;
            do
            {
                int run = GetValue(ParamRun) + 1;
                i += run;

                if (reader.ReadBit())
                {
                    int value = GetValue(ParamColors);
                    for (int j = 0; j < run; j++)
                    {
                        int pos = scanIndex < 1024
                            ? referenceData.Patterns[scanIndex >> 6, scanIndex & 63]
                            : 0;
                        scanIndex++;
                        WriteBlockByte(block, offset + ((pos >> 3) * blockStride) + (pos & 7), (byte)value);
                    }
                }
                else
                {
                    for (int j = 0; j < run; j++)
                    {
                        int pos = scanIndex < 1024
                            ? referenceData.Patterns[scanIndex >> 6, scanIndex & 63]
                            : 0;
                        scanIndex++;
                        WriteBlockByte(block, offset + ((pos >> 3) * blockStride) + (pos & 7), (byte)GetValue(ParamColors));
                    }
                }
            }
            while (i < 63);

            if (i == 63)
            {
                int pos = scanIndex < 1024
                    ? referenceData.Patterns[scanIndex >> 6, scanIndex & 63]
                    : 0;
                WriteBlockByte(block, offset + ((pos >> 3) * blockStride) + (pos & 7), (byte)GetValue(ParamColors));
            }
        }

        private void DecodeResidueBlock(BitReaderLE reader, int destinationOffset)
        {
            int xOff = GetValue(ParamXOff);
            int yOff = GetValue(ParamYOff);
            int sourceOffset = destinationOffset + xOff + (yOff * stride);
            Array.Clear(tempDctBuffer, 0, tempDctBuffer.Length);
            ReadCoefficientsOrResidue(reader, tempDctBuffer, -1, false);
            CopyBlock(sourceOffset, destinationOffset);
            BinkVideoTransforms.AddBlock8x8(tempDctBuffer, planeData, destinationOffset, stride);
        }

        private void DecodeIntraBlock(BitReaderLE reader, byte[] block, int offset, int blockStride)
        {
            tempDctBuffer[0] = GetValue(ParamIntraDc);
            Array.Clear(tempDctBuffer, 1, 63);
            ReadCoefficientsOrResidue(reader, tempDctBuffer, 0, false);
            BinkVideoTransforms.IdctPut(tempDctBuffer, block, offset, blockStride);
        }

        private void DecodeFillBlock(int destinationOffset, int size)
        {
            int value = GetValue(ParamColors);
            for (int y = 0; y < size; y++)
            {
                int rowOffset = destinationOffset + (y * stride);
                for (int x = 0; x < size; x++)
                {
                    if (rowOffset + x < planeData.Length)
                    {
                        WritePlaneByte(rowOffset + x, (byte)value);
                    }
                }
            }
        }

        private void DecodeInterBlock(BitReaderLE reader, int destinationOffset)
        {
            int xOff = GetValue(ParamXOff);
            int yOff = GetValue(ParamYOff);
            int sourceOffset = destinationOffset + xOff + (yOff * stride);
            CopyBlock(sourceOffset, destinationOffset);
            tempDctBuffer[0] = GetValue(ParamInterDc);
            Array.Clear(tempDctBuffer, 1, 63);
            ReadCoefficientsOrResidue(reader, tempDctBuffer, 0, true);
            BinkVideoTransforms.IdctAdd(tempDctBuffer, planeData, destinationOffset, stride);
        }

        private void DecodePatternBlock(byte[] block, int offset, int blockStride)
        {
            int color0 = GetValue(ParamColors);
            int color1 = GetValue(ParamColors);
            for (int i = 0; i < 8; i++)
            {
                int value = GetValue(ParamPattern);
                for (int j = 0; j < 8; j++)
                {
                    WriteBlockByte(block, offset + (i * blockStride) + j, (byte)(((value & 1) == 0) ? color0 : color1));
                    value >>= 1;
                }
            }
        }

        private void DecodeRawBlock(int destinationOffset)
        {
            for (int y = 0; y < 8; y++)
            {
                int rowOffset = destinationOffset + (y * stride);
                for (int x = 0; x < 8; x++)
                {
                    WritePlaneByte(rowOffset + x, (byte)GetValue(ParamColors));
                }
            }
        }

        private void DecodeScaledBlock(BitReaderLE reader, int destinationOffset)
        {
            int subBlock = GetValue(ParamSubBlockTypes);
            switch (subBlock)
            {
                case RawBlock:
                    for (int i = 0; i < 64; i++)
                    {
                        tempScalingBuffer[i] = (byte)GetValue(ParamColors);
                    }
                    break;
                case IntraBlock:
                    DecodeIntraBlock(reader, tempScalingBuffer, 0, 8);
                    break;
                case FillBlock:
                    DecodeFillBlock(destinationOffset, 16);
                    return;
                case RunBlock:
                    DecodeRunBlock(reader, tempScalingBuffer, 0, 8);
                    break;
                case PatternBlock:
                    DecodePatternBlock(tempScalingBuffer, 0, 8);
                    break;
                default:
                throw new InvalidOperationException("Invalid scaled sub-block type " + subBlock.ToString());
            }

            int sourceIndex = 0;
            int destinationLine = destinationOffset;
            int maxDestinationLine = destinationLine + (stride << 4);
            int lineIncrement = (stride << 1) - 15;
            while (destinationLine < maxDestinationLine)
            {
                byte value = tempScalingBuffer[sourceIndex++];
                WritePlaneByte(destinationLine, value);
                WritePlaneByte(destinationLine + stride, value);
                destinationLine++;
                WritePlaneByte(destinationLine, value);
                WritePlaneByte(destinationLine + stride, value);
                destinationLine += (sourceIndex & 0x7) != 0 ? 1 : lineIncrement;
            }
        }

        private void CopyBlock(int sourceOffset, int destinationOffset)
        {
            if (sourceOffset == destinationOffset)
            {
                return;
            }

            int sourceBaseY = FloorDiv(sourceOffset, stride);
            int sourceBaseX = sourceOffset - (sourceBaseY * stride);
            int destinationRow = destinationOffset;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int dstIndex = destinationRow + x;
                    if (dstIndex < planeDataOffset || dstIndex >= planeEndOffset)
                    {
                        continue;
                    }

                    int srcX = Clamp(sourceBaseX + x, 0, currentPlaneWidth - 1);
                    int srcY = Clamp(sourceBaseY + y, 0, currentPlaneHeight - 1);
                    int srcIndex = (srcY * stride) + srcX;
                    planeData[dstIndex] = previousPlaneData[srcIndex];
                }
                destinationRow += stride;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            if (remainder != 0 && ((remainder < 0) != (divisor < 0)))
            {
                quotient--;
            }

            return quotient;
        }

        private void WritePlaneByte(int index, byte value)
        {
            if (index >= planeDataOffset && index < planeEndOffset)
            {
                planeData[index] = value;
            }
        }

        private void WriteBlockByte(byte[] block, int index, byte value)
        {
            if (ReferenceEquals(block, planeData))
            {
                WritePlaneByte(index, value);
                return;
            }

            if (index >= 0 && index < block.Length)
            {
                block[index] = value;
            }
        }

        private void InitLengths(int currentWidth, int blockWidth)
        {
            int alignedWidth = Align8(currentWidth);
            bundles[ParamBlockTypes].LengthBits = IntLog2((alignedWidth >> 3) + 511) + 1;
            bundles[ParamSubBlockTypes].LengthBits = IntLog2((alignedWidth >> 4) + 511) + 1;
            bundles[ParamColors].LengthBits = IntLog2((blockWidth * 64) + 511) + 1;
            bundles[ParamPattern].LengthBits = IntLog2((blockWidth << 3) + 511) + 1;
            bundles[ParamXOff].LengthBits = IntLog2((alignedWidth >> 3) + 511) + 1;
            bundles[ParamYOff].LengthBits = IntLog2((alignedWidth >> 3) + 511) + 1;
            bundles[ParamIntraDc].LengthBits = IntLog2((alignedWidth >> 3) + 511) + 1;
            bundles[ParamInterDc].LengthBits = IntLog2((alignedWidth >> 3) + 511) + 1;
            bundles[ParamRun].LengthBits = IntLog2((blockWidth * 48) + 511) + 1;
        }

        private void ReadPlaneTrees(BitReaderLE reader)
        {
            for (int i = 0; i < ParamCount; i++)
            {
                if (i == ParamColors)
                {
                    for (int treeIndex = 0; treeIndex < 16; treeIndex++)
                    {
                        ReadTree(reader, colorHighTrees[treeIndex]);
                    }
                    colorLastValue = 0;
                }

                if (i != ParamIntraDc && i != ParamInterDc)
                {
                    ReadTree(reader, bundles[i].Tree);
                }

                bundles[i].Reset();
            }
        }

        private void ReadTree(BitReaderLE reader, Tree tree)
        {
            int treeIndex = (int)reader.ReadBits(4);
            tree.TreeIndex = treeIndex;
            if (treeIndex == 0)
            {
                for (int i = 0; i < 16; i++)
                {
                    tree.Symbols[i] = (byte)i;
                }
                return;
            }

            if (reader.ReadBit())
            {
                int len = (int)reader.ReadBits(3);
                bool[] used = new bool[16];
                for (int i = 0; i <= len; i++)
                {
                    byte symbol = (byte)reader.ReadBits(4);
                    tree.Symbols[i] = symbol;
                    used[symbol] = true;
                }

                for (int i = 0; i < 16 && len < 15; i++)
                {
                    if (!used[i])
                    {
                        tree.Symbols[++len] = (byte)i;
                    }
                }
            }
            else
            {
                byte[] current = new byte[16];
                byte[] next = new byte[16];
                for (byte i = 0; i < 16; i++)
                {
                    current[i] = i;
                }

                int depth = (int)reader.ReadBits(2);
                for (int i = 0; i <= depth; i++)
                {
                    int size = 1 << i;
                    for (int t = 0; t < 16; t += size << 1)
                    {
                        Merge(reader, next, t, current, t, size);
                    }
                    byte[] swap = current;
                    current = next;
                    next = swap;
                }

                Array.Copy(current, tree.Symbols, 16);
            }
        }

        private static void Merge(BitReaderLE reader, byte[] destination, int destinationOffset, byte[] source, int sourceOffset, int size)
        {
            int leftOffset = sourceOffset;
            int rightOffset = sourceOffset + size;
            int left = size;
            int right = size;
            int writeOffset = destinationOffset;

            while (left > 0 && right > 0)
            {
                if (!reader.ReadBit())
                {
                    destination[writeOffset++] = source[leftOffset++];
                    left--;
                }
                else
                {
                    destination[writeOffset++] = source[rightOffset++];
                    right--;
                }
            }

            while (left-- > 0)
            {
                destination[writeOffset++] = source[leftOffset++];
            }

            while (right-- > 0)
            {
                destination[writeOffset++] = source[rightOffset++];
            }
        }

        private void ReadBlockTypes(BitReaderLE reader, Bundle bundle)
        {
            int count = BeginBundleRead(reader, bundle);
            if (count <= 0)
            {
                return;
            }

            if (reader.ReadBit())
            {
                int value = (int)reader.ReadBits(4);
                for (int i = 0; i < count; i++)
                {
                    bundle.Data.Add(value);
                }
            }
            else
            {
                int last = 0;
                for (int i = 0; i < count;)
                {
                    int value = DecodeHuff(reader, bundle.Tree);
                    if (value < 12)
                    {
                        last = value;
                        bundle.Data.Add(value);
                        i++;
                    }
                    else
                    {
                        int run = BlockTypeRleLengths[value - 12];
                        for (int j = 0; j < run; j++)
                        {
                            bundle.Data.Add(last);
                        }
                        i += run;
                    }
                }
            }
        }

        private void ReadColors(BitReaderLE reader, Bundle bundle)
        {
            int count = BeginBundleRead(reader, bundle);
            if (count <= 0)
            {
                return;
            }

            bool isRun = reader.ReadBit();
            int iterations = isRun ? 1 : count;
            do
            {
                int highValue = DecodeHuff(reader, colorHighTrees[colorLastValue]);
                int value = DecodeHuff(reader, bundle.Tree) | (highValue << 4);
                colorLastValue = highValue;
                value = value > 127 ? 256 - value : value + 128;

                if (isRun)
                {
                    for (int i = 0; i < count; i++)
                    {
                        bundle.Data.Add(value);
                    }
                }
                else
                {
                    bundle.Data.Add(value);
                }
            }
            while (--iterations > 0);
        }

        private void ReadPatterns(BitReaderLE reader, Bundle bundle)
        {
            int count = BeginBundleRead(reader, bundle);
            if (count <= 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                bundle.Data.Add(DecodeHuff(reader, bundle.Tree) | (DecodeHuff(reader, bundle.Tree) << 4));
            }
        }

        private void ReadMotionValues(BitReaderLE reader, Bundle bundle)
        {
            int count = BeginBundleRead(reader, bundle);
            if (count <= 0)
            {
                return;
            }

            if (reader.ReadBit())
            {
                int value = (int)reader.ReadBits(4);
                if (value != 0)
                {
                    int sign = reader.ReadBit() ? -1 : 0;
                    value = (value ^ sign) - sign;
                }

                for (int i = 0; i < count; i++)
                {
                    bundle.Data.Add((sbyte)value);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int value = DecodeHuff(reader, bundle.Tree);
                    if (value != 0)
                    {
                        int sign = reader.ReadBit() ? -1 : 0;
                        value = (value ^ sign) - sign;
                    }
                    bundle.Data.Add((sbyte)value);
                }
            }
        }

        private void ReadDcs(BitReaderLE reader, Bundle bundle, bool hasSign)
        {
            int count = BeginBundleRead(reader, bundle);
            if (count <= 0)
            {
                return;
            }

            int value = (int)reader.ReadBits(hasSign ? 10 : 11);
            if (value != 0 && hasSign)
            {
                int sign = reader.ReadBit() ? -1 : 0;
                value = (value ^ sign) - sign;
            }

            bundle.Data.Add(value);
            int index = 1;
            while (index < count)
            {
                int len = Math.Min(count - index, 8);
                int size = (int)reader.ReadBits(4);
                if (size != 0)
                {
                    for (int j = 0; j < len; j++)
                    {
                        int delta = (int)reader.ReadBits(size);
                        if (delta != 0)
                        {
                            int sign = reader.ReadBit() ? -1 : 0;
                            delta = (delta ^ sign) - sign;
                        }
                        value += delta;
                        bundle.Data.Add(value);
                    }
                }
                else
                {
                    for (int j = 0; j < len; j++)
                    {
                        bundle.Data.Add(value);
                    }
                }
                index += len;
            }
        }

        private void ReadRuns(BitReaderLE reader, Bundle bundle)
        {
            int count = BeginBundleRead(reader, bundle);
            if (count <= 0)
            {
                return;
            }

            if (reader.ReadBit())
            {
                int value = (int)reader.ReadBits(4);
                for (int i = 0; i < count; i++)
                {
                    bundle.Data.Add(value);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    bundle.Data.Add(DecodeHuff(reader, bundle.Tree));
                }
            }
        }

        private int BeginBundleRead(BitReaderLE reader, Bundle bundle)
        {
            if (!bundle.Active || bundle.Data.Count > bundle.ReadIndex)
            {
                return 0;
            }

            int count = (int)reader.ReadBits(bundle.LengthBits);
            if (count == 0)
            {
                bundle.Active = false;
                return 0;
            }

            return count;
        }

        private int GetValue(int source)
        {
            Bundle bundle = bundles[source];
            if (bundle.ReadIndex >= bundle.Data.Count)
            {
                throw new InvalidOperationException("Bundle underflow for source " + source.ToString());
            }

            return bundle.Data[bundle.ReadIndex++];
        }

        private int DecodeHuff(BitReaderLE reader, Tree tree)
        {
            int code = 0;
            for (int len = 1; len <= HuffmanMaxCodeLength; len++)
            {
                code |= (reader.ReadBit() ? 1 : 0) << (len - 1);
                int symbolIndex = HuffmanSymbolLookup[tree.TreeIndex, len, code] - 1;
                if (symbolIndex >= 0)
                {
                    return tree.Symbols[symbolIndex];
                }
            }

            throw new InvalidOperationException("Invalid Huffman code.");
        }

        private static byte[,,] BuildHuffmanSymbolLookup()
        {
            byte[,,] lookup = new byte[HuffmanTreeCount, HuffmanMaxCodeLength + 1, HuffmanLookupCodeCount];
            for (int treeIndex = 0; treeIndex < HuffmanTreeCount; treeIndex++)
            {
                for (int symbolIndex = 0; symbolIndex < HuffmanSymbolCount; symbolIndex++)
                {
                    int length = HuffmanCodeLengths[treeIndex, symbolIndex];
                    int code = HuffmanCodeBits[treeIndex, symbolIndex];
                    lookup[treeIndex, length, code] = (byte)(symbolIndex + 1);
                }
            }

            return lookup;
        }

        private void ReadCoefficientsOrResidue(BitReaderLE reader, int[] block, int quantStartIndex, bool inter)
        {
            bool residue = quantStartIndex < 0;
            int listStart = 64;
            int listEnd = residue ? 68 : 70;
            int masksCount = 0;
            int coeffCount = 0;

            coeffList[64] = 4;
            coeffList[65] = 24;
            coeffList[66] = 44;
            modeList[64] = 0;
            modeList[65] = 0;
            modeList[66] = 0;
            if (residue)
            {
                masksCount = (int)reader.ReadBits(7);
                coeffList[67] = 0;
                modeList[67] = 2;
            }
            else
            {
                coeffList[67] = 1;
                coeffList[68] = 2;
                coeffList[69] = 3;
                modeList[67] = 3;
                modeList[68] = 3;
                modeList[69] = 3;
            }

            int bits = residue ? 1 << (int)reader.ReadBits(3) : (int)reader.ReadBits(4) - 1;
            while (residue ? bits != 0 : bits >= 0)
            {
                if (residue)
                {
                    for (int i = 0; i < coeffCount; i++)
                    {
                        if (reader.ReadBit())
                        {
                            int currentIndex = coeffIndex[i];
                            int value = block[currentIndex];
                            block[currentIndex] = value < 0 ? value - bits : value + bits;
                            if (masksCount-- == 0)
                            {
                                return;
                            }
                        }
                    }
                }

                int listPos = listStart;
                while (listPos < listEnd)
                {
                    int coefficient = coeffList[listPos];
                    int mode = modeList[listPos];
                    if ((mode | coefficient) == 0 || !reader.ReadBit())
                    {
                        listPos++;
                        continue;
                    }

                    switch (mode)
                    {
                        case 0:
                        case 2:
                            if (mode == 0)
                            {
                                coeffList[listPos] = coefficient + 4;
                                modeList[listPos] = 1;
                            }
                            else
                            {
                                coeffList[listPos] = 0;
                                modeList[listPos++] = 0;
                            }

                            for (int i = coefficient; i < coefficient + 4; i++)
                            {
                                if (reader.ReadBit())
                                {
                                    coeffList[--listStart] = i;
                                    modeList[listStart] = 3;
                                }
                                else if (residue)
                                {
                                    int offset = referenceData.Scan[i];
                                    coeffIndex[coeffCount++] = offset;
                                    block[offset] = reader.ReadBit() ? -bits : bits;
                                    if (masksCount-- == 0)
                                    {
                                        return;
                                    }
                                }
                                else
                                {
                                    int value = bits != 0
                                        ? ApplySignedMagnitude(reader, (int)reader.ReadBits(bits) | (1 << bits))
                                        : 1 - ((reader.ReadBit() ? 1 : 0) << 1);
                                    int blockIndex = referenceData.Scan[i];
                                    block[blockIndex] = value;
                                    coeffIndex[coeffCount++] = i;
                                }
                            }
                            break;
                        case 1:
                            modeList[listPos] = 2;
                            for (int i = coefficient + 4; i < coefficient + 16; i += 4)
                            {
                                coeffList[listEnd] = i;
                                modeList[listEnd++] = 2;
                            }
                            break;
                        case 3:
                            coeffList[listPos] = 0;
                            modeList[listPos++] = 0;
                            if (residue)
                            {
                                int offset = referenceData.Scan[coefficient];
                                coeffIndex[coeffCount++] = offset;
                                block[offset] = reader.ReadBit() ? -bits : bits;
                                if (masksCount-- == 0)
                                {
                                    return;
                                }
                            }
                            else
                            {
                                int value = bits != 0
                                    ? ApplySignedMagnitude(reader, (int)reader.ReadBits(bits) | (1 << bits))
                                    : 1 - ((reader.ReadBit() ? 1 : 0) << 1);
                                int blockIndex = referenceData.Scan[coefficient];
                                block[blockIndex] = value;
                                coeffIndex[coeffCount++] = coefficient;
                            }
                            break;
                    }
                }

                bits = residue ? bits >> 1 : bits - 1;
            }

            if (!residue)
            {
                int quantIndex = (int)reader.ReadBits(4);
                int[,] quantTable = inter ? referenceData.InterQuant : referenceData.IntraQuant;
                block[0] = (block[0] * quantTable[quantIndex, 0]) >> 11;
                while (coeffCount-- > 0)
                {
                    int zigZagIndex = coeffIndex[coeffCount];
                    int blockIndex = referenceData.Scan[zigZagIndex];
                    block[blockIndex] = (block[blockIndex] * quantTable[quantIndex, zigZagIndex]) >> 11;
                }
            }
        }

        private static int ApplySignedMagnitude(BitReaderLE reader, int value)
        {
            int sign = reader.ReadBit() ? -1 : 0;
            return (value ^ sign) - sign;
        }

        private static int Align8(int value) => (value + 7) & ~7;

        private static int IntLog2(int value)
        {
            int result = 0;
            while ((1 << (result + 1)) <= value)
            {
                result++;
            }
            return result;
        }

        private sealed class Tree
        {
            public int TreeIndex { get; set; }
            public byte[] Symbols { get; } = new byte[16];
        }

        private sealed class Bundle
        {
            public int LengthBits { get; set; }
            public Tree Tree { get; } = new Tree();
            public List<int> Data { get; } = new List<int>();
            public int ReadIndex { get; set; }
            public bool Active { get; set; }

            public void Reset()
            {
                Data.Clear();
                ReadIndex = 0;
                Active = true;
            }
        }
    }

    internal sealed class BinkReferenceData
    {
        private const string PackedBaseQuant =
            "11231143113210102232101034232322322312112211333332211010111110001223113211210010212110102323221222221100110032222111100011000000eyopuy4feb531jgwzmirsxwt3asvjj7r3sm2nm4jh8ulwin58cjwmrbnsfcb8mwje2b7yu6vk8fwwgbpezdvdpanec950snamd506soxoiyu5tyo3nse5lyha0qlpde7k5fr2lxpk6zrm67nd3c9t1dqhnc6n4gvhc3yvw4meg2fa83mxfva5w1az2t9fnkykiaj7wj3wpovhkgepdh5529kpareotlnzhvzr2evca6rayt5ulkq1sxt6a58x7mxg1xpjz2ogi6vom0m47tz5ftgr9aekmnura7g6c0w48u30wpasfmbljblwgxtlh8vguh077we04awyg6dtl5pzcczt3djwqhv557xe1y58ygdvvo0oh96usw9k9rl3pot";
        private static readonly int[] EmbeddedScan =
        {
            0, 1, 8, 9, 2, 3, 10, 11, 4, 5, 12, 13, 6, 7, 14, 15,
            20, 21, 28, 29, 22, 23, 30, 31, 16, 17, 24, 25, 32, 33, 40, 41,
            34, 35, 42, 43, 48, 49, 56, 57, 50, 51, 58, 59, 18, 19, 26, 27,
            36, 37, 44, 45, 38, 39, 46, 47, 52, 53, 60, 61, 54, 55, 62, 63
        };

        private static readonly int[,] EmbeddedPatterns =
        {
            {0, 8, 16, 24, 32, 40, 48, 56, 57, 49, 41, 33, 25, 17, 9, 1, 2, 10, 18, 26, 34, 42, 50, 58, 59, 51, 43, 35, 27, 19, 11, 3, 4, 12, 20, 28, 36, 44, 52, 60, 61, 53, 45, 37, 29, 21, 13, 5, 6, 14, 22, 30, 38, 46, 54, 62, 63, 55, 47, 39, 31, 23, 15, 7},
            {59, 58, 57, 56, 48, 49, 50, 51, 43, 42, 41, 40, 32, 33, 34, 35, 27, 26, 25, 24, 16, 17, 18, 19, 11, 10, 9, 8, 0, 1, 2, 3, 4, 5, 6, 7, 15, 14, 13, 12, 20, 21, 22, 23, 31, 30, 29, 28, 36, 37, 38, 39, 47, 46, 45, 44, 52, 53, 54, 55, 63, 62, 61, 60},
            {25, 17, 18, 26, 27, 19, 11, 3, 2, 10, 9, 1, 0, 8, 16, 24, 32, 40, 48, 56, 57, 49, 41, 42, 50, 58, 59, 51, 43, 35, 34, 33, 29, 21, 22, 30, 31, 23, 15, 7, 6, 14, 13, 5, 4, 12, 20, 28, 36, 44, 52, 60, 61, 53, 45, 46, 54, 62, 63, 55, 47, 39, 38, 37},
            {3, 11, 2, 10, 1, 9, 0, 8, 16, 24, 17, 25, 18, 26, 19, 27, 35, 43, 34, 42, 33, 41, 32, 40, 48, 56, 49, 57, 50, 58, 51, 59, 60, 52, 61, 53, 62, 54, 63, 55, 47, 39, 46, 38, 45, 37, 44, 36, 28, 20, 29, 21, 30, 22, 31, 23, 15, 7, 14, 6, 13, 5, 12, 4},
            {24, 25, 16, 17, 8, 9, 0, 1, 2, 3, 10, 11, 18, 19, 26, 27, 28, 29, 20, 21, 12, 13, 4, 5, 6, 7, 14, 15, 22, 23, 30, 31, 39, 38, 47, 46, 55, 54, 63, 62, 61, 60, 53, 52, 45, 44, 37, 36, 35, 34, 43, 42, 51, 50, 59, 58, 57, 56, 49, 48, 41, 40, 33, 32},
            {0, 1, 2, 3, 8, 9, 10, 11, 16, 17, 18, 19, 24, 25, 26, 27, 32, 33, 34, 35, 40, 41, 42, 43, 48, 49, 50, 51, 56, 57, 58, 59, 4, 5, 6, 7, 12, 13, 14, 15, 20, 21, 22, 23, 28, 29, 30, 31, 36, 37, 38, 39, 44, 45, 46, 47, 52, 53, 54, 55, 60, 61, 62, 63},
            {6, 7, 15, 14, 13, 5, 12, 4, 3, 11, 2, 10, 9, 1, 0, 8, 16, 24, 17, 25, 18, 26, 19, 27, 20, 28, 21, 29, 22, 30, 23, 31, 39, 47, 38, 46, 37, 45, 36, 44, 35, 43, 34, 42, 33, 41, 32, 40, 49, 48, 56, 57, 58, 50, 59, 51, 60, 52, 61, 53, 54, 55, 63, 62},
            {0, 1, 2, 3, 4, 5, 6, 7, 15, 14, 13, 12, 11, 10, 9, 8, 16, 17, 18, 19, 20, 21, 22, 23, 31, 30, 29, 28, 27, 26, 25, 24, 32, 33, 34, 35, 36, 37, 38, 39, 47, 46, 45, 44, 43, 42, 41, 40, 48, 49, 50, 51, 52, 53, 54, 55, 63, 62, 61, 60, 59, 58, 57, 56},
            {0, 8, 9, 1, 2, 3, 11, 10, 18, 19, 27, 26, 25, 17, 16, 24, 32, 40, 41, 33, 34, 35, 43, 42, 50, 49, 48, 56, 57, 58, 59, 51, 52, 60, 61, 62, 63, 55, 54, 53, 45, 44, 36, 37, 38, 46, 47, 39, 31, 23, 22, 30, 29, 28, 20, 21, 13, 12, 4, 5, 6, 14, 15, 7},
            {24, 25, 16, 17, 8, 9, 0, 1, 2, 3, 10, 11, 18, 19, 26, 27, 28, 29, 20, 21, 12, 13, 4, 5, 6, 7, 14, 15, 22, 23, 30, 31, 38, 39, 46, 47, 54, 55, 62, 63, 60, 61, 52, 53, 44, 45, 36, 37, 34, 35, 42, 43, 50, 51, 58, 59, 56, 57, 48, 49, 40, 41, 32, 33},
            {0, 8, 1, 9, 2, 10, 3, 11, 19, 27, 18, 26, 17, 25, 16, 24, 32, 40, 33, 41, 34, 42, 35, 43, 51, 59, 50, 58, 49, 57, 48, 56, 60, 52, 61, 53, 62, 54, 63, 55, 47, 39, 46, 38, 45, 37, 44, 36, 31, 23, 30, 22, 29, 21, 28, 20, 12, 4, 13, 5, 14, 6, 15, 7},
            {0, 8, 16, 24, 25, 26, 27, 19, 11, 3, 2, 1, 9, 17, 18, 10, 4, 12, 20, 28, 29, 30, 31, 23, 15, 7, 6, 5, 13, 21, 22, 14, 36, 44, 52, 60, 61, 62, 63, 55, 47, 39, 38, 37, 45, 53, 54, 46, 32, 40, 48, 56, 57, 58, 59, 51, 43, 35, 34, 33, 41, 49, 50, 42},
            {0, 8, 9, 1, 2, 3, 11, 10, 19, 27, 26, 18, 17, 16, 24, 25, 33, 32, 40, 41, 42, 34, 35, 43, 51, 59, 58, 50, 49, 57, 56, 48, 52, 60, 61, 53, 54, 62, 63, 55, 47, 39, 38, 46, 45, 44, 36, 37, 29, 28, 20, 21, 22, 30, 31, 23, 14, 15, 7, 6, 5, 13, 12, 4},
            {24, 16, 8, 0, 1, 2, 3, 11, 19, 27, 26, 25, 17, 10, 9, 18, 28, 20, 12, 4, 5, 6, 7, 15, 23, 31, 30, 29, 21, 14, 13, 22, 60, 52, 44, 36, 37, 38, 39, 47, 55, 63, 62, 61, 53, 46, 45, 54, 56, 48, 40, 32, 33, 34, 35, 43, 51, 59, 58, 57, 49, 42, 41, 50},
            {0, 8, 9, 1, 2, 10, 18, 17, 16, 24, 25, 26, 27, 19, 11, 3, 7, 6, 14, 15, 23, 22, 21, 13, 5, 4, 12, 20, 28, 29, 30, 31, 63, 62, 54, 55, 47, 46, 45, 53, 61, 60, 52, 44, 36, 37, 38, 39, 56, 48, 49, 57, 58, 50, 42, 41, 40, 32, 33, 34, 35, 43, 51, 59},
            {0, 1, 8, 9, 16, 17, 24, 25, 32, 33, 40, 41, 48, 49, 56, 57, 58, 59, 50, 51, 42, 43, 34, 35, 26, 27, 18, 19, 10, 11, 2, 3, 4, 5, 12, 13, 20, 21, 28, 29, 36, 37, 44, 45, 52, 53, 60, 61, 62, 63, 54, 55, 46, 47, 38, 39, 30, 31, 22, 23, 14, 15, 6, 7}
        };
        private static readonly BinkReferenceData Instance = Create();

        private BinkReferenceData(int[] scan, int[,] patterns, int[,] intraQuant, int[,] interQuant)
        {
            Scan = scan;
            Patterns = patterns;
            IntraQuant = intraQuant;
            InterQuant = interQuant;
        }

        public int[] Scan { get; }
        public int[,] Patterns { get; }
        public int[,] IntraQuant { get; }
        public int[,] InterQuant { get; }

        public static BinkReferenceData Load()
        {
            return Instance;
        }

        private static BinkReferenceData Create()
        {
            int[,] intraQuant;
            int[,] interQuant;
            BuildQuantTables(out intraQuant, out interQuant);
            return new BinkReferenceData(EmbeddedScan, EmbeddedPatterns, intraQuant, interQuant);
        }

        private static void BuildQuantTables(out int[,] intraQuant, out int[,] interQuant)
        {
            int[] baseQuant = UnpackValues(4, PackedBaseQuant);
            double[] factors =
            {
                1.0, 4.0 / 3.0, 5.0 / 3.0, 2.0,
                8.0 / 3.0, 7.0 / 2.0, 4.0, 5.0,
                6.0, 8.0, 12.0, 17.0,
                22.0, 28.0, 34.0, 44.0
            };

            intraQuant = new int[16, 64];
            interQuant = new int[16, 64];

            for (int level = 0; level < 32; level++)
            {
                int[,] target = level < 16 ? intraQuant : interQuant;
                int targetRow = level & 15;
                int sourceOffset = level < 16 ? 0 : 64;
                double factor = factors[level & 15];
                for (int i = 0; i < 64; i++)
                {
                    target[targetRow, i] = (int)Math.Round(baseQuant[sourceOffset + i] * factor, MidpointRounding.AwayFromZero);
                }
            }
        }

        private static int[] UnpackValues(int width, string packed)
        {
            int len = packed.Length / width;
            int[] values = new int[len];
            for (int index = 0; index < len; index++)
            {
                char[] token = new char[width];
                for (int i = 0; i < width; i++)
                {
                    token[i] = packed[(i * len) + index];
                }

                values[index] = ParseBase36(token);
            }

            return values;
        }

        private static int ParseBase36(char[] token)
        {
            int value = 0;
            for (int i = 0; i < token.Length; i++)
            {
                char c = char.ToLowerInvariant(token[i]);
                int digit;
                if (c >= '0' && c <= '9')
                {
                    digit = c - '0';
                }
                else if (c >= 'a' && c <= 'z')
                {
                    digit = 10 + (c - 'a');
                }
                else
                {
                    throw new InvalidOperationException("Invalid base36 character: " + c);
                }

                value = checked((value * 36) + digit);
            }

            return value;
        }
    }

    internal sealed class BinkDecodedVideoFrame
    {
        public BinkDecodedVideoFrame(int width, int height, byte[] yuv)
        {
            Width = width;
            Height = height;
            Yuv = yuv;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Yuv { get; }
    }
}
