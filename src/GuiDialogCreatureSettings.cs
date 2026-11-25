using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreatureMarkers
{
    /// <summary>
    /// Settings window opened with .creaturemarkers.
    /// Colours for each kind of creature (colour square, hex box and Edit button for the colour picker),
    /// display options, the search, and for admins the server settings.
    /// Nothing changes until Save &amp; Close.
    /// </summary>
    public class GuiDialogCreatureSettings : GuiDialog
    {
        private readonly CreatureMarkersMod mod;
        private GuiDialogColorPicker picker;

        // Values being edited
        private readonly string[] colors = new string[CreatureCategory.Count];
        private string sizeText, filterText;
        private bool showBars, barsOnlyHurt, trackingLine, onlyMatches, onlyMyLevel;
        private string intervalText, distanceText;
        private bool adminOnly, showCritters, autoPredators;

        // Ignore text-change events while the window fills in its own values.
        private bool composing;

        /// <summary>Order the colour rows appear in (two columns).</summary>
        private static readonly int[] ColorOrder =
        {
            CreatureCategory.Monsters, CreatureCategory.Predators, CreatureCategory.Animals, CreatureCategory.Pets,
            CreatureCategory.Traders, CreatureCategory.Villagers, CreatureCategory.Critters
        };

        public GuiDialogCreatureSettings(ICoreClientAPI capi, CreatureMarkersMod mod) : base(capi)
        {
            this.mod = mod;
        }

        public override string ToggleKeyCombinationCode => null;

        public void Open()
        {
            ClientConfig cfg = mod.ClientConfig;
            for (int i = 0; i < CreatureCategory.Count; i++) colors[i] = cfg.ColorFor(i);
            sizeText = cfg.MarkerSize.ToString();
            filterText = cfg.Filter ?? "";
            showBars = cfg.ShowHealthBars;
            barsOnlyHurt = cfg.HealthBarsOnlyWhenHurt;
            trackingLine = cfg.ShowTrackingLine;
            onlyMatches = cfg.FilterOnlyMatches;
            onlyMyLevel = cfg.OnlyMyLevel;

            CreatureSettingsPacket s = mod.ServerSettings;
            intervalText = s.UpdateIntervalMs.ToString();
            distanceText = s.MaxDistance.ToString();
            adminOnly = s.AdminOnly;
            showCritters = s.ShowCritters;
            autoPredators = s.AutoDetectPredators;

            Compose();
            TryOpen();
        }

        private void Compose()
        {
            const double colWidth = 330;
            const double colGap = 30;
            const double labelWidth = 120;
            const double swatchSize = 28;
            const double hexWidth = 92;
            const double editWidth = 70;
            const double rowHeight = 38;
            const double toggleLabelWidth = 200;
            const double fullWidth = colWidth * 2 + colGap;
            double right = colWidth + colGap;
            double top = GuiStyle.TitleBarHeight + 5;

            CairoFont textFont = CairoFont.WhiteSmallText();
            CairoFont headFont = CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold);
            CairoFont hintFont = CairoFont.WhiteDetailText();

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            GuiComposer composer = capi.Gui
                .CreateCompo("creaturemarkers-settings", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("creaturemarkers:gui-title"), OnTitleClose)
                .BeginChildElements(bgBounds);

            double y = top;

            // Colours, two columns
            composer.AddStaticText(Lang.Get("creaturemarkers:gui-colors"), headFont, ElementBounds.Fixed(0, y, fullWidth, 25));
            y += 30;
            for (int i = 0; i < ColorOrder.Length; i++)
            {
                double x = (i % 2 == 0) ? 0 : right;
                double rowY = y + (i / 2) * rowHeight;
                AddColorRow(composer, x, rowY, labelWidth, swatchSize, hexWidth, editWidth, ColorOrder[i], textFont);
            }
            y += ((ColorOrder.Length + 1) / 2) * rowHeight + 6;

            // Display
            composer.AddStaticText(Lang.Get("creaturemarkers:gui-display"), headFont, ElementBounds.Fixed(0, y, fullWidth, 25));
            y += 30;
            composer.AddStaticText(Lang.Get("creaturemarkers:gui-size"), textFont, ElementBounds.Fixed(0, y + 5, toggleLabelWidth, 25));
            composer.AddTextInput(ElementBounds.Fixed(toggleLabelWidth, y, 60, 28), text => { if (!composing) sizeText = text; }, textFont, "size");
            AddToggleRow(composer, right, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-bars"), showBars, () => { showBars = !showBars; Compose(); return true; }, "bars", textFont);
            y += rowHeight;
            AddToggleRow(composer, 0, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-line"), trackingLine, () => { trackingLine = !trackingLine; Compose(); return true; }, "line", textFont);
            AddToggleRow(composer, right, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-bars-hurt"), barsOnlyHurt, () => { barsOnlyHurt = !barsOnlyHurt; Compose(); return true; }, "barshurt", textFont);
            y += rowHeight;
            AddToggleRow(composer, 0, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-my-level"), onlyMyLevel, () => { onlyMyLevel = !onlyMyLevel; Compose(); return true; }, "mylevel", textFont);
            y += rowHeight + 6;

            // Search
            composer.AddStaticText(Lang.Get("creaturemarkers:gui-search"), headFont, ElementBounds.Fixed(0, y, fullWidth, 25));
            y += 30;
            composer.AddTextInput(ElementBounds.Fixed(0, y, colWidth, 28), text => { if (!composing) filterText = text; }, textFont, "filter");
            AddToggleRow(composer, right, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-only-matches"), onlyMatches, () => { onlyMatches = !onlyMatches; Compose(); return true; }, "only", textFont);
            y += 32;
            composer.AddStaticText(Lang.Get("creaturemarkers:gui-search-hint"), hintFont, ElementBounds.Fixed(0, y, fullWidth, 40));
            y += 44;

            // Server settings, admins only
            if (mod.ServerSettings.IsAdmin)
            {
                composer.AddStaticText(Lang.Get("creaturemarkers:gui-server"), headFont, ElementBounds.Fixed(0, y, fullWidth, 25));
                y += 30;
                composer.AddStaticText(Lang.Get("creaturemarkers:gui-interval"), textFont, ElementBounds.Fixed(0, y + 5, toggleLabelWidth, 25));
                composer.AddTextInput(ElementBounds.Fixed(toggleLabelWidth, y, 80, 28), text => { if (!composing) intervalText = text; }, textFont, "interval");
                composer.AddStaticText(Lang.Get("creaturemarkers:gui-distance"), textFont, ElementBounds.Fixed(right, y + 5, toggleLabelWidth, 25));
                composer.AddTextInput(ElementBounds.Fixed(right + toggleLabelWidth, y, 80, 28), text => { if (!composing) distanceText = text; }, textFont, "distance");
                y += rowHeight;
                AddToggleRow(composer, 0, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-admin-only"), adminOnly, () => { adminOnly = !adminOnly; Compose(); return true; }, "adminonly", textFont);
                AddToggleRow(composer, right, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-critters"), showCritters, () => { showCritters = !showCritters; Compose(); return true; }, "critters", textFont);
                y += rowHeight;
                AddToggleRow(composer, 0, y, toggleLabelWidth, Lang.Get("creaturemarkers:gui-auto-predators"), autoPredators, () => { autoPredators = !autoPredators; Compose(); return true; }, "autopred", textFont);
                y += rowHeight + 6;
            }

            composer.AddButton(Lang.Get("creaturemarkers:cancel"), OnCancel, ElementBounds.Fixed(0, y + 6, 130, 32));
            composer.AddButton(Lang.Get("creaturemarkers:save-close"), OnSave, ElementBounds.Fixed(fullWidth - 170, y + 6, 170, 32));

            composer.EndChildElements();
            SingleComposer = composer.Compose();

            composing = true;
            for (int i = 0; i < ColorOrder.Length; i++)
            {
                int cat = ColorOrder[i];
                SingleComposer.GetTextInput("color" + cat).SetValue(colors[cat]);
            }
            SingleComposer.GetTextInput("size").SetValue(sizeText);
            SingleComposer.GetTextInput("filter").SetValue(filterText);
            if (mod.ServerSettings.IsAdmin)
            {
                SingleComposer.GetTextInput("interval").SetValue(intervalText);
                SingleComposer.GetTextInput("distance").SetValue(distanceText);
            }
            composing = false;
        }

        private static string CategoryLabel(int category)
        {
            return Lang.Get("creaturemarkers:layer-" + CreatureCategory.Keys[category]);
        }

        /// <summary>Label, colour square (click to edit), hex box, Edit button.</summary>
        private void AddColorRow(GuiComposer composer, double x, double y, double labelWidth, double swatchSize, double hexWidth, double editWidth,
            int category, CairoFont font)
        {
            string key = "color" + category;
            double sx = x + labelWidth;
            double hx = sx + swatchSize + 8;
            double ex = hx + hexWidth + 8;

            composer.AddStaticText(CategoryLabel(category), font, ElementBounds.Fixed(x, y + 5, labelWidth, 25));
            composer.AddInteractiveElement(new GuiElementColorSwatch(capi,
                ElementBounds.Fixed(sx, y, swatchSize, swatchSize),
                () => colors[category],
                () => OpenPicker(category)), key + "-swatch");
            composer.AddTextInput(ElementBounds.Fixed(hx, y, hexWidth, 28), text => { if (!composing) colors[category] = text; }, font, key);
            composer.AddButton(Lang.Get("creaturemarkers:edit"), () => { OpenPicker(category); return true; },
                ElementBounds.Fixed(ex, y - 1, editWidth, 30), EnumButtonStyle.Small, key + "-edit");
        }

        private void AddToggleRow(GuiComposer composer, double x, double y, double labelWidth, string label, bool value,
            ActionConsumable onClick, string key, CairoFont font)
        {
            composer.AddStaticText(label, font, ElementBounds.Fixed(x, y + 5, labelWidth, 25));
            composer.AddButton(Lang.Get(value ? "creaturemarkers:yes" : "creaturemarkers:no"), onClick,
                ElementBounds.Fixed(x + labelWidth, y, 60, 28), EnumButtonStyle.Small, key);
        }

        /// <summary>Opens the colour picker for one kind of creature. Save in the picker writes the colour back here.</summary>
        private void OpenPicker(int category)
        {
            try
            {
                if (picker == null)
                {
                    picker = new GuiDialogColorPicker(capi);
                    capi.Gui.RegisterDialog(picker);
                }

                picker.Open(colors[category], CategoryLabel(category), hex =>
                {
                    colors[category] = hex;
                    if (SingleComposer != null)
                    {
                        composing = true;
                        SingleComposer.GetTextInput("color" + category).SetValue(hex);
                        composing = false;
                    }
                });
            }
            catch (Exception e)
            {
                capi.Logger.Error("[creaturemarkers] Could not open the colour picker: {0}", e);
                capi.ShowChatMessage("[Creature Map Markers] Could not open the colour picker: " + e.Message);
            }
        }

        public override void OnGuiClosed()
        {
            if (picker != null && picker.IsOpened()) picker.TryClose();
            base.OnGuiClosed();
        }

        private bool OnSave()
        {
            ClientConfig cfg = mod.ClientConfig;

            for (int i = 0; i < CreatureCategory.Count; i++)
            {
                string hex = ColorHex.Normalize(colors[i]);
                if (hex != null) cfg.SetColor(i, hex);
            }

            int size;
            if (int.TryParse(sizeText, out size)) cfg.MarkerSize = size;
            cfg.ShowHealthBars = showBars;
            cfg.HealthBarsOnlyWhenHurt = barsOnlyHurt;
            cfg.ShowTrackingLine = trackingLine;
            cfg.Filter = (filterText ?? "").Trim();
            cfg.FilterOnlyMatches = onlyMatches;
            cfg.OnlyMyLevel = onlyMyLevel;
            mod.SaveClientConfig();

            if (mod.ServerSettings.IsAdmin)
            {
                int interval, distance;
                if (!int.TryParse(intervalText, out interval)) interval = mod.ServerSettings.UpdateIntervalMs;
                if (!int.TryParse(distanceText, out distance)) distance = mod.ServerSettings.MaxDistance;

                CreatureSettingsPacket s = mod.ServerSettings;
                bool changed = interval != s.UpdateIntervalMs || distance != s.MaxDistance || adminOnly != s.AdminOnly
                    || showCritters != s.ShowCritters || autoPredators != s.AutoDetectPredators;
                if (changed) mod.SendServerSettings(interval, distance, adminOnly, showCritters, autoPredators);
            }

            TryClose();
            return true;
        }

        private bool OnCancel()
        {
            TryClose();
            return true;
        }

        private void OnTitleClose()
        {
            TryClose();
        }
    }
}
