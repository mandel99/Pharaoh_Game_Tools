using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using BinkInspector;

namespace PharaohGameTools
{
    internal static class MjpegMp4Muxer
    {
        private const int JpegQuality = 82;

        private sealed class SampleInfo
        {
            public uint Offset { get; set; }
            public uint Size { get; set; }
            public uint Duration { get; set; }
        }

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
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            using (var reader = new BinkSequentialPacketReader(file))
            {
                WriteFileTypeBox(writer);

                long mdatSizePos = StartBox(writer, "mdat");
                var videoSamples = new List<SampleInfo>(file.FrameIndex.Count);
                var audioSamples = new List<SampleInfo>(file.FrameIndex.Count);
                var videoDecoder = new BinkVideoDecoder(file);
                AudioTrackInfo audioTrack = file.AudioTracks.Count > 0 ? file.AudioTracks[0] : null;
                BinkRdfAudioDecoder audioDecoder = audioTrack != null ? new BinkRdfAudioDecoder(audioTrack) : null;
                int audioChannels = audioTrack != null && audioTrack.IsStereo ? 2 : 1;
                int audioBlockAlign = audioChannels * 2;

                int totalFrames = file.FrameIndex.Count;
                int lastReportedPercent = -1;
                ReportProgress(progress, 0, totalFrames, ref lastReportedPercent);

                for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
                {
                    FramePacket packet = reader.ReadFramePacket(frameIndex);
                    BinkDecodedVideoFrame decodedVideo = videoDecoder.Decode(packet);
                    byte[] jpegFrame = EncodeJpegFrame(decodedVideo.Yuv, decodedVideo.Width, decodedVideo.Height);
                    videoSamples.Add(new SampleInfo
                    {
                        Offset = checked((uint)writer.BaseStream.Position),
                        Size = checked((uint)jpegFrame.Length),
                        Duration = file.FpsDenominator == 0 ? 1u : file.FpsDenominator
                    });

                    writer.Write(jpegFrame);

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
                        audioSamples.Add(new SampleInfo
                        {
                            Offset = checked((uint)writer.BaseStream.Position),
                            Size = checked((uint)pcmBytes.Length),
                            Duration = checked((uint)(pcmBytes.Length / audioBlockAlign))
                        });

                        writer.Write(pcmBytes);
                    }

                    ReportProgress(progress, frameIndex + 1, totalFrames, ref lastReportedPercent);
                }

                EndBox(writer, mdatSizePos);

                byte[] moov = BuildMovieBox(
                    videoSamples,
                    file.Width,
                    file.Height,
                    file.FpsNumerator,
                    file.FpsDenominator,
                    audioSamples,
                    audioTrack,
                    audioChannels);
                writer.Write(moov);
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

        private static void WriteFileTypeBox(BinaryWriter writer)
        {
            long boxStart = StartBox(writer, "ftyp");
            WriteFourCc(writer, "mp42");
            writer.Write(ToBigEndian(0u));
            WriteFourCc(writer, "isom");
            WriteFourCc(writer, "mp42");
            WriteFourCc(writer, "qt  ");
            EndBox(writer, boxStart);
        }

        private static byte[] BuildMovieBox(List<SampleInfo> videoSamples, uint width, uint height, uint fpsNumerator, uint fpsDenominator, List<SampleInfo> audioSamples, AudioTrackInfo audioTrack, int audioChannels)
        {
            uint videoTimeScale = fpsNumerator == 0 ? 30u : fpsNumerator;
            uint videoSampleDuration = fpsDenominator == 0 ? 1u : fpsDenominator;
            ulong videoDuration = (ulong)videoSamples.Count * videoSampleDuration;
            uint audioTimeScale = audioTrack != null ? audioTrack.SampleRate : 0u;
            ulong audioDuration = 0;
            for (int i = 0; i < audioSamples.Count; i++)
            {
                audioDuration += audioSamples[i].Duration;
            }

            const uint movieTimeScale = 1000;
            uint movieDuration = CalculateMovieDuration(movieTimeScale, videoDuration, videoTimeScale, audioDuration, audioTimeScale);

            byte[] mvhd = CreateFullBox("mvhd", 0, 0,
                Combine(
                    ToBigEndian(0u),
                    ToBigEndian(0u),
                    ToBigEndian(movieTimeScale),
                    ToBigEndian(movieDuration),
                    ToBigEndian(0x00010000u),
                    ToBigEndian((ushort)0x0100),
                    new byte[10],
                    CreateIdentityMatrix(),
                    new byte[24],
                    ToBigEndian(audioTrack != null ? 3u : 2u)));

            byte[] vmhd = CreateFullBox("vmhd", 0, 0x00000001,
                Combine(
                    ToBigEndian((ushort)0),
                    ToBigEndian((ushort)0),
                    ToBigEndian((ushort)0),
                    ToBigEndian((ushort)0)));

            byte[] dinf = CreateBox("dinf",
                CreateFullBox("dref", 0, 0,
                    Combine(
                        ToBigEndian(1u),
                        CreateFullBox("url ", 0, 0x00000001, Array.Empty<byte>()))));

            byte[] videoStsd = CreateFullBox("stsd", 0, 0,
                Combine(
                    ToBigEndian(1u),
                    CreateJpegSampleEntry(width, height)));

            byte[] videoStts = BuildTimeToSampleBox(videoSamples);

            byte[] stsc = CreateChunkMapBox();
            byte[] videoStsz = BuildSampleSizeBox(videoSamples);
            byte[] videoStco = BuildChunkOffsetBox(videoSamples);
            byte[] stss = BuildSyncSampleBox(videoSamples.Count);

            byte[] videoStbl = CreateBox("stbl", Combine(videoStsd, videoStts, stsc, videoStsz, videoStco, stss));
            byte[] videoMdia = CreateBox("mdia", Combine(
                CreateMediaHeaderBox(videoTimeScale, checked((uint)videoDuration)),
                CreateHandlerBox("vide", "VideoHandler"),
                CreateBox("minf", Combine(vmhd, dinf, videoStbl))));
            byte[] videoTrak = CreateBox("trak", Combine(
                CreateTrackHeaderBox(1u, ScaleDuration(movieTimeScale, videoDuration, videoTimeScale), width, height, 0),
                videoMdia));

            if (audioTrack == null || audioSamples.Count == 0)
            {
                return CreateBox("moov", Combine(mvhd, videoTrak));
            }

            byte[] audioStsd = CreateFullBox("stsd", 0, 0,
                Combine(
                    ToBigEndian(1u),
                    CreateSoundSampleEntry((ushort)audioChannels, audioTrack.SampleRate, 16)));
            byte[] audioStts = BuildTimeToSampleBox(audioSamples);
            byte[] audioStsz = BuildSampleSizeBox(audioSamples);
            byte[] audioStco = BuildChunkOffsetBox(audioSamples);
            byte[] smhd = CreateFullBox("smhd", 0, 0, Combine(ToBigEndian((ushort)0), ToBigEndian((ushort)0)));
            byte[] audioStbl = CreateBox("stbl", Combine(audioStsd, audioStts, stsc, audioStsz, audioStco));
            byte[] audioMdia = CreateBox("mdia", Combine(
                CreateMediaHeaderBox(audioTimeScale, checked((uint)audioDuration)),
                CreateHandlerBox("soun", "SoundHandler"),
                CreateBox("minf", Combine(smhd, dinf, audioStbl))));
            byte[] audioTrak = CreateBox("trak", Combine(
                CreateTrackHeaderBox(2u, ScaleDuration(movieTimeScale, audioDuration, audioTimeScale), 0, 0, 0x0100),
                audioMdia));

            return CreateBox("moov", Combine(mvhd, videoTrak, audioTrak));
        }

        private static byte[] CreateTrackHeaderBox(uint trackId, uint duration, uint width, uint height, ushort volume)
        {
            return CreateFullBox("tkhd", 0, 0x00000007,
                Combine(
                    ToBigEndian(0u),
                    ToBigEndian(0u),
                    ToBigEndian(trackId),
                    ToBigEndian(0u),
                    ToBigEndian(duration),
                    new byte[8],
                    ToBigEndian((short)0),
                    ToBigEndian((short)0),
                    ToBigEndian(volume),
                    ToBigEndian((short)0),
                    CreateIdentityMatrix(),
                    ToBigEndian(width << 16),
                    ToBigEndian(height << 16)));
        }

        private static byte[] CreateMediaHeaderBox(uint timeScale, uint duration)
        {
            return CreateFullBox("mdhd", 0, 0,
                Combine(
                    ToBigEndian(0u),
                    ToBigEndian(0u),
                    ToBigEndian(timeScale),
                    ToBigEndian(duration),
                    ToBigEndian((ushort)0x55C4),
                    ToBigEndian((ushort)0)));
        }

        private static byte[] CreateHandlerBox(string handlerType, string name)
        {
            return CreateFullBox("hdlr", 0, 0,
                Combine(
                    ToBigEndian(0u),
                    Encoding.ASCII.GetBytes(handlerType),
                    new byte[12],
                    Encoding.ASCII.GetBytes(name),
                    new byte[] { 0 }));
        }

        private static byte[] CreateChunkMapBox()
        {
            return CreateFullBox("stsc", 0, 0,
                Combine(
                    ToBigEndian(1u),
                    ToBigEndian(1u),
                    ToBigEndian(1u),
                    ToBigEndian(1u)));
        }

        private static byte[] BuildTimeToSampleBox(List<SampleInfo> samples)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                var runs = new List<KeyValuePair<uint, uint>>();
                for (int i = 0; i < samples.Count; i++)
                {
                    uint duration = samples[i].Duration == 0 ? 1u : samples[i].Duration;
                    if (runs.Count > 0 && runs[runs.Count - 1].Value == duration)
                    {
                        KeyValuePair<uint, uint> previous = runs[runs.Count - 1];
                        runs[runs.Count - 1] = new KeyValuePair<uint, uint>(previous.Key + 1u, previous.Value);
                    }
                    else
                    {
                        runs.Add(new KeyValuePair<uint, uint>(1u, duration));
                    }
                }

                writer.Write(ToBigEndian((uint)runs.Count));
                for (int i = 0; i < runs.Count; i++)
                {
                    writer.Write(ToBigEndian(runs[i].Key));
                    writer.Write(ToBigEndian(runs[i].Value));
                }

                return CreateFullBox("stts", 0, 0, stream.ToArray());
            }
        }

        private static byte[] CreateSoundSampleEntry(ushort channels, ushort sampleRate, ushort bitsPerSample)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(new byte[6]);
                writer.Write(ToBigEndian((ushort)1));
                writer.Write(ToBigEndian((ushort)0));
                writer.Write(ToBigEndian((ushort)0));
                writer.Write(ToBigEndian(0u));
                writer.Write(ToBigEndian(channels));
                writer.Write(ToBigEndian(bitsPerSample));
                writer.Write(ToBigEndian((ushort)0));
                writer.Write(ToBigEndian((ushort)0));
                writer.Write(ToBigEndian((uint)sampleRate << 16));
                return CreateBox("sowt", stream.ToArray());
            }
        }

        private static byte[] CreateJpegSampleEntry(uint width, uint height)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(new byte[6]);
                writer.Write(ToBigEndian((ushort)1));
                writer.Write(ToBigEndian((ushort)0));
                writer.Write(ToBigEndian((ushort)0));
                writer.Write(ToBigEndian(0u));
                writer.Write(ToBigEndian(0u));
                writer.Write(ToBigEndian(0u));
                writer.Write(ToBigEndian((ushort)width));
                writer.Write(ToBigEndian((ushort)height));
                writer.Write(ToBigEndian(0x00480000u));
                writer.Write(ToBigEndian(0x00480000u));
                writer.Write(ToBigEndian(0u));
                writer.Write(ToBigEndian((ushort)1));

                byte[] compressorName = new byte[32];
                byte[] title = Encoding.ASCII.GetBytes("Photo - JPEG");
                compressorName[0] = (byte)Math.Min(title.Length, 31);
                Array.Copy(title, 0, compressorName, 1, compressorName[0]);
                writer.Write(compressorName);

                writer.Write(ToBigEndian((ushort)0x0018));
                writer.Write(ToBigEndian((short)-1));
                return CreateBox("jpeg", stream.ToArray());
            }
        }

        private static byte[] BuildSampleSizeBox(List<SampleInfo> samples)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ToBigEndian(0u));
                writer.Write(ToBigEndian((uint)samples.Count));
                foreach (SampleInfo sample in samples)
                {
                    writer.Write(ToBigEndian(sample.Size));
                }

                return CreateFullBox("stsz", 0, 0, stream.ToArray());
            }
        }

        private static byte[] BuildChunkOffsetBox(List<SampleInfo> samples)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ToBigEndian((uint)samples.Count));
                foreach (SampleInfo sample in samples)
                {
                    writer.Write(ToBigEndian(sample.Offset));
                }

                return CreateFullBox("stco", 0, 0, stream.ToArray());
            }
        }

        private static byte[] BuildSyncSampleBox(int sampleCount)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ToBigEndian((uint)sampleCount));
                for (int i = 0; i < sampleCount; i++)
                {
                    writer.Write(ToBigEndian((uint)(i + 1)));
                }

                return CreateFullBox("stss", 0, 0, stream.ToArray());
            }
        }

        private static uint CalculateMovieDuration(uint movieTimeScale, ulong videoDuration, uint videoTimeScale, ulong audioDuration, uint audioTimeScale)
        {
            uint video = ScaleDuration(movieTimeScale, videoDuration, videoTimeScale);
            uint audio = ScaleDuration(movieTimeScale, audioDuration, audioTimeScale);
            return Math.Max(video, audio);
        }

        private static uint ScaleDuration(uint outputTimeScale, ulong duration, uint inputTimeScale)
        {
            if (duration == 0 || inputTimeScale == 0)
            {
                return 0;
            }

            ulong scaled = ((duration * outputTimeScale) + (inputTimeScale / 2u)) / inputTimeScale;
            return checked((uint)scaled);
        }

        private static byte[] CreateIdentityMatrix()
        {
            return Combine(
                ToBigEndian(0x00010000u),
                ToBigEndian(0u),
                ToBigEndian(0u),
                ToBigEndian(0u),
                ToBigEndian(0x00010000u),
                ToBigEndian(0u),
                ToBigEndian(0u),
                ToBigEndian(0u),
                ToBigEndian(0x40000000u));
        }

        private static byte[] CreateFullBox(string type, byte version, uint flags, byte[] payload)
        {
            byte[] header = new byte[4];
            header[0] = version;
            header[1] = (byte)((flags >> 16) & 0xFF);
            header[2] = (byte)((flags >> 8) & 0xFF);
            header[3] = (byte)(flags & 0xFF);
            return CreateBox(type, Combine(header, payload));
        }

        private static byte[] CreateBox(string type, byte[] payload)
        {
            return Combine(ToBigEndian(checked((uint)(payload.Length + 8))), Encoding.ASCII.GetBytes(type), payload);
        }

        private static long StartBox(BinaryWriter writer, string type)
        {
            long start = writer.BaseStream.Position;
            writer.Write(ToBigEndian(0u));
            WriteFourCc(writer, type);
            return start;
        }

        private static void EndBox(BinaryWriter writer, long start)
        {
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start;
            writer.Write(ToBigEndian(checked((uint)(end - start))));
            writer.BaseStream.Position = end;
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
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
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

        private static void WriteFourCc(BinaryWriter writer, string value)
        {
            if (value == null || value.Length != 4)
            {
                throw new ArgumentException("FOURCC must be exactly 4 characters.", nameof(value));
            }

            writer.Write(Encoding.ASCII.GetBytes(value));
        }

        private static byte[] Combine(params byte[][] arrays)
        {
            int totalLength = arrays.Sum(array => array.Length);
            byte[] result = new byte[totalLength];
            int offset = 0;
            foreach (byte[] array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }

            return result;
        }

        private static byte[] ToBigEndian(uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        private static byte[] ToBigEndian(ushort value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        private static byte[] ToBigEndian(short value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
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
    }
}
