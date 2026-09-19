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
      mutable H: float32 }

let createCamera () = { X = 0.0f; Y = 0.0f; W = 1.0f; H = 1.0f }

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
