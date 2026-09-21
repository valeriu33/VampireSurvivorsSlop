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

### Feel

**Hit feedback.** A struck entity swaps to a white silhouette baked into the
atlas alongside its normal frames — same draw call, no additive second pass,
and it reads as an impact rather than the colour shift a tint could manage.
Damage numbers spawn as one entity per digit, so they inherit integration,
lifetime and depth sorting from the existing systems instead of needing a text
renderer. Deaths leave a puff tinted to whatever died. Knockback was tripled;
at its old strength a hit read as a number changing rather than a blow landing.

**Run shape.** Elites are a modifier on any enemy type rather than their own
entry — the crowd stays the crowd, but some of it is worth stopping for. Bosses
arrive on a fixed schedule (2:00, 5:00, 8:00, 11:00), one at a time, each
landing with a shockwave that shoves the crowd aside. A boss that outlives its
welcome leaves rather than blocking the schedule forever.

**Camera shake** is trauma-based: impacts add to a 0..1 pool that decays, and
the offset is trauma *squared*, so a bolt stays subtle while a boss death
genuinely shoves the view. The camera itself is displaced, so ground and sprites
move together. Disabled entirely under `prefers-reduced-motion`.

**Sound** is synthesised at runtime — oscillators, envelopes and one shared
noise buffer — so the game ships no audio assets and fetches nothing. The hard
constraint is rate, not fidelity: a nova landing in a crowd resolves a hundred
hits in a tick, so every sound kind is rate-limited and the mixer has a
per-frame voice ceiling. Gem pickups climb in pitch while gems keep arriving,
which turns hoovering up a big drop into a run of notes.

### Performance overlay

Tap the stopwatch chip below the HUD bars, press **P** on desktop, or append
`?perf=1` to the URL to open a live readout:

| | |
|---|---|
| `fps` / `frame` / `worst` / `stutters` | the verdict — average frame time, worst in the last ~2s, and how many of the last 120 frames ran over 20ms |
| `sim` / `draw` / `other` | where the frame went |
| `ticks` | simulation steps per frame; above 1.0 means the loop is catching up |
| `entities` / `sprites` | live entity count and sprites actually drawn after culling |

`sim` and `draw` are **CPU time only**. The GPU runs on past `app.render()`, so
the real bound on frame rate is `frame` (the rAF delta), and `other` is
everything unaccounted for: GPU wait, vsync, and the browser's own work. The
panel rewrites at 5 Hz while the counters accumulate every frame, so reading the
numbers does not distort them.

### Dev affordances

`?perf=1` opens the overlay on load. `?skip=120` starts the clock partway in —
the run's content is time-gated (bosses at 2, 5, 8 and 11 minutes), so without
it every check of the last boss costs eleven real minutes. The skip only moves
the difficulty ramp forward, so it makes the game harder, never easier, and the
world still starts empty.

### Deploying

`dist/` is a plain static site — any static host will serve it.

**GitHub Pages** is wired up in `.github/workflows/pages.yml`: every push to the
default branch builds, runs the simulation test, and deploys. It needs enabling
once by hand — *Settings → Pages → Build and deployment → Source: **GitHub
Actions*** — and until that is set, `configure-pages` fails with a 404. The
build reads `PUBLIC_BASE` for the `/<repo>/` path prefix that project Pages
serve from.

For hosts that serve a **single file**, `npm run build:standalone` emits
`dist-standalone/game.html`: the stylesheet and the whole JS bundle inlined into
one ~560 KB page, with no doctype/html/body wrapper, so a host that supplies its
own document skeleton can serve it directly. It uses a separate Vite config with
`inlineDynamicImports`, because Pixi's lazy WebGL/WebGPU backends would otherwise
split the bundle across several chunks.

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
npm run test:input                     # joystick, pure functions, no DOM
npm run preview &                      # then, in another shell:
npm run test:browser                   # real Chromium, phone viewport
```

**`tests/sim-smoke.mjs`** imports the Fable-compiled simulation into Node and
plays full 5-minute runs with scripted input. It asserts the game is actually
playable (kills, levels, upgrades acquired), that the player can die, that
entity slots are recycled rather than leaked, that the same seed reproduces a
run bit for bit, that the event ring saturates at its cap instead of growing,
and that per-tick cost has not regressed.

It also measures **how long a first run lasts** — playing without god mode,
moving but not kiting cleanly, taking whichever upgrade is offered first. That
check exists because the balance had only ever been validated with god mode on,
and an actual first run was lasting **39 seconds**. It now lands around 130s
for that deliberately mediocre play, which a coherent build and real kiting
should extend several times over.

**`tests/input-check.mjs`** drives `readInto` directly from the compiled
module — it is a pure function, so a record literal stands in for a real stick.
It pins the two properties that are easy to regress and hard to see: throttle
must not depend on deflection, and direction must stay analog rather than
snapping to eight ways.

**`tests/browser-check.mjs`** boots the built bundle in a phone-sized headless
Chromium, drives the joystick with real pointer events, and checks what only a
browser can prove: the canvas honours `devicePixelRatio`, the stick tracks a
drag, the HUD is live, the perf overlay reports plausible numbers, and a
level-up card appears with three choices. It also writes screenshots.

It drives the stick in an orbit rather than a straight line, and polls for the
level-up card instead of sleeping a fixed span — a straight-line hold outruns
its own XP gems, which made the assertion flaky.

It drives pause (the clock must actually stop), and selects overlays by class
rather than DOM order — adding the pause overlay shifted the indices and
silently pointed an assertion at the wrong element.

It also wraps `AudioContext` before the page loads and counts the nodes the
synth creates. Sound is inaudible from a headless browser and produces no
visible output, so counting oscillators is the only way to know it fires at all
— and it catches the noise buffer being rebuilt per sound rather than once.

It asserts HUD geometry too: the header grows when a boss bar appears, and
anything pinned at a fixed offset lands on top of it — which is exactly what
the pause and perf buttons used to do.

`npm run capture -- <url> <prefix> [seconds]` plays for a while and screenshots
mid-combat. Note that headless Chromium runs slower than real time, so reaching
a two-minute boss that way takes far longer than two minutes — use `?skip`. A boot screenshot shows an empty field, which is exactly the state
that hides every problem worth seeing.

Note that **frame timings from this test are meaningless**: headless Chromium
rasterises in software, so the compositor cost lands in `other` and reports
~12fps regardless. It validates correctness, never performance. Real numbers
have to come off a real device via the overlay.

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

### What a boss fight taught us about auto-targeting

A boss is only a fight if your weapons can reach it, and three separate things
stopped that:

1. **Auto-targeting picks the nearest enemy**, and among several hundred that is
   never the boss. It took only incidental splash: 900 HP survived over three
   minutes of sustained fire.
2. **The crowd eats the projectiles.** At the level the first boss arrives the
   bolt has no pierce, so every shot is consumed by the first body it touches.
   Isolating the boss cut the kill from 90s to 34s with nothing else changed.
3. **An unkilled boss suppressed the whole run** — it slowed spawns indefinitely
   and blocked every later boss, dropping a 300s run from 2092 kills to 592.

The fixes were a short-range boss focus, a spawn slowdown and arrival shockwave
that open firing lines, and a hard cap on how long a boss may stay. The focus
*range* turned out to matter as much as the rule:

| focus range | boss 1 | boss 2 | crowd kills |
|---|---|---|---|
| 9 | 26s | 43s | 2373 |
| 13 | 25s | 17s | 592 |

A wide range is a permanent target lock: while a boss is up the crowd goes
entirely unkilled and the player is overrun. Short means closing with the boss
to focus it down, and backing off returns the weapons to crowd control. That
tension is the fight.

### How the simulation talks to the presentation layer

The simulation cannot call into audio or spawn cosmetic entities — it compiles
to the server too. Instead it appends plain numeric records (kind, x, y, value)
to a fixed ring buffer, which the client drains once per frame to play sounds
and spawn damage numbers and puffs.

This is not only for sound. It is the same stream the Phase 4 server will
serialize, so a client can present a hit or a death immediately rather than
waiting for the next position snapshot to imply one.

Cosmetic entities live in the same ECS world as everything else, which is why
they need no systems of their own. When the server becomes authoritative,
applying a snapshot will have to leave them alone rather than reconciling them
away.

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
**screen-aligned** — pushing up moves the character up the screen. Facing is one
of 8 screen directions, but the direction of travel stays analog so it feels
smooth rather than notched.

**On-screen speed is uniform in every direction.** That projection makes a world
unit cover twice the pixels going east-west as north-south, so constant *world*
speed looks twice as fast sideways — which reads as a bug, not as perspective.
The simulation compensates by varying world speed with heading, centred on
`IsoRefW` so the average — and the balance against enemies, who keep constant
world speed — is unchanged. Measured across 8 directions: 108.8 px/s each, a
1.000× spread where it used to be 2.000×. `Player.IsoSpeedEqualise` dials the
compensation from full down to none.

**The inverse projection lives in the simulation, not the client.** It is a
movement rule the Phase 4 server must apply identically, so what goes on the
wire is the player's intent in *screen* space — device-independent, since the
iso basis is fixed, and unable to encode a speed the server did not sanction.

**The stick is digital in speed, analog in direction.** Any deflection past the
dead zone is full speed; how far it is pushed says nothing. Analog throttle on a
thumb stick mostly reads as inconsistent speed. The stick also stays where the
thumb first landed rather than following the finger, which used to slide the
centre — and so the heading it reads — out from under the player.

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
| **3** | Content and polish: more weapons, elites, audio, juice, device perf pass | in progress — overlay, hit feedback, audio, shake, elites, bosses, pause done |
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
