/// Floating virtual joystick, plus keyboard fallback for desktop testing.
///
/// The stick appears wherever the thumb lands rather than at a fixed corner:
/// on a phone held one-handed, a fixed stick is a constant reach, and reaching
/// is what makes players drop the device.
///
/// Directions are SCREEN-aligned. Pushing up moves the character up the screen,
/// which under an isometric projection is a diagonal in world space - the
/// inverse projection lives in Shared.Core.
module Vss.Client.Joystick

open Browser
open Browser.Types
open Fable.Core.JsInterop
open Vss.Client.BrowserEx
open Vss.Shared.Core
open Vss.Shared.Sim

/// Screen-pixel deflection at which the stick reads as fully pushed.
let private MaxRadius = 58.0

/// Deflection below which input is ignored, to absorb thumb jitter.
let private DeadZone = 5.0

[<NoComparison; NoEquality>]
type Joystick =
    { Root: HTMLElement
      Knob: HTMLElement
      mutable Active: bool
      mutable PointerId: float
      mutable OriginX: float
      mutable OriginY: float
      mutable CurX: float
      mutable CurY: float
      mutable KeyUp: bool
      mutable KeyDown: bool
      mutable KeyLeft: bool
      mutable KeyRight: bool }

let private place (j: Joystick) =
    setStyle j.Root "left" (string j.OriginX + "px")
    setStyle j.Root "top" (string j.OriginY + "px")

let private moveKnob (j: Joystick) (dx: float) (dy: float) =
    setStyle j.Knob "transform" ("translate(" + string dx + "px," + string dy + "px)")

let private release (j: Joystick) =
    j.Active <- false
    j.PointerId <- -1.0
    j.Root.classList.remove "active"
    moveKnob j 0.0 0.0

let create (hudRoot: HTMLElement) (surface: HTMLElement) =
    let root = document.createElement "div"
    root.id <- "stick"
    let knob = document.createElement "div"
    knob.id <- "stick-knob"
    root.appendChild knob |> ignore
    hudRoot.appendChild root |> ignore

    let j =
        { Root = root
          Knob = knob
          Active = false
          PointerId = -1.0
          OriginX = 0.0
          OriginY = 0.0
          CurX = 0.0
          CurY = 0.0
          KeyUp = false
          KeyDown = false
          KeyLeft = false
          KeyRight = false }

    addPointerListener surface "pointerdown" (fun e ->
        if not j.Active then
            e.preventDefault ()
            j.Active <- true
            j.PointerId <- e.pointerId
            j.OriginX <- e.clientX
            j.OriginY <- e.clientY
            j.CurX <- e.clientX
            j.CurY <- e.clientY
            place j
            moveKnob j 0.0 0.0
            root.classList.add "active")

    // Move and release are tracked on the window so a drag that slides off the
    // canvas (or off the screen edge) does not leave the stick stuck on.
    onActive
        (box window)
        "pointermove"
        (fun ev ->
            let e = ev :?> PointerEvent
            if j.Active && e.pointerId = j.PointerId then
                e.preventDefault ()
                j.CurX <- e.clientX
                j.CurY <- e.clientY
                let dx = j.CurX - j.OriginX
                let dy = j.CurY - j.OriginY
                let d = sqrt (dx * dx + dy * dy)
                if d > MaxRadius then
                    // Drag the stick along with the thumb once it hits the rim,
                    // so a long swipe never runs out of travel.
                    let s = MaxRadius / d
                    j.OriginX <- j.CurX - dx * s
                    j.OriginY <- j.CurY - dy * s
                    place j
                    moveKnob j (dx * s) (dy * s)
                else
                    moveKnob j dx dy)

    let onEnd =
        fun (ev: Event) ->
            let e = ev :?> PointerEvent
            if j.Active && e.pointerId = j.PointerId then release j

    window.addEventListener ("pointerup", onEnd)
    window.addEventListener ("pointercancel", onEnd)
    // A pointer that leaves the window entirely never fires pointerup.
    window.addEventListener ("blur", (fun _ -> release j))

    let setKey (code: string) (down: bool) =
        match code with
        | "KeyW"
        | "ArrowUp" -> j.KeyUp <- down
        | "KeyS"
        | "ArrowDown" -> j.KeyDown <- down
        | "KeyA"
        | "ArrowLeft" -> j.KeyLeft <- down
        | "KeyD"
        | "ArrowRight" -> j.KeyRight <- down
        | _ -> ()

    window.addEventListener ("keydown", (fun ev -> setKey (ev :?> KeyboardEvent).code true))
    window.addEventListener ("keyup", (fun ev -> setKey (ev :?> KeyboardEvent).code false))

    j

/// Write the current intent into `input` as a world-space vector with
/// magnitude 0..1. Allocation-free: called every tick.
let readInto (j: Joystick) (input: Input) =
    let mutable sx = 0.0f
    let mutable sy = 0.0f

    if j.Active then
        let dx = j.CurX - j.OriginX
        let dy = j.CurY - j.OriginY
        let d = sqrt (dx * dx + dy * dy)
        if d > DeadZone then
            let mag = min 1.0 (d / MaxRadius)
            sx <- float32 (dx / d * mag)
            sy <- float32 (dy / d * mag)
    else
        if j.KeyLeft then sx <- sx - 1.0f
        if j.KeyRight then sx <- sx + 1.0f
        if j.KeyUp then sy <- sy - 1.0f
        if j.KeyDown then sy <- sy + 1.0f
        let m = len sx sy
        if m > 1.0f then
            sx <- sx / m
            sy <- sy / m

    if sx = 0.0f && sy = 0.0f then
        input.MoveX <- 0.0f
        input.MoveY <- 0.0f
    else
        // Screen intent -> world direction, renormalised so that diagonal
        // screen pushes are not faster than axis-aligned ones.
        let mag = min 1.0f (len sx sy)
        let wx = screenToWorldX sx sy
        let wy = screenToWorldY sx sy
        let wl = len wx wy
        if wl > 0.0001f then
            input.MoveX <- wx / wl * mag
            input.MoveY <- wy / wl * mag
        else
            input.MoveX <- 0.0f
            input.MoveY <- 0.0f
