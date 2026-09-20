/// DOM overlay HUD.
///
/// Plain DOM on purpose. A virtual-DOM layer would diff a tree every frame for
/// six numbers; here each value is compared against its last written value and
/// the DOM is touched only when it actually changed.
module Vss.Client.Hud

open Browser
open Browser.Types
open Vss.Client.BrowserEx
open Vss.Shared.Ecs
open Vss.Shared.Content
open Vss.Shared.Sim

[<NoComparison; NoEquality>]
type Hud =
    { Timer: HTMLElement
      Level: HTMLElement
      Kills: HTMLElement
      XpFill: HTMLElement
      HpFill: HTMLElement
      LevelUp: HTMLElement
      Choices: HTMLElement
      GameOver: HTMLElement
      GameOverStats: HTMLElement
      GameOverBuild: HTMLElement
      Mute: HTMLElement
      /// Row the perf chip joins, so it shares the same flow.
      Controls: HTMLElement
      Pause: HTMLElement
      Paused: HTMLElement
      BossWrap: HTMLElement
      BossFill: HTMLElement
      mutable LastBossPct: int
      /// Red edge flash on taking a hit; driven by a class, not an animation
      /// frame, so it costs nothing while idle.
      Vignette: HTMLElement
      mutable HurtUntil: float
      mutable LastSecond: int
      mutable LastLevel: int
      mutable LastKills: int
      mutable LastXpPct: int
      mutable LastHpPct: int
      mutable LastPhase: int
      mutable OffersShown: bool }

let private el (tag: string) (cls: string) =
    let e = document.createElement tag
    if cls <> "" then e.className <- cls
    e

let private text (e: HTMLElement) (s: string) = e.textContent <- s

let private formatTime (t: float32) =
    let total = int t
    let m = total / 60
    let s = total % 60
    (if m < 10 then "0" else "") + string m + ":" + (if s < 10 then "0" else "") + string s

let create (root: HTMLElement) (onPick: int -> unit) (onRestart: unit -> unit) (onPause: unit -> unit) =
    // ---- top bar ----
    let top = el "div" "hud-top"

    let row = el "div" "hud-row"
    let level = el "span" "hud-level"
    let timer = el "span" "hud-timer"
    let kills = el "span" "hud-kills"
    row.appendChild level |> ignore
    row.appendChild timer |> ignore
    row.appendChild kills |> ignore

    let xpBar = el "div" "bar xp"
    let xpFill = el "span" ""
    xpBar.appendChild xpFill |> ignore

    let hpBar = el "div" "bar hp"
    let hpFill = el "span" ""
    hpBar.appendChild hpFill |> ignore

    top.appendChild xpBar |> ignore
    top.appendChild row |> ignore
    top.appendChild hpBar |> ignore
    root.appendChild top |> ignore

    // ---- boss health ----
    // Shown only while a boss is alive. A boss with no visible health is just
    // a large enemy that refuses to die.
    let bossWrap = el "div" "boss-wrap hidden"
    let bossLabel = el "div" "boss-label"
    text bossLabel "BOSS"
    let bossBar = el "div" "bar boss"
    let bossFill = el "span" ""
    bossBar.appendChild bossFill |> ignore
    bossWrap.appendChild bossLabel |> ignore
    bossWrap.appendChild bossBar |> ignore
    top.appendChild bossWrap |> ignore

    // ---- controls row ----
    // In normal flow under the bars rather than pinned at a fixed offset: the
    // header grows when a boss bar appears, and anything pinned to a magic
    // offset collides with it.
    let controls = el "div" "hud-controls"
    root.appendChild controls |> ignore

    // ---- mute ----
    let mute = el "button" "mute-btn"
    mute.addEventListener ("click", (fun e ->
        e.stopPropagation ()
        let muted = Vss.Client.Audio.toggleMuted ()
        mute.className <- if muted then "mute-btn off" else "mute-btn"))
    text mute "\u266A"
    if Vss.Client.Audio.isMuted () then mute.className <- "mute-btn off"
    controls.appendChild mute |> ignore

    // ---- pause ----
    let pause = el "button" "pause-btn"
    text pause "\u2759\u2759"
    pause.addEventListener ("click", (fun e ->
        e.stopPropagation ()
        onPause ()))
    controls.appendChild pause |> ignore

    let paused = el "div" "overlay overlay-paused hidden"
    let pTitle = el "h2" ""
    text pTitle "Paused"
    let pResume = el "button" "btn"
    text pResume "Resume"
    pResume.addEventListener ("click", (fun _ -> onPause ()))
    let pRestart = el "button" "btn ghost"
    text pRestart "Restart run"
    pRestart.addEventListener ("click", (fun _ -> onRestart ()))
    paused.appendChild pTitle |> ignore
    paused.appendChild pResume |> ignore
    paused.appendChild pRestart |> ignore
    root.appendChild paused |> ignore

    // ---- hurt vignette ----
    let vignette = el "div" "vignette"
    root.appendChild vignette |> ignore

    // ---- level-up picker ----
    let levelUp = el "div" "overlay overlay-levelup hidden"
    let luTitle = el "h2" ""
    text luTitle "Level Up"
    let choices = el "div" "choices"
    levelUp.appendChild luTitle |> ignore
    levelUp.appendChild choices |> ignore
    root.appendChild levelUp |> ignore

    // ---- game over ----
    let gameOver = el "div" "overlay overlay-gameover hidden"
    let goTitle = el "h1" ""
    text goTitle "You Died"
    let goStats = el "div" "stats"
    let goBtn = el "button" "btn"
    text goBtn "Run it back"
    goBtn.addEventListener ("click", (fun _ -> onRestart ()))
    gameOver.appendChild goTitle |> ignore
    let goBuild = el "div" "build-list"
    gameOver.appendChild goStats |> ignore
    gameOver.appendChild goBuild |> ignore
    gameOver.appendChild goBtn |> ignore
    root.appendChild gameOver |> ignore

    ignore onPick

    { Timer = timer
      Level = level
      Kills = kills
      XpFill = xpFill
      HpFill = hpFill
      LevelUp = levelUp
      Choices = choices
      GameOver = gameOver
      GameOverStats = goStats
      GameOverBuild = goBuild
      Mute = mute
      Controls = controls
      Pause = pause
      Paused = paused
      BossWrap = bossWrap
      BossFill = bossFill
      LastBossPct = -1
      Vignette = vignette
      HurtUntil = 0.0
      LastSecond = -1
      LastLevel = -1
      LastKills = -1
      LastXpPct = -1
      LastHpPct = -1
      LastPhase = -1
      OffersShown = false }

/// Rebuild the three upgrade cards. Runs once per level-up, not per frame.
let private renderOffers (hud: Hud) (g: GameState) (onPick: int -> unit) =
    hud.Choices.innerHTML <- ""
    for k in 0 .. g.OfferCount - 1 do
        let id = g.Offers.[k]
        let def = upgrades.[id]
        let nextLevel = g.Levels.[id] + 1

        let btn = el "button" "choice"
        let icon = el "div" "choice-icon"
        text icon def.Icon

        let body = el "div" "choice-text"
        let name = el "div" "choice-name"
        text name def.Name
        let lvl = el "span" "lvl"
        text lvl (if nextLevel = 1 then "NEW" else "Lv " + string nextLevel)
        name.appendChild lvl |> ignore

        let desc = el "div" "choice-desc"
        text desc (def.Describe nextLevel)

        body.appendChild name |> ignore
        body.appendChild desc |> ignore
        btn.appendChild icon |> ignore
        btn.appendChild body |> ignore
        btn.addEventListener ("click", (fun _ -> onPick id))
        hud.Choices.appendChild btn |> ignore

let private setHidden (e: HTMLElement) (hidden: bool) =
    if hidden then e.classList.add "hidden" else e.classList.remove "hidden"

/// Flash the screen edge red. Called from the event drain on a player hit.
let hurt (hud: Hud) (nowMs: float) =
    hud.HurtUntil <- nowMs + 260.0
    hud.Vignette.classList.add "on"

let update (hud: Hud) (g: GameState) (onPick: int -> unit) =
    let w = g.World

    if hud.HurtUntil > 0.0 && now () > hud.HurtUntil then
        hud.HurtUntil <- 0.0
        hud.Vignette.classList.remove "on"

    let sec = int g.Time
    if sec <> hud.LastSecond then
        hud.LastSecond <- sec
        text hud.Timer (formatTime g.Time)

    if g.Level <> hud.LastLevel then
        hud.LastLevel <- g.Level
        text hud.Level (string g.Level)

    if g.Kills <> hud.LastKills then
        hud.LastKills <- g.Kills
        text hud.Kills (string g.Kills)

    let xpPct = int (100.0f * (if g.XpNeeded > 0.0f then g.Xp / g.XpNeeded else 0.0f))
    if xpPct <> hud.LastXpPct then
        hud.LastXpPct <- xpPct
        setStyle hud.XpFill "width" (string xpPct + "%")

    let p = g.Player
    if p >= 0 && hasAny w.Flags.[p] Comp.Alive then
        let hpPct = int (100.0f * (if w.MaxHp.[p] > 0.0f then w.Hp.[p] / w.MaxHp.[p] else 0.0f))
        if hpPct <> hud.LastHpPct then
            hud.LastHpPct <- hpPct
            setStyle hud.HpFill "width" (string hpPct + "%")

    // Boss health, shown only while one is alive.
    let boss = g.Boss
    if boss >= 0 && hasAny (w.Flags.[boss]) Comp.Alive && w.MaxHp.[boss] > 0.0f then
        let pct = int (100.0f * w.Hp.[boss] / w.MaxHp.[boss])
        if pct <> hud.LastBossPct then
            hud.LastBossPct <- pct
            setStyle hud.BossFill "width" (string (max 0 pct) + "%")
            hud.BossWrap.classList.remove "hidden"
    elif hud.LastBossPct <> -1 then
        hud.LastBossPct <- -1
        hud.BossWrap.classList.add "hidden"

    // Offers are rolled fresh on every level-up, so redraw whenever we enter
    // the picker rather than trying to diff the offer ids.
    if g.Phase = Phase.LevelUp && not hud.OffersShown then
        renderOffers hud g onPick
        hud.OffersShown <- true
    elif g.Phase <> Phase.LevelUp then
        if hud.OffersShown then
            // Drop the spent buttons rather than leaving them in the hidden
            // overlay, where they stay queryable and clickable.
            hud.Choices.innerHTML <- ""
        hud.OffersShown <- false

    if g.Phase <> hud.LastPhase then
        hud.LastPhase <- g.Phase
        setHidden hud.LevelUp (g.Phase <> Phase.LevelUp)
        setHidden hud.GameOver (g.Phase <> Phase.Dead)
        setHidden hud.Paused (g.Phase <> Phase.Paused)

        if g.Phase = Phase.Dead then
            hud.GameOverStats.innerHTML <- ""

            let stat (label: string) (value: string) =
                let d = el "div" ""
                let b = el "b" ""
                text b value
                d.appendChild b |> ignore
                let s = el "span" ""
                text s label
                d.appendChild s |> ignore
                hud.GameOverStats.appendChild d |> ignore

            stat "survived" (formatTime g.Time)
            stat "kills" (string g.Kills)
            stat "level" (string g.Level)

            // What the run was actually built out of. Without this the summary
            // says how far you got but nothing about how you got there.
            hud.GameOverBuild.innerHTML <- ""
            let mutable any = false
            for id in 0 .. Up.Count - 1 do
                if g.Levels.[id] > 0 then
                    any <- true
                    let chip = el "span" "build-chip"
                    let ic = el "span" "build-icon"
                    text ic upgrades.[id].Icon
                    let nm = el "span" ""
                    text nm (upgrades.[id].Name + " " + string g.Levels.[id])
                    chip.appendChild ic |> ignore
                    chip.appendChild nm |> ignore
                    hud.GameOverBuild.appendChild chip |> ignore
            setHidden hud.GameOverBuild (not any)

/// Force the next `update` to redraw everything, after a restart resets values
/// that would otherwise compare equal to their cached copies.
let invalidate (hud: Hud) =
    // Also drop any spent upgrade buttons. `invalidate` runs on picking a card,
    // which clears OffersShown before `update` would otherwise get the chance -
    // leaving the old buttons queryable inside the hidden overlay.
    hud.Choices.innerHTML <- ""
    hud.OffersShown <- false
    // Otherwise a restart mid-boss leaves the boss bar on screen with no boss.
    hud.LastBossPct <- -1
    hud.BossWrap.classList.add "hidden"
    hud.LastSecond <- -1
    hud.LastLevel <- -1
    hud.LastKills <- -1
    hud.LastXpPct <- -1
    hud.LastHpPct <- -1
    hud.LastPhase <- -1
    hud.OffersShown <- false
