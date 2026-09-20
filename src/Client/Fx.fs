/// Cosmetic entities driven by simulation events: damage numbers and death
/// puffs.
///
/// These live in the same ECS world as everything else, so they inherit
/// integration, lifetime and depth-sorted drawing for free and need no systems
/// of their own. They are nonetheless *client-only*: the simulation never reads
/// them, and nothing branches on their presence.
///
/// (Phase 4 note: when the server becomes authoritative, applying a snapshot
/// will have to leave these alone rather than reconciling them away. They are
/// identifiable by sprite kind - Digit and Puff are client-only - but a
/// dedicated component flag would be the honest fix at that point.)
module Vss.Client.Fx

open Vss.Shared.Core
open Vss.Shared.Ecs
open Vss.Shared.Content
open Vss.Shared.Sim
open Vss.Client.Audio
open Vss.Client.Iso

/// Cosmetics get their own RNG. Drawing from the simulation's stream would
/// advance it by an amount that depends on what the *client* chose to draw,
/// which in Phase 4 desynchronises the client's prediction from the server.
let private fxRng = mkRng 0xC0FFEEu

/// Colours for damage numbers, by source.
let private DamageTint = 0xFFF0C4
let private PlayerHurtTint = 0xFF5F6B

/// Damage numbers rise and drift; the drift keeps two hits on the same enemy
/// from printing exactly on top of each other.
let private spawnDigit (g: GameState) (digit: int) (x: float32) (y: float32) (driftX: float32) (tint: int) (scale: float32) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        setIx w.Flags e (Comp.Alive ||| Comp.Transform ||| Comp.Velocity ||| Comp.Renderable ||| Comp.Lifetime)
        setIx w.Px e x
        setIx w.Py e y
        setIx w.Prevx e x
        setIx w.Prevy e y
        // Equal world components project to pure vertical screen motion, and
        // opposite components to pure horizontal, so rise and drift are set
        // as the sum and difference rather than applied to both axes alike -
        // adding the drift to each would just have made it rise faster.
        let rise = -DamageTextRise * 0.5f
        setIx w.Vx e (rise + driftX)
        setIx w.Vy e (rise - driftX)
        setIx w.Life e DamageTextLife
        setIx w.Sprite e Sprites.Digit
        setIx w.Facing e digit
        setIx w.Tint e tint
        setIx w.Scale e scale
    e

/// Render `value` as a row of digit entities. Two-digit numbers are the common
/// case; anything past four digits is clamped rather than printed, because a
/// five-digit number is wider than the enemy it belongs to.
/// Returns how many digit entities were spawned, which is what the on-screen
/// cap is measured in.
let private spawnNumber (g: GameState) (value: float32) (x: float32) (y: float32) (tint: int) (scale: float32) =
    let n = max 1 (int (value + 0.5f))
    let n = if n > 9999 then 9999 else n

    let mutable digits = 0
    let mutable t = n
    while t > 0 do
        digits <- digits + 1
        t <- t / 10

    // Spread the row around the hit point in world space.
    let spacing = 0.215f * scale
    let drift = nextRange fxRng -0.45f 0.45f
    let mutable i = 0
    while i < digits do
        // Right-to-left, so place from the last digit backwards.
        let place = digits - 1 - i
        let mutable d = n
        let mutable k = 0
        while k < i do
            d <- d / 10
            k <- k + 1
        let digit = d % 10
        let offset = (float32 place - float32 (digits - 1) * 0.5f) * spacing
        spawnDigit g digit (x + offset) (y - offset) drift tint scale |> ignore
        i <- i + 1

    digits

let private spawnPuff (g: GameState) (x: float32) (y: float32) (tint: int) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        setIx w.Flags e (Comp.Alive ||| Comp.Transform ||| Comp.Renderable ||| Comp.Lifetime)
        setIx w.Px e x
        setIx w.Py e y
        setIx w.Prevx e x
        setIx w.Prevy e y
        setIx w.Life e PuffLife
        setIx w.Sprite e Sprites.Puff
        setIx w.Tint e tint
        setIx w.Scale e 1.0f
    e

/// Puffs inherit the colour of whatever died, so a brute's death reads
/// differently from a grunt's.
let private puffTintFor (sprite: int) =
    if sprite = Sprites.Runner then 0xE08A9A
    elif sprite = Sprites.Brute then 0x8FD494
    else 0xB9A6F0

/// Live damage-number entities. Counted fresh each frame rather than tracked
/// incrementally: expiry happens inside the shared lifetime system, which knows
/// nothing about this cap, so a spawn-time counter would only ever climb and
/// would silently switch damage numbers off for the rest of the run.
///
/// The scan is one pass over the entity high-water mark - a few hundred
/// integer tests, against a simulation that already makes several such passes
/// per tick.
let private countDigits (w: World) =
    let mutable n = 0
    let mutable i = 0
    while i < w.Count do
        if hasAny (ix w.Flags i) Comp.Alive && ix w.Sprite i = Sprites.Digit then
            n <- n + 1
        i <- i + 1
    n

/// Drain this frame's events: play their sounds and spawn their cosmetics.
/// Called once per frame, after the simulation has stepped.
let present (g: GameState) (cam: Camera) (onPlayerHurt: unit -> unit) =
    let ev = g.Events
    beginFrame ()

    let mutable liveDigits = countDigits g.World

    let mutable i = 0
    while i < ev.Count do
        let kind = ev.Kind.[i]
        let x = ev.X.[i]
        let y = ev.Y.[i]
        let v = ev.Value.[i]

        play kind v

        if kind = Ev.EnemyHit then
            if liveDigits < MaxDamageTexts then
                liveDigits <- liveDigits + spawnNumber g v x y DamageTint 0.85f
        elif kind = Ev.EnemyDied then
            spawnPuff g x y (puffTintFor (int v)) |> ignore
        elif kind = Ev.PlayerHurt then
            // Always shown, cap or no cap: the player taking damage is the one
            // number that must never be dropped.
            liveDigits <- liveDigits + spawnNumber g v x y PlayerHurtTint 1.15f
            addTrauma cam Shake.PlayerHurt
            onPlayerHurt ()
        elif kind = Ev.NovaCast then
            addTrauma cam Shake.Nova
        elif kind = Ev.EliteDied then
            addTrauma cam Shake.EliteDied
            spawnPuff g x y EliteTint |> ignore
        elif kind = Ev.BossSpawned then
            addTrauma cam Shake.BossSpawn
        elif kind = Ev.BossDied then
            addTrauma cam Shake.BossDied
            // Three staggered puffs read as something large coming apart.
            spawnPuff g x y 0xFF6B57 |> ignore
            spawnPuff g (x + 0.7f) (y - 0.4f) 0xFFC24D |> ignore
            spawnPuff g (x - 0.6f) (y + 0.5f) 0xFF6B57 |> ignore

        i <- i + 1

    clearEvents ev
