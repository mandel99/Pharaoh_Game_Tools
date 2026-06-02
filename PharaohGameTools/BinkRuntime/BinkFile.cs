using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BinkInspector
{
    internal sealed class BinkFile
    {
        private const uint BikTagMask = 0x00FFFFFF;
        private const uint BikTag = 0x004B4942;  // "BIK"
        private const char SupportedRevision = 'f';
        private const ushort AudioFlagStereo = 0x2000;
        private const ushort AudioFlagUseDct = 0x1000;

        private readonly string filePath;
        private readonly List<FrameIndexEntry> frameIndex;
        private readonly List<AudioTrackInfo> audioTracks;

        private BinkFile(
            string filePath,
            uint width,
            uint height,
            uint fpsNumerator,
            uint fpsDenominator,
            List<AudioTrackInfo> audioTracks,
            List<FrameIndexEntry> frameIndex)
        {
            this.filePath = filePath;
            Width = width;
            Height = height;
            FpsNumerator = fpsNumerator;
            FpsDenominator = fpsDenominator;
            this.audioTracks = audioTracks;
            this.frameIndex = frameIndex;
        }

        public string FilePath => filePath;
        public uint Width { get; }
        public uint Height { get; }
        public uint FpsNumerator { get; }
        public uint FpsDenominator { get; }
        public IReadOnlyList<AudioTrackInfo> AudioTracks => audioTracks;
        public IReadOnlyList<FrameIndexEntry> FrameIndex => frameIndex;

        public static BinkFile Load(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII, false))
            {
                uint codecTag = reader.ReadUInt32();
                if (!IsBikSignature(codecTag))
                {
                    throw new InvalidDataException("Unsupported file signature. Expected a plain BIK file.");
                }

                char revision = (char)((codecTag >> 24) & 0xFF);
                if (revision != SupportedRevision)
                {
                    throw new NotSupportedException("Only BIKf video files are supported by the runtime decoder.");
                }

                uint declaredFileSize = checked(reader.ReadUInt32() + 8u);
                uint frameCount = reader.ReadUInt32();
                reader.ReadUInt32(); // largest frame size
                reader.ReadUInt32(); // unknown Bink 1 header field
                uint width = reader.ReadUInt32();
                uint height = reader.ReadUInt32();
                uint fpsNumerator = reader.ReadUInt32();
                uint fpsDenominator = reader.ReadUInt32();
                uint videoFlagsRaw = reader.ReadUInt32();
                uint audioTrackCount = reader.ReadUInt32();

                if (videoFlagsRaw != 0)
                {
                    throw new NotSupportedException("Only plain YUV BIKf videos without alpha, grayscale, or scaling flags are supported.");
                }

                if (audioTrackCount != 1)
                {
                    throw new NotSupportedException("Only Pharaoh's single-track BIKf files are supported.");
                }

                uint maxDecodedSize = reader.ReadUInt32();
                ushort sampleRate = reader.ReadUInt16();
                ushort flags = reader.ReadUInt16();
                if ((flags & AudioFlagUseDct) != 0)
                {
                    throw new NotSupportedException("Only RDFT Bink audio is supported.");
                }

                AudioTrackInfo audioTrack = new AudioTrackInfo(
                    sampleRate,
                    maxDecodedSize,
                    (flags & AudioFlagStereo) != 0);

                reader.ReadUInt32(); // Bink audio track id

                uint nextPos = reader.ReadUInt32();
                bool nextKeyframe = true;
                List<FrameIndexEntry> index = new List<FrameIndexEntry>((int)frameCount);
                for (int i = 0; i < frameCount; i++)
                {
                    bool isKeyframe = nextKeyframe;
                    uint pos = nextPos & ~1u;
                    if (i == frameCount - 1)
                    {
                        nextPos = declaredFileSize;
                        nextKeyframe = false;
                    }
                    else
                    {
                        uint rawNextPos = reader.ReadUInt32();
                        nextKeyframe = (rawNextPos & 1u) != 0;
                        nextPos = rawNextPos & ~1u;
                    }

                    if (nextPos <= pos)
                    {
                        throw new InvalidDataException("Invalid frame index table.");
                    }

                    index.Add(new FrameIndexEntry(pos, nextPos - pos, isKeyframe));
                }

                return new BinkFile(
                    path,
                    width,
                    height,
                    fpsNumerator,
                    fpsDenominator,
                    new List<AudioTrackInfo> { audioTrack },
                    index);
            }
        }

        private static bool IsBikSignature(uint codecTag)
        {
            return (codecTag & BikTagMask) == BikTag;
        }
    }

    internal sealed class AudioTrackInfo
    {
        public AudioTrackInfo(ushort sampleRate, uint maxDecodedSize, bool isStereo)
        {
            SampleRate = sampleRate;
            MaxDecodedSize = maxDecodedSize;
            IsStereo = isStereo;
        }

        public ushort SampleRate { get; }
        public uint MaxDecodedSize { get; }
        public bool IsStereo { get; }
    }

    internal sealed class FrameIndexEntry
    {
        public FrameIndexEntry(uint offset, uint size, bool isKeyframe)
        {
            Offset = offset;
            Size = size;
            IsKeyframe = isKeyframe;
        }

        public uint Offset { get; }
        public uint Size { get; }
        public bool IsKeyframe { get; }
    }

    internal sealed class AudioPacket
    {
        public AudioPacket(byte[] payload)
        {
            Payload = payload;
        }

        public byte[] Payload { get; }
    }

    internal sealed class FramePacket
    {
        public FramePacket(IReadOnlyList<AudioPacket> audioPackets, byte[] videoPayload)
        {
            AudioPackets = audioPackets;
            VideoPayload = videoPayload;
        }

        public IReadOnlyList<AudioPacket> AudioPackets { get; }
        public byte[] VideoPayload { get; }
    }

    internal sealed class BinkSequentialPacketReader : IDisposable
    {
        private readonly BinkFile file;
        private readonly FileStream stream;
        private readonly BinaryReader reader;

        public BinkSequentialPacketReader(BinkFile file)
        {
            this.file = file ?? throw new ArgumentNullException(nameof(file));
            stream = File.OpenRead(file.FilePath);
            reader = new BinaryReader(stream, Encoding.ASCII, false);
        }

        public FramePacket ReadFramePacket(int frameNumber)
        {
            if (frameNumber < 0 || frameNumber >= file.FrameIndex.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(frameNumber));
            }

            FrameIndexEntry entry = file.FrameIndex[frameNumber];
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            uint audioSize = reader.ReadUInt32();
            int bytesConsumed = 4;
            if (audioSize > entry.Size - bytesConsumed)
            {
                throw new InvalidDataException("Audio packet exceeds frame boundary.");
            }

            int encodedSize = 0;
            if (audioSize > 3)
            {
                reader.ReadUInt32(); // decoded sample count, not needed by the runtime path
                bytesConsumed += 4;
                encodedSize = checked((int)audioSize - 4);
            }

            byte[] audioPayload = ReadExact(encodedSize, "audio payload");
            bytesConsumed += audioPayload.Length;

            int videoSize = checked((int)entry.Size - bytesConsumed);
            byte[] videoPayload = ReadExact(videoSize, "video payload");

            return new FramePacket(new List<AudioPacket> { new AudioPacket(audioPayload) }, videoPayload);
        }

        public void Dispose()
        {
            reader.Dispose();
            stream.Dispose();
        }

        private byte[] ReadExact(int count, string description)
        {
            if (count < 0)
            {
                throw new InvalidDataException("Negative " + description + " size.");
            }

            byte[] payload = reader.ReadBytes(count);
            if (payload.Length != count)
            {
                throw new EndOfStreamException("Unexpected end of file while reading " + description + ".");
            }

            return payload;
        }
    }
}
