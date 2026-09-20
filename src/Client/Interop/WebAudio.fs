/// Minimal Web Audio bindings.
///
/// Hand-written for the same reason as the Pixi ones: only the surface the
/// synth actually touches, rather than a whole binding package to maintain.
module Vss.Client.WebAudio

open Fable.Core
open Fable.Core.JsInterop

type IAudioParam =
    abstract value: float with get, set
    abstract setValueAtTime: float * float -> IAudioParam
    abstract linearRampToValueAtTime: float * float -> IAudioParam
    abstract exponentialRampToValueAtTime: float * float -> IAudioParam
    abstract cancelScheduledValues: float -> IAudioParam

type IAudioNode =
    abstract connect: IAudioNode -> IAudioNode
    abstract disconnect: unit -> unit

type IGainNode =
    inherit IAudioNode
    abstract gain: IAudioParam

type IOscillatorNode =
    inherit IAudioNode
    abstract frequency: IAudioParam
    abstract ``type``: string with get, set
    abstract start: float -> unit
    abstract stop: float -> unit

type IBiquadFilterNode =
    inherit IAudioNode
    abstract frequency: IAudioParam
    abstract Q: IAudioParam
    abstract ``type``: string with get, set

type IAudioBuffer =
    abstract getChannelData: int -> float32[]

type IAudioBufferSourceNode =
    inherit IAudioNode
    abstract buffer: IAudioBuffer with get, set
    abstract playbackRate: IAudioParam
    abstract start: float -> unit
    abstract stop: float -> unit

type IAudioContext =
    abstract currentTime: float
    abstract sampleRate: float
    abstract state: string
    abstract destination: IAudioNode
    abstract resume: unit -> JS.Promise<unit>
    abstract createGain: unit -> IGainNode
    abstract createOscillator: unit -> IOscillatorNode
    abstract createBiquadFilter: unit -> IBiquadFilterNode
    abstract createBufferSource: unit -> IAudioBufferSourceNode
    abstract createBuffer: int * int * float -> IAudioBuffer

/// Safari still only exposes the prefixed constructor. Returns null where the
/// browser has no Web Audio at all, which the caller treats as "run silent".
[<Emit("(function(){ try { var C = window.AudioContext || window.webkitAudioContext; return C ? new C() : null } catch (e) { return null } })()")>]
let createContext () : IAudioContext = jsNative
