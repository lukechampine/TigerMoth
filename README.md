# TigerMoth

Save state plugin for *To The Flame* speedrun practice.

- **H** — Save state
- **I** — Load state

Captures position, velocity, rotation, jumps, charging time, hitstop, and facing direction.

## Install (pre-built)

1. Install [BepInEx 5.x for macOS](https://github.com/BepInEx/BepInEx/releases) into your game directory
2. Copy `TigerMoth.dll` into `BepInEx/plugins/TigerMoth/`
3. Launch with `./run_bepinex.sh`

## Build from source

1. Clone this repo next to your `To The Flame.app`
2. `dotnet build -c Release` in the `TigerMoth/` directory
3. Copy `TigerMoth/bin/Release/TigerMoth.dll` to `BepInEx/plugins/TigerMoth/`
