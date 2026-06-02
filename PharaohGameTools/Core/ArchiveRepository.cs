using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal static class ArchiveRepository
    {
        public static List<ArchiveItem> ScanFiles(IEnumerable<string> paths)
        {
            var result = new List<ArchiveItem>();
            foreach (var path in paths)
            {
                if (!path.EndsWith(".sg3", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new ArchiveItem
                {
                    Path = path,
                    SourceDirectory = Path.GetDirectoryName(path),
                    IsLoose555 = false
                });
            }

            return result.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<ArchiveItem> ScanFolder(string folder)
        {
            var items = new List<ArchiveItem>();
            foreach (var sg3 in Directory.GetFiles(folder, "*.sg3").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ArchiveItem
                {
                    Path = sg3,
                    SourceDirectory = folder,
                    IsLoose555 = false
                });
            }

            return items;
        }

        public static SgContainer LoadArchive(ArchiveItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (item.IsLoose555)
            {
                return SgArchive.LoadLoose555(item.Path);
            }

            return SgArchive.LoadFromSg3(item.Path, item.SourceDirectory);
        }
    }
}
