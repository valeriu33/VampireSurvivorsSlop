# Survivors (working title)

A mobile-first, isometric survivors-like built in **F#** and compiled to the browser
with **Fable**, on a hand-rolled **ECS**, designed from the start to grow into
**co-op multiplayer** with an authoritative server.

Current status: **Phases 0–2 complete** — a playable single-player run.

---

## Running it

```bash
npm install        # JS deps (Pixi, Vite)
dotnet tool restore  # Fable
npm run dev        # Fable watch + Vite, served on 0.0.0.0:5173
```

Open `http://<your-lan-ip>:5173` on a phone on the same network. Vite is bound to
all interfaces specifically so the game can be tested where it is meant to be
played; desktop browsers work too, with WASD/arrow keys standing in for the stick.

```bash
npm run build      # Fable -> build/client, then Vite -> dist/
npm run typecheck  # F# typecheck without emitting JS
npm test           # build + headless simulation test
```

### Toolchain

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | **10.0** | Required: Fable 5 ships as a `net10.0` tool |
| Fable | 5.17.2 | `dotnet tool restore` |
| Node | 22 | |
| PixiJS | 8 | WebGL/WebGPU renderer |
| Vite | 8 | Dev server and bundler |

The F# projects target `net8.0`; only the Fable compiler itself needs .NET 10.

---

## Testing

Two layers, because they catch entirely different things.

```bash
npm run test:sim                       # headless simulation, no DOM
npm run preview &                      # then, in another shell:
npm run test:browser                   # real Chromium, phone viewport
```

**`tests/sim-smoke.mjs`** imports the Fable-compiled simulation into Node and
plays full 5-minute runs with scripted input. It asserts the game is actually
playable (kills, levels, upgrades acquired), that the player can die, that
entity slots are recycled rather than leaked, that the same seed reproduces a
run bit for bit, and that per-tick cost has not regressed.

**`tests/browser-check.mjs`** boots the built bundle in a phone-sized headless
Chromium, drives the joystick with real pointer events, and checks what only a
browser can prove: the canvas honours `devicePixelRatio`, the stick tracks a
drag, the HUD is live, and a level-up card appears with three choices. It also
writes screenshots.

The browser layer is not redundant. Run against a build the simulation test
declared healthy, it found three bugs:

| Bug | Why the sim test could not see it |
|---|---|
| `Graphics.poly` silently drew nothing — Fable maps `float[]` to `Float64Array`, Pixi needs a real `Array` | No ground, no XP gems — purely visual |
| Enemies spawned on a circle enclosing a portrait viewport, ~5 screen-widths off to the sides | Simulation was correct; the player just never saw the fight |
| `TilingSprite` fed a `RenderTexture` renders nothing at all | Texture read back with correct pixels; screen stayed at the clear colour |

---

## Architecture

```
src/Shared/      F# simulation — compiles to BOTH JS (Fable) and .NET (server)
  Core.fs          Math, deterministic RNG, isometric basis
  Ecs.fs           SoA entity store, spatial hash
  Content.fs       Balance tables: enemies, weapons, upgrades
  Sim.fs           Run state, entity factories
  Systems.fs       Movement, AI, collision, weapons, pickups, spawning
  Step.fs          The fixed-timestep tick

src/Client/      F# — browser only
  Interop/         Hand-written PixiJS 8 bindings, browser helpers
  Render/          Iso projection, procedural atlas, depth-sorted renderer
  Input/           Floating virtual joystick
  Hud/             DOM overlay
  Main.fs          Bootstrap and frame loop
```

### Why the Shared project exists now, while the game is single-player

It is the entire reason to pick F# for this. In Phase 4 the authoritative server
runs `Shared` compiled to .NET, while the client runs the *same source* compiled
to JS for prediction. Divergence between prediction and server truth becomes a
bug rather than a permanent maintenance tax. That only works if `Shared` never
acquires a dependency on the DOM, Pixi, or threads — so it hasn't, and CI builds
it for .NET on every push to keep it that way.

### Design decisions worth knowing

**Struct-of-arrays ECS, not an ECS library.** Every component is a flat typed
array (Fable maps `float32[]` to `Float32Array`). Entity handles pack a slot
index with a generation counter so stale references are detectable. Systems are
plain functions over the world, run in an explicit order.

**No allocation in the hot path.** No `seq`, no `option`, no tuple returns, no
per-entity closures inside systems. These are the F#-on-JS traps that never show
up on a desktop profile and always show up on a phone.

**Fixed 30 Hz simulation, interpolated rendering.** Display rate and simulation
rate are fully decoupled. This is what makes a 120 Hz tablet and a 40 fps phone
play the same game, and it is a hard prerequisite for the Phase 4 server.

**Isometric is a render-time projection only.** The simulation is a flat 2D
plane; `screenX = (wx - wy) * 32`, `screenY = (wx + wy) * 16`. The joystick is
**screen-aligned** — pushing up moves the character up the screen — and the
inverse projection turns that into world velocity. Facing is one of 8 screen
directions, but movement itself stays analog so it feels smooth rather than
notched.

**Depth sorting without a sort.** Isometric depth is exactly screen Y, and
screen Y is bounded by the viewport, so visible entities are bucketed by integer
scanline in O(n) and written to the sprite pool in that order. Pool slot *k* is
child *k*, so Pixi's child order already *is* the depth order — `sortableChildren`
is never enabled and no comparison sort runs.

**One grid rebuild per tick.** The spatial hash is rebuilt by counting sort right
after integration, so collision queries in the current tick and steering queries
in the next both see current positions.

**Deferred entity removal.** Systems mark entities dead; a single end-of-tick
sweep frees them. No system ever mutates the entity set while iterating it.

**Unchecked indexing in the three hot files.** Fable compiles every `arr.[i]`
read into a bounds-checked helper call. Measured on the headless benchmark at
620 enemies, that check costs 0.60 ms/tick against 0.33 ms/tick without it —
45% of the simulation budget. So `Ecs`, `Systems` and `Renderer` index through
`Core.ix`/`Core.setIx`, which compile to raw `arr[i]` under Fable and to normal
checked indexing on .NET. Everything else keeps the bounds check.

**Adaptive enemy culling.** Despawn distance scales with population pressure.
With a fixed generous margin, distant stragglers that can never catch the player
fill the enemy cap and starve spawning near the player: a 5-minute run produced
537 kills at a 4.0x margin against 1730 at 3.5x, and the whole balance hinged on
which side of that cliff the constant sat. Tightening the cull as the cap fills
removes the cliff — kills now stay in a 1000–2500 band across a 3x sweep of the
same parameter.

---

## Replacing the placeholder art

All textures are currently generated procedurally at boot in
`src/Client/Render/Atlas.fs`. This is a deliberate seam.

Characters, gems, projectiles and the nova ring are drawn with Pixi `Graphics`
and baked with `generateTexture`. The ground tile is drawn on a plain 2D canvas
instead and wrapped with `Texture.from` — a `TilingSprite` fed a `RenderTexture`
renders nothing, which cost an afternoon to pin down and is noted here so it
does not cost another one.

> **Note:** CC0 sprite packs could not be fetched in the environment this was
> built in — `kenney.nl`, `opengameart.org` and `itch.io` are all blocked by the
> egress proxy, and npm carries no usable mirror. The pipeline is built for real
> spritesheets; only the pack itself is missing.

The renderer only ever asks `Atlas` for a `SpriteSet`:

```fsharp
type SpriteSet =
    { Textures: ITexture[]   // 8 frames for directional kinds, 1 otherwise
      AnchorY: float         // where the entity's feet sit in the texture
      RotateByFacing: bool   // rotate one frame instead of swapping frames
      BaseScale: float }
```

To drop in a real pack, rewrite `Atlas.build` to `Assets.load` a spritesheet and
slice frames into that shape. No renderer or gameplay change is needed.

Directional frames are ordered by **screen** facing:
`0 = E, 1 = SE, 2 = S, 3 = SW, 4 = W, 5 = NW, 6 = N, 7 = NE`.

Recommended CC0 sources when you have unrestricted network access:
- **Kenney** "Isometric Characters" / "Tiny Dungeon" (CC0, consistent style)
- **OpenGameArt** — filter by CC0 and "isometric", 8-direction sets exist
- **LPC**-style sheets are 4-direction; they need mirroring or regeneration for 8

---

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| **0** | Toolchain, scaffold, CI | ✅ |
| **1** | ECS core, iso renderer, camera, joystick | ✅ |
| **2** | Vertical slice: enemies, weapons, XP, level-up, death | ✅ |
| **3** | Content and polish: more weapons, elites, audio, juice, device perf pass | — |
| **4** | Netcode foundation: .NET server, protocol, prediction/reconciliation, 2 players on one map | — |
| **5** | Co-op: shared enemy pool, scaling by player count, revive, join-in-progress, AoI + delta compression | — |
| **6** | Meta: characters, unlocks, persistence | — |

### Multiplayer shape (decided, not yet built)

Private co-op rooms — 2–4 players join by code into one shared arena, with
join-in-progress. Authoritative F# server on ASP.NET Core, binary WebSocket
transport, 30 Hz server tick, 15 Hz snapshots.

The hard problem is enemy bandwidth: ~800 enemies at 15 Hz is roughly 96 KB/s
per client uncompressed, which is not viable on mobile data. Mitigations, in the
order they get applied:

1. Quantize positions to `int16` fixed-point
2. Area-of-interest filtering per client viewport
3. Delta-encode against the last acknowledged snapshot
4. If still too large: server-seeded deterministic enemy spawns simulated
   client-side, with the server correcting only enemies that interact with a player

**Deterministic lockstep was considered and rejected.** It would cut bandwidth to
almost nothing, but it requires bit-identical float results between .NET and JS,
and `Math.sin`/`Math.sqrt` carry no such guarantee across runtimes. That would
make every gameplay change a determinism risk. The snapshot model is the robust
choice; the fixed timestep keeps lockstep available as a fallback if bandwidth
ever forces the issue.

---

## Tuning

Balance lives entirely in `src/Shared/Content.fs` — enemy stats, the difficulty
ramp, weapon scaling per level, passive multipliers, and the XP curve. Changing
the feel of the game should never require touching a system.
