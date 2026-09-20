/// All balance data in one place, as plain tables. Tuning the game should never
/// require touching a system.
module Vss.Shared.Content

open Vss.Shared.Core

// ---------------------------------------------------------------------------
// Sprite ids (indices into the atlas built by the client)
// ---------------------------------------------------------------------------

module Sprites =
    let [<Literal>] Player = 0
    let [<Literal>] Grunt = 1
    let [<Literal>] Runner = 2
    let [<Literal>] Brute = 3
    let [<Literal>] Gem = 4
    let [<Literal>] Bolt = 5
    let [<Literal>] Blade = 6
    let [<Literal>] Nova = 7
    /// A single digit of a damage number; `Facing` carries which one.
    let [<Literal>] Digit = 8
    /// Burst left behind by a death.
    let [<Literal>] Puff = 9
    let [<Literal>] Count = 10

/// Sprite kinds with 8 directional facings; everything else is omnidirectional.
let isDirectional (sprite: int) =
    sprite = Sprites.Player
    || sprite = Sprites.Grunt
    || sprite = Sprites.Runner
    || sprite = Sprites.Brute

// ---------------------------------------------------------------------------
// Player
// ---------------------------------------------------------------------------

module Player =
    let MaxHp : float32 = 120.0f
    let Speed : float32 = 3.4f
    let Radius : float32 = 0.45f
    /// Radius at which gems start flying toward the player.
    let PickupRadius : float32 = 3.0f
    /// Radius at which a gem is actually collected.
    let CollectRadius : float32 = 0.6f
    /// Invulnerability after taking a hit. Without this, standing in a crowd
    /// of 40 enemies deletes the player inside a single tick.
    let IFrames : float32 = 0.8f
    let RegenPerSecond : float32 = 0.8f

// ---------------------------------------------------------------------------
// Enemies
// ---------------------------------------------------------------------------

type EnemyDef =
    { Name: string
      Sprite: int
      Hp: float32
      Speed: float32
      Radius: float32
      TouchDamage: float32
      XpValue: float32
      Scale: float32
      /// Seconds into the run before this type can appear.
      UnlockAt: float32
      /// Relative spawn weight once unlocked.
      Weight: float32 }

let enemies : EnemyDef[] =
    [| { Name = "Grunt"
         Sprite = Sprites.Grunt
         Hp = 12.0f
         Speed = 1.9f
         Radius = 0.42f
         TouchDamage = 6.0f
         XpValue = 1.0f
         Scale = 1.0f
         UnlockAt = 0.0f
         Weight = 1.0f }
       { Name = "Runner"
         Sprite = Sprites.Runner
         Hp = 8.0f
         Speed = 3.35f
         Radius = 0.36f
         TouchDamage = 5.0f
         XpValue = 2.0f
         Scale = 0.9f
         UnlockAt = 45.0f
         Weight = 0.6f }
       { Name = "Brute"
         Sprite = Sprites.Brute
         Hp = 58.0f
         Speed = 1.25f
         Radius = 0.74f
         TouchDamage = 15.0f
         XpValue = 6.0f
         Scale = 1.45f
         UnlockAt = 100.0f
         Weight = 0.32f } |]

/// Difficulty ramp. Enemy HP climbs faster than their damage so the run gets
/// longer rather than spikier - a sudden damage cliff just feels unfair.
let hpScale (t: float32) = 1.0f + t / 110.0f
let damageScale (t: float32) = 1.0f + t / 300.0f

/// Seconds between spawn pulses, tightening over the run.
let spawnInterval (t: float32) = clampf 0.18f 1.20f (1.20f - t / 220.0f)

/// Enemies per pulse.
let spawnBatch (t: float32) = 1 + int (t / 38.0f)

/// Hard cap on simultaneous enemies, so the frame budget holds no matter how
/// long the run goes.
let maxEnemies (t: float32) = min 820 (120 + int (t / 0.6f))

// ---------------------------------------------------------------------------
// Weapons and passives
// ---------------------------------------------------------------------------

module Up =
    let [<Literal>] Bolt = 0
    let [<Literal>] Blades = 1
    let [<Literal>] Nova = 2
    let [<Literal>] Might = 3
    let [<Literal>] Swift = 4
    let [<Literal>] Vitality = 5
    let [<Literal>] Magnet = 6
    let [<Literal>] Haste = 7
    let [<Literal>] Count = 8

[<NoComparison; NoEquality>]
type UpgradeDef =
    { Id: int
      Name: string
      Icon: string
      MaxLevel: int
      IsWeapon: bool
      /// Text shown for the level the player would be buying.
      Describe: int -> string }

let upgrades : UpgradeDef[] =
    [| { Id = Up.Bolt
         Name = "Bolt"
         Icon = "✧"
         MaxLevel = 8
         IsWeapon = true
         Describe =
           fun lvl ->
               if lvl = 1 then "Fires a bolt at the nearest enemy."
               elif lvl = 2 || lvl = 4 || lvl = 6 || lvl = 8 then "+1 bolt per volley."
               elif lvl = 3 || lvl = 5 || lvl = 7 then "Bolts pierce one more enemy."
               else "+5 damage, faster volleys." }
       { Id = Up.Blades
         Name = "Blades"
         Icon = "⚔"
         MaxLevel = 8
         IsWeapon = true
         Describe =
           fun lvl ->
               if lvl = 1 then "An orbiting blade shreds what it touches."
               elif lvl = 2 || lvl = 4 || lvl = 6 || lvl = 8 then "+1 blade."
               else "+3 damage, wider orbit." }
       { Id = Up.Nova
         Name = "Nova"
         Icon = "◎"
         MaxLevel = 8
         IsWeapon = true
         Describe =
           fun lvl ->
               if lvl = 1 then "Periodic shockwave damages everything nearby."
               else "+8 damage, bigger blast, shorter delay." }
       { Id = Up.Might
         Name = "Might"
         Icon = "✦"
         MaxLevel = 5
         IsWeapon = false
         Describe = fun _ -> "+10% damage from all sources." }
       { Id = Up.Swift
         Name = "Swift"
         Icon = "➤"
         MaxLevel = 5
         IsWeapon = false
         Describe = fun _ -> "+8% movement speed." }
       { Id = Up.Vitality
         Name = "Vitality"
         Icon = "♥"
         MaxLevel = 5
         IsWeapon = false
         Describe = fun _ -> "+20 max health, and heal for 20." }
       { Id = Up.Magnet
         Name = "Magnet"
         Icon = "◉"
         MaxLevel = 5
         IsWeapon = false
         Describe = fun _ -> "+35% pickup range." }
       { Id = Up.Haste
         Name = "Haste"
         Icon = "⏱"
         MaxLevel = 5
         IsWeapon = false
         Describe = fun _ -> "-7% weapon cooldown." } |]

// ---- Bolt ----------------------------------------------------------------

let boltDamage (lvl: int) = 18.0f + 6.0f * float32 (lvl - 1)
let boltCooldown (lvl: int) = clampf 0.20f 2.0f (0.58f - 0.045f * float32 (lvl - 1))
let boltCount (lvl: int) =
    1
    + (if lvl >= 2 then 1 else 0)
    + (if lvl >= 4 then 1 else 0)
    + (if lvl >= 6 then 1 else 0)
    + (if lvl >= 8 then 1 else 0)
let boltPierce (lvl: int) =
    (if lvl >= 3 then 1 else 0) + (if lvl >= 5 then 1 else 0) + (if lvl >= 7 then 1 else 0)
let BoltSpeed : float32 = 13.5f
let BoltRadius : float32 = 0.28f
let BoltLife : float32 = 1.0f
/// Angular spread between bolts in a multi-shot volley.
let BoltSpread : float32 = 0.16f

// ---- Blades --------------------------------------------------------------

let bladeCount (lvl: int) = 1 + lvl / 2
let bladeDamage (lvl: int) = 9.0f + 3.0f * float32 (lvl - 1)
let bladeOrbit (lvl: int) = 1.95f + 0.11f * float32 (lvl - 1)
let bladeSpin (lvl: int) = 2.15f + 0.07f * float32 (lvl - 1)
let BladeRadius : float32 = 0.42f
/// Minimum delay before a blade can damage the same enemy again.
let BladeRehit : float32 = 0.35f

// ---- Nova ----------------------------------------------------------------

let novaDamage (lvl: int) = 20.0f + 8.0f * float32 (lvl - 1)
let novaRadius (lvl: int) = 2.5f + 0.28f * float32 (lvl - 1)
let novaCooldown (lvl: int) = clampf 1.0f 5.0f (2.6f - 0.18f * float32 (lvl - 1))
/// How long the expanding ring stays on screen.
let NovaVisual : float32 = 0.32f

// ---- Passive multipliers -------------------------------------------------

let mightMul (lvl: int) = 1.0f + 0.10f * float32 lvl
let swiftMul (lvl: int) = 1.0f + 0.08f * float32 lvl
let vitalityBonus (lvl: int) = 20.0f * float32 lvl
let magnetMul (lvl: int) = 1.0f + 0.35f * float32 lvl
let hasteMul (lvl: int) = clampf 0.45f 1.0f (1.0f - 0.07f * float32 lvl)

// ---------------------------------------------------------------------------
// Hit feedback
// ---------------------------------------------------------------------------

/// How long a struck entity renders as a white silhouette. Short enough to read
/// as an impact rather than a state change.
let HitFlashTime : float32 = 0.07f

// Knockback was previously too small to see against the enemies' own forward
// pressure; a hit read as a number changing, not as a blow landing.
let BoltKnockback : float32 = 7.5f
let BladeKnockback : float32 = 5.5f
/// Per-tick multiplier. Lower decays faster - this settles in about a third of
/// a second, so a crowd recoils and closes again rather than sliding.
let KnockbackDecay : float32 = 0.80f

/// Damage numbers.
let DamageTextLife : float32 = 0.62f
let DamageTextRise : float32 = 2.4f
/// Concurrent damage numbers allowed on screen. A nova landing in a dense crowd
/// can resolve a hundred hits in one tick; past this the numbers stop being
/// information and become fog.
[<Literal>]
let MaxDamageTexts = 44

/// Death puff.
let PuffLife : float32 = 0.3f

// ---------------------------------------------------------------------------
// Progression
// ---------------------------------------------------------------------------

/// XP needed to go from `level` to `level + 1`.
///
/// Quadratic on purpose. A flatter curve let a 5-minute run reach level 43,
/// which maxes all 49 available upgrade levels and leaves nothing to chase;
/// this lands a 5-minute run with a scattershot build around level 17.
let xpToNext (level: int) =
    let l = float32 level
    3.0f + 2.0f * l + 1.6f * l * l

/// Live gems past which the field starts hoovering itself up: every gem is
/// drawn to the player regardless of distance, not just those inside the
/// magnet radius.
///
/// Without this, a player who fights in one place leaves a permanent carpet of
/// uncollected gems - measured at over 280 after a single minute. That is XP
/// the player earned and cannot reach, hundreds of wasted entities, and a
/// screen too busy to read. Sweeping them in costs nothing and reads as a
/// reward rather than a cleanup.
[<Literal>]
let GemSoftCap = 90

/// Pull applied to gems outside the magnet radius once the cap is exceeded.
let GemFlushPull : float32 = 7.0f

/// Gem colour tiers, purely cosmetic.
let gemTier (xp: float32) =
    if xp >= 6.0f then 2
    elif xp >= 2.0f then 1
    else 0

[<Literal>]
let OffersPerLevel = 3
