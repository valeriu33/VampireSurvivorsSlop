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

        // Camera shake is the one effect here that can genuinely bother people.
        if prefersReducedMotion () then renderer.Cam.ShakeScale <- 0.0f
        let game = createGame (seedFromClock ())
        let input = emptyInput ()
        let stick = Vss.Client.Joystick.create hudRoot host

        Vss.Client.Audio.init ()
        // Browsers hold the audio context suspended until a gesture, and a
        // gesture here means the first touch of the joystick.
        addPointerListener host "pointerdown" (fun _ -> Vss.Client.Audio.unlock ())

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

        /// Pause is a phase, so the fixed-step loop simply stops advancing.
        let togglePause () =
            if game.Phase = Phase.Playing then game.Phase <- Phase.Paused
            elif game.Phase = Phase.Paused then game.Phase <- Phase.Playing

        hud <-
            Vss.Client.Hud.create
                hudRoot
                onPick
                (fun () ->
                    restart ()
                    Vss.Client.Hud.invalidate hud)
                togglePause

        restart ()

        // `?skip=120` starts the clock partway in. The run's content is gated
        // on elapsed time - bosses at 2, 5, 8 and 11 minutes - so without this
        // every check of the last boss costs eleven real minutes. It only moves
        // the difficulty ramp forward, so it makes the game harder, never
        // easier, and the world still starts empty and fills from scratch.
        let skip = queryNumber "skip"
        if not (System.Double.IsNaN skip) && skip > 0.0 then
            game.Time <- float32 skip

        // Backgrounding the tab pauses rather than banking time, so returning
        // to a phone call does not resume mid-crowd on low health.
        document.addEventListener (
            "visibilitychange",
            fun _ ->
                if document.hidden && game.Phase = Phase.Playing then
                    game.Phase <- Phase.Paused
        )

        window.addEventListener ("resize", (fun _ -> applyViewport ()))
        window.addEventListener ("orientationchange", (fun _ -> applyViewport ()))

        let perf = Vss.Client.Perf.create hud.Controls hudRoot

        let stepSeconds = 1.0 / float TicksPerSecond
        let bootMs = now ()
        let mutable last = bootMs
        let mutable acc = 0.0

        let rec frame (_: float) =
            let t = now ()
            // Keep the real delta for the perf overlay: the clamp below is a
            // simulation guard, and feeding the clamped value to the instrument
            // would make every long frame report as exactly the clamp.
            let rawFrameMs = t - last
            let mutable dt = rawFrameMs / 1000.0
            last <- t

            // A long gap means the tab was hidden, not that the game owes the
            // player four seconds of simulation.
            if dt > 0.25 then dt <- 0.25

            let mutable steps = 0
            let simStart = now ()

            if game.Phase = Phase.Playing then
                acc <- acc + dt
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

            // Present before drawing, not after. Events drained afterwards would
            // have their damage numbers, puffs and camera shake held back until
            // the *next* frame - a whole frame of lag on precisely the feedback
            // this is meant to tighten.
            Vss.Client.Fx.present game renderer.Cam (fun () -> Vss.Client.Hud.hurt hud t)

            let simMs = now () - simStart

            let alpha = float32 (acc / stepSeconds)
            let nowSec = float32 ((t - bootMs) / 1000.0)

            let drawStart = now ()
            Vss.Client.Renderer.draw renderer game alpha nowSec (float32 dt)
            // CPU time to build the render list and submit draws. The GPU runs
            // on past this, so it is not the whole cost of a frame - the rAF
            // delta above is what actually bounds the frame rate.
            let drawMs = now () - drawStart

            Vss.Client.Hud.update hud game onPick

            Vss.Client.Perf.sample perf rawFrameMs simMs drawMs steps game.World.Live game.EnemyCount game.GemCount renderer.PoolUsed
            Vss.Client.Perf.paint perf t (pixelRatio ()) (float vw) (float vh)

            window.requestAnimationFrame frame |> ignore

        window.requestAnimationFrame frame |> ignore)

start ()
