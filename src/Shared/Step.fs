/// The fixed-timestep tick. This is the single definition of "what one step of
/// the game is", shared verbatim by the client predictor and the Phase 4
/// authoritative server.
module Vss.Shared.Step

open Vss.Shared.Core
open Vss.Shared.Ecs
open Vss.Shared.Content
open Vss.Shared.Sim
open Vss.Shared.Systems

/// Advance the simulation by exactly one fixed tick.
///
/// Ordering notes, since this is where subtle bugs live:
///   * The spatial grid is rebuilt right after integration, so every collision
///     query in this tick sees current positions, and next tick's steering
///     queries see them too. One rebuild per tick, never stale.
///   * Removal is deferred to `sweepSystem` at the end, so no system ever
///     mutates the entity set while iterating it.
let step (g: GameState) (input: Input) =
    if g.Phase <> Phase.Playing then () else

    let dt = FixedDt

    beginTick g
    timerSystem g dt

    // Intent and steering, against the grid built at the end of last tick.
    inputSystem g input dt
    aiSystem g dt
    separationSystem g dt
    weaponSystem g dt

    // Movement, then a single grid rebuild.
    integrateSystem g dt
    bladeTransformSystem g dt
    rebuildGrid g.Grid g.World Comp.Enemy

    // Interactions, against fresh positions.
    projectileSystem g dt
    orbiterSystem g dt
    playerContactSystem g dt
    pickupSystem g dt

    // Housekeeping.
    lifetimeSystem g dt
    spawnSystem g dt
    sweepSystem g
    regenSystem g dt

    g.Time <- g.Time + dt
    levelSystem g

/// Fresh run: reset progression, clear the world, place the player, grant the
/// starting weapon.
let startRun (g: GameState) (seed: uint32) =
    let w = g.World
    // Release every slot rather than reallocating the arrays: a fresh set of
    // 25 typed arrays mid-session is a guaranteed GC hitch.
    let mutable i = 0
    while i < w.Count do
        if hasAny w.Flags.[i] Comp.Alive then freeEntity w i
        i <- i + 1

    g.Rng.State <- (if seed = 0u then 0x9E3779B9u else seed)
    g.Player <- -1
    g.Phase <- Phase.Playing
    g.Time <- 0.0f
    g.Kills <- 0
    g.Level <- 1
    g.Xp <- 0.0f
    g.XpNeeded <- xpToNext 1
    g.SpawnTimer <- 0.6f
    g.EnemyCount <- 0
    g.GemCount <- 0
    g.OfferCount <- 0
    Array.fill g.Levels 0 g.Levels.Length 0
    Array.fill g.WeaponCd 0 g.WeaponCd.Length 0.0f

    spawnPlayer g 0.0f 0.0f |> ignore

    // Everyone starts with Bolt so the first thirty seconds are not a walking
    // simulator.
    g.Levels.[Up.Bolt] <- 1
    g.WeaponCd.[Up.Bolt] <- 0.4f
