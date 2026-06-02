using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal static class ImagingCodec
    {
        private const int IsoTileWidth = 58;
        private const int IsoTileHeight = 30;
        private const int IsoTileBytes = 1800;
        private const int IsoLargeTileWidth = 78;
        private const int IsoLargeTileHeight = 40;
        private const int IsoLargeTileBytes = 3200;

        public static Bitmap DecodeImage(SgContainer container, ImageEntry entry)
        {
            if (entry.ReplacementBitmap != null)
            {
                return CloneBitmap(entry.ReplacementBitmap);
            }

            if (entry.CachedPreview != null)
            {
                return CloneBitmap(entry.CachedPreview);
            }

            var bitmap = DecodeOriginalImage(container, entry.Record.Index);
            entry.CachedPreview = CloneBitmap(bitmap);
            return bitmap;
        }

        public static Bitmap DecodeOriginalImage(SgContainer container, int recordIndex)
        {
            var record = container.Records[recordIndex];
            var work = record;
            var mirror = false;
            if (record.IsMirror && record.MirrorOfIndex.HasValue)
            {
                work = container.Records[record.MirrorOfIndex.Value];
                mirror = true;
            }

            var sourcePath = SgArchive.GetResolvedSourcePath(container, recordIndex);
            if (string.IsNullOrEmpty(sourcePath))
            {
                throw new InvalidOperationException("No backing .555 source was resolved for the selected image.");
            }

            var sourceBytes = SgArchive.GetSourceBytes(container, sourcePath);
            var start = BinaryHelpers.DataStart(work);
            var imageBlob = BinaryHelpers.Slice(sourceBytes, start, checked((int)work.Length));
            Bitmap image;

            if (SgConstants.PlainTypes.Contains(work.Type))
            {
                image = DecodeRaw555(imageBlob, work.Width, work.Height, true);
            }
            else if (SgConstants.SpriteTypes.Contains(work.Type))
            {
                image = DecodeTransparentStream(imageBlob, work.Width, work.Height);
            }
            else if (SgConstants.IsometricTypes.Contains(work.Type))
            {
                image = DecodeIsometric(work, imageBlob);
            }
            else if (imageBlob.Length == work.Width * work.Height * 2)
            {
                image = DecodeRaw555(imageBlob, work.Width, work.Height, true);
            }
            else
            {
                throw new NotSupportedException(string.Format("Unsupported image type {0}.", work.Type));
            }

            if (work.AlphaLength > 0)
            {
                var alphaStart = start + checked((int)work.Length);
                var alphaBlob = BinaryHelpers.Slice(sourceBytes, alphaStart, checked((int)work.AlphaLength));
                ApplyAlphaStream(image, alphaBlob);
            }

            if (mirror)
            {
                image.RotateFlip(RotateFlipType.RotateNoneFlipX);
            }

            return image;
        }

        public static byte[] EncodeImageForRecord(SgContainer container, ImageEntry entry, Bitmap replacement)
        {
            var record = entry.Record;
            if (record.IsMirror)
            {
                throw new InvalidOperationException("A mirrored record cannot be saved directly. Replace the original non-mirrored record.");
            }

            if (replacement.Width != record.Width || replacement.Height != record.Height)
            {
                throw new InvalidOperationException(string.Format("Image size must be {0}x{1}.", record.Width, record.Height));
            }

            if (SgConstants.PlainTypes.Contains(record.Type))
            {
                var original = GetOriginalBlobWithoutAlpha(container, record);
                var highBitMode = DetectHighBitMode(original);
                return EncodeRaw555(replacement, highBitMode, true);
            }

            if (SgConstants.SpriteTypes.Contains(record.Type))
            {
                var maxRun = record.Type == 256 ? 16 : 255;
                return EncodeTransparentStream(replacement, maxRun);
            }

            if (SgConstants.IsometricTypes.Contains(record.Type))
            {
                return EncodeIsometric(record, replacement);
            }

            throw new NotSupportedException(string.Format("Replacing type {0} is not supported.", record.Type));
        }

        public static Bitmap CloneBitmap(Bitmap source)
        {
            return new Bitmap(source);
        }

        public static Bitmap DecodeRaw555(byte[] data, int width, int height, bool transparentKey)
        {
            var expected = checked(width * height * 2);
            if (data.Length != expected)
            {
                throw new InvalidDataException(string.Format("RAW555 size mismatch. Expected {0}, got {1}.", expected, data.Length));
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var index = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var color = (ushort)(data[index] | (data[index + 1] << 8));
                    index += 2;
                    bitmap.SetPixel(x, y, transparentKey ? Rgb555ToColor(color) : Rgb555ToOpaqueColor(color));
                }
            }

            return bitmap;
        }

        public static byte[] EncodeRaw555(Bitmap bitmap, int? forceHighBit, bool transparentKey)
        {
            var output = new byte[bitmap.Width * bitmap.Height * 2];
            var index = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var color = ColorToRgb555(bitmap.GetPixel(x, y), transparentKey);
                    if (forceHighBit.HasValue && color != SgConstants.TransparentColor)
                    {
                        color = forceHighBit.Value != 0 ? (ushort)(color | 0x8000) : (ushort)(color & 0x7FFF);
                    }

                    output[index++] = (byte)(color & 0xFF);
                    output[index++] = (byte)((color >> 8) & 0xFF);
                }
            }

            return output;
        }

        public static Bitmap DecodeTransparentStream(byte[] data, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
            }

            var x = 0;
            var y = 0;
            var i = 0;
            while (i < data.Length && y < height)
            {
                var command = data[i++];
                if (command == 0xFF)
                {
                    if (i >= data.Length)
                    {
                        break;
                    }

                    var skip = data[i++];
                    x += skip;
                    while (x >= width && y < height)
                    {
                        x -= width;
                        y++;
                    }

                    continue;
                }

                var run = command;
                for (var j = 0; j < run && y < height; j++)
                {
                    if (i + 1 >= data.Length)
                    {
                        break;
                    }

                    var color = (ushort)(data[i] | (data[i + 1] << 8));
                    i += 2;
                    if (color != SgConstants.TransparentColor)
                    {
                        bitmap.SetPixel(x, y, Rgb555ToColor(color));
                    }

                    x++;
                    if (x >= width)
                    {
                        x = 0;
                        y++;
                    }
                }
            }

            return bitmap;
        }

        public static byte[] EncodeTransparentStream(Bitmap bitmap, int maxDrawRun)
        {
            var output = new List<byte>(bitmap.Width * bitmap.Height * 2);
            for (var y = 0; y < bitmap.Height; y++)
            {
                var x = 0;
                while (x < bitmap.Width)
                {
                    if (bitmap.GetPixel(x, y).A == 0)
                    {
                        var run = 1;
                        while (x + run < bitmap.Width && bitmap.GetPixel(x + run, y).A == 0 && run < 255)
                        {
                            run++;
                        }

                        output.Add(0xFF);
                        output.Add((byte)run);
                        x += run;
                        continue;
                    }

                    var drawRun = 1;
                    while (x + drawRun < bitmap.Width && bitmap.GetPixel(x + drawRun, y).A != 0 && drawRun < maxDrawRun)
                    {
                        drawRun++;
                    }

                    output.Add((byte)drawRun);
                    for (var j = 0; j < drawRun; j++)
                    {
                        var color = ColorToRgb555(bitmap.GetPixel(x + j, y), true);
                        output.Add((byte)(color & 0xFF));
                        output.Add((byte)((color >> 8) & 0xFF));
                    }

                    x += drawRun;
                }
            }

            return output.ToArray();
        }

        public static void ApplyAlphaStream(Bitmap bitmap, byte[] data)
        {
            var x = 0;
            var y = 0;
            var i = 0;
            while (i < data.Length && y < bitmap.Height)
            {
                var command = data[i++];
                if (command == 0xFF)
                {
                    if (i >= data.Length)
                    {
                        break;
                    }

                    var skip = data[i++];
                    x += skip;
                    while (x >= bitmap.Width && y < bitmap.Height)
                    {
                        x -= bitmap.Width;
                        y++;
                    }

                    continue;
                }

                var run = command;
                for (var j = 0; j < run && y < bitmap.Height; j++)
                {
                    if (i >= data.Length)
                    {
                        break;
                    }

                    var a5 = data[i++] & 0x1F;
                    var alpha = (a5 << 3) | (a5 >> 2);
                    var pixel = bitmap.GetPixel(x, y);
                    bitmap.SetPixel(x, y, Color.FromArgb(alpha, pixel.R, pixel.G, pixel.B));
                    x++;
                    if (x >= bitmap.Width)
                    {
                        x = 0;
                        y++;
                    }
                }
            }
        }

        public static byte[] EncodeAlphaStream(Bitmap original, Bitmap replacement)
        {
            var output = new List<byte>();
            for (var y = 0; y < replacement.Height; y++)
            {
                var x = 0;
                while (x < replacement.Width)
                {
                    if (original.GetPixel(x, y).A == replacement.GetPixel(x, y).A)
                    {
                        var run = 1;
                        while (x + run < replacement.Width && original.GetPixel(x + run, y).A == replacement.GetPixel(x + run, y).A && run < 255)
                        {
                            run++;
                        }

                        output.Add(0xFF);
                        output.Add((byte)run);
                        x += run;
                        continue;
                    }

                    var draw = 1;
                    while (x + draw < replacement.Width && original.GetPixel(x + draw, y).A != replacement.GetPixel(x + draw, y).A && draw < 255)
                    {
                        draw++;
                    }

                    output.Add((byte)draw);
                    for (var j = 0; j < draw; j++)
                    {
                        output.Add((byte)((replacement.GetPixel(x + j, y).A >> 3) & 0x1F));
                    }

                    x += draw;
                }
            }

            return output.ToArray();
        }

        public static int? DetectHighBitMode(byte[] rawBlob)
        {
            var seenAny = false;
            var allZero = true;
            var allOne = true;
            for (var i = 0; i + 1 < rawBlob.Length; i += 2)
            {
                var color = (ushort)(rawBlob[i] | (rawBlob[i + 1] << 8));
                if (color == SgConstants.TransparentColor)
                {
                    continue;
                }

                seenAny = true;
                var bit = (color >> 15) & 1;
                if (bit == 1)
                {
                    allZero = false;
                }
                else
                {
                    allOne = false;
                }
            }

            if (!seenAny)
            {
                return null;
            }

            if (allOne)
            {
                return 1;
            }

            if (allZero)
            {
                return 0;
            }

            return null;
        }

        public static byte[] GetOriginalBlobWithoutAlpha(SgContainer container, ImageRecord record)
        {
            var bitmapId = container.BitmapByRecordIndex.ContainsKey(record.Index)
                ? container.BitmapByRecordIndex[record.Index].Id
                : record.BitmapId;
            var sourcePath = container.SourcePathByBitmapId[bitmapId];
            var sourceBytes = SgArchive.GetSourceBytes(container, sourcePath);
            return BinaryHelpers.Slice(sourceBytes, BinaryHelpers.DataStart(record), checked((int)record.Length));
        }

        public static int GetEffectiveBitmapId(SgContainer container, int recordIndex)
        {
            BitmapRecord bitmap;
            return container.BitmapByRecordIndex.TryGetValue(recordIndex, out bitmap) ? bitmap.Id : container.Records[recordIndex].BitmapId;
        }

        
        private static Bitmap DecodeIsometric(ImageRecord record, byte[] imageBlob)
        {
            var size = record.Flags != null && record.Flags.Length > 3 ? record.Flags[3] : 0;
            var width = record.Width;
            var logicalHeight = (width + 2) / 2;
            var heightOffset = record.Height - logicalHeight;

            int tileBytes;
            int tileWidth;
            int tileHeight;
            if (size > 0 && logicalHeight == IsoTileHeight * size)
            {
                tileBytes = IsoTileBytes;
                tileWidth = IsoTileWidth;
                tileHeight = IsoTileHeight;
            }
            else if (size > 0 && logicalHeight == IsoLargeTileHeight * size)
            {
                tileBytes = IsoLargeTileBytes;
                tileWidth = IsoLargeTileWidth;
                tileHeight = IsoLargeTileHeight;
            }
            else
            {
                tileBytes = IsoTileBytes;
                tileWidth = IsoTileWidth;
                tileHeight = IsoTileHeight;
                if (logicalHeight % IsoTileHeight == 0)
                {
                    size = logicalHeight / IsoTileHeight;
                }
                else if (logicalHeight % IsoLargeTileHeight == 0)
                {
                    size = logicalHeight / IsoLargeTileHeight;
                    tileBytes = IsoLargeTileBytes;
                    tileWidth = IsoLargeTileWidth;
                    tileHeight = IsoLargeTileHeight;
                }
            }

            if (size <= 0)
            {
                throw new InvalidDataException("Cannot infer isometric tile size.");
            }

            var bitmap = new Bitmap(record.Width, record.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
            }

            var baseLength = checked((int)record.UncompressedLength);
            var overlayLength = checked((int)record.Length) - baseLength;
            var baseBytes = BinaryHelpers.Slice(imageBlob, 0, baseLength);
            var overlayBytes = overlayLength > 0 ? BinaryHelpers.Slice(imageBlob, baseLength, overlayLength) : Array.Empty<byte>();

            var baseIndex = 0;
            var yOffset = heightOffset;
            for (var y = 0; y < size + (size - 1); y++)
            {
                var xOffset = (y < size ? size - y - 1 : y - size + 1) * tileHeight;
                var tileCount = y < size ? y + 1 : (2 * size) - y - 1;
                for (var t = 0; t < tileCount; t++)
                {
                    var tile = BinaryHelpers.Slice(baseBytes, baseIndex, tileBytes);
                    baseIndex += tileBytes;
                    BlitIsoTile(bitmap, tile, xOffset, yOffset, tileWidth, tileHeight);
                    xOffset += tileWidth + 2;
                }

                yOffset += tileHeight / 2;
            }

            if (overlayBytes.Length > 0)
            {
                using (var overlay = DecodeTransparentStream(overlayBytes, record.Width, record.Height))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.DrawImageUnscaled(overlay, 0, 0);
                }
            }

            return bitmap;
        }

        private static byte[] EncodeIsometric(ImageRecord record, Bitmap bitmap)
        {
            var size = record.Flags != null && record.Flags.Length > 3 ? record.Flags[3] : 0;
            var width = record.Width;
            var logicalHeight = (width + 2) / 2;
            var heightOffset = record.Height - logicalHeight;

            int tileBytes;
            int tileWidth;
            int tileHeight;
            if (size > 0 && logicalHeight == IsoTileHeight * size)
            {
                tileBytes = IsoTileBytes;
                tileWidth = IsoTileWidth;
                tileHeight = IsoTileHeight;
            }
            else if (size > 0 && logicalHeight == IsoLargeTileHeight * size)
            {
                tileBytes = IsoLargeTileBytes;
                tileWidth = IsoLargeTileWidth;
                tileHeight = IsoLargeTileHeight;
            }
            else
            {
                throw new InvalidDataException("Unsupported isometric tile dimensions.");
            }

            var baseBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(baseBitmap))
            {
                g.Clear(Color.Transparent);
            }

            var baseBytes = new List<byte>(checked((int)record.UncompressedLength));
            var yOffset = heightOffset;
            for (var y = 0; y < size + (size - 1); y++)
            {
                var xOffset = (y < size ? size - y - 1 : y - size + 1) * tileHeight;
                var tileCount = y < size ? y + 1 : (2 * size) - y - 1;
                for (var t = 0; t < tileCount; t++)
                {
                    ExtractIsoTile(bitmap, baseBitmap, baseBytes, xOffset, yOffset, tileWidth, tileHeight, tileBytes);
                    xOffset += tileWidth + 2;
                }

                yOffset += tileHeight / 2;
            }

            using (var overlay = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb))
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var source = bitmap.GetPixel(x, y);
                        var baseline = baseBitmap.GetPixel(x, y);
                        overlay.SetPixel(x, y, source.ToArgb() == baseline.ToArgb() ? Color.Transparent : source);
                    }
                }

                var overlayBytes = EncodeTransparentStream(overlay, 255);
                var result = new byte[baseBytes.Count + overlayBytes.Length];
                baseBytes.CopyTo(result, 0);
                Buffer.BlockCopy(overlayBytes, 0, result, baseBytes.Count, overlayBytes.Length);
                return result;
            }
        }

        private static void BlitIsoTile(Bitmap bitmap, byte[] tile, int offsetX, int offsetY, int tileWidth, int tileHeight)
        {
            var halfHeight = tileHeight / 2;
            var i = 0;
            for (var y = 0; y < halfHeight; y++)
            {
                var start = tileHeight - 2 * (y + 1);
                var end = tileWidth - start;
                for (var x = start; x < end; x++)
                {
                    var color = (ushort)(tile[i] | (tile[i + 1] << 8));
                    i += 2;
                    var pixel = Rgb555ToColor(color);
                    if (pixel.A > 0)
                    {
                        bitmap.SetPixel(offsetX + x, offsetY + y, pixel);
                    }
                }
            }

            for (var y = halfHeight; y < tileHeight; y++)
            {
                var start = 2 * y - tileHeight;
                var end = tileWidth - start;
                for (var x = start; x < end; x++)
                {
                    var color = (ushort)(tile[i] | (tile[i + 1] << 8));
                    i += 2;
                    var pixel = Rgb555ToColor(color);
                    if (pixel.A > 0)
                    {
                        bitmap.SetPixel(offsetX + x, offsetY + y, pixel);
                    }
                }
            }
        }

        private static void ExtractIsoTile(Bitmap source, Bitmap baseline, List<byte> output, int offsetX, int offsetY, int tileWidth, int tileHeight, int tileBytes)
        {
            var startCount = output.Count;
            var halfHeight = tileHeight / 2;
            for (var y = 0; y < halfHeight; y++)
            {
                var start = tileHeight - 2 * (y + 1);
                var end = tileWidth - start;
                for (var x = start; x < end; x++)
                {
                    var pixel = source.GetPixel(offsetX + x, offsetY + y);
                    var color = ColorToRgb555(pixel, true);
                    output.Add((byte)(color & 0xFF));
                    output.Add((byte)((color >> 8) & 0xFF));
                    baseline.SetPixel(offsetX + x, offsetY + y, pixel.A == 0 ? Color.Transparent : pixel);
                }
            }

            for (var y = halfHeight; y < tileHeight; y++)
            {
                var start = 2 * y - tileHeight;
                var end = tileWidth - start;
                for (var x = start; x < end; x++)
                {
                    var pixel = source.GetPixel(offsetX + x, offsetY + y);
                    var color = ColorToRgb555(pixel, true);
                    output.Add((byte)(color & 0xFF));
                    output.Add((byte)((color >> 8) & 0xFF));
                    baseline.SetPixel(offsetX + x, offsetY + y, pixel.A == 0 ? Color.Transparent : pixel);
                }
            }

            while (output.Count - startCount < tileBytes)
            {
                output.Add(0);
            }
        }

        private static Color Rgb555ToColor(ushort color)
        {
            if (color == SgConstants.TransparentColor)
            {
                return Color.Transparent;
            }

            var r5 = (color >> 10) & 0x1F;
            var g5 = (color >> 5) & 0x1F;
            var b5 = color & 0x1F;
            var r = (r5 << 3) | (r5 >> 2);
            var g = (g5 << 3) | (g5 >> 2);
            var b = (b5 << 3) | (b5 >> 2);
            return Color.FromArgb(255, r, g, b);
        }

        private static Color Rgb555ToOpaqueColor(ushort color)
        {
            var r5 = (color >> 10) & 0x1F;
            var g5 = (color >> 5) & 0x1F;
            var b5 = color & 0x1F;
            var r = (r5 << 3) | (r5 >> 2);
            var g = (g5 << 3) | (g5 >> 2);
            var b = (b5 << 3) | (b5 >> 2);
            return Color.FromArgb(255, r, g, b);
        }

        private static ushort ColorToRgb555(Color color, bool transparentKey)
        {
            if (transparentKey && color.A == 0)
            {
                return SgConstants.TransparentColor;
            }

            return (ushort)(((color.R >> 3) << 10) | ((color.G >> 3) << 5) | (color.B >> 3));
        }
    }
}
