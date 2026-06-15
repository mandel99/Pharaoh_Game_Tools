using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PharaohGameTools.Core
{
    internal enum EngFileType
    {
        Unknown,
        Text,
        Message
    }

    internal static class TextEngConverter
    {
        private const int TextIndexEntries = 1000;
        private const int TextHeaderSize = 28;
        private const string DefaultTextFileName = "Pharaoh textfile";
        private const string DefaultMessageFileName = "Pharaoh MM file.";
        private static readonly string[] TextTxtHeaderLines =
        {
            "-------------------------------------------------",
            "---Description ------------------------------------------------------------",
            string.Empty,
            "\tAll text above the BEGIN line (below) is comment and may be edited freely.",
            "Beneath the BEGIN line the text is all in-game text with ONE exception:",
            "any text following a * character at the start of a line is control",
            "info and should NOT be altered.",
            string.Empty,
            "Any of the in-game text can be altered, but if you increase the length",
            "of a line it may no longer fit on screen and should be checked in game.",
            string.Empty,
            "A line of text ends with a RETURN. The game prints everything from column 1",
            "until it encounters that RETURN, so trailing spaces and tabs are preserved.",
            string.Empty,
            "If a text is too long to be placed on a single line use the + character",
            "at the FIRST column of the next line to indicate continued text.",
            string.Empty,
            "NOTE",
            "---- The lines beginning with a * character are special marker lines",
            "that provide the compiler with reference points. THEY SHOULD NOT BE",
            "DELETED OR MODIFIED.",
            string.Empty,
            "-- TEXT STARTS HERE  ---- TEXT STARTS HERE  ---- TEXT STARTS HERE  --",
            "-- TEXT STARTS HERE  ---- TEXT STARTS HERE  ---- TEXT STARTS HERE  --",
            string.Empty
        };
        private static readonly string[] MessageTxtHeaderLines =
        {
            "-------------------------------------------------",
            "---Description ------------------------------------------------------------",
            string.Empty,
            "\tAll text above the BEGIN line (below) is comment and may be edited freely.",
            "Beneath the BEGIN line the text describes Pharaoh message entries.",
            "Lines beginning with * are entry markers and should NOT be altered.",
            string.Empty,
            "Keep the field names and overall layout intact.",
            "You may edit the text values, but avoid removing required fields such as",
            "TYPE, BOX_X, TEXT_X, TITLE_TEXT, CAPTION_TEXT, or MAIN_TEXT.",
            string.Empty,
            "Quoted text blocks may span multiple lines. Preserve the opening key and",
            "closing quote so the file remains valid for compilation.",
            string.Empty,
            "-- MESSAGE TEXT STARTS HERE  -- MESSAGE TEXT STARTS HERE  --",
            "-- MESSAGE TEXT STARTS HERE  -- MESSAGE TEXT STARTS HERE  --",
            string.Empty
        };
        private static readonly Encoding DefaultEncoding = Encoding.GetEncoding(1252);
        private static readonly string[] MmI16Fields =
        {
            "type", "subtype", "data",
            "boxX", "boxY", "boxW", "boxH",
            "pic1", "pic1X", "pic1Y",
            "pic2", "pic2X", "pic2Y",
            "titleX", "titleY",
            "captionX", "captionY",
            "textX", "textY",
            "animX", "animY"
        };

        public static string DecodeTxt(byte[] bytes)
        {
            return DecodeTxt(bytes, null);
        }

        public static string DecodeTxt(byte[] bytes, string encodingName)
        {
            return ResolveEncoding(encodingName).GetString(bytes ?? Array.Empty<byte>());
        }

        public static byte[] EncodeTxt(string text)
        {
            return EncodeTxt(text, null);
        }

        public static byte[] EncodeTxt(string text, string encodingName)
        {
            return ResolveEncoding(encodingName).GetBytes(text ?? string.Empty);
        }

        public static EngFileType DetermineEngFileType(byte[] bytes)
        {
            if (bytes == null || bytes.Length < TextHeaderSize)
            {
                return EngFileType.Unknown;
            }

            var first = BitConverter.ToInt32(bytes, 16);
            var second = BitConverter.ToInt32(bytes, 20);
            if ((first == 400 || first == 1000) && first >= second)
            {
                return EngFileType.Message;
            }

            if (first <= 400 && first <= second)
            {
                return EngFileType.Text;
            }

            const int probeOffset = 24 + 8000;
            if (bytes.Length >= probeOffset + 8)
            {
                var zero = BitConverter.ToUInt32(bytes, probeOffset);
                var nonZero = BitConverter.ToUInt32(bytes, probeOffset + 4);
                if (zero == 0 && nonZero != 0)
                {
                    return EngFileType.Text;
                }
            }

            return EngFileType.Unknown;
        }

        public static string ConvertEngToTxt(byte[] bytes)
        {
            return ConvertEngToTxt(bytes, null);
        }

        public static string ConvertEngToTxt(byte[] bytes, string encodingName)
        {
            var encoding = ResolveEncoding(encodingName);
            switch (DetermineEngFileType(bytes))
            {
                case EngFileType.Message:
                    return BuildMessageTxtFromEng(ParseMessageEngFile(bytes, encoding));
                case EngFileType.Text:
                    return BuildTxtFileFromDocument(ParseTextEngFile(bytes, encoding));
                default:
                    throw new InvalidDataException("Unsupported or unknown ENG format.");
            }
        }

        public static byte[] ConvertTxtToEng(string text)
        {
            return ConvertTxtToEng(text, null);
        }

        public static byte[] ConvertTxtToEng(string text, string encodingName)
        {
            var encoding = ResolveEncoding(encodingName);
            if (LooksLikeMessageTxt(text))
            {
                return BuildMessageEngFromTxt(ParseMessageTxt(text), encoding);
            }

            return BuildTextEngFromDocument(ParseTxtFileToDocument(text), encoding);
        }

        public static string GetTextGroupEntry(byte[] bytes, int groupId, int entryIndex)
        {
            if (DetermineEngFileType(bytes) != EngFileType.Text || groupId < 0 || entryIndex < 0)
            {
                return null;
            }

            var document = ParseTextEngFile(bytes, DefaultEncoding);
            List<string> group;
            if (!document.Groups.TryGetValue(groupId, out group) || group == null || entryIndex >= group.Count)
            {
                return null;
            }

            return group[entryIndex];
        }

        public static IReadOnlyList<string> GetTextGroupEntries(byte[] bytes, int groupId)
        {
            if (DetermineEngFileType(bytes) != EngFileType.Text || groupId < 0)
            {
                return Array.Empty<string>();
            }

            var document = ParseTextEngFile(bytes, DefaultEncoding);
            List<string> group;
            if (!document.Groups.TryGetValue(groupId, out group) || group == null)
            {
                return Array.Empty<string>();
            }

            return group.ToArray();
        }

        public static string GetMessageEntrySummary(byte[] bytes, int entryId)
        {
            if (DetermineEngFileType(bytes) != EngFileType.Message || entryId < 0)
            {
                return null;
            }

            var document = ParseMessageEngFile(bytes, DefaultEncoding);
            if (entryId >= document.Entries.Count)
            {
                return null;
            }

            var entry = document.Entries[entryId];
            var value = FirstNonEmpty(entry.TitleText, entry.CaptionText, entry.MainText);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
            return value;
        }

        public static IReadOnlyDictionary<int, string> GetMessageEntrySummaries(byte[] bytes)
        {
            if (DetermineEngFileType(bytes) != EngFileType.Message)
            {
                return new Dictionary<int, string>();
            }

            var document = ParseMessageEngFile(bytes, DefaultEncoding);
            var output = new Dictionary<int, string>();
            foreach (var entry in document.Entries)
            {
                var value = FirstNonEmpty(entry.TitleText, entry.CaptionText, entry.MainText);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                output[entry.Id] = value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
            }

            return output;
        }

        public static bool LooksLikeMessageTxt(string text)
        {
            var sample = GetTextFromBeginMarker(text);
            if (sample.Length > 20000)
            {
                sample = sample.Substring(0, 20000);
            }

            var normalized = NormalizeLineEndings(sample);
            var lines = normalized.Split('\n');
            var entryMarkerCount = 0;
            var mmFieldHits = 0;
            var hasMainText = false;
            var hasLayoutFields = false;

            foreach (var rawLine in lines)
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Regex.IsMatch(line, @"^\*\d+\b"))
                {
                    entryMarkerCount++;
                    continue;
                }

                if (line.IndexOf("TYPE=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("SUB_TYPE=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("DATA=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("BOX_X=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("BOX_Y=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("BOX_W=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("BOX_H=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("TITLE_X=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("TITLE_Y=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("CAPTION_X=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("CAPTION_Y=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("TEXT_X=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("TEXT_Y=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("PIC1=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("PIC2=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("ANIMATION=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("SOUND=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("DELAY=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("ANIM_X=", StringComparison.Ordinal) >= 0) mmFieldHits++;
                if (line.IndexOf("ANIM_Y=", StringComparison.Ordinal) >= 0) mmFieldHits++;

                if (line.IndexOf("MAIN_TEXT=", StringComparison.Ordinal) >= 0
                    || line.IndexOf("TITLE_TEXT=", StringComparison.Ordinal) >= 0
                    || line.IndexOf("CAPTION_TEXT=", StringComparison.Ordinal) >= 0)
                {
                    hasMainText = true;
                }

                if ((line.IndexOf("BOX_X=", StringComparison.Ordinal) >= 0 || line.IndexOf("TEXT_X=", StringComparison.Ordinal) >= 0)
                    && (line.IndexOf("BOX_Y=", StringComparison.Ordinal) >= 0 || line.IndexOf("TEXT_Y=", StringComparison.Ordinal) >= 0))
                {
                    hasLayoutFields = true;
                }
            }

            return entryMarkerCount > 0
                && hasMainText
                && hasLayoutFields
                && mmFieldHits >= 6;
        }

        public static bool TryValidateTxtStructure(string text, out string error)
        {
            var normalized = NormalizeLineEndings(text);
            var normalizedBody = GetTextFromBeginMarker(normalized);
            var lines = normalized.Split('\n');
            var beginIndex = FindMarkerLineIndex(lines, "***BEGIN");
            var endIndex = beginIndex >= 0
                ? FindMarkerLineIndex(lines, "***END", beginIndex + 1)
                : -1;
            var sectionCount = 0;

            if (beginIndex < 0)
            {
                error = "Missing ***BEGIN marker.";
                return false;
            }

            if (endIndex < 0)
            {
                error = "Missing ***END marker.";
                return false;
            }

            if (endIndex <= beginIndex)
            {
                error = "***END must appear after ***BEGIN.";
                return false;
            }

            for (var i = beginIndex + 1; i < endIndex; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).TrimStart();
                if (Regex.IsMatch(trimmed, @"^\*\d+\b"))
                {
                    sectionCount++;
                }
            }

            if (sectionCount == 0)
            {
                error = "No section markers were found between ***BEGIN and ***END.";
                return false;
            }

            if (LooksLikeMessageTxt(text))
            {
                var requiredFields = new[]
                {
                    "TYPE=",
                    "BOX_X=",
                    "TEXT_X=",
                    "MAIN_TEXT="
                };

                foreach (var field in requiredFields)
                {
                    if (normalizedBody.IndexOf(field, StringComparison.Ordinal) < 0)
                    {
                        error = string.Format("The message format is missing required field {0}.", field.TrimEnd('='));
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static TextEngDocument ParseTextEngFile(byte[] bytes, Encoding encoding)
        {
            var document = new TextEngDocument
            {
                Name = ReadName16(bytes, encoding),
                GroupCount = BitConverter.ToInt32(bytes, 16),
                TotalStrings = BitConverter.ToInt32(bytes, 20),
                TotalWords = BitConverter.ToInt32(bytes, 24)
            };

            var indexBase = 16 + 12;
            var usedGroups = ReadUsedTextGroupOffsets(bytes, indexBase);

            var textStart = indexBase + (TextIndexEntries * 8);
            var data = bytes.Skip(textStart).ToArray();
            var textSize = DetermineTextDataSize(data);

            for (var i = 0; i < usedGroups.Count; i++)
            {
                var group = usedGroups[i];
                var startOffset = group.Offset;
                var endOffset = i + 1 == usedGroups.Count ? textSize : usedGroups[i + 1].Offset;
                if (startOffset < 0 || startOffset > endOffset || startOffset > textSize || endOffset > textSize)
                {
                    continue;
                }

                document.Groups[group.Id] = ReadNullTerminatedStrings(data, startOffset, endOffset, encoding);
            }

            return document;
        }

        private static byte[] BuildTextEngFromDocument(TextEngDocument document, Encoding encoding)
        {
            document = document ?? new TextEngDocument();
            return BuildTextEngFromGroups(
                document.Groups,
                encoding,
                string.IsNullOrWhiteSpace(document.Name) ? DefaultTextFileName : document.Name,
                document.GroupCount);
        }

        private static byte[] BuildTextEngFromGroups(Dictionary<int, List<string>> groups, Encoding encoding, string fileName, int? preservedGroupCount)
        {
            var ids = groups.Keys
                .Where(id => id >= 0 && groups[id] != null)
                .OrderBy(id => id)
                .ToList();

            var maxGroupId = ids.Count > 0 ? ids[ids.Count - 1] : 0;
            var groupCount = Math.Max(maxGroupId + 1, preservedGroupCount ?? 0);
            var totalStrings = 0;
            var totalWords = 0;
            foreach (var id in ids)
            {
                foreach (var value in groups[id])
                {
                    totalStrings++;
                    totalWords += CountWords(value);
                }
            }

            var index = new byte[TextIndexEntries * 8];
            var textData = new List<byte>();
            var lastWrittenIndex = -1;
            Action<int, int> writeEmptyEntries = (nextIndex, offset) =>
            {
                for (var i = lastWrittenIndex + 1; i < nextIndex; i++)
                {
                    WriteInt32(index, i * 8, offset);
                    WriteInt32(index, (i * 8) + 4, 0);
                }
            };

            foreach (var id in ids)
            {
                writeEmptyEntries(id, textData.Count);
                lastWrittenIndex = id;
                WriteInt32(index, id * 8, textData.Count);
                WriteInt32(index, (id * 8) + 4, 1);
                foreach (var value in groups[id])
                {
                    textData.AddRange(encoding.GetBytes(value ?? string.Empty));
                    textData.Add(0);
                }
            }

            for (var i = lastWrittenIndex + 1; i < TextIndexEntries; i++)
            {
                WriteInt32(index, i * 8, 0);
                WriteInt32(index, (i * 8) + 4, 0);
            }

            textData.Add(0);
            if ((textData.Count % 2) != 0)
            {
                textData.Add(0);
            }

            var header = new byte[TextHeaderSize];
            Buffer.BlockCopy(WriteName16(fileName, encoding), 0, header, 0, 16);
            WriteInt32(header, 16, groupCount);
            WriteInt32(header, 20, totalStrings);
            WriteInt32(header, 24, totalWords);

            var output = new byte[header.Length + index.Length + textData.Count];
            Buffer.BlockCopy(header, 0, output, 0, header.Length);
            Buffer.BlockCopy(index, 0, output, header.Length, index.Length);
            Buffer.BlockCopy(textData.ToArray(), 0, output, header.Length + index.Length, textData.Count);
            return output;
        }

        private static TextEngDocument ParseTxtFileToDocument(string text)
        {
            var document = new TextEngDocument();
            foreach (var pair in ParseTxtFileToGroups(text, false, false))
            {
                document.Groups[pair.Key] = pair.Value;
            }

            UpdateTextDocumentStatistics(document);
            return document;
        }

        private static Dictionary<int, List<string>> ParseTxtFileToGroups(string text, bool preserveEmptyLines, bool plusAddsNewline)
        {
            var groups = new Dictionary<int, List<string>>();
            var lines = NormalizeLineEndings(text).Split('\n');
            var begin = 0;
            var beginMarkerIndex = FindMarkerLineIndex(lines, "***BEGIN");
            if (beginMarkerIndex >= 0)
            {
                begin = beginMarkerIndex + 1;
            }

            int? currentGroupId = null;
            for (var i = begin; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("*", StringComparison.Ordinal))
                {
                    var idPrefix = new string(line.Substring(1).Trim().TakeWhile(char.IsDigit).ToArray());
                    int parsedId;
                    if (int.TryParse(idPrefix, out parsedId))
                    {
                        currentGroupId = parsedId;
                        if (!groups.ContainsKey(parsedId))
                        {
                            groups[parsedId] = new List<string>();
                        }
                    }

                    continue;
                }

                if (!currentGroupId.HasValue)
                {
                    continue;
                }

                if (line.Length == 0)
                {
                    if (preserveEmptyLines)
                    {
                        groups[currentGroupId.Value].Add(string.Empty);
                    }

                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    var content = line.Substring(1);
                    var list = groups[currentGroupId.Value];
                    if (list.Count == 0)
                    {
                        list.Add(content);
                    }
                    else
                    {
                        list[list.Count - 1] += plusAddsNewline ? "\n" + content : content;
                    }
                }
                else
                {
                    groups[currentGroupId.Value].Add(line);
                }
            }

            return groups;
        }

        private static string BuildTxtFileFromDocument(TextEngDocument document)
        {
            document = document ?? new TextEngDocument();
            var groups = document.Groups ?? new Dictionary<int, List<string>>();
            var lines = new List<string>(TextTxtHeaderLines)
            {
                "***BEGIN--------------------------------------------------------------------"
            };

            var minGroupId = groups.Count == 0 ? 1 : groups.Keys.Min();
            var maxGroupId = groups.Count == 0 ? 0 : groups.Keys.Max();
            if (minGroupId > 1)
            {
                minGroupId = 1;
            }

            for (var id = minGroupId; id <= maxGroupId; id++)
            {
                lines.Add("*" + id + " -----------------------------------------------------------------------");
                List<string> group;
                if (!groups.TryGetValue(id, out group) || group == null || group.Count == 0)
                {
                    continue;
                }

                foreach (var value in group)
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        lines.Add(string.Empty);
                        continue;
                    }

                    var parts = value.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                    lines.Add(parts[0]);
                    for (var i = 1; i < parts.Length; i++)
                    {
                        lines.Add("+" + parts[i]);
                    }
                }
            }

            lines.Add("***END");
            return string.Join("\r\n", lines);
        }

        private static MessageDocument ParseMessageEngFile(byte[] bytes, Encoding encoding)
        {
            var document = new MessageDocument
            {
                Name = ReadName16(bytes, encoding),
                Total = BitConverter.ToInt32(bytes, 16),
                Used = BitConverter.ToInt32(bytes, 20)
            };

            var pos = 24;
            for (var id = 0; id < document.Total; id++)
            {
                var entry = new MessageEntry { Id = id };
                foreach (var field in MmI16Fields)
                {
                    entry.Set(field, BitConverter.ToInt16(bytes, pos));
                    pos += 2;
                }

                entry.Padding = bytes.Skip(pos).Take(14).ToArray();
                pos += 14;
                entry.Delay = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                entry.AnimationOffset = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                entry.SoundOffset = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                entry.TitleOffset = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                entry.CaptionOffset = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                entry.MainOffset = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                document.Entries.Add(entry);
            }

            var textBytes = bytes.Skip(24 + (document.Total * 80)).ToArray();
            foreach (var entry in document.Entries)
            {
                entry.Animation = ReadMessageString(textBytes, entry.AnimationOffset, encoding);
                entry.Sound = ReadMessageString(textBytes, entry.SoundOffset, encoding);
                entry.TitleText = ReadMessageString(textBytes, entry.TitleOffset, encoding);
                entry.CaptionText = ReadMessageString(textBytes, entry.CaptionOffset, encoding);
                entry.MainText = ReadMessageString(textBytes, entry.MainOffset, encoding);
            }

            return document;
        }

        private static string BuildMessageTxtFromEng(MessageDocument document)
        {
            var lines = new List<string>(MessageTxtHeaderLines)
            {
                "***BEGIN",
                string.Empty
            };

            var maxId = Math.Max(0, Math.Min(document.Used - 1, document.Entries.Count - 1));
            for (var id = 0; id <= maxId; id++)
            {
                var entry = document.Entries[id];
                lines.Add("*" + id + "************************************");
                lines.Add(string.Empty);
                lines.Add(string.Format("TYPE={0}\t\tSUB_TYPE={1}  DATA={2}", entry.Type, entry.Subtype, entry.Data));
                lines.Add(string.Format("BOX_X={0} BOX_Y={1} BOX_W={2} BOX_H={3}", entry.BoxX, entry.BoxY, entry.BoxW, entry.BoxH));
                lines.Add(string.Format("PIC1={0}\t\tPIC1_X={1} PIC1_Y={2}", entry.Pic1, entry.Pic1X, entry.Pic1Y));
                lines.Add(string.Format("PIC2={0}\t\tPIC2_X={1} PIC2_Y={2}", entry.Pic2, entry.Pic2X, entry.Pic2Y));
                lines.Add(string.Format("ANIMATION=\"{0}\"\tANIM_X={1} ANIM_Y={2}", EscapeQuotes(entry.Animation), entry.AnimX, entry.AnimY));
                lines.Add(string.Format("SOUND=\"{0}\"\t\tDELAY={1}", EscapeQuotes(entry.Sound), entry.Delay));
                lines.Add(string.Format("TITLE_X={0}\tTITLE_Y={1}", entry.TitleX, entry.TitleY));
                lines.Add(string.Format("CAPTION_X={0}\tCAPTION_Y={1}", entry.CaptionX, entry.CaptionY));
                lines.Add(string.Format("TEXT_X={0}\tTEXT_Y={1}", entry.TextX, entry.TextY));
                PushQuotedBlock(lines, "TITLE_TEXT", entry.TitleText, false);
                PushQuotedBlock(lines, "CAPTION_TEXT", entry.CaptionText, false);
                PushQuotedBlock(lines, "MAIN_TEXT", entry.MainText, true);
                lines.Add(string.Empty);
            }

            lines.Add("***END");
            return string.Join("\r\n", lines);
        }

        private static MessageDocument ParseMessageTxt(string text)
        {
            var lines = NormalizeLineEndings(text).Split('\n');
            var index = FindMarkerLineIndex(lines, "***BEGIN");

            if (index >= lines.Length)
            {
                throw new InvalidDataException("Missing ***BEGIN");
            }

            index++;

            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            var entriesById = new Dictionary<int, MessageEntry>();
            MessageEntry current = null;
            string multilineKey = null;
            var multilineBuffer = new List<string>();

            while (index < lines.Length)
            {
                var line = lines[index];
                var trimmedEnd = line.TrimEnd();
                if (LineStartsWithMarker(trimmedEnd, "***END"))
                {
                    break;
                }

                var star = Regex.Match(trimmedEnd, @"^\*(\d+)");
                if (star.Success)
                {
                    if (multilineKey != null && current != null)
                    {
                        SetMessageField(current, multilineKey, UnescapeQuotes(string.Join("\n", multilineBuffer)));
                        multilineKey = null;
                        multilineBuffer.Clear();
                    }

                    current = CreateDefaultMessageEntry(int.Parse(star.Groups[1].Value));
                    entriesById[current.Id] = current;
                    index++;
                    continue;
                }

                if (current == null || IsIgnorableTextLine(trimmedEnd))
                {
                    index++;
                    continue;
                }

                if (multilineKey != null)
                {
                    var endIndex = FindClosingQuoteIndex(line);
                    if (endIndex >= 0)
                    {
                        multilineBuffer.Add(line.Substring(0, endIndex));
                        SetMessageField(current, multilineKey, UnescapeQuotes(string.Join("\n", multilineBuffer)));
                        multilineKey = null;
                        multilineBuffer.Clear();
                    }
                    else
                    {
                        multilineBuffer.Add(line);
                    }

                    index++;
                    continue;
                }

                var multiMatch = Regex.Match(line, "^([A-Z0-9_]+)=\\\"(.*)$");
                if (multiMatch.Success)
                {
                    var key = multiMatch.Groups[1].Value;
                    var rest = multiMatch.Groups[2].Value;
                    if (key == "TITLE_TEXT" || key == "CAPTION_TEXT" || key == "MAIN_TEXT")
                    {
                        var closingIndex = FindClosingQuoteIndex(rest);
                        if (closingIndex >= 0 && closingIndex == rest.Length - 1)
                        {
                            SetMessageField(current, key, UnescapeQuotes(rest.Substring(0, closingIndex)));
                        }
                        else
                        {
                            multilineKey = key;
                            multilineBuffer.Add(rest);
                        }

                        index++;
                        continue;
                    }
                }

                foreach (var pair in ParseKeyValues(line))
                {
                    SetMessageField(current, pair.Key, pair.Value);
                }

                index++;
            }

            if (multilineKey != null && current != null)
            {
                SetMessageField(current, multilineKey, UnescapeQuotes(string.Join("\n", multilineBuffer)));
            }

            var maxId = entriesById.Count == 0 ? -1 : entriesById.Keys.Max();
            var total = maxId < 0 ? 0 : (maxId < 400 ? 400 : 1000);
            var used = maxId + 1;
            var document = new MessageDocument
            {
                Name = DefaultMessageFileName,
                Total = total,
                Used = used
            };

            for (var id = 0; id < total; id++)
            {
                MessageEntry entry;
                if (!entriesById.TryGetValue(id, out entry))
                {
                    entry = CreateDefaultMessageEntry(id);
                }

                document.Entries.Add(entry);
            }

            return document;
        }

        private static int FindMarkerLineIndex(string[] lines, string marker, int startIndex = 0)
        {
            for (var i = Math.Max(0, startIndex); i < lines.Length; i++)
            {
                if (LineStartsWithMarker(lines[i], marker))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool LineStartsWithMarker(string line, string marker)
        {
            if (string.IsNullOrEmpty(marker))
            {
                return false;
            }

            var trimmed = (line ?? string.Empty).Trim();
            if (trimmed.Length > 0 && trimmed[0] == '\uFEFF')
            {
                trimmed = trimmed.Substring(1).TrimStart();
            }

            return trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetTextFromBeginMarker(string text)
        {
            var normalized = NormalizeLineEndings(text);
            var lines = normalized.Split('\n');
            var beginIndex = FindMarkerLineIndex(lines, "***BEGIN");
            if (beginIndex < 0)
            {
                return normalized;
            }

            return string.Join("\n", lines.Skip(beginIndex));
        }

        private static string NormalizeLineEndings(string? text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
        }

        private static byte[] BuildMessageEngFromTxt(MessageDocument document, Encoding encoding)
        {
            var textData = new List<byte>();
            for (var i = 0; i < 16; i++)
            {
                textData.Add(0);
            }

            Func<string, int> appendCString = value =>
            {
                if (string.IsNullOrEmpty(value))
                {
                    return 0;
                }

                value = NormalizeMessageInlineText(value);
                var offset = textData.Count;
                textData.AddRange(encoding.GetBytes(value));
                textData.Add(0);
                return offset;
            };

            var offsets = new List<MessageOffsets>(document.Total);
            for (var id = 0; id < document.Total; id++)
            {
                var entry = document.Entries[id];
                offsets.Add(new MessageOffsets
                {
                    Animation = appendCString(entry.Animation),
                    Sound = appendCString(entry.Sound),
                    Title = appendCString(entry.TitleText),
                    Caption = appendCString(entry.CaptionText),
                    Main = appendCString(entry.MainText)
                });
            }

            textData.Add(0);
            var output = new byte[24 + (document.Total * 80) + textData.Count];
            Buffer.BlockCopy(WriteName16(string.IsNullOrWhiteSpace(document.Name) ? DefaultMessageFileName : document.Name, encoding), 0, output, 0, 16);
            WriteInt32(output, 16, document.Total);
            WriteInt32(output, 20, document.Used);

            var pos = 24;
            for (var id = 0; id < document.Total; id++)
            {
                var entry = document.Entries[id];
                foreach (var field in MmI16Fields)
                {
                    WriteInt16(output, pos, entry.Get(field));
                    pos += 2;
                }

                var padding = entry.Padding != null && entry.Padding.Length == 14 ? entry.Padding : new byte[14];
                Buffer.BlockCopy(padding, 0, output, pos, 14);
                pos += 14;
                WriteInt32(output, pos, entry.Delay);
                pos += 4;
                var entryOffsets = offsets[id];
                WriteInt32(output, pos, entryOffsets.Animation);
                pos += 4;
                WriteInt32(output, pos, entryOffsets.Sound);
                pos += 4;
                WriteInt32(output, pos, entryOffsets.Title);
                pos += 4;
                WriteInt32(output, pos, entryOffsets.Caption);
                pos += 4;
                WriteInt32(output, pos, entryOffsets.Main);
                pos += 4;
            }

            Buffer.BlockCopy(textData.ToArray(), 0, output, pos, textData.Count);
            return output;
        }

        private static string ReadName16(byte[] bytes, Encoding encoding)
        {
            return (encoding ?? DefaultEncoding).GetString(bytes.Take(16).ToArray()).TrimEnd('\0');
        }

        private static byte[] WriteName16(string name, Encoding encoding)
        {
            var output = new byte[16];
            var encoded = (encoding ?? DefaultEncoding).GetBytes(name ?? string.Empty);
            Buffer.BlockCopy(encoded, 0, output, 0, Math.Min(16, encoded.Length));
            return output;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
        }

        private static void WriteInt16(byte[] buffer, int offset, int value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes((short)value), 0, buffer, offset, 2);
        }

        private static int CountWords(string text)
        {
            var words = 0;
            var inWord = false;
            foreach (var ch in text ?? string.Empty)
            {
                if (char.IsLetter(ch) || (inWord && ch != ' '))
                {
                    if (!inWord)
                    {
                        inWord = true;
                        words++;
                    }
                }
                else if (inWord)
                {
                    inWord = false;
                }
            }

            return words;
        }

        private static string EscapeQuotes(string text)
        {
            return (text ?? string.Empty).Replace("\"", "\\\"");
        }

        private static string UnescapeQuotes(string text)
        {
            return (text ?? string.Empty).Replace("\\\"", "\"");
        }

        private static int FindClosingQuoteIndex(string text, int startIndex = 0)
        {
            if (string.IsNullOrEmpty(text))
            {
                return -1;
            }

            for (var i = startIndex; i < text.Length; i++)
            {
                if (text[i] != '"')
                {
                    continue;
                }

                var backslashCount = 0;
                for (var j = i - 1; j >= 0 && text[j] == '\\'; j--)
                {
                    backslashCount++;
                }

                if ((backslashCount % 2) == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string PrettyBreakMmText(string value)
        {
            return Regex.Replace(value ?? string.Empty, " (?=(@L|@P))", Environment.NewLine);
        }

        private static void PushQuotedBlock(List<string> lines, string key, string value, bool pretty)
        {
            var content = pretty ? PrettyBreakMmText(value) : (value ?? string.Empty);
            var parts = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (parts.Length == 1)
            {
                lines.Add(string.Format("{0}=\"{1}\"", key, EscapeQuotes(parts[0])));
                return;
            }

            lines.Add(string.Format("{0}=\"{1}", key, EscapeQuotes(parts[0])));
            for (var i = 1; i < parts.Length - 1; i++)
            {
                lines.Add(EscapeQuotes(parts[i]));
            }

            lines.Add(EscapeQuotes(parts[parts.Length - 1]) + "\"");
        }

        private static string ReadMessageString(byte[] textBytes, int offset, Encoding encoding)
        {
            if (offset <= 0 || offset >= textBytes.Length)
            {
                return string.Empty;
            }

            var end = offset;
            while (end < textBytes.Length && textBytes[end] != 0)
            {
                end++;
            }

            return (encoding ?? DefaultEncoding).GetString(textBytes.Skip(offset).Take(end - offset).ToArray());
        }

        private static Encoding ResolveEncoding(string encodingName)
        {
            switch ((encodingName ?? string.Empty).Trim())
            {
                case "":
                case "Windows-1252":
                    return DefaultEncoding;
                case "Windows-1250":
                    return Encoding.GetEncoding(1250);
                case "Windows-1251":
                    return Encoding.GetEncoding(1251);
                case "Windows-1253":
                    return Encoding.GetEncoding(1253);
                case "CP949":
                    return Encoding.GetEncoding(949);
                case "Shift_JIS":
                    return Encoding.GetEncoding(932);
                case "c3-tc":
                    return Encoding.GetEncoding(950);
                case "c3-sc":
                    return Encoding.GetEncoding(936);
                default:
                    return Encoding.GetEncoding(encodingName);
            }
        }

        private static List<KeyValuePair<string, string>> ParseKeyValues(string line)
        {
            var output = new List<KeyValuePair<string, string>>();
            var index = 0;
            while (index < line.Length)
            {
                while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
                {
                    index++;
                }

                if (index >= line.Length)
                {
                    break;
                }

                var keyMatch = Regex.Match(line.Substring(index), "^([A-Z0-9_]+)=");
                if (!keyMatch.Success)
                {
                    break;
                }

                var key = keyMatch.Groups[1].Value;
                index += key.Length + 1;

                string value;
                if (index < line.Length && line[index] == '"')
                {
                    index++;
                    var start = index;
                    var endQuoteIndex = FindClosingQuoteIndex(line, index);
                    if (endQuoteIndex < 0)
                    {
                        value = UnescapeQuotes(line.Substring(start));
                        index = line.Length;
                    }
                    else
                    {
                        value = UnescapeQuotes(line.Substring(start, endQuoteIndex - start));
                        index = endQuoteIndex + 1;
                    }
                }
                else
                {
                    var start = index;
                    while (index < line.Length && line[index] != ' ' && line[index] != '\t')
                    {
                        index++;
                    }

                    value = line.Substring(start, index - start);
                }

                output.Add(new KeyValuePair<string, string>(key, value));
            }

            return output;
        }

        private static void SetMessageField(MessageEntry entry, string key, string value)
        {
            var property = key == "SUB_TYPE"
                ? "subtype"
                : Regex.Replace(key.ToLowerInvariant(), "_([a-z])", m => m.Groups[1].Value.ToUpperInvariant());

            int numericValue;
            if (Regex.IsMatch(value ?? string.Empty, @"^-?\d+$") && int.TryParse(value, out numericValue))
            {
                entry.Set(property, numericValue);
            }
            else
            {
                entry.Set(property, value ?? string.Empty);
            }
        }

        private static MessageEntry CreateDefaultMessageEntry(int id)
        {
            return new MessageEntry
            {
                Id = id,
                Padding = new byte[14]
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static List<TextGroupOffset> ReadUsedTextGroupOffsets(byte[] bytes, int indexBase)
        {
            var usedGroups = new List<TextGroupOffset>();
            for (var id = 0; id < TextIndexEntries; id++)
            {
                var baseOffset = indexBase + (id * 8);
                var offset = BitConverter.ToInt32(bytes, baseOffset);
                var used = BitConverter.ToInt32(bytes, baseOffset + 4);
                if (used != 0)
                {
                    usedGroups.Add(new TextGroupOffset { Id = id, Offset = offset });
                }
            }

            usedGroups.Sort((a, b) => a.Id.CompareTo(b.Id));
            return usedGroups;
        }

        private static int DetermineTextDataSize(byte[] data)
        {
            var textSize = data?.Length ?? 0;
            while (textSize > 1 && data[textSize - 1] == 0 && data[textSize - 2] == 0)
            {
                textSize--;
            }

            return textSize;
        }

        private static List<string> ReadNullTerminatedStrings(byte[] data, int startOffset, int endOffset, Encoding encoding)
        {
            var strings = new List<string>();
            var pos = startOffset;
            while (pos < endOffset)
            {
                var start = pos;
                while (pos < endOffset && data[pos] != 0)
                {
                    pos++;
                }

                strings.Add(encoding.GetString(data.Skip(start).Take(pos - start).ToArray()));
                if (pos >= endOffset)
                {
                    break;
                }

                pos++;
            }

            return strings;
        }

        private static void UpdateTextDocumentStatistics(TextEngDocument document)
        {
            if (document == null)
            {
                return;
            }

            if (document.Groups.Count == 0)
            {
                document.GroupCount = 0;
                document.TotalStrings = 0;
                document.TotalWords = 0;
                return;
            }

            document.GroupCount = document.Groups.Keys.Max() + 1;
            document.TotalStrings = document.Groups.Values.Sum(group => group?.Count ?? 0);
            document.TotalWords = document.Groups.Values.Sum(group => (group ?? new List<string>()).Sum(CountWords));
        }

        private static bool IsIgnorableTextLine(string line)
        {
            return string.IsNullOrWhiteSpace(line)
                || line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                || line.TrimStart().StartsWith(";", StringComparison.Ordinal);
        }

        private static string NormalizeMessageInlineText(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        }

        private sealed class TextEngDocument
        {
            public string Name { get; set; }
            public int GroupCount { get; set; }
            public int TotalStrings { get; set; }
            public int TotalWords { get; set; }
            public Dictionary<int, List<string>> Groups { get; } = new Dictionary<int, List<string>>();
        }

        private sealed class TextGroupOffset
        {
            public int Id { get; set; }
            public int Offset { get; set; }
        }

        private sealed class MessageDocument
        {
            public string Name { get; set; }
            public int Total { get; set; }
            public int Used { get; set; }
            public List<MessageEntry> Entries { get; } = new List<MessageEntry>();
        }

        private sealed class MessageOffsets
        {
            public int Animation { get; set; }
            public int Sound { get; set; }
            public int Title { get; set; }
            public int Caption { get; set; }
            public int Main { get; set; }
        }

        private sealed class MessageEntry
        {
            public int Id { get; set; }
            public int Type { get; set; }
            public int Subtype { get; set; }
            public int Data { get; set; }
            public int BoxX { get; set; }
            public int BoxY { get; set; }
            public int BoxW { get; set; }
            public int BoxH { get; set; }
            public int Pic1 { get; set; }
            public int Pic1X { get; set; }
            public int Pic1Y { get; set; }
            public int Pic2 { get; set; }
            public int Pic2X { get; set; }
            public int Pic2Y { get; set; }
            public int TitleX { get; set; }
            public int TitleY { get; set; }
            public int CaptionX { get; set; }
            public int CaptionY { get; set; }
            public int TextX { get; set; }
            public int TextY { get; set; }
            public int AnimX { get; set; }
            public int AnimY { get; set; }
            public byte[] Padding { get; set; }
            public int Delay { get; set; }
            public int AnimationOffset { get; set; }
            public int SoundOffset { get; set; }
            public int TitleOffset { get; set; }
            public int CaptionOffset { get; set; }
            public int MainOffset { get; set; }
            public string Animation { get; set; }
            public string Sound { get; set; }
            public string TitleText { get; set; }
            public string CaptionText { get; set; }
            public string MainText { get; set; }

            public void Set(string key, object value)
            {
                switch (key)
                {
                    case "type": Type = Convert.ToInt32(value); break;
                    case "subtype": Subtype = Convert.ToInt32(value); break;
                    case "data": Data = Convert.ToInt32(value); break;
                    case "boxX": BoxX = Convert.ToInt32(value); break;
                    case "boxY": BoxY = Convert.ToInt32(value); break;
                    case "boxW": BoxW = Convert.ToInt32(value); break;
                    case "boxH": BoxH = Convert.ToInt32(value); break;
                    case "pic1": Pic1 = Convert.ToInt32(value); break;
                    case "pic1X": Pic1X = Convert.ToInt32(value); break;
                    case "pic1Y": Pic1Y = Convert.ToInt32(value); break;
                    case "pic2": Pic2 = Convert.ToInt32(value); break;
                    case "pic2X": Pic2X = Convert.ToInt32(value); break;
                    case "pic2Y": Pic2Y = Convert.ToInt32(value); break;
                    case "titleX": TitleX = Convert.ToInt32(value); break;
                    case "titleY": TitleY = Convert.ToInt32(value); break;
                    case "captionX": CaptionX = Convert.ToInt32(value); break;
                    case "captionY": CaptionY = Convert.ToInt32(value); break;
                    case "textX": TextX = Convert.ToInt32(value); break;
                    case "textY": TextY = Convert.ToInt32(value); break;
                    case "animX": AnimX = Convert.ToInt32(value); break;
                    case "animY": AnimY = Convert.ToInt32(value); break;
                    case "delay": Delay = Convert.ToInt32(value); break;
                    case "animation": Animation = Convert.ToString(value); break;
                    case "sound": Sound = Convert.ToString(value); break;
                    case "titleText": TitleText = Convert.ToString(value); break;
                    case "captionText": CaptionText = Convert.ToString(value); break;
                    case "mainText": MainText = Convert.ToString(value); break;
                }
            }

            public int Get(string key)
            {
                switch (key)
                {
                    case "type": return Type;
                    case "subtype": return Subtype;
                    case "data": return Data;
                    case "boxX": return BoxX;
                    case "boxY": return BoxY;
                    case "boxW": return BoxW;
                    case "boxH": return BoxH;
                    case "pic1": return Pic1;
                    case "pic1X": return Pic1X;
                    case "pic1Y": return Pic1Y;
                    case "pic2": return Pic2;
                    case "pic2X": return Pic2X;
                    case "pic2Y": return Pic2Y;
                    case "titleX": return TitleX;
                    case "titleY": return TitleY;
                    case "captionX": return CaptionX;
                    case "captionY": return CaptionY;
                    case "textX": return TextX;
                    case "textY": return TextY;
                    case "animX": return AnimX;
                    case "animY": return AnimY;
                    default: return 0;
                }
            }
        }
    }
}
