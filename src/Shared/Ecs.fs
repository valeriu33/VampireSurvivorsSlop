/// Struct-of-arrays ECS.
///
/// Every component lives in its own flat primitive array indexed by entity slot.
/// Fable compiles `float32[]` to `Float32Array` and `int[]` to `Int32Array`, so
/// this layout is genuinely contiguous in the browser too - not an array of
/// boxed objects pretending to be one.
///
/// Rules for anything that runs inside a system loop:
///   * no `seq`, no `option`, no tuple returns, no per-entity closures
///   * no allocation whatsoever in the steady state
/// These are the F#-on-JS performance traps, and they only show up on a phone.
module Vss.Shared.Ecs

open Vss.Shared.Core

// ---------------------------------------------------------------------------
// Entity handles
// ---------------------------------------------------------------------------
// A handle packs slot index and generation into one int so a stale reference
// (a projectile pointing at an enemy that died and whose slot got recycled)
// is detectable instead of silently addressing the wrong entity.

[<Literal>]
let IndexBits = 16

[<Literal>]
let IndexMask = 0xFFFF

let inline handleIndex (h: int) = h &&& IndexMask
let inline handleGen (h: int) = h >>> IndexBits
let inline makeHandle (idx: int) (gen: int) = (gen <<< IndexBits) ||| idx

[<Literal>]
let NullHandle = -1

// ---------------------------------------------------------------------------
// Component bitmask
// ---------------------------------------------------------------------------

module Comp =
    let [<Literal>] None = 0
    let [<Literal>] Alive = 1
    let [<Literal>] Transform = 2
    let [<Literal>] Velocity = 4
    let [<Literal>] Renderable = 8
    let [<Literal>] Health = 16
    let [<Literal>] Enemy = 32
    let [<Literal>] Player = 64
    let [<Literal>] Projectile = 128
    let [<Literal>] Pickup = 256
    let [<Literal>] Lifetime = 512
    let [<Literal>] Orbiter = 1024
    /// Marked this tick for removal; swept at the end of the tick so systems
    /// never mutate the entity set while iterating it.
    let [<Literal>] Dead = 2048
    let [<Literal>] Aura = 4096
    let [<Literal>] Damage = 8192

let inline hasAll (flags: int) (mask: int) = (flags &&& mask) = mask
let inline hasAny (flags: int) (mask: int) = (flags &&& mask) <> 0

// ---------------------------------------------------------------------------
// World
// ---------------------------------------------------------------------------

type World =
    { /// Component bitmask per slot.
      Flags: int[]
      /// Generation counter per slot, bumped on free.
      Gen: int[]

      // Transform
      Px: float32[]
      Py: float32[]
      /// Position at the start of the current tick, for render interpolation.
      Prevx: float32[]
      Prevy: float32[]

      // Velocity (steering) and knockback (decays independently)
      Vx: float32[]
      Vy: float32[]
      Kx: float32[]
      Ky: float32[]

      // Physical
      Radius: float32[]
      Speed: float32[]

      // Combat
      Hp: float32[]
      MaxHp: float32[]
      Damage: float32[]
      /// Generic per-entity cooldown: aura re-hit gate on enemies,
      /// invulnerability window on the player.
      Cooldown: float32[]
      Pierce: int[]
      /// Last entity this projectile damaged, so a piercing shot does not
      /// re-hit the same target on consecutive ticks while overlapping it.
      LastHit: int[]
      /// Owning entity handle (projectile shooter, orbiter parent).
      Owner: int[]

      // Pickups
      XpValue: float32[]

      // Lifetime
      Life: float32[]

      // Orbiters
      OrbitAngle: float32[]
      OrbitRadius: float32[]

      // Render
      Sprite: int[]
      Facing: int[]
      AnimT: float32[]
      Scale: float32[]
      /// Seconds of white hit-flash remaining.
      Flash: float32[]
      /// Per-entity multiply tint, 0xRRGGBB. 0xFFFFFF leaves the texture alone.
      Tint: int[]

      // Allocation bookkeeping
      FreeList: int[]
      mutable FreeCount: int
      /// High-water mark: slots [0, Count) have been used at least once.
      mutable Count: int
      mutable Live: int }

let private f32 n : float32[] = Array.zeroCreate n
let private i32 n : int[] = Array.zeroCreate n

let createWorld () =
    let n = MaxEntities
    { Flags = i32 n
      Gen = i32 n
      Px = f32 n
      Py = f32 n
      Prevx = f32 n
      Prevy = f32 n
      Vx = f32 n
      Vy = f32 n
      Kx = f32 n
      Ky = f32 n
      Radius = f32 n
      Speed = f32 n
      Hp = f32 n
      MaxHp = f32 n
      Damage = f32 n
      Cooldown = f32 n
      Pierce = i32 n
      LastHit = i32 n
      Owner = i32 n
      XpValue = f32 n
      Life = f32 n
      OrbitAngle = f32 n
      OrbitRadius = f32 n
      Sprite = i32 n
      Facing = i32 n
      AnimT = f32 n
      Scale = f32 n
      Flash = f32 n
      Tint = i32 n
      FreeList = i32 n
      FreeCount = 0
      Count = 0
      Live = 0 }

/// Allocate a slot. Returns -1 when the world is full; callers treat that as
/// "skip this spawn" rather than growing, so the frame budget stays flat.
let allocEntity (w: World) =
    let idx =
        if w.FreeCount > 0 then
            w.FreeCount <- w.FreeCount - 1
            (ix w.FreeList (w.FreeCount))
        elif w.Count < MaxEntities then
            let i = w.Count
            w.Count <- w.Count + 1
            i
        else
            -1

    if idx >= 0 then
        // Reset everything a stale occupant could have left behind.
        setIx w.Flags idx (Comp.Alive)
        setIx w.Px idx (0.0f)
        setIx w.Py idx (0.0f)
        setIx w.Prevx idx (0.0f)
        setIx w.Prevy idx (0.0f)
        setIx w.Vx idx (0.0f)
        setIx w.Vy idx (0.0f)
        setIx w.Kx idx (0.0f)
        setIx w.Ky idx (0.0f)
        setIx w.Radius idx (0.5f)
        setIx w.Speed idx (0.0f)
        setIx w.Hp idx (1.0f)
        setIx w.MaxHp idx (1.0f)
        setIx w.Damage idx (0.0f)
        setIx w.Cooldown idx (0.0f)
        setIx w.Pierce idx (0)
        setIx w.LastHit idx (-1)
        setIx w.Owner idx (NullHandle)
        setIx w.XpValue idx (0.0f)
        setIx w.Life idx (0.0f)
        setIx w.OrbitAngle idx (0.0f)
        setIx w.OrbitRadius idx (0.0f)
        setIx w.Sprite idx (0)
        setIx w.Facing idx (2)
        setIx w.AnimT idx (0.0f)
        setIx w.Scale idx (1.0f)
        setIx w.Flash idx (0.0f)
        setIx w.Tint idx 0xFFFFFF
        w.Live <- w.Live + 1

    idx

/// Mark for removal. The slot is not reusable until the end-of-tick sweep,
/// so handles stay valid for the rest of the current tick.
let inline killEntity (w: World) (idx: int) =
    if idx >= 0 && hasAny (ix w.Flags idx) Comp.Alive then
        setIx w.Flags idx ((ix w.Flags idx) ||| Comp.Dead)

/// Release a slot for reuse and invalidate every handle pointing at it.
let freeEntity (w: World) (idx: int) =
    if hasAny (ix w.Flags idx) Comp.Alive then
        setIx w.Flags idx (Comp.None)
        setIx w.Gen idx (((ix w.Gen idx) + 1) &&& 0x7FFF)
        setIx w.FreeList (w.FreeCount) (idx)
        w.FreeCount <- w.FreeCount + 1
        w.Live <- w.Live - 1

let inline handleOf (w: World) (idx: int) = makeHandle idx (ix w.Gen idx)

/// Resolve a handle to a slot index, or -1 if the referent is gone.
let inline resolve (w: World) (h: int) =
    if h < 0 then
        -1
    else
        let i = handleIndex h
        if i < w.Count && (ix w.Gen i) = handleGen h && hasAny (ix w.Flags i) Comp.Alive then i else -1

// ---------------------------------------------------------------------------
// Spatial hash
// ---------------------------------------------------------------------------
// Rebuilt from scratch every tick by counting sort. For ~1000 bodies that all
// move every frame, an O(n) rebuild beats maintaining any tree structure.
// Unbounded world, so cells are hashed into a fixed bucket count rather than
// indexed by a bounded grid.

[<Literal>]
let private GridBuckets = 4096

[<Literal>]
let private GridMask = 4095

type Grid =
    { CellSize: float32
      /// Bucket start offsets, length GridBuckets + 1.
      Starts: int[]
      /// Scratch write cursor per bucket.
      Cursor: int[]
      /// Entity indices grouped by bucket.
      Items: int[]
      mutable ItemCount: int }

let createGrid (cellSize: float32) =
    { CellSize = cellSize
      Starts = Array.zeroCreate (GridBuckets + 1)
      Cursor = Array.zeroCreate GridBuckets
      Items = Array.zeroCreate MaxEntities
      ItemCount = 0 }

let inline private cellCoord (g: Grid) (v: float32) = int (floor (v / g.CellSize))

/// Hash a cell coordinate pair into a bucket. Collisions only cost extra
/// candidate tests, never correctness - callers do exact distance checks.
let inline private bucketOf (cx: int) (cy: int) =
    ((cx * 73856093) ^^^ (cy * 19349663)) &&& GridMask

/// Rebuild the grid over every live entity carrying `mask`.
let rebuildGrid (g: Grid) (w: World) (mask: int) =
    let starts = g.Starts
    let cursor = g.Cursor
    Array.fill starts 0 (GridBuckets + 1) 0

    // Pass 1: count per bucket.
    let mutable i = 0
    let mutable total = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f mask && not (hasAny f Comp.Dead) then
            let b = bucketOf (cellCoord g (ix w.Px i)) (cellCoord g (ix w.Py i))
            setIx starts b ((ix starts b) + 1)
            total <- total + 1
        i <- i + 1

    // Pass 2: prefix sum into bucket start offsets.
    let mutable acc = 0
    let mutable b = 0
    while b < GridBuckets do
        let c = (ix starts b)
        setIx starts b (acc)
        setIx cursor b (acc)
        acc <- acc + c
        b <- b + 1
    setIx starts GridBuckets (acc)
    g.ItemCount <- total

    // Pass 3: scatter.
    i <- 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f mask && not (hasAny f Comp.Dead) then
            let bk = bucketOf (cellCoord g (ix w.Px i)) (cellCoord g (ix w.Py i))
            setIx g.Items (ix cursor bk) (i)
            setIx cursor bk ((ix cursor bk) + 1)
        i <- i + 1

/// Scratch buffer for the neighbourhood bucket list. Module-level and reused so
/// queries allocate nothing; the simulation is single-threaded by design.
let nbrBuckets : int[] = Array.zeroCreate 256

/// Collect the distinct buckets covering the AABB around (x, y) +/- radius.
/// Distinctness matters: two different cells can hash to one bucket, and
/// scanning it twice would apply damage twice.
let queryBuckets (g: Grid) (x: float32) (y: float32) (radius: float32) =
    let cx0 = cellCoord g (x - radius)
    let cx1 = cellCoord g (x + radius)
    let cy0 = cellCoord g (y - radius)
    let cy1 = cellCoord g (y + radius)
    let mutable n = 0
    let mutable cx = cx0
    while cx <= cx1 do
        let mutable cy = cy0
        while cy <= cy1 do
            let b = bucketOf cx cy
            let mutable dup = false
            let mutable k = 0
            while k < n do
                if (ix nbrBuckets k) = b then dup <- true
                k <- k + 1
            if not dup && n < nbrBuckets.Length then
                setIx nbrBuckets n (b)
                n <- n + 1
            cy <- cy + 1
        cx <- cx + 1
    n

let inline bucketAt (i: int) = (ix nbrBuckets i)
