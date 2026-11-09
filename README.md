# Adomeji

Adomeji is a desktop pet mod for A Dance of Fire and Ice. Pets move around the game window, interact with gameplay surfaces, react to judgements and clears, and support configurable sprite packs and behavior.

- Original Adomeji by koren
- Modified version by IMPL_
- [View the original project](https://github.com/kkorenn/adomeji)

## Features

- Animated desktop pets with walking, falling, climbing, jumping, dragging, and celebration states
- Interaction with screen boundaries, tiles, decorations, and planets
- Judgement, clear, pure-perfect, and failure reactions
- Multiple pets, weighted sprite-pack selection, and per-pack behavior settings
- Shimeji-ee sprite packs, `actions.xml`, and `sprite-roles.txt` support
- Component-based in-game settings UI
- Versioned runtime with verified automatic updates and rollback
- [TUFReplay](https://tufreplay.impl1113.dev) support: pets show up in rendered replay videos and move at the video's speed

## TUFReplay

Adomeji works with TUFReplay without any setup. Neither mod needs the other.

- **In-game replays**: pets react to the replayed judgements, clears and deaths, as in normal play.
- **Videos rendered from a TUFReplay recording** (with OrbitRender):
  - Pets are in the video.
  - They move at the video's speed, although the game renders slower than real time.
  - They fit the video's resolution, keeping their place on screen.
  - They can't be dragged while the video renders.

`Adomeji/Integrations/TUFReplay` registers with TUFReplay's `ReplayApi`. It finds that API by name and has no reference to `TUFReplay.dll`.

## Requirements

- .NET SDK 8 or newer
- A Dance of Fire and Ice
- Unity Mod Manager installed for A Dance of Fire and Ice

The build scripts use the default macOS Steam installation path. Copy `.env.example` to `.env` and adjust the paths when your installation is elsewhere.

## Build

```bash
./scripts/run.sh check
./scripts/run.sh build
./scripts/run.sh package
```

- `check` validates scripts and runs the executable test projects.
- `build` creates a Debug build, validates Unity/Mono compatibility, and installs it to `Mods/Adomeji`.
- `package` creates a verified Release build and produces `build/Adomeji.zip` and `build/Adomeji.update.json`.

## License and attribution

The original Adomeji project is licensed under the MIT License. Its license is included in [`LICENSE.upstream-adomeji`](LICENSE.upstream-adomeji), with additional attribution and bundled ELLIE sprite information in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
