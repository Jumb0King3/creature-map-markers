using ProtoBuf;

namespace CreatureMarkers
{
    /// <summary>What a creature is. Sent once when a player first sees it, and again only if it changes.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class CreatureInfo
    {
        public long Id;
        public string Code;
        public string Name;
        public int Category;
        public float MaxHealth;
        public string Owner;
    }

    /// <summary>
    /// Changes since the last update, for one player. Server to client, every update tick (an empty one
    /// still tells the client the server is alive).
    /// Positions are parallel arrays (MovedIds[i] is at MovedX[i], MovedY[i], MovedZ[i] with MovedHealth[i]):
    /// only creatures that moved or whose health changed are listed.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class CreatureUpdatePacket
    {
        /// <summary>Forget everything known before applying this update.</summary>
        public bool Full;

        public CreatureInfo[] Added;

        public long[] MovedIds;
        public double[] MovedX;
        public float[] MovedY;
        public double[] MovedZ;
        public float[] MovedHealth;
        public bool[] MovedUnderground;

        public long[] RemovedIds;

        /// <summary>Whether the player receiving this is underground (below the original terrain surface).</summary>
        public bool PlayerUnderground;
    }

    /// <summary>Server settings and this player's rights. Server to client, on join and after a change.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class CreatureSettingsPacket
    {
        public bool Allowed;
        public bool IsAdmin;
        public int UpdateIntervalMs;
        public int MaxDistance;
        public bool AdminOnly;
        public bool ShowCritters;
        public bool AutoDetectPredators;
    }

    /// <summary>New server settings from an admin's settings window. Client to server.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SaveCreatureSettingsPacket
    {
        public int UpdateIntervalMs;
        public int MaxDistance;
        public bool AdminOnly;
        public bool ShowCritters;
        public bool AutoDetectPredators;
    }
}
