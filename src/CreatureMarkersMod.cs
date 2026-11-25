using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreatureMarkers
{
    /// <summary>A creature the client knows about: what it is (sent once) and where it is (sent when it moves).</summary>
    public class KnownCreature
    {
        public long Id;
        public string Code;
        public string Name;
        public int Category;
        public float MaxHealth;
        public string Owner;
        public double X;
        public double Y;
        public double Z;
        public float Health;
        public bool Underground;
        public bool HasPosition;

        /// <summary>Lower-case name and code, for searching.</summary>
        public string SearchText;
    }

    /// <summary>A creature found on the server this tick.</summary>
    public class ScannedCreature
    {
        public long Id;
        public string Code;
        public string Name;
        public int Category;
        public float MaxHealth;
        public string Owner;
        public double X;
        public double Y;
        public double Z;
        public float Health;
        public bool Underground;

        /// <summary>Everything in CreatureInfo, joined: if it changes, the info is sent again.</summary>
        public string InfoSignature;
    }

    /// <summary>What the server last told one player about one creature.</summary>
    public class SentCreature
    {
        public double X;
        public double Y;
        public double Z;
        public float Health;
        public bool Underground;
        public string InfoSignature;
    }

    /// <summary>What the server last told one player, so only changes are sent.</summary>
    public class PlayerSendState
    {
        public readonly Dictionary<long, SentCreature> Sent = new Dictionary<long, SentCreature>();
        public long LastFullMs = -1;
    }

    public class CreatureMarkersMod : ModSystem
    {
        public const string ChannelName = "creaturemarkers";
        public const string ClientConfigFile = "CreatureMarkersConfig.json";
        public const string ServerConfigFile = "CreatureMarkersServerConfig.json";

        /// <summary>A position change smaller than this (blocks) isn't sent; the creature hasn't really moved.</summary>
        private const double MoveThreshold = 0.3;

        /// <summary>Everything is sent again this often, in case anything was missed.</summary>
        private const long FullResendMs = 60000;

        // ---------------- Client state ----------------

        /// <summary>Every creature the server has told us about, by entity id.</summary>
        public readonly Dictionary<long, KnownCreature> Known = new Dictionary<long, KnownCreature>();

        public ClientConfig ClientConfig { get; private set; }

        /// <summary>When the last update arrived (client milliseconds).</summary>
        public long LastPacketMs { get; private set; } = -1;

        /// <summary>Markers are hidden if no update arrives for this long (the server stopped sending).</summary>
        public long StaleAfterMs { get; private set; } = 6000;

        /// <summary>The server settings as last sent, shown in the settings window.</summary>
        public CreatureSettingsPacket ServerSettings { get; private set; } = new CreatureSettingsPacket { UpdateIntervalMs = 2000, AutoDetectPredators = true };

        /// <summary>Whether the server says you are underground right now.</summary>
        public bool PlayerUnderground { get; private set; }

        /// <summary>The creature being tracked (clicked on the map), or 0.</summary>
        public long TrackedId { get; private set; }

        /// <summary>Raised when colours, size or search change, so the map redraws.</summary>
        public event Action ConfigChanged;

        private ICoreClientAPI capi;
        private IClientNetworkChannel clientChannel;
        private GuiDialogCreatureSettings settingsDialog;
        private bool loggedFirstPacket;
        private string[] filterTerms = new string[0];
        private long trackedMissingSinceMs = -1;

        // ---------------- Server state ----------------

        private ICoreServerAPI sapi;
        private IServerNetworkChannel serverChannel;
        private ServerConfig serverConfig;
        private long tickListenerId;
        private bool loggedReadError;
        private bool loggedFirstCount;
        private readonly Dictionary<string, PlayerSendState> sendStates = new Dictionary<string, PlayerSendState>();

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return true;
        }

        public override void Start(ICoreAPI api)
        {
            api.Network
                .RegisterChannel(ChannelName)
                .RegisterMessageType<CreatureUpdatePacket>()
                .RegisterMessageType<CreatureSettingsPacket>()
                .RegisterMessageType<SaveCreatureSettingsPacket>();

            WorldMapManager mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager != null)
            {
                // Below the player markers (0.75), monsters drawn last so they sit on top.
                mapManager.RegisterMapLayer<CritterMapLayer>("creaturemarkers-critters", 0.60);
                mapManager.RegisterMapLayer<AnimalMapLayer>("creaturemarkers-animals", 0.61);
                mapManager.RegisterMapLayer<PetMapLayer>("creaturemarkers-pets", 0.615);
                mapManager.RegisterMapLayer<VillagerMapLayer>("creaturemarkers-villagers", 0.62);
                mapManager.RegisterMapLayer<TraderMapLayer>("creaturemarkers-traders", 0.63);
                mapManager.RegisterMapLayer<PredatorMapLayer>("creaturemarkers-predators", 0.64);
                mapManager.RegisterMapLayer<MonsterMapLayer>("creaturemarkers-monsters", 0.65);
            }
        }

        // =====================================================================
        // Server
        // =====================================================================

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            serverConfig = LoadServerConfig();
            serverChannel = api.Network.GetChannel(ChannelName);
            serverChannel.SetMessageHandler<SaveCreatureSettingsPacket>(OnSaveSettingsReceived);

            api.Event.PlayerJoin += (player) =>
            {
                if (player != null) sendStates.Remove(player.PlayerUID);
                SendSettings(player as IServerPlayer);
            };
            api.Event.PlayerDisconnect += (player) =>
            {
                if (player != null) sendStates.Remove(player.PlayerUID);
            };

            api.ChatCommands
                .Create("creaturemarkers")
                .WithDescription(Lang.Get("creaturemarkers:cmd-desc"))
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnReloadCommand);

            StartTicking();
            api.Logger.Notification("[creaturemarkers] Server side started, checking creatures every {0} ms (max distance {1}, admin only {2}, critters {3})",
                serverConfig.UpdateIntervalMs, serverConfig.MaxDistance, serverConfig.AdminOnly, serverConfig.ShowCritters);
        }

        private ServerConfig LoadServerConfig()
        {
            ServerConfig config = null;
            try
            {
                config = sapi.LoadModConfig<ServerConfig>(ServerConfigFile);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[creaturemarkers] Could not read {0}, using defaults: {1}", ServerConfigFile, e.Message);
            }
            if (config == null) config = new ServerConfig();
            config.Clamp();
            sapi.StoreModConfig(config, ServerConfigFile);
            return config;
        }

        private TextCommandResult OnReloadCommand(TextCommandCallingArgs args)
        {
            serverConfig = LoadServerConfig();
            ApplyServerSettingsChange();
            return TextCommandResult.Success(Lang.Get("creaturemarkers:reloaded", serverConfig.UpdateIntervalMs, serverConfig.MaxDistance, serverConfig.AdminOnly, serverConfig.ShowCritters));
        }

        /// <summary>After settings change: restart the timer, resend everything, tell every player.</summary>
        private void ApplyServerSettingsChange()
        {
            hostileByCode.Clear();
            sendStates.Clear();
            StartTicking();
            foreach (IPlayer player in sapi.World.AllOnlinePlayers) SendSettings(player as IServerPlayer);
        }

        private void OnSaveSettingsReceived(IServerPlayer fromPlayer, SaveCreatureSettingsPacket packet)
        {
            if (fromPlayer == null || packet == null) return;
            if (!fromPlayer.HasPrivilege(Privilege.controlserver))
            {
                fromPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("creaturemarkers:no-permission"), EnumChatType.CommandError);
                return;
            }

            serverConfig.UpdateIntervalMs = packet.UpdateIntervalMs;
            serverConfig.MaxDistance = packet.MaxDistance;
            serverConfig.AdminOnly = packet.AdminOnly;
            serverConfig.ShowCritters = packet.ShowCritters;
            serverConfig.AutoDetectPredators = packet.AutoDetectPredators;
            serverConfig.Clamp();
            sapi.StoreModConfig(serverConfig, ServerConfigFile);
            ApplyServerSettingsChange();

            fromPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("creaturemarkers:settings-saved"), EnumChatType.CommandSuccess);
            sapi.Logger.Notification("[creaturemarkers] {0} changed server settings: every {1} ms, max distance {2}, admin only {3}, critters {4}, auto predators {5}",
                fromPlayer.PlayerName, serverConfig.UpdateIntervalMs, serverConfig.MaxDistance, serverConfig.AdminOnly, serverConfig.ShowCritters, serverConfig.AutoDetectPredators);
        }

        private void StartTicking()
        {
            if (tickListenerId != 0) sapi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = sapi.Event.RegisterGameTickListener(OnServerTick, serverConfig.UpdateIntervalMs);
        }

        private bool MayView(IServerPlayer player)
        {
            return !serverConfig.AdminOnly || player.HasPrivilege(Privilege.controlserver);
        }

        private void SendSettings(IServerPlayer player)
        {
            if (player == null) return;
            serverChannel.SendPacket(new CreatureSettingsPacket
            {
                Allowed = MayView(player),
                IsAdmin = player.HasPrivilege(Privilege.controlserver),
                UpdateIntervalMs = serverConfig.UpdateIntervalMs,
                MaxDistance = serverConfig.MaxDistance,
                AdminOnly = serverConfig.AdminOnly,
                ShowCritters = serverConfig.ShowCritters,
                AutoDetectPredators = serverConfig.AutoDetectPredators
            }, player);
        }

        private void OnServerTick(float dt)
        {
            List<IServerPlayer> receivers = new List<IServerPlayer>();
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                IServerPlayer splayer = player as IServerPlayer;
                if (splayer == null || splayer.Entity == null) continue;
                if (splayer.ConnectionState != EnumClientState.Playing) continue;
                if (!MayView(splayer)) continue;
                receivers.Add(splayer);
            }
            if (receivers.Count == 0) return;

            List<ScannedCreature> all = CollectCreatures();
            if (!loggedFirstCount && all.Count > 0)
            {
                loggedFirstCount = true;
                int[] counts = new int[CreatureCategory.Count];
                foreach (ScannedCreature c in all) if (c.Category >= 0 && c.Category < CreatureCategory.Count) counts[c.Category]++;
                sapi.Logger.Notification("[creaturemarkers] Tracking {0} creatures for {1} player(s): {2} monsters, {3} predators, {4} animals, {5} traders, {6} villagers, {7} critters, {8} pets",
                    all.Count, receivers.Count, counts[CreatureCategory.Monsters], counts[CreatureCategory.Predators], counts[CreatureCategory.Animals],
                    counts[CreatureCategory.Traders], counts[CreatureCategory.Villagers], counts[CreatureCategory.Critters], counts[CreatureCategory.Pets]);
            }

            long now = sapi.World.ElapsedMilliseconds;
            double maxSq = (double)serverConfig.MaxDistance * serverConfig.MaxDistance;

            foreach (IServerPlayer receiver in receivers)
            {
                PlayerSendState state;
                if (!sendStates.TryGetValue(receiver.PlayerUID, out state))
                {
                    state = new PlayerSendState();
                    sendStates[receiver.PlayerUID] = state;
                }

                bool full = state.LastFullMs < 0 || now - state.LastFullMs >= FullResendMs;
                if (full)
                {
                    state.Sent.Clear();
                    state.LastFullMs = now;
                }

                double px = receiver.Entity.Pos.X, pz = receiver.Entity.Pos.Z;
                List<CreatureInfo> added = new List<CreatureInfo>();
                List<long> movedIds = new List<long>();
                List<double> movedX = new List<double>();
                List<float> movedY = new List<float>();
                List<double> movedZ = new List<double>();
                List<float> movedHealth = new List<float>();
                List<bool> movedUnder = new List<bool>();
                HashSet<long> visible = new HashSet<long>();

                foreach (ScannedCreature c in all)
                {
                    if (serverConfig.MaxDistance > 0)
                    {
                        double dx = c.X - px, dz = c.Z - pz;
                        if (dx * dx + dz * dz > maxSq) continue;
                    }
                    visible.Add(c.Id);

                    SentCreature sent;
                    bool known = state.Sent.TryGetValue(c.Id, out sent);
                    if (!known)
                    {
                        sent = new SentCreature();
                        state.Sent[c.Id] = sent;
                    }

                    if (!known || sent.InfoSignature != c.InfoSignature)
                    {
                        added.Add(new CreatureInfo { Id = c.Id, Code = c.Code, Name = c.Name, Category = c.Category, MaxHealth = c.MaxHealth, Owner = c.Owner });
                        sent.InfoSignature = c.InfoSignature;
                    }

                    bool moved = !known
                        || Math.Abs(c.X - sent.X) > MoveThreshold
                        || Math.Abs(c.Y - sent.Y) > MoveThreshold
                        || Math.Abs(c.Z - sent.Z) > MoveThreshold
                        || Math.Abs(c.Health - sent.Health) > 0.01f
                        || c.Underground != sent.Underground;
                    if (moved)
                    {
                        movedIds.Add(c.Id);
                        movedX.Add(c.X);
                        movedY.Add((float)c.Y);
                        movedZ.Add(c.Z);
                        movedHealth.Add(c.Health);
                        movedUnder.Add(c.Underground);
                        sent.X = c.X;
                        sent.Y = c.Y;
                        sent.Z = c.Z;
                        sent.Health = c.Health;
                        sent.Underground = c.Underground;
                    }
                }

                List<long> removed = new List<long>();
                foreach (long id in state.Sent.Keys)
                {
                    if (!visible.Contains(id)) removed.Add(id);
                }
                foreach (long id in removed) state.Sent.Remove(id);

                // Sent even when empty: it tells the client the server is still there.
                serverChannel.SendPacket(new CreatureUpdatePacket
                {
                    Full = full,
                    Added = added.Count > 0 ? added.ToArray() : null,
                    MovedIds = movedIds.Count > 0 ? movedIds.ToArray() : null,
                    MovedX = movedIds.Count > 0 ? movedX.ToArray() : null,
                    MovedY = movedIds.Count > 0 ? movedY.ToArray() : null,
                    MovedZ = movedIds.Count > 0 ? movedZ.ToArray() : null,
                    MovedHealth = movedIds.Count > 0 ? movedHealth.ToArray() : null,
                    MovedUnderground = movedIds.Count > 0 ? movedUnder.ToArray() : null,
                    RemovedIds = removed.Count > 0 ? removed.ToArray() : null,
                    PlayerUnderground = IsUnderground(receiver.Entity.Pos.X, receiver.Entity.Pos.Y, receiver.Entity.Pos.Z)
                }, receiver);
            }
        }

        private MethodInfo terrainHeightMethod;
        private bool terrainHeightLookedUp;
        private bool loggedHeightError;

        /// <summary>
        /// True if the position is UndergroundDepth blocks or more below the original (world generation) terrain
        /// surface there. That height ignores trees and buildings, so forests and houses still count as the surface.
        /// Falls back to the rain map height if the terrain height can't be read. Unknown counts as the surface.
        /// </summary>
        private bool IsUnderground(double x, double y, double z)
        {
            try
            {
                object blockAccessor = sapi.World.BlockAccessor;
                if (!terrainHeightLookedUp)
                {
                    terrainHeightLookedUp = true;
                    terrainHeightMethod = FindHeightMethod(blockAccessor, "GetTerrainMapheightAt") ?? FindHeightMethod(blockAccessor, "GetRainMapHeightAt");
                    sapi.Logger.Notification("[creaturemarkers] Surface height from: {0}", terrainHeightMethod != null ? terrainHeightMethod.Name : "nothing (surface/underground split off)");
                }
                if (terrainHeightMethod == null) return false;

                Vintagestory.API.MathTools.BlockPos pos = new Vintagestory.API.MathTools.Vec3d(x, y, z).AsBlockPos;
                object result = terrainHeightMethod.Invoke(blockAccessor, new object[] { pos });
                int surface = Convert.ToInt32(result);
                if (surface <= 0) return false;
                return y < surface - serverConfig.UndergroundDepth;
            }
            catch (Exception ex)
            {
                if (!loggedHeightError)
                {
                    loggedHeightError = true;
                    sapi.Logger.Warning("[creaturemarkers] Could not read the surface height: {0}", ex.Message);
                }
                return false;
            }
        }

        /// <summary>A public method with this name taking just a BlockPos, on the object or one of its interfaces.</summary>
        private static MethodInfo FindHeightMethod(object owner, string name)
        {
            Type posType = typeof(Vintagestory.API.MathTools.BlockPos);
            MethodInfo method = owner.GetType().GetMethod(name, new Type[] { posType });
            if (method != null) return method;
            foreach (Type iface in owner.GetType().GetInterfaces())
            {
                method = iface.GetMethod(name, new Type[] { posType });
                if (method != null) return method;
            }
            return null;
        }

        /// <summary>Every living, non-player creature the server currently has loaded.</summary>
        private List<ScannedCreature> CollectCreatures()
        {
            List<ScannedCreature> list = new List<ScannedCreature>();
            foreach (Entity e in LoadedEntities())
            {
                if (e == null || !e.Alive) continue;
                if (!(e is EntityAgent)) continue;      // skips dropped items, arrows, boats, falling blocks
                if (e is EntityPlayer) continue;        // players are shown by the game and Far Player Map Markers

                int category = Classify(e);
                if (category < 0) continue;
                if (category == CreatureCategory.Critters && !serverConfig.ShowCritters) continue;

                string name;
                try { name = e.GetName(); } catch (Exception) { name = null; }
                string code = e.Code != null ? e.Code.ToString() : "?";
                if (string.IsNullOrEmpty(name)) name = e.Code != null ? e.Code.Path : "?";

                float health, maxHealth;
                ReadHealth(e, out health, out maxHealth);
                string owner = OwnerName(e);

                list.Add(new ScannedCreature
                {
                    Id = e.EntityId,
                    Code = code,
                    Name = name,
                    Category = category,
                    MaxHealth = maxHealth,
                    Owner = owner,
                    X = e.Pos.X,
                    Y = e.Pos.Y,
                    Z = e.Pos.Z,
                    Health = health,
                    Underground = IsUnderground(e.Pos.X, e.Pos.Y, e.Pos.Z),
                    InfoSignature = category + "|" + name + "|" + owner + "|" + maxHealth.ToString(CultureInfo.InvariantCulture)
                });
            }
            return list;
        }

        /// <summary>
        /// A creature's current and maximum health, from the "health" tree the game keeps on every
        /// creature that can be hurt (synced to nearby players too). Both 0 if it has none.
        /// </summary>
        public static void ReadHealth(Entity e, out float health, out float maxHealth)
        {
            health = 0;
            maxHealth = 0;
            try
            {
                ITreeAttribute tree = e.WatchedAttributes != null ? e.WatchedAttributes.GetTreeAttribute("health") : null;
                if (tree == null) return;
                health = tree.GetFloat("currenthealth");
                maxHealth = tree.GetFloat("maxhealth");
                if (maxHealth <= 0) maxHealth = tree.GetFloat("basemaxhealth");
                if (maxHealth < health) maxHealth = health;
            }
            catch (Exception) { health = 0; maxHealth = 0; }
        }

        /// <summary>How far around each player to look when the loaded-entity list can't be read.</summary>
        private const int FallbackRadius = 512;

        private bool loggedSource;

        /// <summary>
        /// Every entity the server has loaded. Tries the world's LoadedEntities list first (read by reflection,
        /// including through the server's interfaces, because source mods compiled by the game can't use its
        /// collection types directly). If that gives nothing, asks the game for the entities around each player.
        /// </summary>
        private List<Entity> LoadedEntities()
        {
            List<Entity> result = ReadLoadedEntitiesList();
            string source = "LoadedEntities";

            if (result.Count == 0)
            {
                source = "around players";
                Dictionary<long, Entity> seen = new Dictionary<long, Entity>();
                int radius = serverConfig.MaxDistance > 0 ? serverConfig.MaxDistance : FallbackRadius;
                foreach (IPlayer player in sapi.World.AllOnlinePlayers)
                {
                    if (player == null || player.Entity == null) continue;
                    Entity[] around;
                    try
                    {
                        around = sapi.World.GetEntitiesAround(player.Entity.Pos.XYZ, radius, radius, null);
                    }
                    catch (Exception ex)
                    {
                        if (!loggedReadError)
                        {
                            loggedReadError = true;
                            sapi.Logger.Warning("[creaturemarkers] Could not list entities around players: {0}", ex);
                        }
                        continue;
                    }
                    if (around == null) continue;
                    foreach (Entity e in around)
                    {
                        if (e != null && !seen.ContainsKey(e.EntityId)) seen[e.EntityId] = e;
                    }
                }
                result = new List<Entity>(seen.Values);
            }

            if (!loggedSource && result.Count > 0)
            {
                loggedSource = true;
                sapi.Logger.Notification("[creaturemarkers] Reading creatures from: {0}", source);
            }
            return result;
        }

        private List<Entity> ReadLoadedEntitiesList()
        {
            List<Entity> result = new List<Entity>();
            try
            {
                object world = sapi.World;
                object value = null;

                // The class itself, then any interface it implements (the property may be an explicit interface member).
                PropertyInfo prop = world.GetType().GetProperty("LoadedEntities", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.GetIndexParameters().Length == 0) value = prop.GetValue(world, null);
                if (value == null)
                {
                    FieldInfo field = world.GetType().GetField("LoadedEntities", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null) value = field.GetValue(world);
                }
                if (value == null)
                {
                    foreach (Type iface in world.GetType().GetInterfaces())
                    {
                        PropertyInfo ip = iface.GetProperty("LoadedEntities");
                        if (ip == null || ip.GetIndexParameters().Length != 0) continue;
                        value = ip.GetValue(world, null);
                        if (value != null) break;
                    }
                }
                if (value == null) return result;

                IDictionary dict = value as IDictionary;
                if (dict != null)
                {
                    foreach (object v in dict.Values)
                    {
                        Entity e = v as Entity;
                        if (e != null) result.Add(e);
                    }
                    return result;
                }

                IEnumerable seq = value as IEnumerable;
                if (seq == null) return result;
                PropertyInfo valueProp = null;
                foreach (object item in seq)
                {
                    Entity e = item as Entity;
                    if (e == null && item != null)
                    {
                        if (valueProp == null) valueProp = item.GetType().GetProperty("Value");
                        if (valueProp != null) e = valueProp.GetValue(item, null) as Entity;
                    }
                    if (e != null) result.Add(e);
                }
            }
            catch (Exception ex)
            {
                if (!loggedReadError)
                {
                    loggedReadError = true;
                    sapi.Logger.Warning("[creaturemarkers] Could not read the loaded entities list, using the area around players instead: {0}", ex.Message);
                }
            }
            return result;
        }

        /// <summary>Which map layer a creature belongs on, or -1 to hide it.</summary>
        private int Classify(Entity e)
        {
            string path = e.Code != null && e.Code.Path != null ? e.Code.Path.ToLowerInvariant() : "";
            string cls = e.GetType().Name;

            if (Matches(path, ServerConfig.AlwaysExcludedCodes) || Matches(path, serverConfig.ExcludeCodes)) return -1;
            if (Matches(path, serverConfig.MonsterCodes)) return CreatureCategory.Monsters;

            if (cls.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) >= 0 || path.Contains("trader")) return CreatureCategory.Traders;
            if (cls.IndexOf("Villager", StringComparison.OrdinalIgnoreCase) >= 0 || path.Contains("villager")
                || path.StartsWith("humanoid") || cls.IndexOf("Humanoid", StringComparison.OrdinalIgnoreCase) >= 0) return CreatureCategory.Villagers;

            if (Matches(path, serverConfig.PetCodes) || OwnerName(e) != null) return CreatureCategory.Pets;
            if (Matches(path, serverConfig.PredatorCodes)) return CreatureCategory.Predators;
            if (serverConfig.AutoDetectPredators && AttacksPlayersOnSight(e)) return CreatureCategory.Predators;
            if (Matches(path, serverConfig.CritterCodes)) return CreatureCategory.Critters;

            try
            {
                if (e.Properties != null && (e.Properties.Habitat == EnumHabitat.Sea || e.Properties.Habitat == EnumHabitat.Underwater))
                    return CreatureCategory.Critters;
            }
            catch (Exception) { }

            return CreatureCategory.Animals;
        }

        /// <summary>Results of AttacksPlayersOnSight, by creature code (every creature of one code shares its AI).</summary>
        private readonly Dictionary<string, bool> hostileByCode = new Dictionary<string, bool>();

        /// <summary>
        /// True if the creature's AI has a "meleeattack" or "seekentity" task aimed at players that isn't
        /// limited to an emotion state (such as "aggressiveondamage", which only applies after being hurt).
        /// Read from the creature's server behaviour JSON, so it works for creatures from any mod.
        /// </summary>
        private bool AttacksPlayersOnSight(Entity e)
        {
            string key = e.Code != null ? e.Code.ToString() : "";
            bool cached;
            if (hostileByCode.TryGetValue(key, out cached)) return cached;

            bool hostile = false;
            try
            {
                object serverProps = ReadMember(e.Properties, "Server");
                IEnumerable behaviors = ReadMember(serverProps, "BehaviorsAsJsonObj") as IEnumerable;
                if (behaviors != null)
                {
                    foreach (object b in behaviors)
                    {
                        if (b == null) continue;
                        string json = StripWhitespace(b.ToString());
                        if (json.IndexOf("\"taskai\"", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (HasUnprovokedPlayerAttack(json)) { hostile = true; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug("[creaturemarkers] Could not read the AI of {0}: {1}", key, ex.Message);
            }

            hostileByCode[key] = hostile;
            return hostile;
        }

        /// <summary>Removes all whitespace, so the JSON can be searched without caring about formatting.</summary>
        private static string StripWhitespace(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Looks through whitespace-free AI JSON for a "meleeattack" or "seekentity" task whose entityCodes
        /// include players and that has no "whenInEmotionState" (so it isn't only after being hurt).
        /// Plain string searching: source mods compiled by the game can't use Regex (error CS1069).
        /// </summary>
        private static bool HasUnprovokedPlayerAttack(string json)
        {
            const string taskStart = "{\"code\":";
            int pos = 0;
            while (true)
            {
                int start = json.IndexOf(taskStart, pos, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return false;

                int next = json.IndexOf(taskStart, start + taskStart.Length, StringComparison.OrdinalIgnoreCase);
                string task = next < 0 ? json.Substring(start) : json.Substring(start, next - start);
                pos = start + taskStart.Length;

                bool attackTask = task.StartsWith(taskStart + "\"meleeattack\"", StringComparison.OrdinalIgnoreCase)
                    || task.StartsWith(taskStart + "\"seekentity\"", StringComparison.OrdinalIgnoreCase);
                if (!attackTask) continue;

                int codesAt = task.IndexOf("\"entityCodes\":[", StringComparison.OrdinalIgnoreCase);
                if (codesAt < 0) continue;
                int listStart = codesAt + "\"entityCodes\":[".Length;
                int listEnd = task.IndexOf(']', listStart);
                string codes = listEnd < 0 ? task.Substring(listStart) : task.Substring(listStart, listEnd - listStart);
                if (codes.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (task.IndexOf("\"whenInEmotionState\"", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return true;
            }
        }

        /// <summary>The owner's name if a player has tamed or claimed this creature, else null.</summary>
        public static string OwnerName(Entity e)
        {
            try
            {
                if (e.WatchedAttributes == null) return null;
                ITreeAttribute owned = e.WatchedAttributes.GetTreeAttribute("ownedby");
                if (owned != null)
                {
                    string name = owned.GetString("name");
                    if (string.IsNullOrEmpty(name)) name = owned.GetString("playername");
                    if (!string.IsNullOrEmpty(name)) return name;
                    if (!string.IsNullOrEmpty(owned.GetString("uid"))) return "?";
                }
                string direct = e.WatchedAttributes.GetString("ownerName");
                if (string.IsNullOrEmpty(direct)) direct = e.WatchedAttributes.GetString("ownername");
                if (!string.IsNullOrEmpty(direct)) return direct;
            }
            catch (Exception) { }
            return null;
        }

        private static object ReadMember(object owner, string name)
        {
            if (owner == null) return null;
            Type type = owner.GetType();
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(owner, null);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(owner) : null;
        }

        /// <summary>True if the code starts with one of the names, or has one right after a dash ("fwilds-wolf-male").</summary>
        private static bool Matches(string path, string[] names)
        {
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                string name = n.ToLowerInvariant();
                if (path.StartsWith(name) || path.Contains("-" + name)) return true;
            }
            return false;
        }

        // =====================================================================
        // Client
        // =====================================================================

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            ClientConfig = LoadClientConfig();
            UpdateFilterTerms();

            clientChannel = api.Network.GetChannel(ChannelName);
            clientChannel.SetMessageHandler<CreatureUpdatePacket>(OnUpdateReceived);
            clientChannel.SetMessageHandler<CreatureSettingsPacket>(OnSettingsReceived);

            api.ChatCommands
                .Create("creaturemarkers")
                .WithDescription(Lang.Get("creaturemarkers:client-cmd-desc"))
                .BeginSubCommand("status")
                    .WithDescription(Lang.Get("creaturemarkers:status-desc"))
                    .HandleWith(OnStatusCommand)
                .EndSubCommand()
                .BeginSubCommand("find")
                    .WithDescription(Lang.Get("creaturemarkers:find-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("search"))
                    .HandleWith(OnFindCommand)
                .EndSubCommand()
                .BeginSubCommand("clear")
                    .WithDescription(Lang.Get("creaturemarkers:clear-desc"))
                    .HandleWith(OnClearCommand)
                .EndSubCommand()
                .BeginSubCommand("only")
                    .WithDescription(Lang.Get("creaturemarkers:only-desc"))
                    .HandleWith(OnOnlyCommand)
                .EndSubCommand()
                .BeginSubCommand("waypoint")
                    .WithDescription(Lang.Get("creaturemarkers:waypoint-desc"))
                    .HandleWith(OnWaypointCommand)
                .EndSubCommand()
                .BeginSubCommand("untrack")
                    .WithDescription(Lang.Get("creaturemarkers:untrack-desc"))
                    .HandleWith(OnUntrackCommand)
                .EndSubCommand()
                .BeginSubCommand("level")
                    .WithDescription(Lang.Get("creaturemarkers:level-desc"))
                    .HandleWith(OnLevelCommand)
                .EndSubCommand()
                .BeginSubCommand("size")
                    .WithDescription(Lang.Get("creaturemarkers:size-desc"))
                    .WithArgs(api.ChatCommands.Parsers.Int("size"))
                    .HandleWith(OnSizeCommand)
                .EndSubCommand()
                .BeginSubCommand("bars")
                    .WithDescription(Lang.Get("creaturemarkers:bars-desc"))
                    .HandleWith(OnBarsCommand)
                .EndSubCommand()
                .HandleWith(OnSettingsCommand);

            api.Event.RegisterGameTickListener(OnClientTick, 1000);
            api.Logger.Notification("[creaturemarkers] Client side started");
        }

        private ClientConfig LoadClientConfig()
        {
            ClientConfig config = null;
            try
            {
                config = capi.LoadModConfig<ClientConfig>(ClientConfigFile);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[creaturemarkers] Could not read {0}, using defaults: {1}", ClientConfigFile, e.Message);
            }
            if (config == null) config = new ClientConfig();
            config.Clamp();
            capi.StoreModConfig(config, ClientConfigFile);
            return config;
        }

        /// <summary>Saves the player's settings and redraws the map.</summary>
        public void SaveClientConfig()
        {
            ClientConfig.Clamp();
            capi.StoreModConfig(ClientConfig, ClientConfigFile);
            UpdateFilterTerms();
            if (ConfigChanged != null) ConfigChanged();
        }

        /// <summary>Sends new server settings (admins only; the server checks).</summary>
        public void SendServerSettings(int intervalMs, int maxDistance, bool adminOnly, bool showCritters, bool autoPredators)
        {
            if (clientChannel == null) return;
            clientChannel.SendPacket(new SaveCreatureSettingsPacket
            {
                UpdateIntervalMs = intervalMs,
                MaxDistance = maxDistance,
                AdminOnly = adminOnly,
                ShowCritters = showCritters,
                AutoDetectPredators = autoPredators
            });
        }

        private TextCommandResult OnSettingsCommand(TextCommandCallingArgs args)
        {
            if (settingsDialog == null)
            {
                settingsDialog = new GuiDialogCreatureSettings(capi, this);
                capi.Gui.RegisterDialog(settingsDialog);
            }
            settingsDialog.Open();
            return TextCommandResult.Success();
        }

        private TextCommandResult OnStatusCommand(TextCommandCallingArgs args)
        {
            int[] counts = new int[CreatureCategory.Count];
            int matches = 0;
            foreach (KnownCreature c in Known.Values)
            {
                if (c.Category >= 0 && c.Category < CreatureCategory.Count) counts[c.Category]++;
                if (FilterActive && MatchesFilter(c)) matches++;
            }
            string text = Lang.Get("creaturemarkers:status",
                counts[CreatureCategory.Monsters], counts[CreatureCategory.Predators], counts[CreatureCategory.Animals],
                counts[CreatureCategory.Traders], counts[CreatureCategory.Villagers], counts[CreatureCategory.Critters], counts[CreatureCategory.Pets],
                ClientConfig.ShowHealthBars ? Lang.Get("creaturemarkers:on") : Lang.Get("creaturemarkers:off"));
            if (ClientConfig.OnlyMyLevel) text += " " + Lang.Get(PlayerUnderground ? "creaturemarkers:status-level-under" : "creaturemarkers:status-level-surface");
            if (FilterActive) text += " " + Lang.Get("creaturemarkers:status-search", ClientConfig.Filter, matches);
            KnownCreature tracked = Tracked;
            if (tracked != null) text += " " + Lang.Get("creaturemarkers:status-tracking", tracked.Name);
            return TextCommandResult.Success(text);
        }

        private TextCommandResult OnFindCommand(TextCommandCallingArgs args)
        {
            ClientConfig.Filter = (args[0] as string ?? "").Trim();
            SaveClientConfig();
            if (!FilterActive) return TextCommandResult.Success(Lang.Get("creaturemarkers:search-cleared"));

            int matches = 0;
            foreach (KnownCreature c in Known.Values) if (MatchesFilter(c)) matches++;
            return TextCommandResult.Success(Lang.Get(ClientConfig.FilterOnlyMatches ? "creaturemarkers:search-set-only" : "creaturemarkers:search-set", ClientConfig.Filter, matches));
        }

        private TextCommandResult OnClearCommand(TextCommandCallingArgs args)
        {
            ClientConfig.Filter = "";
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get("creaturemarkers:search-cleared"));
        }

        private TextCommandResult OnOnlyCommand(TextCommandCallingArgs args)
        {
            ClientConfig.FilterOnlyMatches = !ClientConfig.FilterOnlyMatches;
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get(ClientConfig.FilterOnlyMatches ? "creaturemarkers:only-on" : "creaturemarkers:only-off"));
        }

        private TextCommandResult OnWaypointCommand(TextCommandCallingArgs args)
        {
            KnownCreature c = Tracked;
            if (c == null) return TextCommandResult.Error(Lang.Get("creaturemarkers:waypoint-none"));

            double x = c.X, y = c.Y, z = c.Z;
            Entity live = capi.World.GetEntityById(c.Id);
            if (live != null && live.Alive) { x = live.Pos.X; y = live.Pos.Y; z = live.Pos.Z; }
            AddWaypointAt(c, x, y, z);
            return TextCommandResult.Success();
        }

        private TextCommandResult OnUntrackCommand(TextCommandCallingArgs args)
        {
            StopTracking(false);
            return TextCommandResult.Success(Lang.Get("creaturemarkers:track-stopped"));
        }

        private TextCommandResult OnSizeCommand(TextCommandCallingArgs args)
        {
            ClientConfig.MarkerSize = (int)args[0];
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get("creaturemarkers:size-set", ClientConfig.MarkerSize));
        }

        private TextCommandResult OnBarsCommand(TextCommandCallingArgs args)
        {
            ClientConfig.ShowHealthBars = !ClientConfig.ShowHealthBars;
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get(ClientConfig.ShowHealthBars ? "creaturemarkers:bars-on" : "creaturemarkers:bars-off"));
        }

        private void OnSettingsReceived(CreatureSettingsPacket packet)
        {
            if (packet == null) return;
            ServerSettings = packet;
            StaleAfterMs = Math.Max(6000, packet.UpdateIntervalMs * 3L);
            if (!packet.Allowed)
            {
                Known.Clear();
                LastPacketMs = -1;
            }
        }

        private void OnUpdateReceived(CreatureUpdatePacket packet)
        {
            LastPacketMs = capi.World.ElapsedMilliseconds;
            if (packet == null) return;

            if (packet.Full) Known.Clear();
            PlayerUnderground = packet.PlayerUnderground;

            if (packet.Added != null)
            {
                foreach (CreatureInfo info in packet.Added)
                {
                    if (info == null) continue;
                    KnownCreature c;
                    if (!Known.TryGetValue(info.Id, out c))
                    {
                        c = new KnownCreature { Id = info.Id };
                        Known[info.Id] = c;
                    }
                    c.Code = info.Code;
                    c.Name = info.Name;
                    c.Category = info.Category;
                    c.MaxHealth = info.MaxHealth;
                    c.Owner = info.Owner;
                    c.SearchText = ((info.Name ?? "") + " " + (info.Code ?? "")).ToLowerInvariant();
                }
            }

            if (packet.MovedIds != null && packet.MovedX != null && packet.MovedY != null && packet.MovedZ != null && packet.MovedHealth != null)
            {
                int n = Math.Min(packet.MovedIds.Length, Math.Min(packet.MovedX.Length, Math.Min(packet.MovedY.Length, Math.Min(packet.MovedZ.Length, packet.MovedHealth.Length))));
                for (int i = 0; i < n; i++)
                {
                    KnownCreature c;
                    if (!Known.TryGetValue(packet.MovedIds[i], out c)) continue;
                    c.X = packet.MovedX[i];
                    c.Y = packet.MovedY[i];
                    c.Z = packet.MovedZ[i];
                    c.Health = packet.MovedHealth[i];
                    if (packet.MovedUnderground != null && i < packet.MovedUnderground.Length) c.Underground = packet.MovedUnderground[i];
                    c.HasPosition = true;
                }
            }

            if (packet.RemovedIds != null)
            {
                foreach (long id in packet.RemovedIds) Known.Remove(id);
            }

            if (!loggedFirstPacket && Known.Count > 0)
            {
                loggedFirstPacket = true;
                capi.Logger.Notification("[creaturemarkers] Receiving creatures from the server ({0} known)", Known.Count);
            }
        }

        /// <summary>True while the server's data is fresh enough to draw.</summary>
        public bool HasFreshData
        {
            get { return LastPacketMs >= 0 && capi != null && capi.World.ElapsedMilliseconds - LastPacketMs <= StaleAfterMs; }
        }

        // ---------------- Search ----------------

        public bool FilterActive { get { return filterTerms.Length > 0; } }

        private void UpdateFilterTerms()
        {
            List<string> terms = new List<string>();
            string filter = ClientConfig != null ? ClientConfig.Filter : null;
            if (!string.IsNullOrEmpty(filter))
            {
                foreach (string part in filter.Split(','))
                {
                    string t = part.Trim().ToLowerInvariant();
                    if (t.Length > 0) terms.Add(t);
                }
            }
            filterTerms = terms.ToArray();
        }

        /// <summary>True if the creature's name or code contains any search term.</summary>
        public bool MatchesFilter(KnownCreature c)
        {
            if (filterTerms.Length == 0) return true;
            string text = c.SearchText ?? "";
            foreach (string t in filterTerms)
            {
                if (text.Contains(t)) return true;
            }
            return false;
        }

        // ---------------- Surface / underground ----------------

        /// <summary>False if "only my level" is on and the creature is on the other level (surface vs underground). The tracked creature always shows.</summary>
        public bool OnMyLevel(KnownCreature c)
        {
            if (ClientConfig == null || !ClientConfig.OnlyMyLevel) return true;
            if (c.Id == TrackedId) return true;
            return c.Underground == PlayerUnderground;
        }

        private TextCommandResult OnLevelCommand(TextCommandCallingArgs args)
        {
            ClientConfig.OnlyMyLevel = !ClientConfig.OnlyMyLevel;
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get(ClientConfig.OnlyMyLevel ? "creaturemarkers:level-on" : "creaturemarkers:level-off"));
        }

        // ---------------- Tracking and waypoints ----------------

        public KnownCreature Tracked
        {
            get
            {
                if (TrackedId == 0) return null;
                KnownCreature c;
                return Known.TryGetValue(TrackedId, out c) ? c : null;
            }
        }

        /// <summary>Left-click on a marker: start tracking it, or stop if it was already tracked.</summary>
        public void ToggleTracking(KnownCreature c)
        {
            if (c == null) return;
            if (TrackedId == c.Id)
            {
                StopTracking(false);
                capi.ShowChatMessage(Lang.Get("creaturemarkers:track-stopped"));
                return;
            }
            TrackedId = c.Id;
            trackedMissingSinceMs = -1;
            capi.ShowChatMessage(Lang.Get("creaturemarkers:track-started", c.Name));
        }

        public void StopTracking(bool lost)
        {
            KnownCreature c = Tracked;
            if (lost && TrackedId != 0) capi.ShowChatMessage(Lang.Get("creaturemarkers:track-lost", c != null ? c.Name : "?"));
            TrackedId = 0;
            trackedMissingSinceMs = -1;
        }

        /// <summary>Shift + left-click on a marker: put a waypoint where the creature is now.</summary>
        public void AddWaypointAt(KnownCreature c, double x, double y, double z)
        {
            if (c == null) return;
            string color = ColorHex.Normalize(ClientConfig.ColorFor(c.Category)) ?? "#FFFFFF";
            if (color.Length > 7) color = color.Substring(0, 7);
            string title = string.IsNullOrEmpty(c.Name) ? "?" : c.Name;
            string cmd = string.Format(CultureInfo.InvariantCulture, "/waypoint addati circle ={0} ={1} ={2} false {3} {4}",
                (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), color, title);
            capi.SendChatMessage(cmd);
            capi.ShowChatMessage(Lang.Get("creaturemarkers:waypoint-added", title));
        }

        private void OnClientTick(float dt)
        {
            if (TrackedId == 0) return;
            if (Known.ContainsKey(TrackedId))
            {
                trackedMissingSinceMs = -1;
                return;
            }
            long now = capi.World.ElapsedMilliseconds;
            if (trackedMissingSinceMs < 0) { trackedMissingSinceMs = now; return; }
            if (now - trackedMissingSinceMs > 5000) StopTracking(true);
        }
    }
}
