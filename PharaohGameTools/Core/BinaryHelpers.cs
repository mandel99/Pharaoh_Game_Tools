using System;
using System.IO;
using System.Text;

namespace PharaohGameTools.Core
{
    internal static class BinaryHelpers
    {
        public static string ReadCString(byte[] buffer, int offset, int length)
        {
            var end = offset;
            var max = offset + length;
            while (end < max && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        }

        public static string SanitizeFolderName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "Unnamed";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = input.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                foreach (var c in invalid)
                {
                    if (chars[i] == c)
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            var result = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(result) ? "Unnamed" : result;
        }

        public static string NormalizeStem(string pathOrName)
        {
            var stem = Path.GetFileNameWithoutExtension(pathOrName) ?? string.Empty;
            var builder = new StringBuilder(stem.Length);
            foreach (var ch in stem)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }

        public static int DataStart(ImageRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            return record.Flags != null && record.Flags.Length > 0 && record.Flags[0] != 0
                ? checked((int)record.Offset - 1)
                : checked((int)record.Offset);
        }

        public static bool IsDummyRecord(ImageRecord record)
        {
            return record != null
                && record.Index == 0
                && record.Offset == 0
                && record.Length == 0
                && record.UncompressedLength == 0
                && record.Width == 0
                && record.Height == 0
                && record.Type == 0;
        }

        public static byte[] Slice(byte[] data, int offset, int length)
        {
            var output = new byte[length];
            Buffer.BlockCopy(data, offset, output, 0, length);
            return output;
        }

        public static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
