using System;
using System.Collections.Generic;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal sealed class SprAmbientProfileEntry
    {
        public int InternalGroupId { get; set; }
        public int StartImage { get; set; }
        public int EndImage { get; set; }
        public string GroupName { get; set; }
        public string SubgroupBaseName { get; set; }
        public string[] DirectionNames { get; set; }

        public int Length => EndImage - StartImage + 1;

        public bool Matches(ImageRecord record, StructuralSubgroup subgroup)
        {
            if (record == null || subgroup == null)
            {
                return false;
            }

            return subgroup.PhysicalOrder - 1 == InternalGroupId
                && record.Index >= StartImage
                && record.Index <= EndImage
                && subgroup.StartImage == StartImage
                && subgroup.EndImage >= EndImage;
        }

        public bool TryGetDirectionalSubgroupName(ImageRecord record, out string subgroupName)
        {
            return SpriteProfileCommon.TryGetDirectionalSubgroupName(record, StartImage, Length, SubgroupBaseName, DirectionNames, out subgroupName);
        }
    }

    internal static class SprAmbientProfile
    {
        private static readonly IReadOnlyList<SprAmbientProfileEntry> Entries = new[]
        {
            Entry(0, 1, 36, "CargoFlotsam", "Flotsam"),
            Entry(1, 37, 49, "ShadNE", "NE"),
            Entry(2, 50, 62, "ShadSE", "SE"),
            Entry(3, 63, 75, "ShadSW", "SW"),
            Entry(4, 76, 88, "ShadNW", "NW"),
            Entry(5, 89, 103, "Smoksml", "Smoke"),
            Entry(6, 104, 139, "Dancers", "Dance"),
            Entry(7, 140, 165, "JugglerAlone", "Juggle"),
            Entry(8, 166, 190, "Fish_Jumping", "Jump"),
            Entry(9, 191, 202, "musicians2", "Perform"),
            Entry(10, 203, 214, "musicians1", "Perform"),
            Entry(11, 215, 237, "Bubbles", "Bubble"),
            Entry(12, 238, 257, "Smokbig", "Smoke"),
            Entry(13, 258, 353, "Bedouin", "Walk", SpriteProfileCommon.EightWayDirections),
            Entry(14, 354, 361, "Bedouin", "Die"),
            Entry(15, 362, 457, "Bedouin", "Idle", SpriteProfileCommon.EightWayDirections),
            Entry(16, 458, 469, "TempleWorship", "Worship"),
            Entry(17, 470, 565, "Javelin", "Walk", SpriteProfileCommon.EightWayDirections),
            Entry(18, 566, 573, "Javelin", "Die"),
            Entry(19, 574, 669, "Javelin", "Attack", SpriteProfileCommon.EightWayDirections),
            Entry(20, 670, 765, "DonkeyGuy", "Walk", SpriteProfileCommon.EightWayDirections),
            Entry(21, 766, 773, "DonkeyGuy", "Die"),
            Entry(22, 774, 869, "Hippo", "Walk", SpriteProfileCommon.EightWayDirections),
            Entry(23, 870, 877, "Hippo", "Die"),
            Entry(24, 878, 933, "Hippo", "Attack", SpriteProfileCommon.EightWayDirections),
            Entry(25, 934, 1029, "Hippo", "Swim", SpriteProfileCommon.EightWayDirections),
            Entry(26, 1030, 1125, "Hippo", "SwimAttack", SpriteProfileCommon.EightWayDirections),
            Entry(27, 1126, 1221, "Hippo", "SwimIdle", SpriteProfileCommon.EightWayDirections),
            Entry(28, 1222, 1317, "Hippo", "Eat", SpriteProfileCommon.EightWayDirections),
            Entry(29, 1318, 1354, "HippoDance", "Dance"),
            Entry(30, 1355, 1450, "Antelope", "Walk", SpriteProfileCommon.EightWayDirections),
            Entry(31, 1451, 1458, "Antelope", "Die"),
            Entry(32, 1459, 1554, "Antelope", "Move", SpriteProfileCommon.EightWayDirections),
            Entry(33, 1555, 1682, "Antelope", "EatIdle", SpriteProfileCommon.EightWayDirections),
            Entry(34, 1683, 1778, "Antelope", "Idle", SpriteProfileCommon.EightWayDirections),
            Entry(35, 1779, 1842, "Antelope", "Run", SpriteProfileCommon.EightWayDirections),
            Entry(36, 1843, 1938, "Hunter_Antelope", "Walk", SpriteProfileCommon.EightWayDirections),
            Entry(37, 1939, 1946, "Hunter_Antelope", "Die"),
            Entry(38, 1947, 2042, "Hunter_Antelope", "Hunt", SpriteProfileCommon.EightWayDirections),
            Entry(39, 2043, 2138, "Hunter_Antelope", "Fight", SpriteProfileCommon.EightWayDirections),
            Entry(40, 2139, 2234, "Hunter_Antelope", "FightPacked", SpriteProfileCommon.EightWayDirections),
            Entry(41, 2235, 2378, "Hunter_Antelope", "Pack", SpriteProfileCommon.EightWayDirections),
            Entry(42, 2379, 2474, "Hunter_Antelope", "MovePack", SpriteProfileCommon.EightWayDirections),
            Entry(43, 2475, 2506, "javelinShadow", "Shadow", SpriteProfileCommon.ThirtyTwoDirections),
            Entry(44, 2507, 2538, "javelin_itself", "Projectile", SpriteProfileCommon.ThirtyTwoDirections),
            Entry(45, 2539, 2550, "YardWar", "Work"),
            Entry(46, 2551, 2646, "FishWharf", "Work", SpriteProfileCommon.FourWayDirections),
            Entry(47, 2647, 2670, "GrainScribe", "Work"),
            Entry(48, 2671, 2687, "Mining", "Work"),
            Entry(49, 2688, 2694, "QuarryWorker1", "Work"),
            Entry(50, 2695, 2701, "QuarryWorker2", "Work"),
            Entry(51, 2702, 2717, "Warehouse", "Work"),
            Entry(52, 2718, 2729, "YardCargo", "Work"),
            Entry(53, 2730, 2741, "YardFerry", "Work"),
            Entry(54, 2742, 2753, "YardReed", "Work"),
            Entry(55, 2754, 2853, "DockDude", "Wait", SpriteProfileCommon.FourWayDirections),
            Entry(56, 2854, 2933, "DockDude", "Work", SpriteProfileCommon.FourWayDirections),
        };

        public static bool IsMatch(SgContainer container)
        {
            if (container?.Bitmaps == null || container.StructuralSubgroups == null)
            {
                return false;
            }

            var names = new HashSet<string>(
                container.Bitmaps
                    .Select(x => x?.FileName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            return container.StructuralSubgroups.Count == 57
                && names.Contains("CargoFlotsam")
                && names.Contains("Bedouin")
                && names.Contains("DockDude")
                && names.Contains("Hunter_Antelope");
        }

        public static SprAmbientProfileEntry FindEntry(SgContainer container, ImageRecord record, StructuralSubgroup subgroup)
        {
            if (!IsMatch(container) || record == null || subgroup == null)
            {
                return null;
            }

            return Entries.FirstOrDefault(x => x.Matches(record, subgroup));
        }

        public static string GetSubgroupName(SprAmbientProfileEntry entry, ImageRecord record)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            string subgroupName;
            if (entry.TryGetDirectionalSubgroupName(record, out subgroupName))
            {
                return subgroupName;
            }

            return entry.SubgroupBaseName;
        }

        public static int GetDirectionCount(SprAmbientProfileEntry entry)
        {
            return entry?.DirectionNames?.Length ?? 0;
        }

        public static int GetFramesPerDirection(SprAmbientProfileEntry entry)
        {
            var directionCount = GetDirectionCount(entry);
            if (entry == null || directionCount <= 0 || entry.Length % directionCount != 0)
            {
                return 0;
            }

            return entry.Length / directionCount;
        }

        private static SprAmbientProfileEntry Entry(int internalGroupId, int startImage, int endImage, string groupName, string subgroupBaseName, string[] directionNames = null)
        {
            return new SprAmbientProfileEntry
            {
                InternalGroupId = internalGroupId,
                StartImage = startImage,
                EndImage = endImage,
                GroupName = groupName,
                SubgroupBaseName = subgroupBaseName,
                DirectionNames = directionNames
            };
        }
    }
}
