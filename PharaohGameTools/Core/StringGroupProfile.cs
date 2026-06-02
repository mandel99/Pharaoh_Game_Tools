using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PharaohGameTools.Core
{
    internal sealed class StringGroupProfileEntry
    {
        public string FileName { get; set; }
        public string GroupName { get; set; }
        public int InternalGroupStart { get; set; }
        public int InternalGroupEnd { get; set; }
        public int ImageStart { get; set; }
        public int ImageEnd { get; set; }

        public bool Matches(string fileName, ImageRecord record, StructuralSubgroup subgroup)
        {
            if (record == null || subgroup == null)
            {
                return false;
            }

            var subgroupId = subgroup.PhysicalOrder - 1;
            return string.Equals(FileName, fileName, StringComparison.OrdinalIgnoreCase)
                && subgroupId >= InternalGroupStart
                && subgroupId <= InternalGroupEnd
                && record.Index >= ImageStart
                && record.Index <= ImageEnd;
        }

        public string GetSubgroupName(StructuralSubgroup subgroup, ImageRecord record)
        {
            if (subgroup == null || record == null)
            {
                return string.Empty;
            }

            var subgroupLength = subgroup.EndImage - subgroup.StartImage + 1;
            var directionCount = GetDirectionCount(subgroup, subgroupLength);
            if (directionCount == 32)
            {
                var directionIndex = Math.Max(0, Math.Min(31, record.Index - subgroup.StartImage));
                return "Dir" + (directionIndex + 1).ToString("D2");
            }

            var baseName = GetSubgroupBaseName(subgroup, subgroupLength);
            if (directionCount == 8)
            {
                var directionIndex = Math.Abs(record.Index - subgroup.StartImage) % SpriteProfileCommon.EightWayDirections.Length;
                if (baseName.StartsWith("part_", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = string.Empty;
                }

                return SpriteProfileCommon.FormatDirectionalName(baseName, SpriteProfileCommon.EightWayDirections[directionIndex]);
            }

            return baseName;
        }

        public int GetDirectionCount(StructuralSubgroup subgroup, int subgroupLength)
        {
            if (subgroupLength <= 1)
            {
                return 0;
            }

            if (IsExpeditionCartGroup() && subgroupLength >= 8 && subgroupLength <= 9)
            {
                return 8;
            }

            if (IsResourceCartGroup() && subgroupLength >= 8 && subgroupLength <= 9)
            {
                return 8;
            }

            if ((GroupName.IndexOf("arrow", StringComparison.OrdinalIgnoreCase) >= 0
                    || GroupName.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                && subgroupLength == 32)
            {
                return 32;
            }

            if (IsEightDirectionStaticGroup() && subgroupLength >= 8 && subgroupLength <= 9)
            {
                return 8;
            }

            if (IsBoatLikeGroup()
                && subgroup != null
                && subgroup.PhysicalOrder - 1 > InternalGroupStart
                && subgroupLength >= 8
                && subgroupLength <= 9)
            {
                return 8;
            }

            return subgroupLength > 8 && subgroupLength % 8 == 0 ? 8 : 0;
        }

        public bool ShouldAnimate(StructuralSubgroup subgroup, int subgroupLength)
        {
            if (IsExpeditionCartGroup())
            {
                return false;
            }

            if (IsResourceCartGroup())
            {
                return false;
            }

            var directionCount = GetDirectionCount(subgroup, subgroupLength);
            if (directionCount == 32 && subgroupLength == 32)
            {
                return false;
            }

            if (directionCount == 8)
            {
                return subgroupLength / 8 > 1;
            }

            return subgroupLength > 1;
        }

        private string GetSubgroupBaseName(StructuralSubgroup subgroup, int subgroupLength)
        {
            var subgroupId = subgroup.PhysicalOrder - 1;
            var relativeIndex = (subgroupId - InternalGroupStart) + 1;

            if (IsBirdGroup())
            {
                return relativeIndex == 1 ? "flying" : "Die";
            }

            if (IsPeasantGroup())
            {
                switch (relativeIndex)
                {
                    case 1: return "Walk";
                    case 2: return "Die";
                    case 3: return "Work";
                    case 4: return "Seeding";
                    case 5: return "Harvest";
                    case 6: return "SledPull";
                    case 7: return "SledPull_02";
                }
            }

            if (IsFishingBoatGroup())
            {
                switch (relativeIndex)
                {
                    case 1: return "Sail";
                    case 2: return "Fishing";
                    case 3: return "Die";
                    case 4: return string.Empty;
                    case 5: return "Idle";
                    case 6: return "Idle_02";
                }
            }

            if (IsLocustGroup())
            {
                return "swarm_" + relativeIndex.ToString("D2");
            }

            if (IsResourceCartGroup())
            {
                return GetResourceCartSubgroupName(subgroupId);
            }

            if (IsExpeditionCartGroup())
            {
                return GetExpeditionCartSubgroupName(subgroupId);
            }

            if (IsEightDirectionStaticGroup())
            {
                return string.Empty;
            }

            if (IsBoatLikeGroup())
            {
                if (relativeIndex == 2 && subgroupLength == 11)
                {
                    return "Die";
                }

                if (string.Equals(GroupName, "Trade_Boat_Worker", StringComparison.OrdinalIgnoreCase)
                    && relativeIndex == 3
                    && subgroupLength >= 8
                    && subgroupLength <= 9)
                {
                    return "Idle";
                }

                if (subgroupLength >= 8 && subgroupLength <= 9)
                {
                    return string.Empty;
                }
            }

            if (InternalGroupStart != InternalGroupEnd && subgroupLength <= 9)
            {
                return "Die";
            }

            if (InternalGroupStart == InternalGroupEnd)
            {
                return subgroupLength <= 1 ? string.Empty : "part_01";
            }

            return "part_" + relativeIndex.ToString("D2");
        }

        private bool IsBoatLikeGroup()
        {
            return GroupName.IndexOf("Boat", StringComparison.OrdinalIgnoreCase) >= 0
                || GroupName.IndexOf("Transport", StringComparison.OrdinalIgnoreCase) >= 0
                || GroupName.IndexOf("Ferry", StringComparison.OrdinalIgnoreCase) >= 0
                || GroupName.IndexOf("tradeship", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsLocustGroup()
        {
            return GroupName.IndexOf("locust", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsBirdGroup()
        {
            return string.Equals(GroupName, "Birds", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPeasantGroup()
        {
            return string.Equals(GroupName, "Peasant", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsResourceCartGroup()
        {
            return string.Equals(GroupName, "ResourceCart", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsExpeditionCartGroup()
        {
            return string.Equals(GroupName, "cart_exp", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsFishingBoatGroup()
        {
            return string.Equals(GroupName, "FishingBoat", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsEightDirectionStaticGroup()
        {
            return GroupName.IndexOf("sled_", StringComparison.OrdinalIgnoreCase) >= 0
                || GroupName.IndexOf("sled/scraper", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string GetResourceCartSubgroupName(int subgroupId)
        {
            switch (subgroupId)
            {
                case 77: return "Empty";
                case 89: return "Bricks";
                case 91: return "Barley";
                case 92: return "Beer";
                case 93: return "Flax";
                case 95: return "Gems";
                case 97: return "Timber";
                case 98: return "Gold";
                case 99: return "Reeds";
                case 100: return "Papyrus";
                case 101: return "Stone";
                case 103: return "Granite";
                case 104: return "Limestone";
                case 107: return "Copper";
                case 108: return "Cart";
                default:
                    return "Cart_" + (subgroupId - InternalGroupStart + 1).ToString("D2");
            }
        }

        private string GetExpeditionCartSubgroupName(int subgroupId)
        {
            switch (subgroupId)
            {
                case 15: return "Empty";
                case 16: return "Copper";
                case 17: return "Gold";
                case 18: return "Gems";
                case 19: return "Weapons";
                default:
                    return "Cargo_" + (subgroupId - InternalGroupStart + 1).ToString("D2");
            }
        }
    }

    internal static class StringGroupProfile
    {
        private static readonly IReadOnlyList<StringGroupProfileEntry> Entries = new[]
        {
            Entry("SprMain.sg3", "hunter_arrow", 0, 0, 1, 32),
            Entry("SprMain.sg3", "hunter_arrow_shadow", 1, 1, 33, 64),
            Entry("SprMain.sg3", "Emmigrant", 2, 3, 65, 168),
            Entry("SprMain.sg3", "Architect", 4, 5, 169, 272),
            Entry("SprMain.sg3", "Fireman", 6, 8, 273, 672),
            Entry("SprMain.sg3", "GranBoy", 9, 10, 673, 776),
            Entry("SprMain.sg3", "hunter_stick", 11, 11, 777, 781),
            Entry("SprMain.sg3", "Vagrant", 12, 13, 782, 885),
            Entry("SprMain.sg3", "Immigrant", 14, 15, 886, 989),
            Entry("SprMain.sg3", "MarketBuyer", 16, 17, 990, 1093),
            Entry("SprMain.sg3", "MarketTrader", 18, 19, 1094, 1197),
            Entry("SprMain.sg3", "Policeman", 20, 22, 1198, 1373),
            Entry("SprMain.sg3", "Croc", 23, 27, 1374, 1709),
            Entry("SprMain.sg3", "Pharaoh", 28, 28, 1710, 1805),
            Entry("SprMain.sg3", "Rioter", 29, 31, 1806, 1973),
            Entry("SprMain.sg3", "Thief", 32, 33, 1974, 2077),
            Entry("SprMain.sg3", "Transport", 34, 36, 2078, 2128),
            Entry("SprMain.sg3", "Taxman", 37, 38, 2129, 2232),
            Entry("SprMain.sg3", "Gatherer", 39, 40, 2233, 2448),
            Entry("SprMain.sg3", "TaxCollector", 41, 42, 2449, 2552),
            Entry("SprMain.sg3", "CartPusher", 43, 44, 2553, 2656),
            Entry("SprMain.sg3", "Hunter_Ostrich", 45, 51, 2657, 3288),
            Entry("SprMain.sg3", "Donkey", 52, 53, 3289, 3392),
            Entry("SprMain.sg3", "Wall_Guy", 54, 56, 3393, 3592),
            Entry("SprMain.sg3", "Teacher/Librarian", 57, 58, 3593, 3696),
            Entry("SprMain.sg3", "WaterCarrier", 59, 60, 3697, 3800),
            Entry("SprMain.sg3", "Leg_Miss", 61, 63, 3801, 4000),
            Entry("SprMain.sg3", "Leg_Aux", 64, 66, 4001, 4168),
            Entry("SprMain.sg3", "Leg_Heavy", 67, 70, 4169, 4376),
            Entry("SprMain.sg3", "Doctor", 71, 72, 4377, 4480),
            Entry("SprMain.sg3", "Lumberjack", 73, 76, 4481, 4776),
            Entry("SprMain.sg3", "ResourceCart", 77, 108, 4777, 5416),
            Entry("SprMain.sg3", "BrickWalker", 109, 112, 5417, 5776),
            Entry("SprMain.sg3", "Leg_Horse", 113, 113, 5777, 5872),
            Entry("SprMain.sg3", "Birds", 114, 115, 5873, 5922),
            Entry("SprMain.sg3", "Peasant", 116, 122, 5923, 6546),
            Entry("SprMain.sg3", "Trade_Boat", 118, 118, 6027, 6130),
            Entry("SprMain.sg3", "DockPusher", 119, 119, 6131, 6234),
            Entry("SprMain.sg3", "DanceWalker", 120, 120, 6235, 6354),
            Entry("SprMain.sg3", "Trade_Boat_Worker", 123, 125, 6547, 6597),
            Entry("SprMain.sg3", "CartPusher", 126, 127, 6598, 6701),
            Entry("SprMain.sg3", "Dancer", 128, 129, 6702, 6805),
            Entry("SprMain.sg3", "Juggler", 130, 131, 6806, 6909),
            Entry("SprMain.sg3", "SenetPlayer", 132, 133, 6910, 7013),
            Entry("SprMain.sg3", "FishingBoat", 134, 137, 7014, 7135),
            Entry("SprMain.sg3", "Ferry_Boat", 138, 140, 7137, 7187),
            Entry("SprMain.sg3", "War_Boat", 141, 144, 7188, 7246),
            Entry("SprMain.sg3", "Carpenter", 145, 146, 7247, 7350),
            Entry("SprMain.sg3", "StoneWalker", 147, 155, 7351, 7822),
            Entry("SprMain.sg3", "Ostrich", 156, 160, 7823, 8110),
            Entry("SprMain.sg3", "Hyena", 161, 165, 8111, 8356),
            Entry("SprMain.sg3", "sled/scraper_variants", 166, 179, 8357, 8468),
            Entry("SprMain.sg3", "Apothecary", 180, 181, 8469, 8572),
            Entry("SprMain.sg3", "Dentist", 182, 183, 8573, 8676),
            Entry("SprMain.sg3", "HunterBird", 184, 186, 8677, 8876),
            Entry("SprMain.sg3", "Ptah", 187, 188, 8877, 8980),
            Entry("SprMain.sg3", "Governor", 189, 190, 8981, 9084),
            Entry("SprMain.sg3", "MusicWalker", 191, 192, 9085, 9188),
            Entry("SprMain.sg3", "Seth", 193, 194, 9189, 9292),
            Entry("SprMain.sg3", "Embalmer", 195, 196, 9293, 9396),
            Entry("SprMain.sg3", "Osiris", 197, 198, 9397, 9500),
            Entry("SprMain.sg3", "Scribe", 199, 200, 9501, 9604),
            Entry("SprMain.sg3", "Teacher", 201, 202, 9605, 9708),
            Entry("SprMain.sg3", "Diseased", 203, 205, 9709, 9908),
            Entry("SprMain.sg3", "Walker/LaborSeeker", 206, 207, 9909, 10012),
            Entry("SprMain.sg3", "Bast/Noble", 208, 209, 10013, 10116),
            Entry("SprMain.sg3", "Ra", 210, 211, 10117, 10220),
            Entry("SprMain.sg3", "Magistrate", 212, 213, 10221, 10325),

            Entry("SprMain2.sg3", "asp", 0, 4, 1, 390),
            Entry("SprMain2.sg3", "Lion", 5, 9, 391, 726),
            Entry("SprMain2.sg3", "scorpion", 10, 14, 727, 972),
            Entry("SprMain2.sg3", "cart_exp", 15, 19, 973, 1076),
            Entry("SprMain2.sg3", "zookeeper", 20, 21, 1077, 1180),
            Entry("SprMain2.sg3", "frog", 22, 26, 1181, 1474),
            Entry("SprMain2.sg3", "sled_copper", 27, 27, 1475, 1482),
            Entry("SprMain2.sg3", "tombrobber", 28, 29, 1483, 1585),
            Entry("SprMain2.sg3", "locust", 30, 34, 1586, 1615),
            Entry("SprMain2.sg3", "artist", 35, 38, 1616, 1910),
            Entry("SprMain2.sg3", "Sled_Lamps", 39, 39, 1911, 1918),
            Entry("SprMain2.sg3", "mummy", 40, 42, 1919, 2198),
            Entry("SprMain2.sg3", "Blood_Transport", 43, 45, 2199, 2249),
            Entry("SprMain2.sg3", "Blood_Ferry", 46, 48, 2250, 2300),
            Entry("SprMain2.sg3", "Blood_tradeship", 49, 51, 2301, 2352),
        };

        public static bool TryResolve(SgContainer container, ImageRecord record, StructuralSubgroup subgroup, out StringGroupProfileEntry entry)
        {
            entry = null;
            if (container == null || record == null || subgroup == null)
            {
                return false;
            }

            var fileName = Path.GetFileName(container.SourcePath) ?? string.Empty;
            entry = Entries.FirstOrDefault(x => x.Matches(fileName, record, subgroup));
            return entry != null;
        }

        public static string GetSubgroupName(StringGroupProfileEntry entry, StructuralSubgroup subgroup, ImageRecord record)
        {
            return entry == null ? string.Empty : entry.GetSubgroupName(subgroup, record);
        }

        public static bool ShouldAnimate(StringGroupProfileEntry entry, StructuralSubgroup subgroup)
        {
            if (entry == null || subgroup == null)
            {
                return false;
            }

            return entry.ShouldAnimate(subgroup, subgroup.EndImage - subgroup.StartImage + 1);
        }

        public static int GetDirectionCount(StringGroupProfileEntry entry, StructuralSubgroup subgroup)
        {
            if (entry == null || subgroup == null)
            {
                return 0;
            }

            return entry.GetDirectionCount(subgroup, subgroup.EndImage - subgroup.StartImage + 1);
        }

        private static StringGroupProfileEntry Entry(string fileName, string groupName, int internalGroupStart, int internalGroupEnd, int imageStart, int imageEnd)
        {
            return new StringGroupProfileEntry
            {
                FileName = fileName,
                GroupName = groupName,
                InternalGroupStart = internalGroupStart,
                InternalGroupEnd = internalGroupEnd,
                ImageStart = imageStart,
                ImageEnd = imageEnd
            };
        }
    }
}
