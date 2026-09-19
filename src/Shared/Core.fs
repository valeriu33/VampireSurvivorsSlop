/// Math, RNG and the isometric basis. Everything here is pure and allocation-free
/// so it can run identically in the browser (Fable) and on the .NET server.
module Vss.Shared.Core

// ---------------------------------------------------------------------------
// Simulation constants
// ---------------------------------------------------------------------------

/// Simulation runs at a fixed rate; rendering interpolates between ticks.
/// Fixed-step is a hard requirement for the Phase 4 authoritative server:
/// client prediction and server truth must advance in identical increments.
[<Literal>]
let TicksPerSecond = 30

let FixedDt : float32 = 1.0f / float32 TicksPerSecond

/// Hard ceiling on live entities. Fixed rather than growable on purpose: a
/// mid-run reallocation of 25 typed arrays is exactly the kind of hitch that
/// ruins a phone's frame budget.
[<Literal>]
let MaxEntities = 8192

// ---------------------------------------------------------------------------
// Isometric basis
// ---------------------------------------------------------------------------
// The simulation is a plain flat 2D plane. "Isometric" is purely a render-time
// projection, plus the input rotation that keeps the joystick screen-aligned.
//
//   screenX = (wx - wy) * IsoHalfW
//   screenY = (wx + wy) * IsoHalfH
//
// 2:1 diamonds (IsoHalfW = 2 * IsoHalfH), the classic iso tile ratio.

let IsoHalfW : float32 = 32.0f
let IsoHalfH : float32 = 16.0f

let inline isoX (wx: float32) (wy: float32) = (wx - wy) * IsoHalfW
let inline isoY (wx: float32) (wy: float32) = (wx + wy) * IsoHalfH

/// Screen delta -> world delta. Inverse of the projection above.
/// Used to turn a screen-aligned joystick push into world-space velocity.
let inline screenToWorldX (sx: float32) (sy: float32) =
    0.5f * (sx / IsoHalfW + sy / IsoHalfH)

let inline screenToWorldY (sx: float32) (sy: float32) =
    0.5f * (sy / IsoHalfH - sx / IsoHalfW)

// ---------------------------------------------------------------------------
// Unchecked array access
// ---------------------------------------------------------------------------
// Fable compiles every `arr.[i]` READ into a bounds-checked helper call, to
// preserve .NET's IndexOutOfRangeException semantics. In the innermost
// collision and steering loops that check dominates the work: measured on the
// headless benchmark (620 enemies, 300s run), bounds-checked reads cost
// 0.60 ms/tick against 0.33 ms/tick unchecked - 45% of the simulation budget.
//
// So the hot files (Ecs, Systems, Renderer) index through `ix`/`setIx`, and
// everything else keeps the checked accessor. This is a deliberate, measured
// trade of a safety net for headroom, confined to code that is covered by the
// smoke test and never indexes with anything but a loop counter or a slot id.

#if FABLE_COMPILER
[<Fable.Core.Emit("$0[$1]")>]
let ix (arr: 'T[]) (i: int) : 'T = failwith "replaced by Fable"

[<Fable.Core.Emit("$0[$1] = $2")>]
let setIx (arr: 'T[]) (i: int) (v: 'T) : unit = failwith "replaced by Fable"
#else
let inline ix (arr: 'T[]) (i: int) : 'T = arr.[i]
let inline setIx (arr: 'T[]) (i: int) (v: 'T) : unit = arr.[i] <- v
#endif

// ---------------------------------------------------------------------------
// Scalar helpers
// ---------------------------------------------------------------------------

let inline clampf (lo: float32) (hi: float32) (v: float32) =
    if v < lo then lo elif v > hi then hi else v

let inline lerpf (a: float32) (b: float32) (t: float32) = a + (b - a) * t

let inline lenSq (x: float32) (y: float32) = x * x + y * y

let inline len (x: float32) (y: float32) = sqrt (x * x + y * y)

let Pi : float32 = 3.14159265f
let TwoPi : float32 = 6.2831853f

/// 8-way facing index from a WORLD-space direction.
/// The vector is projected to screen space first, so facings line up with what
/// the player actually sees rather than with the hidden world axes.
/// 0 = E, 1 = SE, 2 = S, 3 = SW, 4 = W, 5 = NW, 6 = N, 7 = NE (screen-relative,
/// +Y pointing down, matching canvas coordinates).
let facingOf (wx: float32) (wy: float32) =
    if lenSq wx wy < 1e-8f then
        -1 // caller keeps the previous facing
    else
        let sx = isoX wx wy
        let sy = isoY wx wy
        let a = atan2 sy sx // -pi..pi
        let idx = int (floor (a / (Pi / 4.0f) + 0.5f))
        (idx + 8) % 8

// ---------------------------------------------------------------------------
// Deterministic RNG (xorshift32)
// ---------------------------------------------------------------------------
// System.Random is off-limits: the server and every client must produce the
// same stream from the same seed. xorshift32 is pure 32-bit integer work, which
// JS bitwise operators reproduce exactly.

type Rng = { mutable State: uint32 }

let mkRng (seed: uint32) =
    { State = if seed = 0u then 0x9E3779B9u else seed }

let inline nextUInt (r: Rng) =
    let mutable x = r.State
    x <- x ^^^ (x <<< 13)
    x <- x ^^^ (x >>> 17)
    x <- x ^^^ (x <<< 5)
    r.State <- x
    x

/// Uniform in [0, 1).
let inline nextFloat (r: Rng) =
    float32 (nextUInt r >>> 8) / 16777216.0f

/// Uniform in [lo, hi).
let inline nextRange (r: Rng) (lo: float32) (hi: float32) =
    lo + (hi - lo) * nextFloat r

/// Uniform integer in [0, n).
let inline nextInt (r: Rng) (n: int) =
    if n <= 0 then 0 else int (nextFloat r * float32 n) % n

/// Uniform angle in [0, 2pi).
let inline nextAngle (r: Rng) = nextFloat r * TwoPi
