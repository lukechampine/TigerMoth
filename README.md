# TigerMoth

A speedrun practice and timer mod for *To The Flame*.

## Features

### Save States

- **H** — Save state at current position
- **I** — Load saved state (reloads scene and restores)
- **1** — Reset to game start (practice first split)
- **2–5** — Load hardcoded checkpoints (Before Church, Before Gift, Mid-Tower, Near End)

Captures full game state: position, velocity, jumps, facing direction, charging time, hitstop, animation, camera, spider, and ExtraJump powerup.

### Split Timer

Automatic split timing with four segments: **Church**, **Gift**, **Tower**, **End**. Splits trigger on area entry (Church, Tower), item pickup (Gift), or run completion (End).

- Tracks **personal best** and **gold segments** (best individual split times), persisted across sessions
- Shows **delta times** against PB (normal mode) or gold segments (practice mode)
- **Live delta** appears when approaching PB/gold pace
- **Best Possible Time** updates as you complete splits (actual segments + remaining golds)
- **Practice mode** activates on any state load — skips the loaded split, shows segment-only times, and labels the BPT as "Sum of Best"

### Ghost

A translucent ghost moth replays your best run or gold segments for visual pacing.

- **PB ghost** plays during normal runs
- **Gold segment ghosts** play during practice mode (one per split)
- **G** — Toggle ghost visibility
- Ghost data is saved to disk alongside PB/gold times

### Camera Zoom

- **[** — Zoom in
- **]** — Zoom out

Works reliably in all areas, including sections where the game dynamically adjusts camera zoom.

## Install (pre-built)

1. Install [BepInEx 5.x for macOS](https://github.com/BepInEx/BepInEx/releases) into your game directory
2. Copy `TigerMoth.dll` into `BepInEx/plugins/TigerMoth/`
3. Launch with `./run_bepinex.sh`

## Build from source

1. Clone this repo next to your `To The Flame.app`
2. `dotnet build -c Release` in the `TigerMoth/` directory
3. The DLL is automatically copied to `BepInEx/plugins/TigerMoth/`
