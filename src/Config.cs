using System;
using System.Globalization;

namespace CreatureMarkers
{
    /// <summary>The kinds of creature, each drawn on its own map layer.</summary>
    public static class CreatureCategory
    {
        public const int Monsters = 0;
        public const int Predators = 1;
        public const int Animals = 2;
        public const int Traders = 3;
        public const int Villagers = 4;
        public const int Critters = 5;
        public const int Pets = 6;
        public const int Count = 7;

        public static readonly string[] Keys = { "monsters", "predators", "animals", "traders", "villagers", "critters", "pets" };
    }

    /// <summary>Server settings: ModConfig/CreatureMarkersServerConfig.json.</summary>
    public class ServerConfig
    {
        public const int MinIntervalMs = 500;
        public const int MaxIntervalMs = 10000;

        /// <summary>How often creature positions are checked and changes sent, in milliseconds (500 to 10000).</summary>
        public int UpdateIntervalMs = 2000;

        /// <summary>Only show creatures within this many blocks of each player. 0 = everything the server has loaded.</summary>
        public int MaxDistance = 0;

        /// <summary>Only players with the controlserver privilege (admins) see creatures.</summary>
        public bool AdminOnly = false;

        /// <summary>Also send fish, butterflies and other small critters (there can be a lot of them).</summary>
        public bool ShowCritters = false;

        /// <summary>Creature codes counted as monsters. Matches the start of the code, or any part after a dash.</summary>
        public string[] MonsterCodes = { "drifter", "locust", "bell", "shiver", "bowtorn", "eidolon", "erel", "nightmare", "deepdrifter", "corrupt" };

        /// <summary>
        /// Creatures whose AI attacks players on sight (not only after being hurt) are counted as predators
        /// automatically, from any mod. These codes are counted as predators as well.
        /// </summary>
        public string[] PredatorCodes = { "wolf", "bear", "hyena", "lion", "tiger", "panther", "jaguar", "leopard", "crocodile", "alligator", "wolverine", "coyote", "jackal" };

        /// <summary>Find predators automatically from their AI (attacks players without being provoked).</summary>
        public bool AutoDetectPredators = true;

        /// <summary>Tamed animals and pets. Anything with an owner is counted as a pet too.</summary>
        public string[] PetCodes = { "tame-", "dog", "cat-" };

        /// <summary>Creature codes counted as small critters (sent only when ShowCritters is on).</summary>
        public string[] CritterCodes = { "butterfly", "fish", "moth", "firefly", "bee", "dragonfly" };

        /// <summary>Anything this many blocks or more below the original terrain surface counts as underground.</summary>
        public int UndergroundDepth = 3;

        /// <summary>Creature codes never shown.</summary>
        public string[] ExcludeCodes = { "armorstand", "strawdummy", "mannequin" };

        public void Clamp()
        {
            UpdateIntervalMs = Math.Max(MinIntervalMs, Math.Min(MaxIntervalMs, UpdateIntervalMs));
            MaxDistance = Math.Max(0, MaxDistance);
            UndergroundDepth = Math.Max(1, Math.Min(64, UndergroundDepth));
            if (MonsterCodes == null) MonsterCodes = new string[0];
            if (PredatorCodes == null) PredatorCodes = new string[0];
            if (PetCodes == null) PetCodes = new string[0];
            if (CritterCodes == null) CritterCodes = new string[0];
            if (ExcludeCodes == null) ExcludeCodes = new string[0];
        }
    }

    /// <summary>Each player's own display settings: ModConfig/CreatureMarkersConfig.json.</summary>
    public class ClientConfig
    {
        /// <summary>Marker size in GUI pixels (4 to 32).</summary>
        public int MarkerSize = 9;

        public string MonsterColorHex = "#E0302A";
        public string PredatorColorHex = "#FF8C1A";
        public string AnimalColorHex = "#7BD14B";
        public string TraderColorHex = "#F2D33B";
        public string VillagerColorHex = "#4FB4FF";
        public string CritterColorHex = "#B98CFF";
        public string PetColorHex = "#FF7EC8";
        public string BorderColorHex = "#141414";

        /// <summary>Draw a small health bar under creature markers.</summary>
        public bool ShowHealthBars = true;

        /// <summary>Only draw the bar when the creature has lost health (keeps the map tidy).</summary>
        public bool HealthBarsOnlyWhenHurt = true;

        /// <summary>Search terms, separated by commas (matched against the creature's name and code). Empty = no filter.</summary>
        public string Filter = "";

        /// <summary>With a filter: true hides everything else, false highlights matches and fades the rest.</summary>
        public bool FilterOnlyMatches = false;

        /// <summary>Only show creatures on your level: surface creatures while you're on the surface, underground ones while you're underground.</summary>
        public bool OnlyMyLevel = true;

        /// <summary>Draw a dotted line from you to the creature you are tracking.</summary>
        public bool ShowTrackingLine = true;

        public string ColorFor(int category)
        {
            switch (category)
            {
                case CreatureCategory.Monsters: return MonsterColorHex;
                case CreatureCategory.Predators: return PredatorColorHex;
                case CreatureCategory.Animals: return AnimalColorHex;
                case CreatureCategory.Traders: return TraderColorHex;
                case CreatureCategory.Villagers: return VillagerColorHex;
                case CreatureCategory.Pets: return PetColorHex;
                default: return CritterColorHex;
            }
        }

        public void SetColor(int category, string hex)
        {
            switch (category)
            {
                case CreatureCategory.Monsters: MonsterColorHex = hex; break;
                case CreatureCategory.Predators: PredatorColorHex = hex; break;
                case CreatureCategory.Animals: AnimalColorHex = hex; break;
                case CreatureCategory.Traders: TraderColorHex = hex; break;
                case CreatureCategory.Villagers: VillagerColorHex = hex; break;
                case CreatureCategory.Pets: PetColorHex = hex; break;
                default: CritterColorHex = hex; break;
            }
        }

        public void Clamp()
        {
            MarkerSize = Math.Max(4, Math.Min(32, MarkerSize));
            if (Filter == null) Filter = "";
            for (int i = 0; i < CreatureCategory.Count; i++)
            {
                if (ColorHex.Normalize(ColorFor(i)) == null) SetColor(i, new ClientConfig().ColorFor(i));
            }
            if (ColorHex.Normalize(BorderColorHex) == null) BorderColorHex = "#141414";
        }
    }

    /// <summary>Hex colour helpers. Accepts #RGB, #RRGGBB and #RRGGBBAA, with or without the #.</summary>
    public static class ColorHex
    {
        public static bool TryParse(string hex, out double r, out double g, out double b, out double a)
        {
            r = g = b = 0;
            a = 1;

            string h = Normalize(hex);
            if (h == null) return false;

            r = Convert.ToInt32(h.Substring(1, 2), 16) / 255.0;
            g = Convert.ToInt32(h.Substring(3, 2), 16) / 255.0;
            b = Convert.ToInt32(h.Substring(5, 2), 16) / 255.0;
            if (h.Length == 9) a = Convert.ToInt32(h.Substring(7, 2), 16) / 255.0;
            return true;
        }

        /// <summary>Returns the colour as upper-case #RRGGBB or #RRGGBBAA, or null if it isn't a valid colour.</summary>
        public static string Normalize(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;

            string h = hex.Trim();
            if (h.StartsWith("#")) h = h.Substring(1);

            if (h.Length == 3)
            {
                h = new string(new char[] { h[0], h[0], h[1], h[1], h[2], h[2] });
            }

            if (h.Length != 6 && h.Length != 8) return null;

            foreach (char c in h)
            {
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return null;
            }

            return "#" + h.ToUpper(CultureInfo.InvariantCulture);
        }
    }
}
