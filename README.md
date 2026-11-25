# Creature Map Markers

**Creature Map Markers** shows every creature the server has loaded on your world map and minimap, updated live.

**Seven layers, each with its own colour and shape** – turn any of them on or off in the map's layer list:

- **Monsters** (red diamond): drifters, locusts, bells, shivers, bowtorn
- **Predators** (orange triangle): anything that attacks players on sight, detected automatically from each creature's AI. Works with creatures from any mod.
- **Animals** (green circle)
- **Pets & tamed** (pink heart), with the owner's name
- **Traders** (yellow square)
- **Villagers & NPCs** (blue rounded square)
- **Fish & critters** (purple dot), off by default

**Surface or underground:** you only see creatures on your level. Go into a cave and the map switches to underground creatures. Forests and houses still count as the surface.

- **Hover** a marker for its name, health and distance. Hurt creatures get a health bar.
- **Click** a creature to track it: you get a ring, a dotted line from you, and it stays on the map edge when it's off-screen. **Shift+click** drops a waypoint on it.
- **Search** for creatures by name, like "wolf, bear". Matches are highlighted and the rest fade or hide.

**Settings window:** type `.creaturemarkers`. Set colours with a colour picker, plus marker size, health bars, the search, and (for admins) server settings: update speed, max distance, admin only, fish & critters, and auto-detect predators.

**Commands:** `.creaturemarkers status`, `find <names>`, `clear`, `only`, `level`, `waypoint`, `untrack`, `size <4–32>`, `bars`.

It only sends changes, so busy areas stay light on the network. It can only show creatures in loaded areas, meaning the chunks around players.

Tested with Feverstone Wilds, Cats, Wolf Taming, NQ People and A Culinary Artillery. Install it on the server; players joining get it automatically.

## Download

ModDB (1-click install): https://mods.vintagestory.at/creaturemarkers

## Report a problem

Please report bugs on the [Issues](../../issues) tab and include your game version, the mod version and your log. Security problems: see [SECURITY.md](SECURITY.md).

**Where to find your log**

The logs are in the Vintage Story data folder, in a folder called `Logs`:

| System | Data folder |
|---|---|
| Windows | `%appdata%\VintagestoryData` (paste that into the File Explorer address bar) |
| Linux | `~/.config/VintagestoryData` |
| Mac | `~/Library/Application Support/VintagestoryData` |

- **Playing on a server:** send `client-main.log` from your own game. If you run the server, also send `server-main.log` from the server's data folder.
- **Singleplayer:** `client-main.log` and `server-main.log` are both in the data folder above.
- Attach the file to the issue (drag it into the box), or paste just the lines with errors. Reproduce the problem first, then grab the log straight afterwards, so the error is near the end.
