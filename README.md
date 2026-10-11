# Cant be Bozja'ed (Botja)
((NOT FINAL, PLACEHOLDER))

Botja is a Dalamud plugin for FFXIV's Bozjan Southern Front and Zadnor.

Open the main window with `/cbb` or settings with `/cbb config`. The main window opens on entering either zone.

## Features

### FATE list and prioritization

- Lists active FATEs with sector, progress, remaining time, and travel ETA.
- Prioritizes FATEs by configurable weights; supports progress limits, sector limits, and a blacklist.
- Walks to a selected FATE or automatically navigates there. Mark chained FATEs for follow-up waits.

### Navigation and Auto Mode

- Uses aethernet travel and walks the remaining distance; mounts for longer trips when possible.
- Auto Mode selects and travels between FATEs, and queues recruiting Critical Engagements in the background.
- Switches to a CE when it commences; can wait near marked FATEs for follow-ups.

Navigation uses Lifestream for aethernet travel and vnavmesh for pathfinding. Hostile avoidance uses visible NPCs and heuristic aggro shapes; it cannot guarantee detection of every threat.

### Combat-AI integration

Enables selected combat-AI backends during FATEs and CEs, then disables them while travelling or when the activity ends. Includes a manual **Start Combat AI** control. Supports BossMod Misc AI, Wrath, Rotation Solver Reborn, BossMod, and BossMod Reborn.

### Other utilities

- Nearby-hostile status and aggro-shape visualization.
- Fragment appraising overlay with skip selection and list processing.
- Field-note exchange controls.

## Requirements

- vnavmesh for pathfinding and movement.
- Lifestream for aethernet travel.
- Optional combat-AI plugins for selected backends.

The build resolves Dalamud assemblies from the local Dalamud installation. Launching FFXIV through Dalamud populates the local development assembly folder used by the build.