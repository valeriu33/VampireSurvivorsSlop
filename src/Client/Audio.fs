/// Procedural sound.
///
/// Every sound is synthesised at runtime - oscillators, envelopes and a noise
/// buffer - so the game ships no audio assets and makes no network requests for
/// them. It also means the palette is tunable as numbers rather than as files.
///
/// The hard constraint is rate: a nova landing in a crowd resolves a hundred
/// hits in one tick, and a hundred simultaneous voices is both a CPU spike and
/// an unpleasant noise. Each sound kind is therefore rate-limited, and the
/// whole mixer has a voice ceiling.
module Vss.Client.Audio

open Fable.Core
open Fable.Core.JsInterop
open Vss.Client.WebAudio
open Vss.Shared.Sim

// localStorage can throw outright in a private window or with site data
// blocked, so it is reached through guarded emits rather than a binding.
[<Emit("(function(){try{return localStorage.getItem($0)}catch(e){return null}})()")>]
let private lsGet (key: string) : string = jsNative

[<Emit("(function(){try{localStorage.setItem($0,$1)}catch(e){}})()")>]
let private lsSet (key: string) (value: string) : unit = jsNative

let private StorageKey = "vss.muted"

/// Minimum gap between two plays of the same sound kind, in seconds. Without
/// these, a dense crowd turns every hit into one continuous buzz.
let private MinGap =
    [| 0.045 // EnemyHit
       0.060 // EnemyDied
       0.070 // BoltFired
       0.000 // NovaCast - rare by construction
       0.045 // GemPickup
       0.120 // PlayerHurt
       0.000 // LevelUp
       0.060 // EliteDied
       0.000 // BossSpawned
       0.000 |] // BossDied

/// Ceiling on voices started within one frame. Past this the frame's remaining
/// sounds are dropped; nobody can distinguish the 13th simultaneous hit.
[<Literal>]
let private MaxVoicesPerFrame = 12

[<NoComparison; NoEquality>]
type Mixer =
    { Ctx: IAudioContext
      Master: IGainNode
      Noise: IAudioBuffer
      /// Last play time per event kind, in context time.
      LastAt: float[]
      mutable Muted: bool
      mutable VoicesThisFrame: int
      /// Consecutive quick pickups, for the rising pitch on a gem streak.
      mutable PickupStreak: int
      mutable LastPickupAt: float }

let mutable private mixer: Mixer option = None

let private readMuted () = lsGet StorageKey = "1"

let private writeMuted (v: bool) = lsSet StorageKey (if v then "1" else "0")

/// One second of white noise, reused by every percussive sound.
let private buildNoise (ctx: IAudioContext) =
    let len = int ctx.sampleRate
    let buf = ctx.createBuffer (1, len, ctx.sampleRate)
    let data = buf.getChannelData 0
    // xorshift rather than Math.random, so the noise floor is identical
    // between sessions and a recording is reproducible.
    let mutable seed = 0x9E3779B9u
    for i in 0 .. len - 1 do
        seed <- seed ^^^ (seed <<< 13)
        seed <- seed ^^^ (seed >>> 17)
        seed <- seed ^^^ (seed <<< 5)
        data.[i] <- float32 (seed >>> 8) / 8388608.0f - 1.0f
    buf

let init () =
    if mixer.IsNone then
        let ctx = createContext ()
        if not (isNull (box ctx)) then
            let master = ctx.createGain ()
            master.gain.value <- 0.32
            master.connect ctx.destination |> ignore
            mixer <-
                Some
                    { Ctx = ctx
                      Master = master
                      Noise = buildNoise ctx
                      LastAt = Array.zeroCreate MinGap.Length
                      Muted = readMuted ()
                      VoicesThisFrame = 0
                      PickupStreak = 0
                      LastPickupAt = 0.0 }

/// Browsers start the context suspended until a gesture. Safe to call often.
let unlock () =
    match mixer with
    | Some m when m.Ctx.state <> "running" -> m.Ctx.resume () |> ignore
    | _ -> ()

let isMuted () =
    match mixer with
    | Some m -> m.Muted
    | None -> readMuted ()

let setMuted (v: bool) =
    writeMuted v
    match mixer with
    | Some m ->
        m.Muted <- v
        m.Master.gain.value <- if v then 0.0 else 0.32
    | None -> ()

let toggleMuted () =
    setMuted (not (isMuted ()))
    isMuted ()

/// Call once per frame, before draining events.
let beginFrame () =
    match mixer with
    | Some m -> m.VoicesThisFrame <- 0
    | None -> ()

let private canPlay (m: Mixer) (kind: int) =
    not m.Muted
    && m.VoicesThisFrame < MaxVoicesPerFrame
    && (kind < 0
        || kind >= MinGap.Length
        || m.Ctx.currentTime - m.LastAt.[kind] >= MinGap.[kind])

/// A pitched blip with an exponential decay. `freqTo` sweeps the pitch across
/// the note; equal values hold it.
let private tone (m: Mixer) (wave: string) (freqFrom: float) (freqTo: float) (dur: float) (gain: float) =
    let t = m.Ctx.currentTime
    let osc = m.Ctx.createOscillator ()
    let g = m.Ctx.createGain ()
    osc.``type`` <- wave
    osc.frequency.setValueAtTime (freqFrom, t) |> ignore
    if freqTo <> freqFrom then
        osc.frequency.exponentialRampToValueAtTime (max 1.0 freqTo, t + dur) |> ignore
    // A few ms of attack: ramping from exactly zero would click.
    g.gain.setValueAtTime (0.0001, t) |> ignore
    g.gain.linearRampToValueAtTime (gain, t + 0.008) |> ignore
    g.gain.exponentialRampToValueAtTime (0.0001, t + dur) |> ignore
    osc.connect (g :> IAudioNode) |> ignore
    g.connect (m.Master :> IAudioNode) |> ignore
    osc.start t
    osc.stop (t + dur + 0.02)
    m.VoicesThisFrame <- m.VoicesThisFrame + 1

/// A filtered noise burst - impacts and deaths.
let private burst (m: Mixer) (filter: string) (freqFrom: float) (freqTo: float) (dur: float) (gain: float) =
    let t = m.Ctx.currentTime
    let src = m.Ctx.createBufferSource ()
    let flt = m.Ctx.createBiquadFilter ()
    let g = m.Ctx.createGain ()
    src.buffer <- m.Noise
    // Start somewhere random in the buffer so repeats do not phase together.
    // Vary the read rate so repeated bursts do not phase into a tone.
    src.playbackRate.value <- 0.85 + JS.Math.random () * 0.3
    flt.``type`` <- filter
    flt.frequency.setValueAtTime (freqFrom, t) |> ignore
    if freqTo <> freqFrom then
        flt.frequency.exponentialRampToValueAtTime (max 40.0 freqTo, t + dur) |> ignore
    g.gain.setValueAtTime (gain, t) |> ignore
    g.gain.exponentialRampToValueAtTime (0.0001, t + dur) |> ignore
    src.connect (flt :> IAudioNode) |> ignore
    flt.connect (g :> IAudioNode) |> ignore
    g.connect (m.Master :> IAudioNode) |> ignore
    src.start t
    src.stop (t + dur + 0.02)
    m.VoicesThisFrame <- m.VoicesThisFrame + 1

/// Present one simulation event.
let play (kind: int) (value: float32) =
    match mixer with
    | None -> ()
    | Some m ->
        if canPlay m kind then
            m.LastAt.[kind] <- m.Ctx.currentTime

            if kind = Ev.EnemyHit then
                burst m "highpass" 1500.0 900.0 0.045 0.30
            elif kind = Ev.EnemyDied then
                burst m "lowpass" 1400.0 260.0 0.14 0.34
            elif kind = Ev.BoltFired then
                tone m "square" 760.0 520.0 0.05 0.07
            elif kind = Ev.NovaCast then
                tone m "sawtooth" 420.0 70.0 0.34 0.20
                burst m "lowpass" 2600.0 300.0 0.3 0.22
            elif kind = Ev.GemPickup then
                // Pitch climbs while gems keep arriving, and resets once the
                // stream stops - the usual pickup-streak trick, and it makes
                // hoovering up a big drop feel like a run of notes.
                let now = m.Ctx.currentTime
                if now - m.LastPickupAt > 0.55 then m.PickupStreak <- 0
                else m.PickupStreak <- min 11 (m.PickupStreak + 1)
                m.LastPickupAt <- now
                let step = 2.0 ** (float m.PickupStreak / 12.0)
                tone m "sine" (1080.0 * step) (1500.0 * step) 0.07 0.10
            elif kind = Ev.PlayerHurt then
                tone m "triangle" 190.0 80.0 0.22 0.34
                burst m "lowpass" 900.0 160.0 0.16 0.3
            elif kind = Ev.EliteDied then
                // Same shape as an ordinary death, pitched down and longer, so
                // it lands as a heavier version of a familiar sound.
                burst m "lowpass" 1100.0 180.0 0.26 0.4
                tone m "triangle" 320.0 150.0 0.2 0.16
            elif kind = Ev.BossSpawned then
                // A slow rise, so it reads as something arriving.
                tone m "sawtooth" 70.0 190.0 0.85 0.26
                burst m "lowpass" 260.0 90.0 0.8 0.24
            elif kind = Ev.BossDied then
                burst m "lowpass" 1800.0 90.0 0.75 0.45
                tone m "sawtooth" 220.0 45.0 0.7 0.3
                tone m "sine" 523.25 1046.5 0.5 0.16
            elif kind = Ev.LevelUp then
                // Major triad, arpeggiated by scheduling three short notes.
                tone m "sine" 523.25 523.25 0.13 0.16
                tone m "sine" 659.25 659.25 0.19 0.14
                tone m "sine" 783.99 783.99 0.3 0.13

            ignore value
