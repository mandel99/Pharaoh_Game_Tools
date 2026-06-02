using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace PharaohGameTools.Core
{
    internal sealed class SgHeader
    {
        public uint SgFileSize { get; set; }
        public uint Version { get; set; }
        public uint Unknown1 { get; set; }
        public int MaxImageRecords { get; set; }
        public int NumImageRecords { get; set; }
        public int NumBitmapRecords { get; set; }
        public int NumBitmapRecordsWithoutSystem { get; set; }
        public uint TotalFileSize { get; set; }
        public uint FileSize555 { get; set; }
        public uint FileSizeExternal { get; set; }
        public ushort[] RawSubblockStarts { get; set; }
        public List<int> SubblockStarts { get; } = new List<int>();
    }

    internal sealed class BitmapRecord
    {
        public int Id { get; set; }
        public string FileName { get; set; }
        public string Comment { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint NumImages { get; set; }
        public uint StartIndex { get; set; }
        public uint EndIndex { get; set; }
        public string FolderName { get; set; }
        public string Source555Name { get; set; }
        public bool IsExternal { get; set; }
        public bool UseDefaultSource555 { get; set; }
    }

    internal sealed class ImageRecord
    {
        public int Index { get; set; }
        public uint Offset { get; set; }
        public uint Length { get; set; }
        public uint UncompressedLength { get; set; }
        public int InvertOffset { get; set; }
        public short Width { get; set; }
        public short Height { get; set; }
        public ushort GroupId { get; set; }
        public ushort GroupIndex { get; set; }
        public ushort NumSprites { get; set; }
        public short SpriteOffsetX { get; set; }
        public short SpriteOffsetY { get; set; }
        public bool CanReverse { get; set; }
        public byte SpeedId { get; set; }
        public bool IsFullyCompressed { get; set; }
        public bool IsExternal { get; set; }
        public bool HasIsometricTop { get; set; }
        public ushort Type { get; set; }
        public byte[] Flags { get; set; }
        public byte BitmapId { get; set; }
        public uint AlphaOffset { get; set; }
        public uint AlphaLength { get; set; }
        public byte BmpGroupId => BitmapId;

        public bool IsMirror => InvertOffset < 0;
        public bool HasData => Length > 0;
        public int? MirrorOfIndex
        {
            get
            {
                if (!IsMirror)
                {
                    return null;
                }

                var value = Index + InvertOffset;
                return value >= 0 ? (int?)value : null;
            }
        }
    }

    internal sealed class StructuralSubgroup
    {
        public int SlotIndex { get; set; }
        public int StartImage { get; set; }
        public int EndImage { get; set; }
        public int PhysicalOrder { get; set; }
        public string ExplicitTailName { get; set; }
    }

    internal sealed class ImageNameResolution
    {
        public int ImageId { get; set; }
        public int StructuralGroupId { get; set; }
        public int StructuralGroupIndex { get; set; }
        public int StructuralSubgroupSlot { get; set; }
        public int StructuralSubgroupStart { get; set; }
        public int StructuralSubgroupEnd { get; set; }
        public int BmpGroupId { get; set; }
        public string BmpNameRaw { get; set; }
        public string FinalGroupName { get; set; }
        public string FinalSubgroupName { get; set; }
        public string NameSource { get; set; }
    }

    internal sealed class ImageEntry
    {
        public ImageEntry(SgContainer container, ImageRecord record, BitmapRecord bitmap)
        {
            Container = container;
            Record = record;
            Bitmap = bitmap;
            DisplayId = record.Index;
        }

        public SgContainer Container { get; }
        public ImageRecord Record { get; }
        public BitmapRecord Bitmap { get; }
        public int DisplayId { get; }
        public ImageNameResolution Resolution { get; set; }
        public string Name
        {
            get
            {
                if (Resolution != null
                    && !string.IsNullOrWhiteSpace(Resolution.BmpNameRaw)
                    && (Resolution.BmpGroupId != 0 || string.Equals(Resolution.BmpNameRaw, "system.bmp", StringComparison.OrdinalIgnoreCase)))
                {
                    return Resolution.BmpNameRaw;
                }

                if (!string.IsNullOrWhiteSpace(Source555Name))
                {
                    return Path.GetFileNameWithoutExtension(Source555Name);
                }

                return Container != null && !string.IsNullOrWhiteSpace(Container.SourcePath)
                    ? Path.GetFileNameWithoutExtension(Container.SourcePath)
                    : GroupName;
            }
        }
        public string GroupName
        {
            get
            {
                if (Resolution != null && !string.IsNullOrWhiteSpace(Resolution.FinalGroupName))
                {
                    return Resolution.FinalGroupName;
                }

                return Bitmap?.FileName ?? "Unknown";
            }
        }
        public string SubgroupName
        {
            get
            {
                var subgroupName = Resolution?.FinalSubgroupName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(subgroupName))
                {
                    return string.Empty;
                }

                var groupName = GroupName;
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    if (string.Equals(subgroupName, groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }

                    var prefix = groupName + "__";
                    if (subgroupName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        subgroupName = subgroupName.Substring(prefix.Length);
                    }
                }

                var separatorIndex = subgroupName.IndexOf("__", StringComparison.Ordinal);
                if (separatorIndex >= 0)
                {
                    subgroupName = subgroupName.Substring(0, separatorIndex);
                }

                return subgroupName;
            }
        }
        public string NameSource => Resolution?.NameSource ?? string.Empty;
        public string Source555Name => Bitmap?.Source555Name ?? string.Empty;
        public bool IsModified { get; set; }
        public Bitmap ReplacementBitmap { get; set; }
        public Bitmap CachedPreview { get; set; }

        public override string ToString()
        {
            return string.Format("{0:D4} - {1}", DisplayId, GroupName);
        }
    }

    internal sealed class SgContainer
    {
        public string DisplayName { get; set; }
        public string SourcePath { get; set; }
        public string SourceDirectory { get; set; }
        public bool IsLoose555 { get; set; }
        public bool HasPendingChanges { get; set; }
        public List<BitmapRecord> Bitmaps { get; } = new List<BitmapRecord>();
        public List<ImageRecord> Records { get; } = new List<ImageRecord>();
        public List<ImageEntry> Images { get; } = new List<ImageEntry>();
        public List<StructuralSubgroup> StructuralSubgroups { get; } = new List<StructuralSubgroup>();
        public Dictionary<int, BitmapRecord> BitmapByRecordIndex { get; } = new Dictionary<int, BitmapRecord>();
        public Dictionary<int, BitmapRecord> SyntheticBitmaps { get; } = new Dictionary<int, BitmapRecord>();
        public Dictionary<int, string> TrailingGroupNames { get; } = new Dictionary<int, string>();
        public Dictionary<int, string> SourcePathByBitmapId { get; } = new Dictionary<int, string>();
        public Dictionary<string, byte[]> SourceBytes { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Available555Files { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Sg3Bytes { get; set; }
        public SgHeader Header { get; set; }
        public int LooseWidth { get; set; }
        public int LooseHeight { get; set; }
        public byte[] Loose555Bytes { get; set; }
    }

    internal sealed class ArchiveItem
    {
        public string Path { get; set; }
        public string SourceDirectory { get; set; }
        public bool IsLoose555 { get; set; }
        public bool IsLoaded { get; set; }
        public bool WasSaved { get; set; }
        public string LoadError { get; set; }
        public SgContainer Container { get; set; }
        public Task<SgContainer> LoadingTask { get; set; }

        public string DisplayName => System.IO.Path.GetFileName(Path);
    }

    internal sealed class SaveResult
    {
        public List<string> WrittenFiles { get; } = new List<string>();
    }

    internal sealed class ScanResult
    {
        public List<SgContainer> Containers { get; } = new List<SgContainer>();
        public List<string> Errors { get; } = new List<string>();
    }

    internal static class SgConstants
    {
        public const int HeaderSize = 680;
        public const int BitmapRecordSize = 200;
        public const int MaxBitmapRecords = 200;
        public const int TrailingGroupNameCount = 300;
        public const int TrailingGroupNameSize = 48;
        public const ushort TransparentColor = 0xF81F;
        public static readonly HashSet<ushort> PlainTypes = new HashSet<ushort> { 0, 1, 10, 12, 13 };
        public static readonly HashSet<ushort> SpriteTypes = new HashSet<ushort> { 256, 257, 276 };
        public static readonly HashSet<ushort> IsometricTypes = new HashSet<ushort> { 30 };
    }
}
