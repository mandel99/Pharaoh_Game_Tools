using System;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal static class SpriteProfileCommon
    {
        public static readonly string[] EightWayDirections = { "NE", "E", "SE", "S", "SW", "W", "NW", "N" };
        public static readonly string[] FourWayDirections = { "Dir01", "Dir02", "Dir03", "Dir04" };
        public static readonly string[] ThirtyTwoDirections = Enumerable.Range(1, 32).Select(x => "Dir" + x.ToString("D2")).ToArray();

        public static string FormatDirectionalName(string baseName, string directionName)
        {
            return string.IsNullOrWhiteSpace(baseName)
                ? directionName
                : baseName + "_" + directionName;
        }

        public static bool TryGetDirectionalSubgroupName(ImageRecord record, int startImage, int length, string subgroupBaseName, string[] directionNames, out string subgroupName)
        {
            subgroupName = null;
            if (record == null || directionNames == null || directionNames.Length == 0)
            {
                return false;
            }

            var relativeIndex = record.Index - startImage;
            if (relativeIndex < 0 || relativeIndex >= length || length % directionNames.Length != 0)
            {
                return false;
            }

            subgroupName = FormatDirectionalName(subgroupBaseName, directionNames[relativeIndex % directionNames.Length]);
            return true;
        }
    }
}
