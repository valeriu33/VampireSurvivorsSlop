/// On-screen performance overlay.
///
/// Exists because the only numbers measured so far come from desktop Node: the
/// simulation benchmark times `step` and nothing else, and says nothing about
/// the draw path or about any phone. This puts real per-frame figures where
/// they can actually be read - on the device, during a run.
///
/// The overlay must not distort what it measures, so the DOM is rewritten at
/// 5 Hz while the counters accumulate every frame.
module Vss.Client.Perf

open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open Vss.Client.BrowserEx

/// Frames kept for the rolling min/avg/worst window (~2s at 60fps).
[<Literal>]
let private Window = 120

/// A frame slower than this counts as a stutter. 20ms ~ a missed frame at 60Hz.
let private StutterMs = 20.0

/// DOM refresh interval. Numbers that change 60 times a second are unreadable
/// anyway, and rewriting text nodes every frame would be self-defeating.
let private RefreshMs = 200.0

[<NoComparison; NoEquality>]
type Perf =
    { Chip: HTMLElement
      Panel: HTMLElement
      Rows: HTMLElement[]
      mutable Visible: bool

      /// Ring buffer of whole-frame times, in ms.
      Frames: float[]
      mutable Head: int
      mutable Filled: int

      // Accumulated across the frames since the last DOM refresh.
      mutable SimAcc: float
      mutable RenderAcc: float
      mutable TickAcc: int
      mutable Samples: int

      mutable LastPaint: float
      mutable Entities: int
      mutable Sprites: int }

let private RowLabels =
    [| "fps"; "frame"; "worst"; "stutters"; "sim"; "draw"; "other"; "ticks"; "entities"; "sprites"; "viewport" |]

let create (hudRoot: HTMLElement) =
    let chip = document.createElement "button"
    chip.className <- "perf-chip"
    chip.textContent <- "⏱"
    chip.setAttribute ("aria-label", "Toggle performance overlay")

    let panel = document.createElement "div"
    panel.className <- "perf-panel"
    panel.setAttribute ("hidden", "hidden")

    let rows =
        RowLabels
        |> Array.map (fun label ->
            let row = document.createElement "div"
            row.className <- "perf-row"
            let k = document.createElement "span"
            k.className <- "perf-k"
            k.textContent <- label
            let v = document.createElement "span"
            v.className <- "perf-v"
            v.textContent <- "-"
            row.appendChild k |> ignore
            row.appendChild v |> ignore
            panel.appendChild row |> ignore
            v)

    hudRoot.appendChild chip |> ignore
    hudRoot.appendChild panel |> ignore

    let p =
        { Chip = chip
          Panel = panel
          Rows = rows
          Visible = false
          Frames = Array.zeroCreate Window
          Head = 0
          Filled = 0
          SimAcc = 0.0
          RenderAcc = 0.0
          TickAcc = 0
          Samples = 0
          LastPaint = 0.0
          Entities = 0
          Sprites = 0 }

    let setVisible v =
        p.Visible <- v
        if v then panel.removeAttribute "hidden" else panel.setAttribute ("hidden", "hidden")
        chip.className <- if v then "perf-chip on" else "perf-chip"

    chip.addEventListener ("click", (fun e ->
        e.stopPropagation ()
        setVisible (not p.Visible)))

    // Desktop shortcut, and a URL switch so a link can be handed over with the
    // overlay already open.
    window.addEventListener ("keydown", (fun ev ->
        let e = ev :?> KeyboardEvent
        if e.code = "KeyP" then setVisible (not p.Visible)))

    if window.location.search.Contains "perf" then setVisible true

    p

/// Record one frame. `frameMs` is the rAF delta, so it includes GPU wait and
/// vsync; `simMs` and `drawMs` are CPU time only.
let sample (p: Perf) (frameMs: float) (simMs: float) (drawMs: float) (ticks: int) (entities: int) (sprites: int) =
    p.Frames.[p.Head] <- frameMs
    p.Head <- (p.Head + 1) % Window
    if p.Filled < Window then p.Filled <- p.Filled + 1

    p.SimAcc <- p.SimAcc + simMs
    p.RenderAcc <- p.RenderAcc + drawMs
    p.TickAcc <- p.TickAcc + ticks
    p.Samples <- p.Samples + 1
    p.Entities <- entities
    p.Sprites <- sprites

/// Fixed-decimal formatting. F#'s own float-to-string gives full precision
/// ("16.700000000000003"), which is unreadable in a panel refreshed 5x a second.
[<Fable.Core.Emit("$0.toFixed($1)")>]
let private fmt (v: float) (places: int) : string = jsNative

/// Rewrite the panel if enough time has passed. Cheap no-op while hidden.
let paint (p: Perf) (nowMs: float) (dpr: float) (vw: float) (vh: float) =
    if p.Visible && p.Samples > 0 && nowMs - p.LastPaint >= RefreshMs then
        p.LastPaint <- nowMs

        let mutable total = 0.0
        let mutable worst = 0.0
        let mutable stutters = 0
        let mutable i = 0
        while i < p.Filled do
            let f = p.Frames.[i]
            total <- total + f
            if f > worst then worst <- f
            if f > StutterMs then stutters <- stutters + 1
            i <- i + 1

        let n = float p.Filled
        let avgFrame = if n > 0.0 then total / n else 0.0
        let samples = float p.Samples
        let sim = p.SimAcc / samples
        let draw = p.RenderAcc / samples
        // Whatever is left is GPU wait, vsync and the browser's own work.
        let other = max 0.0 (avgFrame - sim - draw)

        let set i (v: string) = p.Rows.[i].textContent <- v
        set 0 (fmt (if avgFrame > 0.0 then 1000.0 / avgFrame else 0.0) 0)
        set 1 (fmt avgFrame 1 + " ms")
        set 2 (fmt worst 1 + " ms")
        set 3 (string stutters + " / " + string p.Filled)
        set 4 (fmt sim 2 + " ms")
        set 5 (fmt draw 2 + " ms")
        set 6 (fmt other 2 + " ms")
        set 7 (fmt (float p.TickAcc / samples) 2)
        set 8 (string p.Entities)
        set 9 (string p.Sprites)
        set 10 (fmt vw 0 + "x" + fmt vh 0 + " @" + fmt dpr 1 + "x")

        p.SimAcc <- 0.0
        p.RenderAcc <- 0.0
        p.TickAcc <- 0
        p.Samples <- 0
    elif not p.Visible && p.Samples > 240 then
        // Keep the accumulators from drifting while hidden.
        p.SimAcc <- 0.0
        p.RenderAcc <- 0.0
        p.TickAcc <- 0
        p.Samples <- 0
