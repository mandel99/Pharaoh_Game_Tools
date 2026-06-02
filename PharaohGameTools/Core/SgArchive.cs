using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal static class SgArchive
    {
        public static SgContainer LoadFromSg3(string sg3Path, string sourceDirectory)
        {
            var container = new SgContainer
            {
                DisplayName = Path.GetFileName(sg3Path),
                SourcePath = sg3Path,
                SourceDirectory = sourceDirectory,
                Sg3Bytes = File.ReadAllBytes(sg3Path)
            };

            container.Header = ReadHeader(container.Sg3Bytes);
            ReadBitmapRecords(container);
            ReadImageRecords(container);
            BuildStructuralSubgroups(container);
            ReadTrailingGroupNames(container);
            Index555Files(container, sourceDirectory);
            BuildImageEntries(container);
            return container;
        }

        public static SgContainer LoadLoose555(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            var size = GuessRaw555Dimensions(bytes.Length);
            var container = new SgContainer
            {
                DisplayName = Path.GetFileName(filePath),
                SourcePath = filePath,
                SourceDirectory = Path.GetDirectoryName(filePath),
                IsLoose555 = true,
                Loose555Bytes = bytes,
                LooseWidth = size.Item1,
                LooseHeight = size.Item2
            };

            var bitmap = new BitmapRecord
            {
                Id = 0,
                FileName = Path.GetFileNameWithoutExtension(filePath),
                FolderName = BinaryHelpers.SanitizeFolderName(Path.GetFileNameWithoutExtension(filePath)),
                Source555Name = Path.GetFileName(filePath),
                Width = (uint)size.Item1,
                Height = (uint)size.Item2,
                IsExternal = false,
            };
            container.Bitmaps.Add(bitmap);
            var record = new ImageRecord
            {
                Index = 0,
                Offset = 0,
                Length = (uint)bytes.Length,
                UncompressedLength = (uint)bytes.Length,
                Width = (short)size.Item1,
                Height = (short)size.Item2,
                Type = 1,
                Flags = new byte[4],
                BitmapId = 0
            };
            container.Records.Add(record);
            container.BitmapByRecordIndex[0] = bitmap;
            container.SourcePathByBitmapId[0] = filePath;
            container.SourceBytes[filePath] = bytes;
            container.Images.Add(new ImageEntry(container, record, bitmap));
            return container;
        }

        internal static byte[] GetSourceBytes(SgContainer container, string sourcePath)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            if (string.IsNullOrEmpty(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            byte[] bytes;
            if (!container.SourceBytes.TryGetValue(sourcePath, out bytes) || bytes == null)
            {
                bytes = File.ReadAllBytes(sourcePath);
                container.SourceBytes[sourcePath] = bytes;
            }

            return bytes;
        }

        internal static string GetResolvedSourcePath(SgContainer container, int recordIndex)
        {
            return ResolveSourcePath(container, recordIndex);
        }

        public static SaveResult SaveContainer(SgContainer container, string outputDirectory)
        {
            if (container.IsLoose555)
            {
                return SaveLoose555(container, outputDirectory);
            }

            return SaveSg3(container, outputDirectory);
        }

        private static SaveResult SaveLoose555(SgContainer container, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var result = new SaveResult();
            var entry = container.Images[0];
            byte[] bytes;
            if (entry.ReplacementBitmap == null)
            {
                bytes = container.Loose555Bytes;
            }
            else
            {
                var highBitMode = ImagingCodec.DetectHighBitMode(container.Loose555Bytes);
                bytes = ImagingCodec.EncodeRaw555(entry.ReplacementBitmap, highBitMode, false);
            }

            var outPath = Path.Combine(outputDirectory, Path.GetFileName(container.SourcePath));
            File.WriteAllBytes(outPath, bytes);
            result.WrittenFiles.Add(outPath);
            return result;
        }

        private static SaveResult SaveSg3(SgContainer container, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var result = new SaveResult();
            var updatedRecords = container.Records.Select(CloneRecord).ToList();
            var rebuiltSources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var entriesBySourcePath = BuildEntriesBySourcePath(container);

            foreach (var sourcePair in container.SourceBytes)
            {
                var sourcePath = sourcePair.Key;
                var original = GetSourceBytes(container, sourcePath);
                List<ImageEntry> records;
                if (!entriesBySourcePath.TryGetValue(sourcePath, out records) || records.Count == 0)
                {
                    rebuiltSources[sourcePath] = original;
                    continue;
                }

                var rebuilt = new List<byte>(original.Length);
                var firstStart = BinaryHelpers.DataStart(records[0].Record);
                AppendSlice(rebuilt, original, 0, firstStart);

                for (var i = 0; i < records.Count; i++)
                {
                    var entry = records[i];
                    var record = entry.Record;
                    byte[] blob;
                    uint newColorLength = record.Length;
                    uint newAlphaLength = record.AlphaLength;
                    if (entry.ReplacementBitmap == null)
                    {
                        blob = GetOriginalBlobWithAlpha(container, record);
                    }
                    else
                    {
                        blob = BuildReplacementBlob(container, entry, out newColorLength, out newAlphaLength);
                    }

                    updatedRecords[record.Index].Offset = (uint)(rebuilt.Count + (record.Flags != null && record.Flags.Length > 0 && record.Flags[0] != 0 ? 1 : 0));
                    updatedRecords[record.Index].Length = newColorLength;
                    updatedRecords[record.Index].AlphaLength = newAlphaLength;
                    updatedRecords[record.Index].AlphaOffset = newAlphaLength > 0 ? updatedRecords[record.Index].Offset + newColorLength : 0;
                    rebuilt.AddRange(blob);

                    if (i < records.Count - 1)
                    {
                        var currentEnd = BinaryHelpers.DataStart(record) + checked((int)record.Length + (int)record.AlphaLength);
                        var nextStart = BinaryHelpers.DataStart(records[i + 1].Record);
                        if (nextStart > currentEnd)
                        {
                            AppendSlice(rebuilt, original, currentEnd, nextStart - currentEnd);
                        }
                    }
                }

                var last = records[records.Count - 1].Record;
                var tailStart = BinaryHelpers.DataStart(last) + checked((int)last.Length + (int)last.AlphaLength);
                if (tailStart < original.Length)
                {
                    AppendSlice(rebuilt, original, tailStart, original.Length - tailStart);
                }

                rebuiltSources[sourcePath] = rebuilt.ToArray();
            }

            var updatedSg3 = (byte[])container.Sg3Bytes.Clone();
            var recordTableOffset = SgConstants.HeaderSize + (SgConstants.MaxBitmapRecords * SgConstants.BitmapRecordSize);
            var hasAlpha = container.Header.Version >= 0xD6;
            var recordSize = hasAlpha ? 72 : 64;
            for (var i = 0; i < updatedRecords.Count; i++)
            {
                var rec = updatedRecords[i];
                var pos = recordTableOffset + (i * recordSize);
                WriteUInt32(updatedSg3, pos + 0, rec.Offset);
                WriteUInt32(updatedSg3, pos + 4, rec.Length);
                WriteUInt32(updatedSg3, pos + 8, rec.UncompressedLength);
                WriteInt32(updatedSg3, pos + 16, rec.InvertOffset);
                WriteInt16(updatedSg3, pos + 20, rec.Width);
                WriteInt16(updatedSg3, pos + 22, rec.Height);
                WriteUInt16(updatedSg3, pos + 50, rec.Type);
                Buffer.BlockCopy(rec.Flags ?? new byte[4], 0, updatedSg3, pos + 52, 4);
                updatedSg3[pos + 56] = rec.BitmapId;
                if (hasAlpha)
                {
                    WriteUInt32(updatedSg3, pos + 64, rec.AlphaOffset);
                    WriteUInt32(updatedSg3, pos + 68, rec.AlphaLength);
                }
            }

            var sg3Out = Path.Combine(outputDirectory, Path.GetFileName(container.SourcePath));
            File.WriteAllBytes(sg3Out, updatedSg3);
            result.WrittenFiles.Add(sg3Out);

            foreach (var sourcePair in rebuiltSources)
            {
                var outPath = Path.Combine(outputDirectory, Path.GetFileName(sourcePair.Key));
                File.WriteAllBytes(outPath, sourcePair.Value);
                result.WrittenFiles.Add(outPath);
            }

            container.HasPendingChanges = false;
            return result;
        }

        private static Dictionary<string, List<ImageEntry>> BuildEntriesBySourcePath(SgContainer container)
        {
            var entriesBySourcePath = new Dictionary<string, List<ImageEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in container.Images)
            {
                if (entry == null || entry.Record == null || entry.Record.IsMirror || !entry.Record.HasData)
                {
                    continue;
                }

                var sourcePath = ResolveSourcePath(container, entry.Record.Index);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    continue;
                }

                List<ImageEntry> entries;
                if (!entriesBySourcePath.TryGetValue(sourcePath, out entries))
                {
                    entries = new List<ImageEntry>();
                    entriesBySourcePath[sourcePath] = entries;
                }

                entries.Add(entry);
            }

            foreach (var pair in entriesBySourcePath)
            {
                pair.Value.Sort((left, right) => BinaryHelpers.DataStart(left.Record).CompareTo(BinaryHelpers.DataStart(right.Record)));
            }

            return entriesBySourcePath;
        }

        private static void AppendSlice(List<byte> target, byte[] source, int offset, int length)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (source == null || length <= 0)
            {
                return;
            }

            var slice = BinaryHelpers.Slice(source, offset, length);
            target.AddRange(slice);
        }

        private static byte[] BuildReplacementBlob(SgContainer container, ImageEntry entry, out uint colorLength, out uint alphaLength)
        {
            var colorBlob = ImagingCodec.EncodeImageForRecord(container, entry, entry.ReplacementBitmap);
            colorLength = (uint)colorBlob.Length;
            if (entry.Record.AlphaLength == 0)
            {
                alphaLength = 0;
                return colorBlob;
            }

            using (var original = ImagingCodec.DecodeOriginalImage(container, entry.Record.Index))
            {
                var alphaBlob = ImagingCodec.EncodeAlphaStream(original, entry.ReplacementBitmap);
                alphaLength = (uint)alphaBlob.Length;
                var blob = new byte[colorBlob.Length + alphaBlob.Length];
                Buffer.BlockCopy(colorBlob, 0, blob, 0, colorBlob.Length);
                Buffer.BlockCopy(alphaBlob, 0, blob, colorBlob.Length, alphaBlob.Length);
                return blob;
            }
        }

        private static string ResolveSourcePath(SgContainer container, int recordIndex)
        {
            BitmapRecord bitmap;
            if (container.BitmapByRecordIndex.TryGetValue(recordIndex, out bitmap) && bitmap != null)
            {
                string path;
                if (container.SourcePathByBitmapId.TryGetValue(bitmap.Id, out path))
                {
                    return path;
                }

                path = ResolveSourcePath(container, bitmap, container.Records[recordIndex]);
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }

            var record = container.Records[recordIndex];
            string fallback;
            if (container.SourcePathByBitmapId.TryGetValue(record.BitmapId, out fallback))
            {
                return fallback;
            }

            bitmap = container.Bitmaps.FirstOrDefault(x => x.Id == record.BitmapId);
            return bitmap == null ? null : ResolveSourcePath(container, bitmap, record);
        }

        private static byte[] GetOriginalBlobWithAlpha(SgContainer container, ImageRecord record)
        {
            var sourcePath = ResolveSourcePath(container, record.Index);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return Array.Empty<byte>();
            }

            var sourceBytes = GetSourceBytes(container, sourcePath);
            var start = BinaryHelpers.DataStart(record);
            var length = checked((int)record.Length + (int)record.AlphaLength);
            return BinaryHelpers.Slice(sourceBytes, start, length);
        }

        private static void BuildImageEntries(SgContainer container)
        {
            foreach (var record in container.Records)
            {
                if (BinaryHelpers.IsDummyRecord(record))
                {
                    continue;
                }

                var bitmap = GetBitmapForRecord(container, record);
                container.BitmapByRecordIndex[record.Index] = bitmap;

                var sourcePath = ResolveSourcePath(container, bitmap, record);
                if (!string.IsNullOrEmpty(sourcePath))
                {
                    bitmap.Source555Name = Path.GetFileName(sourcePath);
                    container.SourcePathByBitmapId[bitmap.Id] = sourcePath;
                }

                var entry = new ImageEntry(container, record, bitmap)
                {
                    Resolution = ResolveImageName(container, record, bitmap)
                };
                container.Images.Add(entry);
            }
        }

        private static BitmapRecord GetBitmapForRecord(SgContainer container, ImageRecord record)
        {
            if (container == null || container.Bitmaps.Count == 0)
            {
                return CreateDefaultBitmap(record);
            }

            var subgroupBitmap = TryGetFixedEnemyBitmap(container, record);
            if (subgroupBitmap != null)
            {
                return subgroupBitmap;
            }

            if (record != null)
            {
                var explicitBitmap = container.Bitmaps.FirstOrDefault(x =>
                    (x.StartIndex > 0 && x.EndIndex >= x.StartIndex && record.Index >= x.StartIndex && record.Index <= x.EndIndex) ||
                    (x.StartIndex > 0 && x.NumImages > 0 && record.Index >= x.StartIndex && record.Index < x.StartIndex + x.NumImages));
                if (explicitBitmap != null)
                {
                    return explicitBitmap;
                }
            }

            var headerMappedBitmap = TryGetHeaderMappedBitmap(container, record);
            if (headerMappedBitmap != null)
            {
                return headerMappedBitmap;
            }

            if (record != null && record.BitmapId < container.Bitmaps.Count)
            {
                return container.Bitmaps[record.BitmapId];
            }

            return CreateDefaultBitmap(record);
        }

        private static ImageNameResolution ResolveImageName(SgContainer container, ImageRecord record, BitmapRecord bitmap)
        {
            var subgroup = FindStructuralSubgroup(container, record);
            var bmpNameRaw = GetBmpNameRaw(container, record);
            var tailName = subgroup == null ? null : subgroup.ExplicitTailName;
            var defaultGroupName = subgroup == null ? "group_0" : "group_" + subgroup.PhysicalOrder;
            var defaultSubgroupName = subgroup == null
                ? "group_0__seq00"
                : string.Format("slot_{0:D3}__seq{1:D2}", subgroup.SlotIndex, subgroup.PhysicalOrder);
            var resolution = new ImageNameResolution
            {
                ImageId = record.Index,
                StructuralGroupId = subgroup?.PhysicalOrder ?? 0,
                StructuralGroupIndex = subgroup == null ? 0 : Math.Max(0, record.Index - subgroup.StartImage),
                StructuralSubgroupSlot = subgroup?.SlotIndex ?? 0,
                StructuralSubgroupStart = subgroup?.StartImage ?? 0,
                StructuralSubgroupEnd = subgroup?.EndImage ?? 0,
                BmpGroupId = record.BmpGroupId,
                BmpNameRaw = bmpNameRaw ?? string.Empty,
                FinalGroupName = defaultGroupName,
                FinalSubgroupName = defaultSubgroupName,
                NameSource = "structural_fallback"
            };

            if (IsTerrainSystemBaseBlock(container, record, bitmap))
            {
                resolution.FinalGroupName = string.IsNullOrWhiteSpace(bmpNameRaw) ? "system.bmp" : bmpNameRaw;
                resolution.FinalSubgroupName = resolution.FinalGroupName;
                resolution.NameSource = "explicit_bmp_group_id";
                return resolution;
            }

            if (!string.IsNullOrWhiteSpace(tailName))
            {
                resolution.FinalGroupName = tailName;
                resolution.FinalSubgroupName = tailName;
                resolution.NameSource = "pyramid_tail_table";
                return resolution;
            }

            if (TryApplyStringGroupProfile(container, resolution, subgroup, record))
            {
                return resolution;
            }

            if (TryApplySprAmbientProfile(container, resolution, subgroup, record))
            {
                return resolution;
            }

            if (TryApplyEnemyMacroRule(container, resolution, subgroup))
            {
                return resolution;
            }

            if (bitmap != null
                && !string.IsNullOrWhiteSpace(bitmap.FileName)
                && !bitmap.FileName.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
            {
                resolution.FinalGroupName = bitmap.FileName;
                resolution.NameSource = "explicit_bmp_group_id";
                return resolution;
            }

            if (!string.IsNullOrWhiteSpace(bmpNameRaw))
            {
                resolution.NameSource = "explicit_bmp_group_id";
                return resolution;
            }

            if (bitmap != null && !bitmap.UseDefaultSource555 && !string.IsNullOrWhiteSpace(bitmap.FileName))
            {
                resolution.BmpNameRaw = bitmap.FileName;
                resolution.NameSource = "heuristic_entity_group";
            }

            return resolution;
        }

        private static bool TryApplySprAmbientProfile(SgContainer container, ImageNameResolution resolution, StructuralSubgroup subgroup, ImageRecord record)
        {
            if (container == null || resolution == null || subgroup == null || record == null)
            {
                return false;
            }

            var entry = SprAmbientProfile.FindEntry(container, record, subgroup);
            if (entry == null)
            {
                return false;
            }

            resolution.FinalGroupName = entry.GroupName;
            resolution.FinalSubgroupName = SprAmbientProfile.GetSubgroupName(entry, record);
            resolution.NameSource = "heuristic_entity_group";
            return true;
        }

        private static bool TryApplyStringGroupProfile(SgContainer container, ImageNameResolution resolution, StructuralSubgroup subgroup, ImageRecord record)
        {
            if (container == null || resolution == null || subgroup == null || record == null)
            {
                return false;
            }

            if (!StringGroupProfile.TryResolve(container, record, subgroup, out var entry))
            {
                return false;
            }

            resolution.FinalGroupName = entry.GroupName;
            resolution.FinalSubgroupName = StringGroupProfile.GetSubgroupName(entry, subgroup, record);
            resolution.NameSource = "heuristic_entity_group";
            return true;
        }

        private static bool IsTerrainSystemBaseBlock(SgContainer container, ImageRecord record, BitmapRecord bitmap)
        {
            if (container == null || record == null || bitmap == null)
            {
                return false;
            }

            if (!string.Equals(bitmap.FileName, "system.bmp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (record.Index > 200)
            {
                return false;
            }

            return container.TrailingGroupNames.Count > 8
                && container.TrailingGroupNames.Values.Any(x => x.StartsWith("SPR_", StringComparison.OrdinalIgnoreCase));
        }

        private static BitmapRecord TryGetHeaderMappedBitmap(SgContainer container, ImageRecord record)
        {
            if (container == null || record == null || record.BitmapId != 0)
            {
                return null;
            }

            if (container.Bitmaps.Any(x => x.StartIndex > 0 || x.EndIndex > 0 || x.NumImages > 0))
            {
                return null;
            }

            var groups = container.StructuralSubgroups;
            if (groups.Count <= 1)
            {
                return null;
            }

            StructuralSubgroup matchedGroup = null;
            foreach (var group in groups)
            {
                if (record.Index < group.StartImage)
                {
                    break;
                }

                matchedGroup = group;
            }

            if (matchedGroup == null)
            {
                matchedGroup = groups[0];
            }

            BitmapRecord bitmap;
            if (container.SyntheticBitmaps.TryGetValue(matchedGroup.PhysicalOrder, out bitmap))
            {
                return bitmap;
            }

            var groupName = "group_" + matchedGroup.PhysicalOrder;
            bitmap = new BitmapRecord
            {
                Id = 1000 + matchedGroup.PhysicalOrder,
                FileName = groupName,
                FolderName = groupName,
                Source555Name = string.Empty,
                UseDefaultSource555 = true
            };
            container.SyntheticBitmaps[matchedGroup.PhysicalOrder] = bitmap;
            return bitmap;
        }

        private static StructuralSubgroup FindStructuralSubgroup(SgContainer container, ImageRecord record)
        {
            if (container == null || record == null || container.StructuralSubgroups.Count == 0)
            {
                return null;
            }

            StructuralSubgroup matched = null;
            foreach (var subgroup in container.StructuralSubgroups)
            {
                if (record.Index < subgroup.StartImage)
                {
                    break;
                }

                matched = subgroup;
            }

            return matched;
        }

        private static string GetBmpNameRaw(SgContainer container, ImageRecord record)
        {
            if (container == null || record == null || record.BmpGroupId >= container.Bitmaps.Count)
            {
                return string.Empty;
            }

            var bitmap = container.Bitmaps[record.BmpGroupId];
            return bitmap == null ? string.Empty : bitmap.FileName;
        }

        private static bool TryApplyEnemyMacroRule(SgContainer container, ImageNameResolution resolution, StructuralSubgroup subgroup)
        {
            if (container == null || resolution == null || subgroup == null)
            {
                return false;
            }

            if (!TryResolveEnemyMacroName(container, subgroup, out var macroName))
            {
                return false;
            }

            resolution.FinalGroupName = macroName;
            resolution.FinalSubgroupName = GetEnemySubgroupName(
                macroName,
                subgroup.PhysicalOrder,
                resolution.StructuralGroupIndex,
                container.StructuralSubgroups.Count,
                subgroup.EndImage - subgroup.StartImage + 1);
            resolution.NameSource = "heuristic_macro_group";
            return true;
        }

        private static bool TryResolveEnemyMacroName(SgContainer container, StructuralSubgroup subgroup, out string macroName)
        {
            macroName = null;
            if (container == null || subgroup == null)
            {
                return false;
            }

            var enemyRoles = TryGetEnemyRoleNames(container);
            var subgroupCount = container.StructuralSubgroups.Count;
            if (enemyRoles == null || (subgroupCount != 17 && subgroupCount != 10))
            {
                return false;
            }

            if (subgroupCount == 10)
            {
                macroName = GetCompactEnemyMacroName(container, subgroup, enemyRoles)
                    ?? GetClosestEnemyMacroName(container, subgroup, enemyRoles)
                    ?? enemyRoles.Missile;
                return !string.IsNullOrWhiteSpace(macroName);
            }

            if (subgroup.PhysicalOrder <= 4)
            {
                macroName = enemyRoles.Missile;
            }
            else if (subgroup.PhysicalOrder <= 7)
            {
                macroName = enemyRoles.Aux;
            }
            else if (subgroup.PhysicalOrder <= 10)
            {
                macroName = enemyRoles.Transport;
            }
            else if (subgroup.PhysicalOrder <= 13)
            {
                macroName = enemyRoles.Warship;
            }
            else
            {
                macroName = enemyRoles.Chariot;
            }

            return !string.IsNullOrWhiteSpace(macroName);
        }

        private static string GetCompactEnemyMacroName(SgContainer container, StructuralSubgroup subgroup, EnemyRoleNames enemyRoles)
        {
            if (container == null || subgroup == null || enemyRoles == null || container.StructuralSubgroups.Count != 10)
            {
                return null;
            }

            var lengths = container.StructuralSubgroups
                .OrderBy(x => x.PhysicalOrder)
                .Select(x => x.EndImage - x.StartImage + 1)
                .ToArray();
            if (lengths.Length != 10)
            {
                return null;
            }

            var hasEarlyDeathMarkers = lengths[1] <= 12 && lengths[4] <= 12;
            if (!hasEarlyDeathMarkers)
            {
                return null;
            }

            if (subgroup.PhysicalOrder <= 3)
            {
                return enemyRoles.Missile;
            }

            if (subgroup.PhysicalOrder <= 6)
            {
                return enemyRoles.Aux;
            }

            if (subgroup.PhysicalOrder == 7)
            {
                return enemyRoles.Transport;
            }

            return enemyRoles.Warship;
        }

        private sealed class EnemyRoleNames
        {
            public string Missile { get; set; }
            public string Aux { get; set; }
            public string Transport { get; set; }
            public string Warship { get; set; }
            public string Chariot { get; set; }
        }

        private static EnemyRoleNames TryGetEnemyRoleNames(SgContainer container)
        {
            if (container?.Bitmaps == null || container.Bitmaps.Count < 4)
            {
                return null;
            }

            string missile = null;
            string aux = null;
            string transport = null;
            string warship = null;
            string chariot = null;

            foreach (var bitmap in container.Bitmaps)
            {
                var name = bitmap?.FileName ?? string.Empty;
                var upper = name.ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (missile == null && (upper.Contains("MISSILE") || upper.Contains("_MISS") || upper.EndsWith("MISS", StringComparison.Ordinal)))
                {
                    missile = name;
                    continue;
                }

                if (aux == null && upper.Contains("AUX"))
                {
                    aux = name;
                    continue;
                }

                if (transport == null && upper.Contains("TRANSPORT"))
                {
                    transport = name;
                    continue;
                }

                if (warship == null && upper.Contains("WARSHIP"))
                {
                    warship = name;
                    continue;
                }

                if (chariot == null && upper.Contains("CHARIOT"))
                {
                    chariot = name;
                }
            }

            if (missile == null || aux == null || transport == null || warship == null)
            {
                return null;
            }

            return new EnemyRoleNames
            {
                Missile = missile,
                Aux = aux,
                Transport = transport,
                Warship = warship,
                Chariot = chariot ?? warship
            };
        }

        private static string GetEnemySubgroupName(string macroName, int physicalOrder, int structuralGroupIndex, int subgroupCount, int subgroupLength)
        {
            var directions = new[] { "NE", "E", "SE", "S", "SW", "W", "NW", "N" };
            var direction = directions[Math.Abs(structuralGroupIndex) % directions.Length];

            if (subgroupCount == 10)
            {
                if (subgroupLength == 8)
                {
                    return "Die";
                }

                if (subgroupLength == 88)
                {
                    return "Die_" + direction;
                }

                if (subgroupLength == 128
                    && (string.Equals(macroName, "ENEMY_TRANSPORT", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(macroName, "ENEMY_WARSHIP", StringComparison.OrdinalIgnoreCase)
                        || macroName.IndexOf("TRANSPORT", StringComparison.OrdinalIgnoreCase) >= 0
                        || macroName.IndexOf("WARSHIP", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var dir32 = (Math.Abs(structuralGroupIndex) % 32) + 1;
                    return "Dir" + dir32.ToString("D2");
                }

                return direction;
            }

            switch (physicalOrder)
            {
                case 2:
                case 6:
                case 9:
                case 12:
                case 15:
                    return "Die";
                case 10:
                case 13:
                    return "Idle_" + direction;
                default:
                    return direction;
            }
        }

        private static string GetClosestEnemyMacroName(SgContainer container, StructuralSubgroup subgroup, EnemyRoleNames enemyRoles)
        {
            if (container == null || subgroup == null || enemyRoles == null)
            {
                return null;
            }

            var frames = container.Records
                .Where(x => x != null
                    && !BinaryHelpers.IsDummyRecord(x)
                    && x.Index >= subgroup.StartImage
                    && x.Index <= subgroup.EndImage
                    && x.Width > 0
                    && x.Height > 0)
                .ToList();
            if (frames.Count == 0)
            {
                return null;
            }

            var maxWidth = frames.Max(x => x.Width);
            var maxHeight = frames.Max(x => x.Height);

            var candidates = new[]
            {
                enemyRoles.Missile,
                enemyRoles.Aux,
                enemyRoles.Transport,
                enemyRoles.Warship
            };

            var bestName = (string)null;
            var bestDistance = int.MaxValue;
            foreach (var candidateName in candidates)
            {
                var bitmap = container.Bitmaps.FirstOrDefault(x => string.Equals(x.FileName, candidateName, StringComparison.OrdinalIgnoreCase));
                if (bitmap == null)
                {
                    continue;
                }

                var distance = Math.Abs((int)bitmap.Width - maxWidth) + Math.Abs((int)bitmap.Height - maxHeight);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestName = candidateName;
                }
            }

            return bestName;
        }

        private static BitmapRecord TryGetFixedEnemyBitmap(SgContainer container, ImageRecord record)
        {
            if (container == null || record == null)
            {
                return null;
            }

            var subgroup = FindStructuralSubgroup(container, record);
            if (subgroup == null || !TryResolveEnemyMacroName(container, subgroup, out var macroName))
            {
                return null;
            }

            return container.Bitmaps.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x?.FileName)
                && string.Equals(x.FileName, macroName, StringComparison.OrdinalIgnoreCase));
        }

        private static BitmapRecord CreateDefaultBitmap(ImageRecord record)
        {
            return new BitmapRecord
            {
                Id = record?.BitmapId ?? 0,
                FileName = "Default",
                FolderName = "Default",
                Source555Name = string.Empty,
                UseDefaultSource555 = true
            };
        }

        private static string ResolveSourcePath(SgContainer container, BitmapRecord bitmap, ImageRecord record)
        {
            if (container == null || bitmap == null)
            {
                return null;
            }

            string path;
            if (container.SourcePathByBitmapId.TryGetValue(bitmap.Id, out path) && !string.IsNullOrEmpty(path))
            {
                return path;
            }

            var default555Name = ChangeExtension(Path.GetFileName(container.SourcePath), ".555");
            var bitmap555Name = ChangeExtension(bitmap.FileName, ".555");
            var useBitmapName = !bitmap.UseDefaultSource555
                && !string.Equals(bitmap.FileName, "Default", StringComparison.OrdinalIgnoreCase)
                && record != null
                && record.Flags != null
                && record.Flags.Length > 0
                && record.Flags[0] != 0;
            var primaryName = useBitmapName ? bitmap555Name : default555Name;
            var secondaryName = string.Equals(primaryName, bitmap555Name, StringComparison.OrdinalIgnoreCase)
                ? default555Name
                : bitmap555Name;

            path = TryResolveIndexed555(container, primaryName) ?? TryResolveIndexed555(container, secondaryName);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            bitmap.Source555Name = Path.GetFileName(path);
            bitmap.IsExternal = !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                Path.GetFileNameWithoutExtension(container.SourcePath),
                StringComparison.OrdinalIgnoreCase);
            container.SourcePathByBitmapId[bitmap.Id] = path;
            if (!container.SourceBytes.ContainsKey(path))
            {
                container.SourceBytes[path] = null;
            }

            return path;
        }

        private static void Index555Files(SgContainer container, string sourceDirectory)
        {
            if (container == null || string.IsNullOrEmpty(sourceDirectory))
            {
                return;
            }

            foreach (var directory in new[] { sourceDirectory, Path.Combine(sourceDirectory, "555") })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(directory, "*.555"))
                {
                    var fileName = Path.GetFileName(file);
                    if (!container.Available555Files.ContainsKey(fileName))
                    {
                        container.Available555Files[fileName] = file;
                    }
                }
            }
        }

        private static string TryResolveIndexed555(SgContainer container, string fileName)
        {
            if (container == null || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string path;
            return container.Available555Files.TryGetValue(fileName, out path) ? path : null;
        }

        private static string ChangeExtension(string fileName, string extension)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            return Path.ChangeExtension(fileName, extension);
        }

        private static void BuildStructuralSubgroups(SgContainer container)
        {
            container.StructuralSubgroups.Clear();
            if (container == null || container.Header?.RawSubblockStarts == null)
            {
                return;
            }

            var subgroups = new List<StructuralSubgroup>();
            for (var slotIndex = 0; slotIndex < container.Header.RawSubblockStarts.Length; slotIndex++)
            {
                var start = container.Header.RawSubblockStarts[slotIndex];
                if (start <= 0)
                {
                    continue;
                }

                subgroups.Add(new StructuralSubgroup
                {
                    SlotIndex = slotIndex,
                    StartImage = start
                });
            }

            if (subgroups.Count == 0)
            {
                return;
            }

            subgroups = subgroups
                .OrderBy(x => x.StartImage)
                .ThenBy(x => x.SlotIndex)
                .ToList();

            var hasSystemBaseBlock = container.Bitmaps.Count > 0
                && string.Equals(container.Bitmaps[0].FileName, "system.bmp", StringComparison.OrdinalIgnoreCase)
                && subgroups[0].StartImage > 1;
            if (hasSystemBaseBlock)
            {
                subgroups.Insert(0, new StructuralSubgroup
                {
                    SlotIndex = 0,
                    StartImage = 1
                });
            }

            for (var i = 0; i < subgroups.Count; i++)
            {
                subgroups[i].PhysicalOrder = i + 1;
                subgroups[i].EndImage = i + 1 < subgroups.Count
                    ? subgroups[i + 1].StartImage - 1
                    : container.Records.Count - 1;
            }

            foreach (var subgroup in subgroups)
            {
                container.StructuralSubgroups.Add(subgroup);
            }
        }

        private static List<HeaderGroup> GetHeaderGroups(SgHeader header)
        {
            var groups = new List<HeaderGroup>();
            if (header?.RawSubblockStarts == null || header.RawSubblockStarts.Length == 0)
            {
                return groups;
            }

            var groupId = 1;
            for (var i = 0; i < header.RawSubblockStarts.Length; i++)
            {
                var start = header.RawSubblockStarts[i];
                if (start <= 0)
                {
                    continue;
                }

                groups.Add(new HeaderGroup
                {
                    Id = groupId++,
                    Start = start
                });
            }

            groups.Sort((left, right) =>
            {
                var result = left.Start.CompareTo(right.Start);
                return result != 0 ? result : left.Id.CompareTo(right.Id);
            });

            return groups;
        }

        private static void ReadBitmapRecords(SgContainer container)
        {
            var offset = SgConstants.HeaderSize;
            for (var i = 0; i < container.Header.NumBitmapRecords; i++)
            {
                var record = new BitmapRecord
                {
                    Id = i,
                    FileName = BinaryHelpers.ReadCString(container.Sg3Bytes, offset + 0, 65),
                    Comment = BinaryHelpers.ReadCString(container.Sg3Bytes, offset + 65, 51),
                    Width = ReadUInt32(container.Sg3Bytes, offset + 116),
                    Height = ReadUInt32(container.Sg3Bytes, offset + 120),
                    NumImages = ReadUInt32(container.Sg3Bytes, offset + 124),
                    StartIndex = ReadUInt32(container.Sg3Bytes, offset + 128),
                    EndIndex = ReadUInt32(container.Sg3Bytes, offset + 132),
                };
                record.FolderName = BinaryHelpers.SanitizeFolderName(Path.GetFileNameWithoutExtension(record.FileName));
                if (string.IsNullOrWhiteSpace(record.FolderName))
                {
                    record.FolderName = "Bitmap_" + i.ToString("D3");
                }
                container.Bitmaps.Add(record);
                offset += SgConstants.BitmapRecordSize;
            }
        }

        private static void ReadImageRecords(SgContainer container)
        {
            var hasAlpha = container.Header.Version >= 0xD6;
            var recordSize = hasAlpha ? 72 : 64;
            var offset = SgConstants.HeaderSize + (SgConstants.MaxBitmapRecords * SgConstants.BitmapRecordSize);

            container.Records.Add(ReadImageRecord(container, offset, 0, hasAlpha));
            offset += recordSize;

            for (var i = 0; i < container.Header.NumImageRecords; i++)
            {
                container.Records.Add(ReadImageRecord(container, offset, i + 1, hasAlpha));
                offset += recordSize;
            }
        }

        private static ImageRecord ReadImageRecord(SgContainer container, int offset, int index, bool hasAlpha)
        {
            return new ImageRecord
            {
                Index = index,
                Offset = ReadUInt32(container.Sg3Bytes, offset + 0),
                Length = ReadUInt32(container.Sg3Bytes, offset + 4),
                UncompressedLength = ReadUInt32(container.Sg3Bytes, offset + 8),
                InvertOffset = ReadInt32(container.Sg3Bytes, offset + 16),
                Width = ReadInt16(container.Sg3Bytes, offset + 20),
                Height = ReadInt16(container.Sg3Bytes, offset + 22),
                GroupId = ReadUInt16(container.Sg3Bytes, offset + 24),
                GroupIndex = ReadUInt16(container.Sg3Bytes, offset + 26),
                NumSprites = ReadUInt16(container.Sg3Bytes, offset + 30),
                SpriteOffsetX = ReadInt16(container.Sg3Bytes, offset + 34),
                SpriteOffsetY = ReadInt16(container.Sg3Bytes, offset + 36),
                CanReverse = container.Sg3Bytes[offset + 48] != 0,
                IsFullyCompressed = container.Sg3Bytes[offset + 51] != 0,
                IsExternal = container.Sg3Bytes[offset + 52] != 0,
                HasIsometricTop = container.Sg3Bytes[offset + 53] != 0,
                SpeedId = container.Sg3Bytes[offset + 58],
                Type = ReadUInt16(container.Sg3Bytes, offset + 50),
                Flags = new[] { container.Sg3Bytes[offset + 52], container.Sg3Bytes[offset + 53], container.Sg3Bytes[offset + 54], container.Sg3Bytes[offset + 55] },
                BitmapId = container.Sg3Bytes[offset + 56],
                AlphaOffset = hasAlpha ? ReadUInt32(container.Sg3Bytes, offset + 64) : 0,
                AlphaLength = hasAlpha ? ReadUInt32(container.Sg3Bytes, offset + 68) : 0,
            };
        }

        private static void ReadTrailingGroupNames(SgContainer container)
        {
            if (container == null || container.Sg3Bytes == null || container.Header == null)
            {
                return;
            }

            var recordSize = container.Header.Version >= 0xD6 ? 72 : 64;
            var trailingTableOffset = SgConstants.HeaderSize
                + (SgConstants.MaxBitmapRecords * SgConstants.BitmapRecordSize)
                + (container.Header.MaxImageRecords * recordSize);
            var trailingTableSize = SgConstants.TrailingGroupNameCount * SgConstants.TrailingGroupNameSize;
            if (trailingTableOffset < 0 || trailingTableOffset + trailingTableSize > container.Sg3Bytes.Length)
            {
                return;
            }

            for (var i = 0; i < SgConstants.TrailingGroupNameCount; i++)
            {
                var offset = trailingTableOffset + (i * SgConstants.TrailingGroupNameSize);
                var name = BinaryHelpers.ReadCString(container.Sg3Bytes, offset, SgConstants.TrailingGroupNameSize);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    container.TrailingGroupNames[i] = name;
                }
            }

            foreach (var subgroup in container.StructuralSubgroups)
            {
                string tailName;
                var tailSlotIndex = GetTailNameSlotIndex(subgroup.SlotIndex);
                if (tailSlotIndex >= 0 && container.TrailingGroupNames.TryGetValue(tailSlotIndex, out tailName))
                {
                    subgroup.ExplicitTailName = tailName;
                }
            }
        }

        private static int GetTailNameSlotIndex(int subgroupSlotIndex)
        {
            return subgroupSlotIndex - 20;
        }

        private static SgHeader ReadHeader(byte[] bytes)
        {
            var header = new SgHeader
            {
                SgFileSize = ReadUInt32(bytes, 0),
                Version = ReadUInt32(bytes, 4),
                Unknown1 = ReadUInt32(bytes, 8),
                MaxImageRecords = ReadInt32(bytes, 12),
                NumImageRecords = ReadInt32(bytes, 16),
                NumBitmapRecords = ReadInt32(bytes, 20),
                NumBitmapRecordsWithoutSystem = ReadInt32(bytes, 24),
                TotalFileSize = ReadUInt32(bytes, 28),
                FileSize555 = ReadUInt32(bytes, 32),
                FileSizeExternal = ReadUInt32(bytes, 36),
                RawSubblockStarts = new ushort[320]
            };

            for (var i = 0; i < 320; i++)
            {
                var value = ReadUInt16(bytes, 40 + (i * 2));
                header.RawSubblockStarts[i] = value;
                if (value != 0)
                {
                    header.SubblockStarts.Add(value);
                }
            }

            return header;
        }

        private static Tuple<int, int> GuessRaw555Dimensions(int byteLength)
        {
            if (byteLength == 640 * 480 * 2) return Tuple.Create(640, 480);
            if (byteLength == 800 * 600 * 2) return Tuple.Create(800, 600);
            if (byteLength == 1024 * 768 * 2) return Tuple.Create(1024, 768);
            if (byteLength % 2 != 0) throw new InvalidDataException("RAW .555 size must be even.");

            var pixels = byteLength / 2;
            Tuple<int, int> best = null;
            var bestScore = double.MaxValue;
            for (var w = 16; w * w <= pixels; w++)
            {
                if (pixels % w != 0) continue;
                var h = pixels / w;
                if (h < 16) continue;
                var score = Math.Abs(((double)h / w) - 1.0);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = Tuple.Create(w, h);
                }
            }

            if (best == null) throw new InvalidDataException("Could not infer RAW .555 dimensions.");
            return best;
        }

        private static ImageRecord CloneRecord(ImageRecord source)
        {
            return new ImageRecord
            {
                Index = source.Index,
                Offset = source.Offset,
                Length = source.Length,
                UncompressedLength = source.UncompressedLength,
                InvertOffset = source.InvertOffset,
                Width = source.Width,
                Height = source.Height,
                GroupId = source.GroupId,
                GroupIndex = source.GroupIndex,
                NumSprites = source.NumSprites,
                SpriteOffsetX = source.SpriteOffsetX,
                SpriteOffsetY = source.SpriteOffsetY,
                CanReverse = source.CanReverse,
                SpeedId = source.SpeedId,
                IsFullyCompressed = source.IsFullyCompressed,
                IsExternal = source.IsExternal,
                HasIsometricTop = source.HasIsometricTop,
                Type = source.Type,
                Flags = source.Flags == null ? new byte[4] : (byte[])source.Flags.Clone(),
                BitmapId = source.BitmapId,
                AlphaOffset = source.AlphaOffset,
                AlphaLength = source.AlphaLength,
            };
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return BitConverter.ToUInt16(data, offset);
        }

        private static short ReadInt16(byte[] data, int offset)
        {
            return BitConverter.ToInt16(data, offset);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return BitConverter.ToUInt32(data, offset);
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            return BitConverter.ToInt32(data, offset);
        }

        private static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, offset, 2);
        }

        private static void WriteInt16(byte[] data, int offset, short value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, offset, 2);
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, offset, 4);
        }

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, offset, 4);
        }

        private sealed class HeaderGroup
        {
            public int Id { get; set; }
            public int Start { get; set; }
        }
    }
}
