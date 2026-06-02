using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using BinkInspector;

namespace PharaohGameTools
{
    internal static class AviPcmExporter
    {
        private const uint AviHasIndex = 0x10;
        private const uint AviIsInterleaved = 0x100;
        private const uint AviKeyFrame = 0x10;
        private const int JpegQuality = 82;

        public static void Export(BinkFile file, string outputPath, IProgress<ExportProgressInfo>? progress = null)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path is required.", nameof(outputPath));
            }

            string folder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            using (var reader = new BinkSequentialPacketReader(file))
            {
                var videoDecoder = new BinkVideoDecoder(file);
                AudioTrackInfo audioTrack = file.AudioTracks.Count > 0 ? file.AudioTracks[0] : null;
                BinkRdfAudioDecoder audioDecoder = audioTrack != null ? new BinkRdfAudioDecoder(audioTrack) : null;
                int audioChannels = audioTrack != null && audioTrack.IsStereo ? 2 : 1;
                int audioSampleRate = audioTrack != null ? audioTrack.SampleRate : 0;
                int audioBlockAlign = audioChannels * 2;
                int audioAvgBytesPerSecond = audioSampleRate * audioBlockAlign;

                long riffSizePos = StartList(writer, "RIFF", "AVI ");
                long hdrlSizePos = StartList(writer, "LIST", "hdrl");

                long avihDataPos = StartChunk(writer, "avih");
                WriteZeros(writer, 56);
                EndChunk(writer, avihDataPos);

                long videoStrlSizePos = StartList(writer, "LIST", "strl");
                long videoStrhDataPos = StartChunk(writer, "strh");
                WriteZeros(writer, 56);
                EndChunk(writer, videoStrhDataPos);

                long videoStrfDataPos = StartChunk(writer, "strf");
                WriteBitmapInfoHeader(writer, (int)file.Width, (int)file.Height, 24, FourCc("MJPG"), (int)(file.Width * file.Height * 3));
                EndChunk(writer, videoStrfDataPos);
                EndList(writer, videoStrlSizePos);

                long audioStrhDataPos = -1;
                if (audioTrack != null)
                {
                    long audioStrlSizePos = StartList(writer, "LIST", "strl");
                    audioStrhDataPos = StartChunk(writer, "strh");
                    WriteZeros(writer, 56);
                    EndChunk(writer, audioStrhDataPos);

                    long audioStrfDataPos = StartChunk(writer, "strf");
                    WritePcmWaveFormat(writer, (short)audioChannels, audioSampleRate, audioAvgBytesPerSecond, (short)audioBlockAlign, 16);
                    EndChunk(writer, audioStrfDataPos);
                    EndList(writer, audioStrlSizePos);
                }

                EndList(writer, hdrlSizePos);

                long moviSizePos = StartList(writer, "LIST", "movi");
                long moviListOffsetBase = moviSizePos + 4;
                int maxVideoChunkSize = 0;
                int maxAudioChunkSize = 0;
                int audioBytesWritten = 0;
                var indexEntries = new List<AviIndexEntry>(file.FrameIndex.Count * 2);

                int totalFrames = file.FrameIndex.Count;
                int lastReportedPercent = -1;
                ReportProgress(progress, 0, totalFrames, ref lastReportedPercent);

                for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
                {
                    FramePacket packet = reader.ReadFramePacket(frameIndex);
                    BinkDecodedVideoFrame decodedVideo = videoDecoder.Decode(packet);
                    byte[] jpegFrame = EncodeJpegFrame(decodedVideo.Yuv, decodedVideo.Width, decodedVideo.Height);
                    maxVideoChunkSize = Math.Max(maxVideoChunkSize, jpegFrame.Length);
                    WriteAviChunk(writer, moviListOffsetBase, "00dc", jpegFrame, AviKeyFrame, indexEntries);

                    if (audioDecoder == null)
                    {
                        continue;
                    }

                    foreach (AudioPacket audioPacket in packet.AudioPackets)
                    {
                        if (audioPacket.Payload == null || audioPacket.Payload.Length == 0)
                        {
                            continue;
                        }

                        float[] decodedAudio = audioDecoder.DecodePacket(audioPacket.Payload);
                        if (decodedAudio.Length == 0)
                        {
                            continue;
                        }

                        byte[] pcmBytes = ConvertFloatSamplesToPcm16(decodedAudio);
                        audioBytesWritten += pcmBytes.Length;
                        maxAudioChunkSize = Math.Max(maxAudioChunkSize, pcmBytes.Length);
                        WriteAviChunk(writer, moviListOffsetBase, "01wb", pcmBytes, 0, indexEntries);
                    }

                    ReportProgress(progress, frameIndex + 1, totalFrames, ref lastReportedPercent);
                }

                EndList(writer, moviSizePos);

                long idx1DataPos = StartChunk(writer, "idx1");
                foreach (AviIndexEntry entry in indexEntries)
                {
                    WriteFourCc(writer, entry.ChunkId);
                    writer.Write(entry.Flags);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Size);
                }
                EndChunk(writer, idx1DataPos);

                EndList(writer, riffSizePos);

                long totalFrameCount = file.FrameIndex.Count;
                long microSecPerFrame = GetMicrosecondsPerFrame(file);
                int maxBytesPerSecond = EstimateMaxBytesPerSecond(file, maxVideoChunkSize, audioAvgBytesPerSecond);
                int suggestedBuffer = Math.Max(maxVideoChunkSize, maxAudioChunkSize);

                PatchAviMainHeader(
                    writer,
                    avihDataPos,
                    (uint)microSecPerFrame,
                    (uint)maxBytesPerSecond,
                    AviHasIndex | AviIsInterleaved,
                    (uint)totalFrameCount,
                    audioTrack != null ? 2u : 1u,
                    (uint)suggestedBuffer,
                    file.Width,
                    file.Height);

                PatchVideoStreamHeader(
                    writer,
                    videoStrhDataPos,
                    file,
                    (uint)totalFrameCount,
                    (uint)maxVideoChunkSize,
                    (short)file.Width,
                    (short)file.Height);

                if (audioTrack != null)
                {
                    PatchAudioStreamHeader(
                        writer,
                        audioStrhDataPos,
                        audioAvgBytesPerSecond,
                        audioBlockAlign,
                        audioBytesWritten / Math.Max(audioBlockAlign, 1),
                        maxAudioChunkSize);
                }
            }
        }

        private static void ReportProgress(IProgress<ExportProgressInfo>? progress, int processedFrames, int totalFrames, ref int lastReportedPercent)
        {
            if (progress == null)
            {
                return;
            }

            int safeTotalFrames = Math.Max(totalFrames, 1);
            int percent = (int)Math.Round((processedFrames * 100.0) / safeTotalFrames, MidpointRounding.AwayFromZero);
            if (percent == lastReportedPercent && processedFrames < safeTotalFrames)
            {
                return;
            }

            lastReportedPercent = percent;
            progress.Report(new ExportProgressInfo
            {
                StageText = $"Converting frame {Math.Min(processedFrames, safeTotalFrames):N0} / {safeTotalFrames:N0}",
                ProcessedFrames = Math.Min(processedFrames, safeTotalFrames),
                TotalFrames = safeTotalFrames,
                Percent = percent
            });
        }

        private static long StartList(BinaryWriter writer, string listId, string listType)
        {
            WriteFourCc(writer, listId);
            long sizePos = writer.BaseStream.Position;
            writer.Write(0);
            WriteFourCc(writer, listType);
            return sizePos;
        }

        private static void EndList(BinaryWriter writer, long sizePos)
        {
            long endPos = writer.BaseStream.Position;
            writer.BaseStream.Position = sizePos;
            writer.Write(checked((int)(endPos - sizePos - 4)));
            writer.BaseStream.Position = endPos;
        }

        private static long StartChunk(BinaryWriter writer, string chunkId)
        {
            WriteFourCc(writer, chunkId);
            long sizePos = writer.BaseStream.Position;
            writer.Write(0);
            return sizePos + 4;
        }

        private static void EndChunk(BinaryWriter writer, long dataPos)
        {
            long endPos = writer.BaseStream.Position;
            long sizePos = dataPos - 4;
            int size = checked((int)(endPos - sizePos - 4));
            writer.BaseStream.Position = sizePos;
            writer.Write(size);
            writer.BaseStream.Position = endPos;
            if ((size & 1) != 0)
            {
                writer.Write((byte)0);
            }
        }

        private static void WriteAviChunk(BinaryWriter writer, long moviListOffsetBase, string chunkId, byte[] data, uint flags, List<AviIndexEntry> indexEntries)
        {
            uint offset = checked((uint)(writer.BaseStream.Position - moviListOffsetBase));
            WriteFourCc(writer, chunkId);
            writer.Write(data.Length);
            writer.Write(data);
            if ((data.Length & 1) != 0)
            {
                writer.Write((byte)0);
            }

            indexEntries.Add(new AviIndexEntry
            {
                ChunkId = chunkId,
                Flags = flags,
                Offset = offset,
                Size = checked((uint)data.Length)
            });
        }

        private static void PatchAviMainHeader(BinaryWriter writer, long dataPos, uint microSecPerFrame, uint maxBytesPerSecond, uint flags, uint totalFrames, uint streams, uint suggestedBuffer, uint width, uint height)
        {
            writer.BaseStream.Position = dataPos;
            writer.Write(microSecPerFrame);
            writer.Write(maxBytesPerSecond);
            writer.Write(0u);
            writer.Write(flags);
            writer.Write(totalFrames);
            writer.Write(0u);
            writer.Write(streams);
            writer.Write(suggestedBuffer);
            writer.Write(width);
            writer.Write(height);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.BaseStream.Seek(0, SeekOrigin.End);
        }

        private static void PatchVideoStreamHeader(BinaryWriter writer, long dataPos, BinkFile file, uint frameCount, uint suggestedBuffer, short width, short height)
        {
            writer.BaseStream.Position = dataPos;
            WriteFourCc(writer, "vids");
            WriteFourCc(writer, "MJPG");
            writer.Write(0u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(file.FpsDenominator == 0 ? 1u : file.FpsDenominator);
            writer.Write(file.FpsNumerator == 0 ? 1u : file.FpsNumerator);
            writer.Write(0u);
            writer.Write(frameCount);
            writer.Write(suggestedBuffer);
            writer.Write(uint.MaxValue);
            writer.Write(0u);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write(width);
            writer.Write(height);
            writer.BaseStream.Seek(0, SeekOrigin.End);
        }

        private static void PatchAudioStreamHeader(BinaryWriter writer, long dataPos, int avgBytesPerSecond, int blockAlign, int lengthInSamples, int suggestedBuffer)
        {
            writer.BaseStream.Position = dataPos;
            WriteFourCc(writer, "auds");
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write((uint)blockAlign);
            writer.Write((uint)avgBytesPerSecond);
            writer.Write(0u);
            writer.Write((uint)lengthInSamples);
            writer.Write((uint)suggestedBuffer);
            writer.Write(uint.MaxValue);
            writer.Write((uint)blockAlign);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.BaseStream.Seek(0, SeekOrigin.End);
        }

        private static void WriteBitmapInfoHeader(BinaryWriter writer, int width, int height, short bitCount, uint compression, int imageSize)
        {
            writer.Write(40u);
            writer.Write(width);
            writer.Write(height);
            writer.Write((short)1);
            writer.Write(bitCount);
            writer.Write(compression);
            writer.Write(imageSize);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);
        }

        private static void WritePcmWaveFormat(BinaryWriter writer, short channels, int samplesPerSecond, int avgBytesPerSecond, short blockAlign, short bitsPerSample)
        {
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(samplesPerSecond);
            writer.Write(avgBytesPerSecond);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
        }

        private static void WriteZeros(BinaryWriter writer, int count)
        {
            writer.Write(new byte[count]);
        }

        private static void WriteFourCc(BinaryWriter writer, string value)
        {
            if (value == null || value.Length != 4)
            {
                throw new ArgumentException("FOURCC must be exactly 4 characters.", nameof(value));
            }

            writer.Write((byte)value[0]);
            writer.Write((byte)value[1]);
            writer.Write((byte)value[2]);
            writer.Write((byte)value[3]);
        }

        private static uint FourCc(string value)
        {
            return (uint)value[0] |
                ((uint)value[1] << 8) |
                ((uint)value[2] << 16) |
                ((uint)value[3] << 24);
        }

        private static long GetMicrosecondsPerFrame(BinkFile file)
        {
            uint numerator = file.FpsNumerator == 0 ? 1u : file.FpsNumerator;
            uint denominator = file.FpsDenominator == 0 ? 1u : file.FpsDenominator;
            return ((1000000L * denominator) + (numerator / 2)) / numerator;
        }

        private static int EstimateMaxBytesPerSecond(BinkFile file, int maxVideoChunkSize, int audioAvgBytesPerSecond)
        {
            double fps = file.FpsDenominator == 0 ? 30.0 : (double)file.FpsNumerator / file.FpsDenominator;
            int videoBytesPerSecond = (int)Math.Ceiling(maxVideoChunkSize * Math.Max(fps, 1.0));
            return videoBytesPerSecond + audioAvgBytesPerSecond;
        }

        private static byte[] EncodeJpegFrame(byte[] yuv, int width, int height)
        {
            using (var bitmap = CreateBitmapFromYuv(yuv, width, height))
            using (var stream = new MemoryStream())
            {
                ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegCodec == null)
                {
                    bitmap.Save(stream, ImageFormat.Jpeg);
                }
                else
                {
                    try
                    {
                        using (var parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);
                            bitmap.Save(stream, jpegCodec, parameters);
                        }
                    }
                    catch (ArgumentException)
                    {
                        stream.SetLength(0);
                        bitmap.Save(stream, ImageFormat.Jpeg);
                    }
                }

                return stream.ToArray();
            }
        }

        private static Bitmap CreateBitmapFromYuv(byte[] yuv, int width, int height)
        {
            int chromaWidth = (width + 1) >> 1;
            int yPlaneSize = width * height;
            int uvPlaneSize = chromaWidth * ((height + 1) >> 1);
            int uOffset = yPlaneSize;
            int vOffset = yPlaneSize + uvPlaneSize;

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            Rectangle bounds = new Rectangle(0, 0, width, height);
            BitmapData data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int stride = data.Stride;
                byte[] pixels = new byte[stride * height];
                for (int y = 0; y < height; y++)
                {
                    int yRow = y * width;
                    int uvRow = (y >> 1) * chromaWidth;
                    int pixelRow = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int ySample = yuv[yRow + x];
                        int uvIndex = uvRow + (x >> 1);
                        int uSample = yuv[uOffset + uvIndex];
                        int vSample = yuv[vOffset + uvIndex];

                        int c = ySample - 16;
                        int d = uSample - 128;
                        int e = vSample - 128;
                        if (c < 0)
                        {
                            c = 0;
                        }

                        int red = ClampToByte((298 * c + 409 * e + 128) >> 8);
                        int green = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
                        int blue = ClampToByte((298 * c + 516 * d + 128) >> 8);

                        int pixelOffset = pixelRow + (x * 3);
                        pixels[pixelOffset] = (byte)blue;
                        pixels[pixelOffset + 1] = (byte)green;
                        pixels[pixelOffset + 2] = (byte)red;
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        private static byte[] ConvertFloatSamplesToPcm16(float[] samples)
        {
            byte[] pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = samples[i];
                if (sample < -1f)
                {
                    sample = -1f;
                }
                else if (sample > 1f)
                {
                    sample = 1f;
                }

                short pcmValue = (short)Math.Round(sample * short.MaxValue);
                int offset = i * 2;
                pcm[offset] = (byte)(pcmValue & 0xFF);
                pcm[offset + 1] = (byte)((pcmValue >> 8) & 0xFF);
            }

            return pcm;
        }

        private static int ClampToByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 255)
            {
                return 255;
            }

            return value;
        }

        private sealed class AviIndexEntry
        {
            public string ChunkId { get; set; }
            public uint Flags { get; set; }
            public uint Offset { get; set; }
            public uint Size { get; set; }
        }

    }
}
