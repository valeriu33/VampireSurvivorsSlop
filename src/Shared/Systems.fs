/// The simulation systems. Each is `GameState -> float32 -> unit` and runs in
/// the fixed order defined by Step.fs.
///
/// These loops are the hot path. They use `while` rather than `for ... in`,
/// index arrays directly, and allocate nothing.
module Vss.Shared.Systems

open Vss.Shared.Core
open Vss.Shared.Ecs
open Vss.Shared.Content
open Vss.Shared.Sim

// ---------------------------------------------------------------------------
// Frame bookkeeping
// ---------------------------------------------------------------------------

/// Snapshot positions for render interpolation. One typed-array copy per
/// component rather than a per-entity loop.
let beginTick (g: GameState) =
    let w = g.World
    if w.Count > 0 then
        Array.blit w.Px 0 w.Prevx 0 w.Count
        Array.blit w.Py 0 w.Prevy 0 w.Count

/// Decay every per-entity timer in one pass.
let timerSystem (g: GameState) (dt: float32) =
    let w = g.World
    let mutable i = 0
    while i < w.Count do
        if hasAny (ix w.Flags i) Comp.Alive then
            if (ix w.Cooldown i) > 0.0f then setIx w.Cooldown i ((ix w.Cooldown i) - dt)
            if (ix w.Flash i) > 0.0f then setIx w.Flash i ((ix w.Flash i) - dt)
        i <- i + 1

// ---------------------------------------------------------------------------
// Input
// ---------------------------------------------------------------------------

let inputSystem (g: GameState) (input: Input) (_dt: float32) =
    let w = g.World
    let p = g.Player
    if p >= 0 && hasAny (ix w.Flags p) Comp.Alive then
        let spd = Player.Speed * speedMul g
        let m = len input.MoveX input.MoveY
        if m > 0.001f then
            // Clamp rather than normalise: a half-pushed stick should walk.
            let scale = (if m > 1.0f then 1.0f / m else 1.0f) * spd
            setIx w.Vx p (input.MoveX * scale)
            setIx w.Vy p (input.MoveY * scale)
            let f = facingOf input.MoveX input.MoveY
            if f >= 0 then setIx w.Facing p (f)
        else
            setIx w.Vx p (0.0f)
            setIx w.Vy p (0.0f)

// ---------------------------------------------------------------------------
// Enemy steering
// ---------------------------------------------------------------------------

let aiSystem (g: GameState) (_dt: float32) =
    let w = g.World
    let p = g.Player
    if p < 0 then () else

    let tx = (ix w.Px p)
    let ty = (ix w.Py p)
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Enemy) && not (hasAny f Comp.Dead) then
            let dx = tx - (ix w.Px i)
            let dy = ty - (ix w.Py i)
            let d = len dx dy
            if d > 0.0001f then
                let inv = (ix w.Speed i) / d
                setIx w.Vx i (dx * inv)
                setIx w.Vy i (dy * inv)
                let fc = facingOf dx dy
                if fc >= 0 then setIx w.Facing i (fc)
        i <- i + 1

/// Light mutual repulsion so a crowd reads as a crowd instead of collapsing
/// into one pixel. Applied to the steering velocity, not as a hard constraint -
/// exact separation is not worth the solver cost here.
let separationSystem (g: GameState) (_dt: float32) =
    let w = g.World
    let grid = g.Grid
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Enemy) && not (hasAny f Comp.Dead) then
            let x = (ix w.Px i)
            let y = (ix w.Py i)
            let ri = (ix w.Radius i)
            let mutable ax = 0.0f
            let mutable ay = 0.0f
            let nb = queryBuckets grid x y (ri + 0.85f)
            let mutable b = 0
            while b < nb do
                let bk = bucketAt b
                let s = (ix grid.Starts bk)
                let e = (ix grid.Starts (bk + 1))
                let mutable k = s
                while k < e do
                    let j = (ix grid.Items k)
                    if j <> i then
                        let dx = x - (ix w.Px j)
                        let dy = y - (ix w.Py j)
                        let minD = ri + (ix w.Radius j)
                        let d2 = lenSq dx dy
                        if d2 < minD * minD && d2 > 1e-6f then
                            let d = sqrt d2
                            // Linear falloff; strongest when fully overlapped.
                            let push = (minD - d) / minD
                            ax <- ax + dx / d * push
                            ay <- ay + dy / d * push
                    k <- k + 1
                b <- b + 1

            if ax <> 0.0f || ay <> 0.0f then
                let s = (ix w.Speed i) * 1.5f
                setIx w.Vx i ((ix w.Vx i) + ax * s)
                setIx w.Vy i ((ix w.Vy i) + ay * s)
        i <- i + 1

// ---------------------------------------------------------------------------
// Weapons
// ---------------------------------------------------------------------------

/// Nearest living enemy to (x, y), or -1. Linear scan: this runs once per
/// volley, not per frame, so the grid is not worth the setup.
let private nearestEnemy (w: World) (x: float32) (y: float32) =
    let mutable best = -1
    let mutable bestD = System.Single.MaxValue
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Enemy) && not (hasAny f Comp.Dead) then
            let d = lenSq ((ix w.Px i) - x) ((ix w.Py i) - y)
            if d < bestD then
                bestD <- d
                best <- i
        i <- i + 1
    best

let inline private damageEnemy (g: GameState) (e: int) (amount: float32) =
    let w = g.World
    setIx w.Hp e ((ix w.Hp e) - amount)
    setIx w.Flash e HitFlashTime
    emit g.Events Ev.EnemyHit (ix w.Px e) (ix w.Py e) amount
    if (ix w.Hp e) <= 0.0f then killEntity w e

/// Keep the live blade entities in sync with the Blades upgrade level.
/// Called on level-up rather than every tick.
let syncBlades (g: GameState) =
    let w = g.World
    let lvl = (ix g.Levels (Up.Blades))

    let mutable i = 0
    while i < w.Count do
        if hasAll (ix w.Flags i) (Comp.Alive ||| Comp.Orbiter) then freeEntity w i
        i <- i + 1

    if lvl > 0 && g.Player >= 0 then
        let n = bladeCount lvl
        let orbit = bladeOrbit lvl
        let dmg = bladeDamage lvl * damageMul g
        let mutable k = 0
        while k < n do
            spawnBlade g (TwoPi * float32 k / float32 n) orbit dmg |> ignore
            k <- k + 1

let weaponSystem (g: GameState) (dt: float32) =
    let w = g.World
    let p = g.Player
    if p < 0 || not (hasAny (ix w.Flags p) Comp.Alive) then () else

    let px = (ix w.Px p)
    let py = (ix w.Py p)
    let dmgMul = damageMul g
    let cdMul = cooldownMul g

    // ---- Bolt ----
    let boltLvl = (ix g.Levels (Up.Bolt))
    if boltLvl > 0 then
        setIx g.WeaponCd (Up.Bolt) ((ix g.WeaponCd (Up.Bolt)) - dt)
        if (ix g.WeaponCd (Up.Bolt)) <= 0.0f then
            let target = nearestEnemy w px py
            if target >= 0 then
                let dx = (ix w.Px target) - px
                let dy = (ix w.Py target) - py
                let d = len dx dy
                if d > 0.0001f then
                    let bx = dx / d
                    let by = dy / d
                    let n = boltCount boltLvl
                    let dmg = boltDamage boltLvl * dmgMul
                    let pierce = boltPierce boltLvl
                    let mutable k = 0
                    while k < n do
                        // Fan the volley around the aim direction.
                        let off = (float32 k - float32 (n - 1) * 0.5f) * BoltSpread
                        let c = cos off
                        let s = sin off
                        spawnBolt g px py (bx * c - by * s) (bx * s + by * c) dmg pierce |> ignore
                        k <- k + 1
                    emit g.Events Ev.BoltFired px py (float32 n)
                    setIx g.WeaponCd (Up.Bolt) (boltCooldown boltLvl * cdMul)
            else
                // Nothing to shoot at; retry shortly rather than burning the volley.
                setIx g.WeaponCd (Up.Bolt) (0.15f)

    // ---- Nova ----
    let novaLvl = (ix g.Levels (Up.Nova))
    if novaLvl > 0 then
        setIx g.WeaponCd (Up.Nova) ((ix g.WeaponCd (Up.Nova)) - dt)
        if (ix g.WeaponCd (Up.Nova)) <= 0.0f then
            setIx g.WeaponCd (Up.Nova) (novaCooldown novaLvl * cdMul)
            let r = novaRadius novaLvl
            let dmg = novaDamage novaLvl * dmgMul
            spawnNovaVisual g px py r |> ignore
            emit g.Events Ev.NovaCast px py r
            // Instantaneous, so no re-hit gate is needed.
            let nb = queryBuckets g.Grid px py r
            let mutable b = 0
            while b < nb do
                let bk = bucketAt b
                let s = (ix g.Grid.Starts bk)
                let e = (ix g.Grid.Starts (bk + 1))
                let mutable k = s
                while k < e do
                    let j = (ix g.Grid.Items k)
                    if hasAll (ix w.Flags j) (Comp.Alive ||| Comp.Enemy) && not (hasAny (ix w.Flags j) Comp.Dead) then
                        let reach = r + (ix w.Radius j)
                        if lenSq ((ix w.Px j) - px) ((ix w.Py j) - py) <= reach * reach then
                            damageEnemy g j dmg
                    k <- k + 1
                b <- b + 1

/// Pin the blades to their orbit around the player. Runs *after* integration
/// so they track the player's current position instead of trailing it by a tick.
let bladeTransformSystem (g: GameState) (dt: float32) =
    let w = g.World
    let p = g.Player
    let lvl = (ix g.Levels (Up.Blades))
    if p < 0 || lvl = 0 || not (hasAny (ix w.Flags p) Comp.Alive) then () else

    let px = (ix w.Px p)
    let py = (ix w.Py p)
    let spin = bladeSpin lvl
    let orbit = bladeOrbit lvl
    let mutable i = 0
    while i < w.Count do
        if hasAll (ix w.Flags i) (Comp.Alive ||| Comp.Orbiter) then
            let a = (ix w.OrbitAngle i) + spin * dt
            setIx w.OrbitAngle i ((if a > TwoPi then a - TwoPi else a))
            setIx w.OrbitRadius i (orbit)
            setIx w.Prevx i (ix w.Px i)
            setIx w.Prevy i (ix w.Py i)
            setIx w.Px i (px + cos a * orbit)
            setIx w.Py i (py + sin a * orbit)
        i <- i + 1

// ---------------------------------------------------------------------------
// Movement
// ---------------------------------------------------------------------------

let integrateSystem (g: GameState) (dt: float32) =
    let w = g.World
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Transform ||| Comp.Velocity) && not (hasAny f Comp.Dead) then
            setIx w.Px i ((ix w.Px i) + ((ix w.Vx i) + (ix w.Kx i)) * dt)
            setIx w.Py i ((ix w.Py i) + ((ix w.Vy i) + (ix w.Ky i)) * dt)
            // Exponential-ish knockback decay.
            setIx w.Kx i ((ix w.Kx i) * KnockbackDecay)
            setIx w.Ky i ((ix w.Ky i) * KnockbackDecay)
            let spd = len (ix w.Vx i) (ix w.Vy i)
            if spd > 0.05f then setIx w.AnimT i ((ix w.AnimT i) + dt * spd)
        i <- i + 1

// ---------------------------------------------------------------------------
// Collision
// ---------------------------------------------------------------------------

/// Largest enemy radius in the content tables; used to pad query radii so a
/// big body is never missed by a query centred on a small one.
// Despawn margins, as multiples of the viewport half-extents.
//
// This is adaptive rather than a single constant, and it matters more than it
// looks. With a fixed generous margin, distant stragglers that will never catch
// the player accumulate until they fill the enemy cap, which starves spawning
// near the player - measured at a fixed 4.0x, a 5-minute run produced 537 kills
// against 1730 at 3.5x, and the cliff between them was sharp enough that the
// whole balance hinged on a magic number.
//
// So: while the population is comfortable, keep a wide margin and let chasers
// chase. As it approaches the cap, tighten aggressively, spending the budget on
// enemies that can still reach the player.
let private DespawnMarginFar : float32 = 6.0f
let private DespawnMarginNear : float32 = 1.7f
/// Fraction of the cap at which tightening begins.
let private CullPressureStart : float32 = 0.7f

let private maxEnemyRadius =
    let mutable m = 0.0f
    for d in enemies do
        if d.Radius > m then m <- d.Radius
    m

let projectileSystem (g: GameState) (_dt: float32) =
    let w = g.World
    let grid = g.Grid
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Projectile) && not (hasAny f Comp.Dead) then
            let x = (ix w.Px i)
            let y = (ix w.Py i)
            let r = (ix w.Radius i)
            let mutable consumed = false
            let nb = queryBuckets grid x y (r + maxEnemyRadius)
            let mutable b = 0
            while b < nb && not consumed do
                let bk = bucketAt b
                let s = (ix grid.Starts bk)
                let e = (ix grid.Starts (bk + 1))
                let mutable k = s
                while k < e && not consumed do
                    let j = (ix grid.Items k)
                    let jf = (ix w.Flags j)
                    if hasAll jf (Comp.Alive ||| Comp.Enemy)
                       && not (hasAny jf Comp.Dead)
                       && (ix w.LastHit i) <> j then
                        let reach = r + (ix w.Radius j)
                        if lenSq ((ix w.Px j) - x) ((ix w.Py j) - y) <= reach * reach then
                            damageEnemy g j (ix w.Damage i)
                            // Nudge the victim back so a crowd visibly reacts.
                            let dx = (ix w.Vx i)
                            let dy = (ix w.Vy i)
                            let vl = len dx dy
                            if vl > 0.0001f then
                                setIx w.Kx j ((ix w.Kx j) + dx / vl * BoltKnockback)
                                setIx w.Ky j ((ix w.Ky j) + dy / vl * BoltKnockback)
                            // Remembering only the last victim is enough: a
                            // pierce count of 1-2 never re-crosses a body it
                            // already left.
                            setIx w.LastHit i (j)
                            if (ix w.Pierce i) <= 0 then
                                killEntity w i
                                consumed <- true
                            else
                                setIx w.Pierce i ((ix w.Pierce i) - 1)
                    k <- k + 1
                b <- b + 1
        i <- i + 1

/// Orbiting blades. Enemies carry the re-hit gate in `Cooldown`, so a stack of
/// blades cannot delete a target in one tick.
let orbiterSystem (g: GameState) (_dt: float32) =
    let w = g.World
    let grid = g.Grid
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Orbiter) && not (hasAny f Comp.Dead) then
            let x = (ix w.Px i)
            let y = (ix w.Py i)
            let r = (ix w.Radius i)
            let dmg = (ix w.Damage i)
            let nb = queryBuckets grid x y (r + maxEnemyRadius)
            let mutable b = 0
            while b < nb do
                let bk = bucketAt b
                let s = (ix grid.Starts bk)
                let e = (ix grid.Starts (bk + 1))
                let mutable k = s
                while k < e do
                    let j = (ix grid.Items k)
                    let jf = (ix w.Flags j)
                    if hasAll jf (Comp.Alive ||| Comp.Enemy)
                       && not (hasAny jf Comp.Dead)
                       && (ix w.Cooldown j) <= 0.0f then
                        let reach = r + (ix w.Radius j)
                        if lenSq ((ix w.Px j) - x) ((ix w.Py j) - y) <= reach * reach then
                            damageEnemy g j dmg
                            setIx w.Cooldown j (BladeRehit)
                            let dx = (ix w.Px j) - x
                            let dy = (ix w.Py j) - y
                            let d = len dx dy
                            if d > 0.0001f then
                                setIx w.Kx j ((ix w.Kx j) + dx / d * BladeKnockback)
                                setIx w.Ky j ((ix w.Ky j) + dy / d * BladeKnockback)
                    k <- k + 1
                b <- b + 1
        i <- i + 1

/// Enemies touching the player. The player's own `Cooldown` is the i-frame
/// window, so only one hit lands per window no matter how deep the crowd is.
let playerContactSystem (g: GameState) (_dt: float32) =
    let w = g.World
    let p = g.Player
    if p < 0 || not (hasAny (ix w.Flags p) Comp.Alive) then () else
    if (ix w.Cooldown p) > 0.0f then () else

    let x = (ix w.Px p)
    let y = (ix w.Py p)
    let r = (ix w.Radius p)
    let mutable hit = false
    let nb = queryBuckets g.Grid x y (r + maxEnemyRadius)
    let mutable b = 0
    while b < nb && not hit do
        let bk = bucketAt b
        let s = (ix g.Grid.Starts bk)
        let e = (ix g.Grid.Starts (bk + 1))
        let mutable k = s
        while k < e && not hit do
            let j = (ix g.Grid.Items k)
            let jf = (ix w.Flags j)
            if hasAll jf (Comp.Alive ||| Comp.Enemy) && not (hasAny jf Comp.Dead) then
                let reach = r + (ix w.Radius j)
                if lenSq ((ix w.Px j) - x) ((ix w.Py j) - y) <= reach * reach then
                    setIx w.Hp p ((ix w.Hp p) - (ix w.Damage j))
                    setIx w.Cooldown p (Player.IFrames)
                    setIx w.Flash p (0.18f)
                    emit g.Events Ev.PlayerHurt x y (ix w.Damage j)
                    hit <- true
                    if (ix w.Hp p) <= 0.0f then
                        setIx w.Hp p (0.0f)
                        g.Phase <- Phase.Dead
            k <- k + 1
        b <- b + 1

// ---------------------------------------------------------------------------
// Pickups
// ---------------------------------------------------------------------------

let pickupSystem (g: GameState) (dt: float32) =
    let w = g.World
    let p = g.Player
    if p < 0 || not (hasAny (ix w.Flags p) Comp.Alive) then () else

    let px = (ix w.Px p)
    let py = (ix w.Py p)
    let magnet = pickupRadius g
    let magnet2 = magnet * magnet
    let collect2 = Player.CollectRadius * Player.CollectRadius
    // Past the cap the magnet has no range limit, so a littered field clears
    // itself instead of stranding XP the player already earned.
    let flushing = g.GemCount > GemSoftCap

    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Pickup) && not (hasAny f Comp.Dead) then
            let dx = px - (ix w.Px i)
            let dy = py - (ix w.Py i)
            let d2 = lenSq dx dy
            if d2 <= collect2 then
                g.Xp <- g.Xp + (ix w.XpValue i)
                emit g.Events Ev.GemPickup (ix w.Px i) (ix w.Py i) (ix w.XpValue i)
                killEntity w i
            elif d2 <= magnet2 then
                let d = sqrt d2
                // Accelerate as it closes, so collection feels snappy.
                let pull = 6.0f + 10.0f * (1.0f - d / magnet)
                setIx w.Vx i (dx / d * pull)
                setIx w.Vy i (dy / d * pull)
            elif flushing then
                let d = sqrt d2
                if d > 0.0001f then
                    setIx w.Vx i (dx / d * GemFlushPull)
                    setIx w.Vy i (dy / d * GemFlushPull)
            else
                // Drift to a stop once out of range.
                setIx w.Vx i ((ix w.Vx i) * (1.0f - 6.0f * dt))
                setIx w.Vy i ((ix w.Vy i) * (1.0f - 6.0f * dt))
        i <- i + 1

let lifetimeSystem (g: GameState) (dt: float32) =
    let w = g.World
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Lifetime) && not (hasAny f Comp.Dead) then
            setIx w.Life i ((ix w.Life i) - dt)
            if (ix w.Life i) <= 0.0f then killEntity w i
        i <- i + 1

// ---------------------------------------------------------------------------
// Spawning
// ---------------------------------------------------------------------------

let spawnSystem (g: GameState) (dt: float32) =
    let w = g.World
    let p = g.Player
    if p < 0 || not (hasAny (ix w.Flags p) Comp.Alive) then () else

    let px = (ix w.Px p)
    let py = (ix w.Py p)

    // Cull anything that wandered far enough off-screen to be irrelevant,
    // measured in screen space so the margin is the same on every edge.
    // These are released without a reward: `Hp > 0` tells the sweep it was
    // despawned rather than killed.
    let cap = maxEnemies g.Time
    let pressure = float32 g.EnemyCount / float32 (max 1 cap)
    let tighten = clampf 0.0f 1.0f ((pressure - CullPressureStart) / (1.0f - CullPressureStart))
    let margin = lerpf DespawnMarginFar DespawnMarginNear tighten
    let cullW = g.ViewHalfW * margin
    let cullH = g.ViewHalfH * margin
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Enemy) && not (hasAny f Comp.Dead) then
            let dx = (ix w.Px i) - px
            let dy = (ix w.Py i) - py
            if abs (isoX dx dy) > cullW || abs (isoY dx dy) > cullH then killEntity w i
        i <- i + 1

    g.SpawnTimer <- g.SpawnTimer - dt
    if g.SpawnTimer <= 0.0f then
        g.SpawnTimer <- spawnInterval g.Time

        // Weighted pick among the types unlocked at this point in the run.
        let mutable total = 0.0f
        let mutable d = 0
        while d < enemies.Length do
            if g.Time >= (ix enemies d).UnlockAt then total <- total + (ix enemies d).Weight
            d <- d + 1

        let batch = spawnBatch g.Time
        let mutable n = 0
        while n < batch && g.EnemyCount < cap do
            let mutable roll = nextFloat g.Rng * total
            let mutable pick = 0
            let mutable found = false
            let mutable k = 0
            while k < enemies.Length && not found do
                if g.Time >= (ix enemies k).UnlockAt then
                    roll <- roll - (ix enemies k).Weight
                    if roll <= 0.0f then
                        pick <- k
                        found <- true
                k <- k + 1

            // Pick a screen-space direction, walk out to the viewport edge
            // along it, then convert that offset back into world space. The
            // result hugs the visible rectangle instead of a circle enclosing
            // it, so spawns are equally close on every edge.
            let a = nextAngle g.Rng
            let ca = cos a
            let sa = sin a
            let tx = if abs ca > 1e-4f then g.ViewHalfW / abs ca else 1e9f
            let ty = if abs sa > 1e-4f then g.ViewHalfH / abs sa else 1e9f
            let t = (min tx ty) * nextRange g.Rng 1.05f 1.18f
            let sx = ca * t
            let sy = sa * t
            spawnEnemy g pick (px + screenToWorldX sx sy) (py + screenToWorldY sx sy) |> ignore
            n <- n + 1

// ---------------------------------------------------------------------------
// End-of-tick sweep
// ---------------------------------------------------------------------------

/// Release everything marked Dead, awarding XP for enemies that actually died
/// of damage. Deferring removal to here is what lets systems iterate the entity
/// set safely.
let sweepSystem (g: GameState) =
    let w = g.World
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Dead) then
            if hasAny f Comp.Pickup then g.GemCount <- g.GemCount - 1

            if hasAny f Comp.Enemy then
                g.EnemyCount <- g.EnemyCount - 1
                if (ix w.Hp i) <= 0.0f then
                    g.Kills <- g.Kills + 1
                    // Carry the sprite id so the client can tint the death puff
                    // to match whatever just died.
                    emit g.Events Ev.EnemyDied (ix w.Px i) (ix w.Py i) (float32 (ix w.Sprite i))
                    // Spawn before freeing so the gem cannot land in this slot.
                    spawnGem g (ix w.Px i) (ix w.Py i) (ix w.XpValue i) |> ignore
            freeEntity w i
        i <- i + 1

// ---------------------------------------------------------------------------
// Progression
// ---------------------------------------------------------------------------

/// Promote to the next level if enough XP has accrued. One level per call:
/// the player picks a card before any further levels are granted.
let levelSystem (g: GameState) =
    if g.Phase = Phase.Playing && g.Xp >= g.XpNeeded then
        g.Xp <- g.Xp - g.XpNeeded
        g.Level <- g.Level + 1
        g.XpNeeded <- xpToNext g.Level
        // Everything maxed: keep playing rather than showing an empty picker.
        if rollOffers g > 0 then
            g.Phase <- Phase.LevelUp
            let w = g.World
            let p = g.Player
            if p >= 0 then emit g.Events Ev.LevelUp (ix w.Px p) (ix w.Py p) (float32 g.Level)

let applyUpgrade (g: GameState) (id: int) =
    if id >= 0 && id < Up.Count && (ix g.Levels id) < (ix upgrades id).MaxLevel then
        setIx g.Levels id ((ix g.Levels id) + 1)

        if id = Up.Vitality then
            let w = g.World
            let p = g.Player
            if p >= 0 then
                setIx w.MaxHp p (Player.MaxHp + vitalityBonus (ix g.Levels (Up.Vitality)))
                setIx w.Hp p (min (ix w.MaxHp p) ((ix w.Hp p) + 20.0f))

        if id = Up.Blades then syncBlades g
        // Might changes blade damage, which is cached on the blade entities.
        elif id = Up.Might && (ix g.Levels (Up.Blades)) > 0 then syncBlades g

    g.OfferCount <- 0
    if g.Phase = Phase.LevelUp then g.Phase <- Phase.Playing

let regenSystem (g: GameState) (dt: float32) =
    let w = g.World
    let p = g.Player
    if p >= 0 && hasAny (ix w.Flags p) Comp.Alive && (ix w.Hp p) > 0.0f then
        setIx w.Hp p (min (ix w.MaxHp p) ((ix w.Hp p) + Player.RegenPerSecond * dt))
