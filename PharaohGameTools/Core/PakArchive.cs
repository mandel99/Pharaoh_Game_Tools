using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PharaohGameTools.Core
{
    internal sealed class PakContainer
    {
        public string SourcePath { get; set; }
        public string DisplayName => Path.GetFileName(SourcePath);
        public bool HasPendingChanges { get; set; }
        public List<PakEntry> Entries { get; } = new List<PakEntry>();
    }

    internal sealed class PakEntry
    {
        public int Index { get; set; }
        public int Offset { get; set; }
        public int Size { get; set; }
        public int? FileVersion { get; set; }
        public int? MissionTitleOffset { get; set; }
        public byte[] Data { get; set; }
        public bool IsModified { get; set; }
        public string InferredName { get; set; }
        public string CampaignCityName { get; set; }
        public string MapFileName { get; set; }
        public string MissionName { get; set; }
        public string FileName => string.Format("{0:D3}.sav", Index + 1);
        public string CityName => !string.IsNullOrWhiteSpace(CampaignCityName)
            ? CampaignCityName
            : !string.IsNullOrWhiteSpace(MapFileName)
                ? MapFileName
                : !string.IsNullOrWhiteSpace(InferredName)
                    ? InferredName
                    : !string.IsNullOrWhiteSpace(MissionName)
                        ? MissionName
                        : FileName;
        public string DisplayName => CityName;
        public string MissionDisplayName => string.IsNullOrWhiteSpace(MissionName) ? string.Empty : MissionName;
    }

    internal static class PakArchive
    {
        private const uint UncompressedChunkMarker = 0x80000000u;

        private sealed class SaveChunkDefinition
        {
            public SaveChunkDefinition(int size, bool compressed, string name = null)
            {
                Size = size;
                Compressed = compressed;
                Name = name;
            }

            public int Size { get; }
            public bool Compressed { get; }
            public string Name { get; }
        }

        private sealed class CampaignMissionDefinition
        {
            public int ScenarioId { get; set; }
        }

        private sealed class CampaignSupportData
        {
            public List<string> MissionNames { get; } = new List<string>();
            public List<CampaignMissionDefinition> MissionDefinitions { get; } = new List<CampaignMissionDefinition>();
        }

        public static PakContainer Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var offsets = new List<int>();
            for (var position = 0; position + 4 <= bytes.Length; position += 4)
            {
                var offset = BitConverter.ToInt32(bytes, position);
                if (offset == 0)
                {
                    break;
                }

                offsets.Add(offset);
            }

            if (offsets.Count == 0)
            {
                throw new InvalidDataException("PAK file does not contain any entry offsets.");
            }

            var container = new PakContainer
            {
                SourcePath = path
            };
            var campaignSupport = TryLoadCampaignSupport(Path.GetDirectoryName(path));

            for (var index = 0; index < offsets.Count; index++)
            {
                var start = offsets[index];
                var end = index + 1 < offsets.Count ? offsets[index + 1] : bytes.Length;
                if (start < 0 || end < start || end > bytes.Length)
                {
                    throw new InvalidDataException(string.Format("Invalid PAK entry range at index {0}: {1}-{2}", index, start, end));
                }

                var size = end - start;
                var data = new byte[size];
                Buffer.BlockCopy(bytes, start, data, 0, size);
                var inferredName = InferEntryName(data, index);
                var metadata = ResolveCampaignMetadata(campaignSupport, index);
                var missionTitle = TryReadMissionTitle(data) ?? metadata;
                var missionTitleOffset = TryReadMissionTitleOffset(data);
                container.Entries.Add(new PakEntry
                {
                    Index = index,
                    Offset = start,
                    Size = size,
                    FileVersion = TryReadFileVersion(data),
                    Data = data,
                    InferredName = inferredName,
                    CampaignCityName = metadata,
                    MapFileName = TryReadMapFileName(data),
                    MissionName = missionTitle,
                    MissionTitleOffset = missionTitleOffset
                });
            }

            return container;
        }

        public static PakContainer Clone(PakContainer source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var clone = new PakContainer
            {
                SourcePath = source.SourcePath,
                HasPendingChanges = source.HasPendingChanges
            };

            foreach (var entry in source.Entries.OrderBy(x => x.Index))
            {
                clone.Entries.Add(new PakEntry
                {
                    Index = entry.Index,
                    Offset = entry.Offset,
                    Size = entry.Size,
                    FileVersion = entry.FileVersion,
                    MissionTitleOffset = entry.MissionTitleOffset,
                    Data = entry.Data == null ? null : (byte[])entry.Data.Clone(),
                    IsModified = entry.IsModified,
                    InferredName = entry.InferredName,
                    CampaignCityName = entry.CampaignCityName,
                    MapFileName = entry.MapFileName,
                    MissionName = entry.MissionName
                });
            }

            return clone;
        }

        public static void ReplaceEntry(PakContainer container, PakEntry entry, byte[] data)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            entry.Data = data.ToArray();
            entry.Size = entry.Data.Length;
            entry.FileVersion = TryReadFileVersion(entry.Data);
            entry.InferredName = InferEntryName(entry.Data, entry.Index);
            entry.MapFileName = TryReadMapFileName(entry.Data);
            entry.MissionName = TryReadMissionTitle(entry.Data);
            entry.MissionTitleOffset = TryReadMissionTitleOffset(entry.Data);
            entry.IsModified = true;
            container.HasPendingChanges = true;
        }

        public static void Save(PakContainer container, string outputPath)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            var entries = container.Entries.OrderBy(x => x.Index).ToList();
            var headerSize = (entries.Count + 1) * 4;
            var offsets = new int[entries.Count];
            var currentOffset = headerSize;
            for (var i = 0; i < entries.Count; i++)
            {
                offsets[i] = currentOffset;
                currentOffset += entries[i].Data?.Length ?? 0;
            }

            var output = new byte[currentOffset];
            for (var i = 0; i < offsets.Length; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(offsets[i]), 0, output, i * 4, 4);
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var data = entries[i].Data ?? Array.Empty<byte>();
                Buffer.BlockCopy(data, 0, output, offsets[i], data.Length);
                entries[i].Offset = offsets[i];
                entries[i].Size = data.Length;
                entries[i].IsModified = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, output);
            container.HasPendingChanges = false;
        }

        private static string InferEntryName(byte[] data, int index)
        {
            var asciiStrings = ExtractAsciiStrings(data).ToList();
            var mapName = TryReadEmbeddedMapFileName(asciiStrings);
            if (!string.IsNullOrWhiteSpace(mapName))
            {
                return mapName;
            }

            var scenarioMapName = TryReadScenarioMapName(data);
            if (!string.IsNullOrWhiteSpace(scenarioMapName))
            {
                return scenarioMapName;
            }

            var title = asciiStrings.FirstOrDefault(value =>
                value.Length >= 6
                && Regex.IsMatch(value, @"^[A-Z][A-Za-z0-9 ,'\-]{5,}$")
                && !value.Equals("map editing", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("Brief description", StringComparison.OrdinalIgnoreCase)
                && value.IndexOf(".map", StringComparison.OrdinalIgnoreCase) < 0);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            return string.Format("{0:D3}.sav", index + 1);
        }

        private static string TryReadMapFileName(byte[] data)
        {
            var asciiStrings = ExtractAsciiStrings(data).ToList();
            var mapName = TryReadEmbeddedMapFileName(asciiStrings);
            if (!string.IsNullOrWhiteSpace(mapName))
            {
                return mapName;
            }

            var scenarioMapName = TryReadScenarioMapName(data);
            if (!string.IsNullOrWhiteSpace(scenarioMapName))
            {
                return scenarioMapName;
            }

            return null;
        }

        private static string TryReadEmbeddedMapFileName(IEnumerable<string> asciiStrings)
        {
            if (asciiStrings == null)
            {
                return null;
            }

            return asciiStrings
                .SelectMany(value => Regex.Matches(value, @"[A-Za-z0-9_\-]+\.(?:map|sav)", RegexOptions.IgnoreCase).Cast<Match>())
                .Select(match => match.Value)
                .FirstOrDefault();
        }

        private static string ResolveCampaignMetadata(CampaignSupportData campaignSupport, int entryIndex)
        {
            if (campaignSupport == null || entryIndex < 0 || entryIndex >= campaignSupport.MissionDefinitions.Count)
            {
                return null;
            }

            var definition = campaignSupport.MissionDefinitions[entryIndex];
            string missionName = null;
            if (definition.ScenarioId >= 0 && definition.ScenarioId < campaignSupport.MissionNames.Count)
            {
                missionName = campaignSupport.MissionNames[definition.ScenarioId];
            }
            return missionName;
        }

        private static CampaignSupportData TryLoadCampaignSupport(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var campaignPath = Path.Combine(directory, "campaign.txt");
            if (!File.Exists(campaignPath))
            {
                return null;
            }

            var support = ParseCampaignFile(campaignPath);
            if (support == null)
            {
                return null;
            }

            return support;
        }

        private static CampaignSupportData ParseCampaignFile(string path)
        {
            var lines = File.ReadAllLines(path, Encoding.GetEncoding(1250));
            var support = new CampaignSupportData();
            var inMissionNames = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inMissionNames = line.Equals("[MISSION_NAMES]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inMissionNames)
                {
                    support.MissionNames.Add(line);
                    continue;
                }

                if (!line.StartsWith("mission=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Substring("mission=".Length)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .ToArray();
                if (parts.Length < 3)
                {
                    continue;
                }

                int scenarioId;
                if (!int.TryParse(parts[0], out scenarioId))
                {
                    continue;
                }

                support.MissionDefinitions.Add(new CampaignMissionDefinition
                {
                    ScenarioId = scenarioId
                });
            }

            return support.MissionDefinitions.Count == 0 && support.MissionNames.Count == 0 ? null : support;
        }

        private static string TryReadScenarioMapName(byte[] data)
        {
            if (data == null || data.Length < 8 + 6004)
            {
                return null;
            }

            try
            {
                var fileVersion = BitConverter.ToInt32(data, 4);
                var offset = 8 + 6004;
                foreach (var chunk in GetMissionPakSchema(fileVersion))
                {
                    if (!chunk.Compressed)
                    {
                        if (offset + chunk.Size > data.Length)
                        {
                            return null;
                        }

                        if (string.Equals(chunk.Name, "scenario_map_name", StringComparison.Ordinal))
                        {
                            return ReadZeroTerminatedText(data, offset, chunk.Size);
                        }

                        offset += chunk.Size;
                        continue;
                    }

                    if (offset + 4 > data.Length)
                    {
                        return null;
                    }

                    var header = BitConverter.ToUInt32(data, offset);
                    offset += 4;

                    if (header == UncompressedChunkMarker)
                    {
                        if (offset + chunk.Size > data.Length)
                        {
                            return null;
                        }

                        offset += chunk.Size;
                    }
                    else
                    {
                        var compressedSize = unchecked((int)header);
                        if (compressedSize < 0 || offset + compressedSize > data.Length)
                        {
                            return null;
                        }

                        offset += compressedSize;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string TryReadMissionTitle(byte[] data)
        {
            int offset;
            if (!TryGetChunkPayloadOffset(data, "scenario_info", out offset))
            {
                return null;
            }

            const int subtitleOffsetInChunk = 60;
            const int subtitleLength = 64;
            var absoluteOffset = offset + subtitleOffsetInChunk;
            if (absoluteOffset < 0 || absoluteOffset + subtitleLength > data.Length)
            {
                return null;
            }

            return ReadZeroTerminatedText(data, absoluteOffset, subtitleLength);
        }

        private static int? TryReadMissionTitleOffset(byte[] data)
        {
            int offset;
            if (!TryGetChunkPayloadOffset(data, "scenario_info", out offset))
            {
                return null;
            }

            const int subtitleOffsetInChunk = 60;
            var absoluteOffset = offset + subtitleOffsetInChunk;
            return absoluteOffset >= 0 && absoluteOffset < data.Length
                ? (int?)absoluteOffset
                : null;
        }

        private static bool TryGetChunkPayloadOffset(byte[] data, string chunkName, out int payloadOffset)
        {
            payloadOffset = 0;
            if (data == null || data.Length < 8 + 6004 || string.IsNullOrWhiteSpace(chunkName))
            {
                return false;
            }

            try
            {
                var fileVersion = BitConverter.ToInt32(data, 4);
                var offset = 8 + 6004;
                foreach (var chunk in GetMissionPakSchema(fileVersion))
                {
                    if (!chunk.Compressed)
                    {
                        if (offset + chunk.Size > data.Length)
                        {
                            return false;
                        }

                        if (string.Equals(chunk.Name, chunkName, StringComparison.Ordinal))
                        {
                            payloadOffset = offset;
                            return true;
                        }

                        offset += chunk.Size;
                        continue;
                    }

                    if (offset + 4 > data.Length)
                    {
                        return false;
                    }

                    var header = BitConverter.ToUInt32(data, offset);
                    offset += 4;

                    if (header == UncompressedChunkMarker)
                    {
                        if (offset + chunk.Size > data.Length)
                        {
                            return false;
                        }

                        if (string.Equals(chunk.Name, chunkName, StringComparison.Ordinal))
                        {
                            payloadOffset = offset;
                            return true;
                        }

                        offset += chunk.Size;
                        continue;
                    }

                    var compressedSize = unchecked((int)header);
                    if (compressedSize < 0 || offset + compressedSize > data.Length)
                    {
                        return false;
                    }

                    if (string.Equals(chunk.Name, chunkName, StringComparison.Ordinal))
                    {
                        payloadOffset = offset;
                        return true;
                    }

                    offset += compressedSize;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static IEnumerable<SaveChunkDefinition> GetMissionPakSchema(int fileVersion)
        {
            yield return Chunk(207936, true);
            yield return Chunk(51984, true);
            yield return Chunk(103968, true);
            yield return Chunk(207936, true);
            yield return Chunk(51984, true);
            yield return Chunk(103968, true);
            yield return Chunk(51984, true);
            yield return Chunk(51984, true);
            yield return Chunk(51984, false);
            yield return Chunk(51984, true);
            yield return Chunk(51984, true);
            yield return Chunk(103968, true);
            yield return Chunk(51984, true);
            yield return Chunk(51984, true);
            yield return Chunk(776000, true);
            yield return Chunk(2000, true);
            yield return Chunk(500000, true);
            yield return Chunk(7200, true);
            yield return Chunk(12, false);
            yield return Chunk(37808, true);
            yield return Chunk(72, false);
            yield return Chunk(1056000, true);
            yield return Chunk(4, false);
            yield return Chunk(20, false);
            yield return Chunk(8, false);
            yield return Chunk(8, false);
            yield return Chunk(8, false);
            yield return Chunk(8, false);
            yield return Chunk(12, false);
            yield return Chunk(6466, true);
            yield return Chunk(288, false);
            yield return Chunk(288, false);
            yield return Chunk(84, false);
            yield return NamedChunk(1592, false, "scenario_info");
            yield return Chunk(4, false);
            yield return Chunk(48000, true);
            yield return Chunk(182, false);
            yield return Chunk(8, false);
            yield return Chunk(4, false);
            yield return Chunk(12, false);
            yield return Chunk(3232, true);
            yield return Chunk(4, false);
            yield return Chunk(8960, false);
            yield return Chunk(4, false);
            yield return Chunk(8804, false);
            yield return Chunk(1000, true);
            yield return Chunk(1000, true);
            yield return Chunk(8000, true);
            yield return Chunk(32, false);
            yield return Chunk(24, false);
            yield return Chunk(39200, false);
            yield return Chunk(2880, true);
            yield return Chunk(2880, true);
            yield return Chunk(50, false);
            yield return NamedChunk(65, false, "scenario_map_name");
            yield return Chunk(32, false);
            yield return Chunk(12, false);
            yield return Chunk(396, false);
            yield return Chunk(51984, false);
            yield return Chunk(18600, false);
            yield return Chunk(28, false);
            yield return Chunk(fileVersion < 149 ? 11000 : 11200, false);
            yield return Chunk(2200, false);
            yield return Chunk(16, false);
            yield return Chunk(8200, false);
            yield return Chunk(1280, true);
            yield return Chunk(fileVersion < 160 ? 15200 : 19600, true);
            yield return Chunk(16200, true);
            yield return Chunk(51984, false);
            yield return Chunk(20, false);
            yield return Chunk(528, false);
            yield return Chunk(fileVersion < 147 ? 32 : 36, true);
            yield return Chunk(207936, true);
            yield return Chunk(312, false);
            yield return Chunk(64, false);
            yield return Chunk(41, false);
            yield return Chunk(51984, true);
            yield return Chunk(1, false);
            yield return Chunk(51984, true);
            yield return Chunk(240, false);
            yield return Chunk(432, false);
            yield return Chunk(8, false);
            if (fileVersion >= 160)
            {
                yield return Chunk(20, false);
            }
        }

        private static SaveChunkDefinition Chunk(int size, bool compressed)
        {
            return new SaveChunkDefinition(size, compressed);
        }

        private static SaveChunkDefinition NamedChunk(int size, bool compressed, string name)
        {
            return new SaveChunkDefinition(size, compressed, name);
        }

        private static int? TryReadFileVersion(byte[] data)
        {
            if (data == null || data.Length < 8)
            {
                return null;
            }

            return BitConverter.ToInt32(data, 4);
        }

        private static string ReadZeroTerminatedText(byte[] data, int offset, int maxLength)
        {
            var length = 0;
            while (length < maxLength && offset + length < data.Length && data[offset + length] != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return null;
            }

            var value = Encoding.ASCII.GetString(data, offset, length).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static IEnumerable<string> ExtractAsciiStrings(byte[] data)
        {
            var builder = new StringBuilder();
            foreach (var value in data ?? Array.Empty<byte>())
            {
                if (value >= 32 && value <= 126)
                {
                    builder.Append((char)value);
                }
                else
                {
                    if (builder.Length >= 4)
                    {
                        yield return builder.ToString();
                    }

                    builder.Clear();
                }
            }

            if (builder.Length >= 4)
            {
                yield return builder.ToString();
            }
        }
    }
}
