using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreatureMarkers
{
    /// <summary>
    /// Draws one kind of creature on the world map and minimap. Each kind is its own layer,
    /// so it can be switched on or off in the map's layer list.
    /// Shapes: monsters = diamond, predators = triangle, animals = circle, traders = square,
    /// villagers = rounded square, pets = heart, critters = small circle.
    /// Left-click a marker to track it (Shift + left-click puts a waypoint on it).
    /// </summary>
    public abstract class CreatureMapLayerBase : MapLayer
    {
        /// <summary>Most names listed when hovering over a crowd.</summary>
        private const int MaxHoverLines = 8;

        /// <summary>Faded look for creatures that don't match the search (premultiplied alpha).</summary>
        private static readonly Vec4f FadedTint = new Vec4f(0.3f, 0.3f, 0.3f, 0.3f);
        private static readonly Vec4f BarBack = new Vec4f(0.08f, 0.08f, 0.08f, 0.9f);
        private static readonly Vec4f LineTint = new Vec4f(1f, 1f, 1f, 0.85f);

        protected readonly ICoreClientAPI capi;
        private readonly Dictionary<string, LoadedTexture> textureCache = new Dictionary<string, LoadedTexture>();
        private CreatureMarkersMod modSystem;
        private Vec2f viewPos = new Vec2f();

        // Telling a click from a map drag: the map's view of a fixed world point moves while dragging.
        private readonly Vec3d dragReference = new Vec3d(0, 0, 0);
        private Vec2f dragReferenceView = new Vec2f();
        private bool hasDragReference;
        private bool mapMovedSinceClick;

        protected CreatureMapLayerBase(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
        }

        /// <summary>Which CreatureCategory this layer draws.</summary>
        protected abstract int CategoryId { get; }

        public override string Title => Lang.Get("creaturemarkers:layer-" + CreatureCategory.Keys[CategoryId]);

        public override string LayerGroupCode => "creaturemarkers-" + CreatureCategory.Keys[CategoryId];

        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        private CreatureMarkersMod Mod
        {
            get
            {
                if (modSystem == null && capi != null)
                {
                    modSystem = capi.ModLoader.GetModSystem<CreatureMarkersMod>();
                    if (modSystem != null) modSystem.ConfigChanged += ClearTextures;
                }
                return modSystem;
            }
        }

        private float BaseSize(CreatureMarkersMod mod)
        {
            float size = (float)GuiElement.scaled(mod.ClientConfig.MarkerSize);
            return CategoryId == CreatureCategory.Critters ? size * 0.7f : size;
        }

        // =====================================================================
        // Drawing
        // =====================================================================

        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (!Active || capi == null) return;
            CreatureMarkersMod mod = Mod;
            if (mod == null || mod.ClientConfig == null || !mod.HasFreshData) return;

            ClientConfig cfg = mod.ClientConfig;
            float size = BaseSize(mod);
            LoadedTexture tex = GetShapeTexture(cfg.ColorFor(CategoryId), cfg.BorderColorHex, size);
            if (tex == null) return;

            bool filtering = mod.FilterActive;
            float bigSize = size * 1.4f;
            LoadedTexture bigTex = filtering ? GetShapeTexture(cfg.ColorFor(CategoryId), cfg.BorderColorHex, bigSize) : null;

            // Faded, non-matching creatures first so matches are drawn on top of them.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (KnownCreature c in mod.Known.Values)
                {
                    if (c.Category != CategoryId || !c.HasPosition) continue;
                    if (!mod.OnMyLevel(c)) continue;
                    bool match = !filtering || mod.MatchesFilter(c);
                    if (filtering && !match && cfg.FilterOnlyMatches) continue;
                    if ((pass == 0) == (filtering && match)) continue;
                    if (c.Id == mod.TrackedId) continue;    // drawn last, below

                    Entity live;
                    Vec3d pos = PositionOf(c, out live);
                    double x, y;
                    bool clamped;
                    if (!ScreenPos(mapElem, pos, size, false, out x, out y, out clamped)) continue;

                    if (filtering && match)
                    {
                        DrawRing(x, y, bigSize * 1.25f, 0.9f);
                        DrawTexture(bigTex, x, y, bigSize, 50, null);
                    }
                    else
                    {
                        DrawTexture(tex, x, y, size, 50, filtering ? FadedTint : null);
                    }

                    if (!filtering || match) DrawHealthBarFor(mod, c, live, x, y, filtering && match ? bigSize : size);
                }
            }

            // The tracked creature: always visible (held on the map edge when off-screen), with a ring and a line from you.
            KnownCreature tracked = mod.Tracked;
            if (tracked != null && tracked.Category == CategoryId && tracked.HasPosition)
            {
                Entity live;
                Vec3d pos = PositionOf(tracked, out live);
                double x, y;
                bool clamped;
                float tSize = size * 1.5f;
                if (ScreenPos(mapElem, pos, tSize, true, out x, out y, out clamped))
                {
                    if (cfg.ShowTrackingLine) DrawTrackingLine(mapElem, x, y);
                    float pulse = 1f + 0.15f * (float)Math.Sin(capi.World.ElapsedMilliseconds / 200.0);
                    DrawRing(x, y, tSize * 1.4f * pulse, 1f);
                    DrawTexture(GetShapeTexture(cfg.ColorFor(CategoryId), cfg.BorderColorHex, tSize), x, y, tSize, 56, null);
                    if (!clamped) DrawHealthBarFor(mod, tracked, live, x, y, tSize);
                }
            }
        }

        private void DrawTexture(LoadedTexture tex, double x, double y, float size, float z, Vec4f tint)
        {
            if (tex == null) return;
            capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId,
                (float)(x - size / 2), (float)(y - size / 2), size, size, z, tint);
        }

        private void DrawHealthBarFor(CreatureMarkersMod mod, KnownCreature c, Entity live, double x, double y, float size)
        {
            ClientConfig cfg = mod.ClientConfig;
            if (!cfg.ShowHealthBars) return;
            float health, maxHealth;
            HealthOf(c, live, out health, out maxHealth);
            if (maxHealth <= 0) return;
            if (cfg.HealthBarsOnlyWhenHurt && health >= maxHealth - 0.01f) return;
            DrawHealthBar(x, y + size / 2 + 1, Math.Max(size * 1.4f, (float)GuiElement.scaled(10)), health / maxHealth);
        }

        /// <summary>A small bar: dark background, then green to yellow to red fill.</summary>
        private void DrawHealthBar(double cx, double top, float width, float fraction)
        {
            LoadedTexture white = GetWhiteTexture();
            if (white == null) return;

            fraction = Math.Max(0, Math.Min(1, fraction));
            float height = Math.Max(2f, (float)GuiElement.scaled(3));
            float left = (float)(cx - width / 2);

            capi.Render.Render2DTexturePremultipliedAlpha(white.TextureId, left - 1, (float)top - 1, width + 2, height + 2, 51, BarBack);
            if (fraction <= 0) return;

            float r = fraction > 0.5f ? (1 - fraction) * 2 : 1;
            float g = fraction > 0.5f ? 1 : fraction * 2;
            Vec4f fill = new Vec4f(r * 0.95f, g * 0.85f, 0.1f, 1);
            capi.Render.Render2DTexturePremultipliedAlpha(white.TextureId, left, (float)top, width * fraction, height, 52, fill);
        }

        /// <summary>A dotted line from your own position on the map to the tracked creature.</summary>
        private void DrawTrackingLine(GuiElementMap mapElem, double tx, double ty)
        {
            if (capi.World.Player == null || capi.World.Player.Entity == null) return;
            LoadedTexture white = GetWhiteTexture();
            if (white == null) return;

            double sx, sy;
            bool clamped;
            if (!ScreenPos(mapElem, capi.World.Player.Entity.Pos.XYZ, 0, true, out sx, out sy, out clamped)) return;

            double dx = tx - sx, dy = ty - sy;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double step = GuiElement.scaled(9);
            if (length < step * 2) return;

            float dot = (float)Math.Max(2, GuiElement.scaled(3));
            int dots = (int)(length / step);
            for (int i = 1; i < dots; i++)
            {
                double f = i / (double)dots;
                capi.Render.Render2DTexturePremultipliedAlpha(white.TextureId,
                    (float)(sx + dx * f - dot / 2), (float)(sy + dy * f - dot / 2), dot, dot, 53, LineTint);
            }
        }

        /// <summary>A white outline ring, used for search matches and the tracked creature.</summary>
        private void DrawRing(double x, double y, float size, float alpha)
        {
            LoadedTexture ring = GetRingTexture(size);
            if (ring == null) return;
            DrawTexture(ring, x, y, size, 49, alpha >= 1 ? null : new Vec4f(alpha, alpha, alpha, alpha));
        }

        // =====================================================================
        // Hover and click
        // =====================================================================

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (capi == null) return;
            TrackMapMovement(mapElem);

            if (!Active) return;
            CreatureMarkersMod mod = Mod;
            if (mod == null || mod.ClientConfig == null || !mod.HasFreshData) return;

            Vec3d myPos = capi.World.Player != null && capi.World.Player.Entity != null ? capi.World.Player.Entity.Pos.XYZ : null;
            int lines = 0, extra = 0;

            foreach (KnownCreature c in HitCreatures(mod, mapElem, args.X, args.Y))
            {
                if (lines >= MaxHoverLines) { extra++; continue; }
                lines++;

                Entity live;
                Vec3d pos = PositionOf(c, out live);
                float health, maxHealth;
                HealthOf(c, live, out health, out maxHealth);

                string name = string.IsNullOrEmpty(c.Name) ? "?" : c.Name;
                List<string> details = new List<string>();
                if (!string.IsNullOrEmpty(c.Owner)) details.Add(c.Owner == "?" ? Lang.Get("creaturemarkers:tamed") : Lang.Get("creaturemarkers:owned-by", c.Owner));
                if (maxHealth > 0) details.Add(Lang.Get("creaturemarkers:health", FormatHp(health), FormatHp(maxHealth)));
                if (myPos != null) details.Add(Lang.Get("creaturemarkers:blocks-away", (int)Math.Round(myPos.DistanceTo(pos))));
                if (c.Id == mod.TrackedId) details.Add(Lang.Get("creaturemarkers:tracking"));
                hoverText.AppendLine(details.Count > 0 ? name + " (" + string.Join(", ", details) + ")" : name);
            }
            if (extra > 0) hoverText.AppendLine(Lang.Get("creaturemarkers:and-more", extra));
            if (lines > 0) hoverText.AppendLine(Lang.Get("creaturemarkers:click-hint"));
        }

        /// <summary>Left-click a marker to track it (again to stop); Shift + left-click puts a waypoint there.</summary>
        public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
        {
            if (capi == null) return;
            TrackMapMovement(mapElem);
            bool wasDrag = mapMovedSinceClick;
            mapMovedSinceClick = false;

            if (!Active || args.Handled || args.Button != EnumMouseButton.Left || wasDrag) return;
            CreatureMarkersMod mod = Mod;
            if (mod == null || mod.ClientConfig == null || !mod.HasFreshData) return;

            KnownCreature nearest = null;
            double nearestDist = double.MaxValue;
            foreach (KnownCreature c in HitCreatures(mod, mapElem, args.X, args.Y))
            {
                Entity live;
                double x, y;
                bool clamped;
                bool keepAtEdge = c.Id == mod.TrackedId;
                if (!ScreenPos(mapElem, PositionOf(c, out live), BaseSize(mod), keepAtEdge, out x, out y, out clamped)) continue;
                double d = (x - args.X) * (x - args.X) + (y - args.Y) * (y - args.Y);
                if (d < nearestDist) { nearestDist = d; nearest = c; }
            }
            if (nearest == null) return;

            args.Handled = true;
            bool shift = IsShiftDown();
            if (shift)
            {
                Entity live;
                Vec3d pos = PositionOf(nearest, out live);
                mod.AddWaypointAt(nearest, pos.X, pos.Y, pos.Z);
            }
            else
            {
                mod.ToggleTracking(nearest);
            }
        }

        public override void OnMapOpenedClient()
        {
            base.OnMapOpenedClient();
            hasDragReference = false;
            mapMovedSinceClick = false;
        }

        private static System.Reflection.PropertyInfo keyStateProperty;
        private static int[] shiftKeyIndexes;

        /// <summary>
        /// Whether a Shift key is held. While the map is open the player's movement controls aren't updated,
        /// so the keyboard state is read directly (by reflection, in case the property's name differs),
        /// with the player's Shift control as a backup.
        /// </summary>
        private bool IsShiftDown()
        {
            try
            {
                if (shiftKeyIndexes == null)
                {
                    List<int> indexes = new List<int>();
                    foreach (string name in new string[] { "ShiftLeft", "ShiftRight", "LShift", "RShift" })
                    {
                        try
                        {
                            int index = Convert.ToInt32(Enum.Parse(typeof(GlKeys), name));
                            if (!indexes.Contains(index)) indexes.Add(index);
                        }
                        catch (Exception) { }
                    }
                    shiftKeyIndexes = indexes.ToArray();
                    keyStateProperty = FindProperty(capi.Input, "KeyboardKeyStateRaw") ?? FindProperty(capi.Input, "KeyboardKeyState");
                }

                bool[] keys = keyStateProperty != null ? keyStateProperty.GetValue(capi.Input, null) as bool[] : null;
                if (keys != null)
                {
                    foreach (int index in shiftKeyIndexes)
                    {
                        if (index >= 0 && index < keys.Length && keys[index]) return true;
                    }
                }
            }
            catch (Exception) { }

            return capi.World.Player != null && capi.World.Player.Entity != null && capi.World.Player.Entity.Controls.ShiftKey;
        }

        /// <summary>A public property by name, on the object's class or any interface it implements.</summary>
        private static System.Reflection.PropertyInfo FindProperty(object owner, string name)
        {
            System.Reflection.PropertyInfo prop = owner.GetType().GetProperty(name);
            if (prop != null) return prop;
            foreach (Type iface in owner.GetType().GetInterfaces())
            {
                prop = iface.GetProperty(name);
                if (prop != null) return prop;
            }
            return null;
        }

        /// <summary>Notes whether the map has been dragged since the last click.</summary>
        private void TrackMapMovement(GuiElementMap mapElem)
        {
            Vec2f now = new Vec2f();
            mapElem.TranslateWorldPosToViewPos(dragReference, ref now);
            if (hasDragReference && (Math.Abs(now.X - dragReferenceView.X) > 1 || Math.Abs(now.Y - dragReferenceView.Y) > 1))
            {
                mapMovedSinceClick = true;
            }
            dragReferenceView = now;
            hasDragReference = true;
        }

        /// <summary>This layer's visible creatures under the mouse.</summary>
        private List<KnownCreature> HitCreatures(CreatureMarkersMod mod, GuiElementMap mapElem, double mouseX, double mouseY)
        {
            List<KnownCreature> hits = new List<KnownCreature>();
            ClientConfig cfg = mod.ClientConfig;
            bool filtering = mod.FilterActive;
            double half = Math.Max(5, BaseSize(mod) * (filtering ? 0.7 : 0.5));

            foreach (KnownCreature c in mod.Known.Values)
            {
                if (c.Category != CategoryId || !c.HasPosition) continue;
                if (!mod.OnMyLevel(c)) continue;
                bool tracked = c.Id == mod.TrackedId;
                if (filtering && cfg.FilterOnlyMatches && !tracked && !mod.MatchesFilter(c)) continue;

                Entity live;
                double x, y;
                bool clamped;
                if (!ScreenPos(mapElem, PositionOf(c, out live), BaseSize(mod), tracked, out x, out y, out clamped)) continue;
                double reach = tracked ? half * 1.5 : half;
                if (Math.Abs(x - mouseX) > reach || Math.Abs(y - mouseY) > reach) continue;
                hits.Add(c);
            }
            return hits;
        }

        // =====================================================================
        // Positions and health
        // =====================================================================

        /// <summary>The live position if this creature is near enough for the client to have it, else the server's last position.</summary>
        private Vec3d PositionOf(KnownCreature c, out Entity live)
        {
            live = capi.World.GetEntityById(c.Id);
            if (live != null && live.Alive) return live.Pos.XYZ;
            live = null;
            return new Vec3d(c.X, c.Y, c.Z);
        }

        /// <summary>Live health if the creature is near enough for this game to have it, else what the server last sent.</summary>
        private static void HealthOf(KnownCreature c, Entity live, out float health, out float maxHealth)
        {
            health = c.Health;
            maxHealth = c.MaxHealth;
            if (live == null) return;
            float h, m;
            CreatureMarkersMod.ReadHealth(live, out h, out m);
            if (m > 0) { health = h; maxHealth = m; }
        }

        private static string FormatHp(float v)
        {
            double r = Math.Round(v, 1);
            return r == Math.Floor(r) ? ((int)r).ToString() : r.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Where a world position lands on the map element. Off the map returns false, unless keepAtEdge is set:
        /// then it is moved onto the nearest edge in the creature's direction (clamped = true).
        /// </summary>
        private bool ScreenPos(GuiElementMap mapElem, Vec3d worldPos, float size, bool keepAtEdge, out double x, out double y, out bool clamped)
        {
            mapElem.TranslateWorldPosToViewPos(worldPos, ref viewPos);

            double left = mapElem.Bounds.renderX;
            double top = mapElem.Bounds.renderY;
            double right = left + mapElem.Bounds.InnerWidth;
            double bottom = top + mapElem.Bounds.InnerHeight;

            x = left + viewPos.X;
            y = top + viewPos.Y;
            clamped = false;

            if (x >= left && x <= right && y >= top && y <= bottom) return true;
            if (!keepAtEdge) return false;

            double half = size / 2;
            double cx = (left + right) / 2;
            double cy = (top + bottom) / 2;
            double halfW = (right - left) / 2 - half;
            double halfH = (bottom - top) / 2 - half;
            if (halfW <= 0 || halfH <= 0) return false;

            double dx = x - cx;
            double dy = y - cy;
            double scale = Math.Min(
                dx != 0 ? halfW / Math.Abs(dx) : double.MaxValue,
                dy != 0 ? halfH / Math.Abs(dy) : double.MaxValue);

            x = cx + dx * scale;
            y = cy + dy * scale;
            clamped = true;
            return true;
        }

        // =====================================================================
        // Textures
        // =====================================================================

        private LoadedTexture GetShapeTexture(string fillHex, string borderHex, float size)
        {
            int px = Math.Max(6, (int)Math.Ceiling(size));
            string key = "shape|" + fillHex + "|" + borderHex + "|" + px;
            LoadedTexture existing;
            if (textureCache.TryGetValue(key, out existing)) return existing;

            double fr, fg, fb, fa, br, bg, bb, ba;
            if (!ColorHex.TryParse(fillHex, out fr, out fg, out fb, out fa)) { fr = 1; fg = 1; fb = 1; fa = 1; }
            if (!ColorHex.TryParse(borderHex, out br, out bg, out bb, out ba)) { br = 0; bg = 0; bb = 0; ba = 1; }

            double c = px / 2.0;
            double line = Math.Max(1.2, px / 9.0);
            double r = c - line;

            LoadedTexture tex;
            using (Cairo.ImageSurface surface = new Cairo.ImageSurface(Cairo.Format.Argb32, px, px))
            using (Cairo.Context ctx = new Cairo.Context(surface))
            {
                switch (CategoryId)
                {
                    case CreatureCategory.Monsters:     // diamond
                        ctx.MoveTo(c, c - r);
                        ctx.LineTo(c + r, c);
                        ctx.LineTo(c, c + r);
                        ctx.LineTo(c - r, c);
                        ctx.ClosePath();
                        break;
                    case CreatureCategory.Predators:    // triangle pointing up
                        ctx.MoveTo(c, c - r);
                        ctx.LineTo(c + r, c + r * 0.8);
                        ctx.LineTo(c - r, c + r * 0.8);
                        ctx.ClosePath();
                        break;
                    case CreatureCategory.Traders:      // square
                        ctx.Rectangle(c - r * 0.85, c - r * 0.85, r * 1.7, r * 1.7);
                        break;
                    case CreatureCategory.Villagers:    // rounded square
                        RoundedSquare(ctx, c, r * 0.85, r * 0.35);
                        break;
                    case CreatureCategory.Pets:         // heart
                        ctx.MoveTo(c, c + r * 0.9);
                        ctx.CurveTo(c - r * 1.5, c - r * 0.1, c - r * 0.55, c - r * 1.15, c, c - r * 0.35);
                        ctx.CurveTo(c + r * 0.55, c - r * 1.15, c + r * 1.5, c - r * 0.1, c, c + r * 0.9);
                        ctx.ClosePath();
                        break;
                    default:                            // animals and critters: circle
                        ctx.Arc(c, c, r * 0.85, 0, 2 * Math.PI);
                        break;
                }

                ctx.SetSourceRGBA(fr, fg, fb, fa);
                ctx.FillPreserve();
                ctx.LineWidth = line;
                ctx.SetSourceRGBA(br, bg, bb, ba);
                ctx.Stroke();

                int textureId = capi.Gui.LoadCairoTexture(surface, true);
                tex = new LoadedTexture(capi, textureId, px, px);
            }

            textureCache[key] = tex;
            return tex;
        }

        /// <summary>A white ring with a dark outline, so it shows on light and dark terrain.</summary>
        private LoadedTexture GetRingTexture(float size)
        {
            int px = Math.Max(8, (int)Math.Ceiling(size));
            string key = "ring|" + px;
            LoadedTexture existing;
            if (textureCache.TryGetValue(key, out existing)) return existing;

            double c = px / 2.0;
            double line = Math.Max(1.5, px / 12.0);
            double r = c - line * 1.5;

            LoadedTexture tex;
            using (Cairo.ImageSurface surface = new Cairo.ImageSurface(Cairo.Format.Argb32, px, px))
            using (Cairo.Context ctx = new Cairo.Context(surface))
            {
                ctx.Arc(c, c, r, 0, 2 * Math.PI);
                ctx.LineWidth = line * 2.2;
                ctx.SetSourceRGBA(0.05, 0.05, 0.05, 0.9);
                ctx.StrokePreserve();
                ctx.LineWidth = line;
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Stroke();

                int textureId = capi.Gui.LoadCairoTexture(surface, true);
                tex = new LoadedTexture(capi, textureId, px, px);
            }

            textureCache[key] = tex;
            return tex;
        }

        private LoadedTexture GetWhiteTexture()
        {
            LoadedTexture existing;
            if (textureCache.TryGetValue("white", out existing)) return existing;

            LoadedTexture tex;
            using (Cairo.ImageSurface surface = new Cairo.ImageSurface(Cairo.Format.Argb32, 4, 4))
            using (Cairo.Context ctx = new Cairo.Context(surface))
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Paint();
                int textureId = capi.Gui.LoadCairoTexture(surface, true);
                tex = new LoadedTexture(capi, textureId, 4, 4);
            }
            textureCache["white"] = tex;
            return tex;
        }

        private static void RoundedSquare(Cairo.Context ctx, double c, double half, double radius)
        {
            double l = c - half, t = c - half, rgt = c + half, b = c + half;
            ctx.NewPath();
            ctx.Arc(rgt - radius, t + radius, radius, -Math.PI / 2, 0);
            ctx.Arc(rgt - radius, b - radius, radius, 0, Math.PI / 2);
            ctx.Arc(l + radius, b - radius, radius, Math.PI / 2, Math.PI);
            ctx.Arc(l + radius, t + radius, radius, Math.PI, 3 * Math.PI / 2);
            ctx.ClosePath();
        }

        private void ClearTextures()
        {
            foreach (LoadedTexture tex in textureCache.Values)
            {
                if (tex != null) tex.Dispose();
            }
            textureCache.Clear();
        }

        public override void Dispose()
        {
            if (modSystem != null) modSystem.ConfigChanged -= ClearTextures;
            ClearTextures();
            base.Dispose();
        }
    }

    public class MonsterMapLayer : CreatureMapLayerBase
    {
        public MonsterMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Monsters;
    }

    public class PredatorMapLayer : CreatureMapLayerBase
    {
        public PredatorMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Predators;
    }

    public class AnimalMapLayer : CreatureMapLayerBase
    {
        public AnimalMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Animals;
    }

    public class TraderMapLayer : CreatureMapLayerBase
    {
        public TraderMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Traders;
    }

    public class VillagerMapLayer : CreatureMapLayerBase
    {
        public VillagerMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Villagers;
    }

    public class PetMapLayer : CreatureMapLayerBase
    {
        public PetMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Pets;
    }

    public class CritterMapLayer : CreatureMapLayerBase
    {
        public CritterMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int CategoryId => CreatureCategory.Critters;
    }
}
