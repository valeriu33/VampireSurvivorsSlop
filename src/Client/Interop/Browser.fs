/// Small browser helpers that Fable.Browser.Dom does not cover, plus the
/// mobile-specific incantations (wake lock, fullscreen, gesture suppression).
module Vss.Client.BrowserEx

open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types

/// High-resolution clock, in milliseconds.
[<Emit("performance.now()")>]
let now () : float = jsNative

/// Capped device pixel ratio. Rendering a 3x-density phone at full resolution
/// triples fragment work for detail nobody can see at arm's length.
let pixelRatio () =
    let dpr: float = window?devicePixelRatio
    let dpr = if System.Double.IsNaN dpr || dpr <= 0.0 then 1.0 else dpr
    min dpr 2.0

let byId (id: string) : HTMLElement = document.getElementById id

/// Keep the screen awake during a run. Not supported everywhere, and a
/// rejection is unremarkable, so failures are swallowed.
let requestWakeLock () : unit =
    emitJsStatement
        ()
        """
        try {
          if (navigator.wakeLock && navigator.wakeLock.request) {
            navigator.wakeLock.request('screen').catch(function () {});
          }
        } catch (e) { /* unsupported */ }
        """

/// Suppress the gestures that would otherwise fight the joystick: pinch zoom,
/// double-tap zoom, long-press selection, and the context menu.
let suppressGestures () : unit =
    emitJsStatement
        ()
        """
        var stop = function (e) { e.preventDefault(); };
        document.addEventListener('gesturestart', stop, { passive: false });
        document.addEventListener('gesturechange', stop, { passive: false });
        document.addEventListener('contextmenu', stop, { passive: false });
        document.addEventListener('dblclick', stop, { passive: false });
        document.addEventListener('touchmove', function (e) {
          if (e.touches && e.touches.length > 1) { e.preventDefault(); }
        }, { passive: false });
        """

/// Fable.Browser.Dom models no inline style, so styles go through a thin emit.
[<Emit("$0.style.setProperty($1, $2)")>]
let setStyle (el: HTMLElement) (prop: string) (value: string) : unit = jsNative

/// `addEventListener` with `{ passive: false }`, which the binding's overloads
/// do not express. Non-passive listeners are required: the joystick has to be
/// able to `preventDefault` a touch drag.
[<Emit("$0.addEventListener($1, $2, { passive: false })")>]
let onActive (target: obj) (name: string) (handler: Event -> unit) : unit = jsNative

let addPointerListener (el: HTMLElement) (name: string) (handler: PointerEvent -> unit) =
    onActive (box el) name (fun e -> handler (e :?> PointerEvent))
