/// Screen-space side of the isometric projection: the camera, and the view
/// sizing that tells the simulation where "just off-screen" is.
module Vss.Client.Iso

open Vss.Shared.Core

type Camera =
    { /// Camera centre, in screen space (already projected).
      mutable X: float32
      mutable Y: float32
      /// Viewport size in CSS pixels.
      mutable W: float32
      mutable H: float32

      /// Accumulated impact, 0..1. Squared before it becomes an offset, so a
      /// small knock stays subtle while a boss death genuinely shoves the view.
      mutable Trauma: float32
      /// Scales all shake; zeroed for `prefers-reduced-motion`.
      mutable ShakeScale: float32 }

let createCamera () =
    { X = 0.0f
      Y = 0.0f
      W = 1.0f
      H = 1.0f
      Trauma = 0.0f
      ShakeScale = 1.0f }

/// Largest shake offset, in screen pixels, at full trauma.
let private ShakeMax = 17.0f

/// Trauma bled off per second. Fast enough that a hit is a jolt rather than a
/// wobble.
let private TraumaDecay = 1.9f

let inline addTrauma (cam: Camera) (amount: float32) =
    cam.Trauma <- clampf 0.0f 1.0f (cam.Trauma + amount)

/// Advance the shake and return the offset to apply to the camera this frame.
/// Two different frequencies per axis, so the motion never looks like a circle.
let shakeStep (cam: Camera) (dt: float32) (nowSec: float32) =
    if cam.Trauma > 0.0f then
        cam.Trauma <- max 0.0f (cam.Trauma - TraumaDecay * dt)

    if cam.Trauma <= 0.0f || cam.ShakeScale <= 0.0f then
        struct (0.0f, 0.0f)
    else
        let mag = cam.Trauma * cam.Trauma * ShakeMax * cam.ShakeScale
        let ox = mag * (sin (nowSec * 47.0f) * 0.6f + sin (nowSec * 31.0f) * 0.4f)
        let oy = mag * (sin (nowSec * 41.0f + 1.7f) * 0.6f + sin (nowSec * 23.0f) * 0.4f)
        struct (ox, oy)

/// Snap the camera onto a world position. Following the player with a spring
/// would add lag that makes a twin-stick-ish game feel soggy, so it is rigid.
let inline centerOn (cam: Camera) (wx: float32) (wy: float32) =
    cam.X <- isoX wx wy
    cam.Y <- isoY wx wy

let inline toScreenX (cam: Camera) (wx: float32) (wy: float32) =
    isoX wx wy - cam.X + cam.W * 0.5f

let inline toScreenY (cam: Camera) (wx: float32) (wy: float32) =
    isoY wx wy - cam.Y + cam.H * 0.5f

/// Screen-space half-extent of the culling test, padded for large sprites.
let CullPad : float32 = 96.0f
