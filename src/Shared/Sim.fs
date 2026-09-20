/// Run-level state and entity factories. Sits between the raw ECS and the
/// systems that drive it.
module Vss.Shared.Sim

open Vss.Shared.Core
open Vss.Shared.Ecs
open Vss.Shared.Content

module Phase =
    let [<Literal>] Playing = 0
    /// Simulation frozen while the player picks an upgrade.
    let [<Literal>] LevelUp = 1
    let [<Literal>] Dead = 2
    /// Player-requested, or forced when the tab is hidden.
    let [<Literal>] Paused = 3

/// One tick of player intent. World-space direction, magnitude 0..1.
/// This is exactly what Phase 4 will put on the wire - nothing else about the
/// player's state is client-authored.
type Input =
    { mutable MoveX: float32
      mutable MoveY: float32 }

let emptyInput () = { MoveX = 0.0f; MoveY = 0.0f }

// ---------------------------------------------------------------------------
// Presentation events
// ---------------------------------------------------------------------------
//
// The simulation must stay free of any client dependency - it compiles to the
// server too - so it cannot call into audio or spawn cosmetic entities itself.
// Instead it appends plain numeric records to a ring buffer that the client
// drains once per frame.
//
// This is not only for sound: it is the same stream the Phase 4 server will
// serialize to tell clients what happened, so hits and deaths can be presented
// without waiting for the next position snapshot to imply them.

module Ev =
    let [<Literal>] EnemyHit = 0
    let [<Literal>] EnemyDied = 1
    let [<Literal>] BoltFired = 2
    let [<Literal>] NovaCast = 3
    let [<Literal>] GemPickup = 4
    let [<Literal>] PlayerHurt = 5
    let [<Literal>] LevelUp = 6
    let [<Literal>] EliteDied = 7
    let [<Literal>] BossSpawned = 8
    let [<Literal>] BossDied = 9

/// Capacity of the event ring. A frame that somehow produces more than this
/// (a nova landing on a very large crowd) drops the overflow rather than
/// growing: a missed tick of feedback is invisible, an allocation is not.
[<Literal>]
let MaxEvents = 384

type Events =
    { Kind: int[]
      X: float32[]
      Y: float32[]
      /// Damage dealt, XP gained - whatever the kind implies.
      Value: float32[]
      mutable Count: int }

let createEvents () =
    { Kind = Array.zeroCreate MaxEvents
      X = Array.zeroCreate MaxEvents
      Y = Array.zeroCreate MaxEvents
      Value = Array.zeroCreate MaxEvents
      Count = 0 }

let inline emit (e: Events) (kind: int) (x: float32) (y: float32) (value: float32) =
    if e.Count < MaxEvents then
        e.Kind.[e.Count] <- kind
        e.X.[e.Count] <- x
        e.Y.[e.Count] <- y
        e.Value.[e.Count] <- value
        e.Count <- e.Count + 1

/// Called by the client after it has presented the frame's events.
let inline clearEvents (e: Events) = e.Count <- 0

type GameState =
    { World: World
      Grid: Grid
      Rng: Rng

      mutable Player: int
      mutable Phase: int

      mutable Time: float32
      mutable Kills: int
      mutable Level: int
      mutable Xp: float32
      mutable XpNeeded: float32

      /// Per-upgrade levels, indexed by Content.Up ids. 0 = not owned.
      Levels: int[]
      /// Remaining cooldown per weapon, indexed by weapon id.
      WeaponCd: float32[]
      /// The upgrade ids currently on offer; length is filled to OfferCount.
      Offers: int[]
      mutable OfferCount: int

      mutable SpawnTimer: float32
      mutable EnemyCount: int
      /// Live pickups, tracked so the sweep-in threshold costs no extra pass.
      mutable GemCount: int

      /// Index into `bossTimes` of the next boss due.
      mutable NextBoss: int
      /// Slot of the living boss, or -1. Drives the HUD health bar.
      mutable Boss: int
      /// Seconds left before the current boss leaves of its own accord.
      mutable BossTimer: float32

      /// Half-extents of the viewport, in SCREEN pixels, set by the client.
      ///
      /// Spawning used to use a world-space radius big enough to enclose the
      /// viewport, but a circle that covers a tall portrait rect reaches about
      /// five screen-widths out to the sides: enemies spawned there were
      /// effectively invisible, and the gems they dropped on death were far
      /// outside pickup range. Spawns are now placed on the viewport rectangle
      /// itself, so they are always just off the edge the player can see.
      mutable ViewHalfW: float32
      mutable ViewHalfH: float32

      /// Scratch buffer used by the level-up roll; avoids allocating per level.
      RollScratch: int[]

      /// Things that happened this frame, for the client to present.
      /// See the Events section at the bottom of this file.
      Events: Events }

let createGame (seed: uint32) =
    { World = createWorld ()
      // Cell size a little above the largest common query radius keeps the
      // neighbourhood scan at 2x2 buckets for most lookups.
      Grid = createGrid 1.5f
      Rng = mkRng seed
      Player = -1
      Phase = Phase.Playing
      Time = 0.0f
      Kills = 0
      Level = 1
      Xp = 0.0f
      XpNeeded = xpToNext 1
      Levels = Array.zeroCreate Up.Count
      WeaponCd = Array.zeroCreate 3
      Offers = Array.zeroCreate OffersPerLevel
      OfferCount = 0
      SpawnTimer = 0.0f
      EnemyCount = 0
      GemCount = 0
      NextBoss = 0
      Boss = -1
      BossTimer = 0.0f
      ViewHalfW = 200.0f
      ViewHalfH = 400.0f
      RollScratch = Array.zeroCreate Up.Count
      Events = createEvents () }

// ---------------------------------------------------------------------------
// Derived stats
// ---------------------------------------------------------------------------

let inline damageMul (g: GameState) = mightMul g.Levels.[Up.Might]
let inline speedMul (g: GameState) = swiftMul g.Levels.[Up.Swift]
let inline cooldownMul (g: GameState) = hasteMul g.Levels.[Up.Haste]
let inline pickupRadius (g: GameState) = Player.PickupRadius * magnetMul g.Levels.[Up.Magnet]

// ---------------------------------------------------------------------------
// Factories
// ---------------------------------------------------------------------------

let spawnPlayer (g: GameState) (x: float32) (y: float32) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        w.Flags.[e] <-
            Comp.Alive ||| Comp.Transform ||| Comp.Velocity ||| Comp.Renderable
            ||| Comp.Health ||| Comp.Player
        w.Px.[e] <- x
        w.Py.[e] <- y
        w.Prevx.[e] <- x
        w.Prevy.[e] <- y
        w.Radius.[e] <- Player.Radius
        w.Speed.[e] <- Player.Speed
        w.MaxHp.[e] <- Player.MaxHp
        w.Hp.[e] <- Player.MaxHp
        w.Sprite.[e] <- Sprites.Player
        w.Facing.[e] <- 2
        g.Player <- e
    e

let spawnEnemy (g: GameState) (defIdx: int) (x: float32) (y: float32) (elite: bool) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        let d = enemies.[defIdx]
        w.Flags.[e] <-
            Comp.Alive ||| Comp.Transform ||| Comp.Velocity ||| Comp.Renderable
            ||| Comp.Health ||| Comp.Enemy ||| Comp.Damage
            ||| (if elite then Comp.Elite else Comp.None)
        w.Px.[e] <- x
        w.Py.[e] <- y
        w.Prevx.[e] <- x
        w.Prevy.[e] <- y
        w.Radius.[e] <- d.Radius * (if elite then EliteScaleMul else 1.0f)
        w.Speed.[e] <- d.Speed * (if elite then EliteSpeedMul else 1.0f)
        let hp = d.Hp * hpScale g.Time * (if elite then EliteHpMul else 1.0f)
        w.MaxHp.[e] <- hp
        w.Hp.[e] <- hp
        w.Damage.[e] <- d.TouchDamage * damageScale g.Time * (if elite then EliteDamageMul else 1.0f)
        w.XpValue.[e] <- d.XpValue * (if elite then EliteXpMul else 1.0f)
        w.Sprite.[e] <- d.Sprite
        w.Scale.[e] <- d.Scale * (if elite then EliteScaleMul else 1.0f)
        w.Tint.[e] <- if elite then EliteTint else 0xFFFFFF
        w.Facing.[e] <- 2
        g.EnemyCount <- g.EnemyCount + 1
    e

/// Bosses are enemies with their own stat block, one at a time, tracked on the
/// GameState so the HUD can show a health bar for whatever is currently the
/// run's problem.
let spawnBoss (g: GameState) (index: int) (x: float32) (y: float32) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        let d = bossFor index
        w.Flags.[e] <-
            Comp.Alive ||| Comp.Transform ||| Comp.Velocity ||| Comp.Renderable
            ||| Comp.Health ||| Comp.Enemy ||| Comp.Damage ||| Comp.Boss
        w.Px.[e] <- x
        w.Py.[e] <- y
        w.Prevx.[e] <- x
        w.Prevy.[e] <- y
        w.Radius.[e] <- d.Radius
        w.Speed.[e] <- d.Speed
        let hp = d.Hp
        w.MaxHp.[e] <- hp
        w.Hp.[e] <- hp
        w.Damage.[e] <- d.Damage
        w.XpValue.[e] <- d.XpValue
        w.Sprite.[e] <- Sprites.Brute
        w.Scale.[e] <- d.Scale
        w.Tint.[e] <- d.Tint
        w.Facing.[e] <- 2
        g.EnemyCount <- g.EnemyCount + 1
        g.Boss <- e
        g.BossTimer <- BossDuration
        emit g.Events Ev.BossSpawned x y (float32 index)
    e

let spawnBolt (g: GameState) (x: float32) (y: float32) (dx: float32) (dy: float32) (dmg: float32) (pierce: int) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        w.Flags.[e] <-
            Comp.Alive ||| Comp.Transform ||| Comp.Velocity ||| Comp.Renderable
            ||| Comp.Projectile ||| Comp.Lifetime ||| Comp.Damage
        w.Px.[e] <- x
        w.Py.[e] <- y
        w.Prevx.[e] <- x
        w.Prevy.[e] <- y
        w.Vx.[e] <- dx * BoltSpeed
        w.Vy.[e] <- dy * BoltSpeed
        w.Radius.[e] <- BoltRadius
        w.Damage.[e] <- dmg
        w.Pierce.[e] <- pierce
        w.Life.[e] <- BoltLife
        w.Sprite.[e] <- Sprites.Bolt
        w.Facing.[e] <- max 0 (facingOf dx dy)
    e

let spawnBlade (g: GameState) (angle: float32) (orbit: float32) (dmg: float32) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        w.Flags.[e] <-
            Comp.Alive ||| Comp.Transform ||| Comp.Renderable ||| Comp.Orbiter ||| Comp.Damage
        w.Radius.[e] <- BladeRadius
        w.Damage.[e] <- dmg
        w.OrbitAngle.[e] <- angle
        w.OrbitRadius.[e] <- orbit
        w.Sprite.[e] <- Sprites.Blade
        w.Owner.[e] <- handleOf w g.Player
    e

/// The nova ring is cosmetic only - damage is applied once, on the tick it is
/// cast, by the weapon system.
let spawnNovaVisual (g: GameState) (x: float32) (y: float32) (radius: float32) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        w.Flags.[e] <- Comp.Alive ||| Comp.Transform ||| Comp.Renderable ||| Comp.Lifetime
        w.Px.[e] <- x
        w.Py.[e] <- y
        w.Prevx.[e] <- x
        w.Prevy.[e] <- y
        w.Radius.[e] <- radius
        w.Life.[e] <- NovaVisual
        w.Sprite.[e] <- Sprites.Nova
        w.Scale.[e] <- radius
    e

let spawnGem (g: GameState) (x: float32) (y: float32) (xp: float32) =
    let w = g.World
    let e = allocEntity w
    if e >= 0 then
        w.Flags.[e] <- Comp.Alive ||| Comp.Transform ||| Comp.Velocity ||| Comp.Renderable ||| Comp.Pickup
        w.Px.[e] <- x
        w.Py.[e] <- y
        w.Prevx.[e] <- x
        w.Prevy.[e] <- y
        w.Radius.[e] <- 0.25f
        w.XpValue.[e] <- xp
        w.Sprite.[e] <- Sprites.Gem
        w.Facing.[e] <- gemTier xp
        g.GemCount <- g.GemCount + 1
    e

// ---------------------------------------------------------------------------
// Level-up offers
// ---------------------------------------------------------------------------

/// Roll `OffersPerLevel` distinct upgrades the player can still take.
/// Falls back to fewer offers (or none) once everything is maxed.
let rollOffers (g: GameState) =
    let pool = g.RollScratch
    let mutable n = 0
    let mutable i = 0
    while i < Up.Count do
        if g.Levels.[i] < upgrades.[i].MaxLevel then
            pool.[n] <- i
            n <- n + 1
        i <- i + 1

    // Partial Fisher-Yates: shuffle only as far as we need to draw.
    let draw = min OffersPerLevel n
    let mutable k = 0
    while k < draw do
        let j = k + nextInt g.Rng (n - k)
        let tmp = pool.[k]
        pool.[k] <- pool.[j]
        pool.[j] <- tmp
        g.Offers.[k] <- pool.[k]
        k <- k + 1

    g.OfferCount <- draw
    draw
