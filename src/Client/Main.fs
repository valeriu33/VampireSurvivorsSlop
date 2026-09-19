/// Bootstrap and the frame loop.
///
/// The loop is a fixed-timestep accumulator: the simulation only ever advances
/// in whole 1/30s ticks, and the renderer interpolates between the last two.
/// Display rate and simulation rate are fully decoupled, which is what keeps a
/// 120 Hz tablet and a struggling 40 fps phone playing the same game - and is a
/// hard prerequisite for the authoritative server in Phase 4.
module Vss.Client.Main

open Browser
open Fable.Core
open Fable.Core.JsInterop
open Vss.Client.BrowserEx
open Vss.Client.Pixi
open Vss.Shared.Core
open Vss.Shared.Sim
open Vss.Shared.Systems

[<Emit("$0.then($1)")>]
let private pthen (p: JS.Promise<'a>) (f: 'a -> unit) : unit = jsNative

/// Cap on catch-up ticks per frame. Without it, a backgrounded tab returns and
/// tries to simulate the entire gap in one frame, freezing the page.
[<Literal>]
let private MaxCatchUpTicks = 5

let private seedFromClock () =
    let t = now ()
    uint32 (abs (int t) ||| 1)

let private start () =
    suppressGestures ()

    let host = byId "canvas-host"
    let hudRoot = byId "hud"

    let app = newApplication ()

    let initOpts =
        createObj [ "width" ==> window.innerWidth
                    "height" ==> window.innerHeight
                    "background" ==> 0x0B0D14
                    "antialias" ==> true
                    "resolution" ==> pixelRatio ()
                    "autoDensity" ==> true
                    // We drive rendering from our own loop so that simulation
                    // and draw stay in a known order.
                    "autoStart" ==> false ]

    pthen (app.init initOpts) (fun () ->
        host.appendChild app.canvas |> ignore

        let atlas = Vss.Client.Atlas.build app.renderer

        let mutable vw = float32 window.innerWidth
        let mutable vh = float32 window.innerHeight

        let renderer = Vss.Client.Renderer.create app atlas vw vh
        let game = createGame (seedFromClock ())
        let input = emptyInput ()
        let stick = Vss.Client.Joystick.create hudRoot host

        let applyViewport () =
            vw <- float32 window.innerWidth
            vh <- float32 window.innerHeight
            app.renderer.resize (float vw, float vh)
            Vss.Client.Renderer.resize renderer vw vh
            // Spawns ride just outside whatever the device can actually see.
            game.ViewHalfW <- vw * 0.5f
            game.ViewHalfH <- vh * 0.5f

        applyViewport ()

        let restart () =
            Vss.Shared.Step.startRun game (seedFromClock ())
            requestWakeLock ()

        let mutable hud = Unchecked.defaultof<Vss.Client.Hud.Hud>

        let onPick (id: int) =
            applyUpgrade game id
            Vss.Client.Hud.invalidate hud

        hud <-
            Vss.Client.Hud.create hudRoot onPick (fun () ->
                restart ()
                Vss.Client.Hud.invalidate hud)

        restart ()

        window.addEventListener ("resize", (fun _ -> applyViewport ()))
        window.addEventListener ("orientationchange", (fun _ -> applyViewport ()))

        let stepSeconds = 1.0 / float TicksPerSecond
        let bootMs = now ()
        let mutable last = bootMs
        let mutable acc = 0.0

        let rec frame (_: float) =
            let t = now ()
            let mutable dt = (t - last) / 1000.0
            last <- t

            // A long gap means the tab was hidden, not that the game owes the
            // player four seconds of simulation.
            if dt > 0.25 then dt <- 0.25

            if game.Phase = Phase.Playing then
                acc <- acc + dt
                let mutable steps = 0
                while acc >= stepSeconds && steps < MaxCatchUpTicks do
                    Vss.Client.Joystick.readInto stick input
                    Vss.Shared.Step.step game input
                    acc <- acc - stepSeconds
                    steps <- steps + 1
                // Still behind after the cap: drop the debt rather than
                // spiralling further behind on every subsequent frame.
                if acc >= stepSeconds then acc <- 0.0
            else
                // Paused for the level-up picker or the death screen. Holding
                // the accumulator at zero stops time from "banking" while the
                // player reads their options.
                acc <- 0.0

            let alpha = float32 (acc / stepSeconds)
            let nowSec = float32 ((t - bootMs) / 1000.0)

            Vss.Client.Renderer.draw renderer game alpha nowSec
            Vss.Client.Hud.update hud game onPick

            window.requestAnimationFrame frame |> ignore

        window.requestAnimationFrame frame |> ignore)

start ()
