using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal static class BatchWorkspace
    {
        internal sealed class ImportResult
        {
            public int ChangedCount { get; set; }
            public int SizeMismatchCount { get; set; }
            public List<string> Messages { get; } = new List<string>();
        }

        public static void ExportContainer(
            SgContainer container,
            string outputFolder,
            bool skipSystemImages = false,
            Action<int, int, ImageEntry> progressCallback = null,
            Func<bool> continueCallback = null)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            Directory.CreateDirectory(outputFolder);

            var exportImages = container.Images
                .Where(x => !(skipSystemImages && IsSystemImage(x)))
                .OrderBy(x => x.DisplayId)
                .ToList();

            for (var i = 0; i < exportImages.Count; i++)
            {
                if (continueCallback != null && !continueCallback())
                {
                    return;
                }

                var image = exportImages[i];
                progressCallback?.Invoke(i + 1, exportImages.Count, image);

                var groupFolderName = BinaryHelpers.SanitizeFolderName(image.GroupName);
                var fileName = GetSgImageId(image).ToString("D4") + ".png";
                var relativePath = Path.Combine(groupFolderName, fileName);
                var absolutePath = Path.Combine(outputFolder, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

                try
                {
                    using (var bitmap = ImagingCodec.DecodeImage(container, image))
                    {
                        bitmap.Save(absolutePath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch
                {
                    // Keep exporting the remaining images even if one decode fails.
                }
            }
        }

        internal static bool IsSystemImage(ImageEntry image)
        {
            if (image == null)
            {
                return false;
            }

            if (image.Resolution != null && !string.IsNullOrWhiteSpace(image.Resolution.BmpNameRaw))
            {
                return string.Equals(image.Resolution.BmpNameRaw, "system.bmp", StringComparison.OrdinalIgnoreCase);
            }

            if (image.Bitmap != null && string.Equals(image.Bitmap.FileName, "system.bmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return image.Resolution != null
                && string.Equals(image.Resolution.BmpNameRaw, "system.bmp", StringComparison.OrdinalIgnoreCase);
        }

        public static ImportResult ApplyImport(SgContainer container, string workspaceFolder)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            if (!Directory.Exists(workspaceFolder))
            {
                throw new DirectoryNotFoundException("Workspace folder was not found: " + workspaceFolder);
            }

            var importedFilesById = Directory.GetFiles(workspaceFolder, "*.png", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Path = path,
                    FileName = Path.GetFileNameWithoutExtension(path)
                })
                .Where(x => x.FileName.Length == 4 && x.FileName.All(char.IsDigit))
                .GroupBy(x => int.Parse(x.FileName))
                .ToDictionary(g => g.Key, g => g.First().Path);

            var result = new ImportResult();
            foreach (var entry in container.Images.OrderBy(x => x.DisplayId))
            {
                if (entry.Record.IsMirror)
                {
                    continue;
                }

                string imagePath;
                if (!importedFilesById.TryGetValue(GetSgImageId(entry), out imagePath) || !File.Exists(imagePath))
                {
                    continue;
                }

                using (var source = new Bitmap(imagePath))
                {
                    if (source.Width != entry.Record.Width || source.Height != entry.Record.Height)
                    {
                        result.SizeMismatchCount++;
                        result.Messages.Add(string.Format(
                            "{0}: {1} has size {2}x{3}, expected {4}x{5}.",
                            Path.GetFileName(container.SourcePath),
                            Path.GetFileName(imagePath),
                            source.Width,
                            source.Height,
                            entry.Record.Width,
                            entry.Record.Height));
                        continue;
                    }

                    using (var original = ImagingCodec.DecodeImage(container, entry))
                    {
                        if (BitmapsEqual(source, original))
                        {
                            continue;
                        }
                    }

                    if (entry.ReplacementBitmap != null)
                    {
                        entry.ReplacementBitmap.Dispose();
                    }

                    entry.ReplacementBitmap = new Bitmap(source);
                    entry.IsModified = true;
                    entry.Container.HasPendingChanges = true;
                    if (entry.CachedPreview != null)
                    {
                        entry.CachedPreview.Dispose();
                        entry.CachedPreview = null;
                    }

                    result.ChangedCount++;
                }
            }

            return result;
        }

        private static int GetSgImageId(ImageEntry image)
        {
            return image == null || image.Record == null ? 0 : image.Record.Index + 1;
        }

        private static bool BitmapsEqual(Bitmap left, Bitmap right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (left.Width != right.Width || left.Height != right.Height)
            {
                return false;
            }

            for (var y = 0; y < left.Height; y++)
            {
                for (var x = 0; x < left.Width; x++)
                {
                    if (left.GetPixel(x, y) != right.GetPixel(x, y))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
