/**
 * Headless simulation smoke test + benchmark.
 *
 * Runs full-length runs against the Fable-compiled Shared simulation in Node,
 * with no renderer and no DOM. Two jobs:
 *
 *   1. Prove the game actually plays - enemies spawn, damage lands, XP accrues,
 *      levels happen, entity slots get recycled, nothing goes NaN.
 *   2. Report per-tick simulation cost at peak load, which is the number the
 *      mobile frame budget is spent against.
 *
 * Run: npm run test:sim
 */

import { createGame, emptyInput } from '../build/client/Shared/Sim.js'
import { step, startRun } from '../build/client/Shared/Step.js'
import { applyUpgrade } from '../build/client/Shared/Systems.js'

const TICKS_PER_SECOND = 30

function fail(msg) {
  console.error(`FAIL: ${msg}`)
  process.exitCode = 1
}

function check(cond, msg) {
  if (cond) console.log(`  ok   ${msg}`)
  else fail(msg)
}

/** Count live entities by scanning the flag array (Comp.Alive === 1). */
function liveCount(w) {
  let n = 0
  for (let i = 0; i < w.Count; i++) if ((w.Flags[i] & 1) !== 0) n++
  return n
}

function anyNaN(w) {
  for (let i = 0; i < w.Count; i++) {
    if ((w.Flags[i] & 1) === 0) continue
    if (!Number.isFinite(w.Px[i]) || !Number.isFinite(w.Py[i])) return i
    if (!Number.isFinite(w.Hp[i])) return i
  }
  return -1
}

/**
 * Play `seconds` of a run with a scripted input pattern, auto-picking the first
 * offer on every level-up.
 */
function playRun({ seconds, seed, godMode = false, sample = false }) {
  const g = createGame(seed)
  startRun(g, seed)

  const input = emptyInput()
  const w = g.World

  // A wandering circle: keeps the player moving through spawns rather than
  // sitting in a corner where nothing ever reaches them.
  const totalTicks = seconds * TICKS_PER_SECOND
  let levelUps = 0
  let maxLive = 0
  let maxEnemies = 0
  const samples = []

  for (let t = 0; t < totalTicks; t++) {
    const a = (t / TICKS_PER_SECOND) * 0.45
    input.MoveX = Math.cos(a)
    input.MoveY = Math.sin(a)

    if (godMode && g.Player >= 0) w.Hp[g.Player] = w.MaxHp[g.Player]

    const t0 = sample ? performance.now() : 0
    step(g, input)
    if (sample) samples.push(performance.now() - t0)

    // Phase 1 === LevelUp. Take the first offer, as a player would.
    if (g.Phase === 1) {
      applyUpgrade(g, g.Offers[0])
      levelUps++
    }

    const live = liveCount(w)
    if (live > maxLive) maxLive = live
    if (g.EnemyCount > maxEnemies) maxEnemies = g.EnemyCount

    // Phase 2 === Dead.
    if (g.Phase === 2 && !godMode) break
  }

  return { g, w, levelUps, maxLive, maxEnemies, samples }
}

console.log('\n=== 1. A run actually plays ===')
{
  const { g, w, levelUps, maxEnemies } = playRun({ seconds: 300, seed: 12345, godMode: true })

  check(g.Time > 290, `simulated ${g.Time.toFixed(0)}s of game time`)
  check(maxEnemies > 100, `enemy population peaked at ${maxEnemies}`)
  // Ranges, not floors: a balance regression can go either way, and a run that
  // levels *too* fast exhausts every upgrade before the run is over.
  check(g.Kills > 800 && g.Kills < 20000, `player killed ${g.Kills} enemies`)
  check(levelUps >= 10 && levelUps <= 30, `player levelled up ${levelUps} times (reached level ${g.Level})`)

  const maxed = Array.from(g.Levels).filter((l, i) => l >= (i < 3 ? 8 : 5)).length
  check(maxed < 8, `${maxed}/8 upgrade lines maxed - still something left to chase`)
  check(g.Xp >= 0 && g.XpNeeded > 0, `XP bookkeeping stayed sane (${g.Xp.toFixed(1)} / ${g.XpNeeded.toFixed(1)})`)
  check(anyNaN(w) === -1, 'no NaN positions or health anywhere')

  const owned = Array.from(g.Levels).filter((l) => l > 0).length
  check(owned >= 3, `acquired ${owned} distinct upgrades`)
}

console.log('\n=== 1b. The presentation event stream ===')
{
  const g = createGame(2468)
  startRun(g, 2468)
  const input = emptyInput()
  const ev = g.Events

  // Nothing drains the buffer here, so a long run must saturate it at the cap
  // and then refuse more rather than growing.
  for (let t = 0; t < 120 * TICKS_PER_SECOND; t++) {
    input.MoveX = Math.cos(t / 20)
    input.MoveY = Math.sin(t / 20)
    if (g.Player >= 0) g.World.Hp[g.Player] = g.World.MaxHp[g.Player]
    step(g, input)
    if (g.Phase === 1) applyUpgrade(g, g.Offers[0])
  }

  check(ev.Count === ev.Kind.length, `buffer saturated at its ${ev.Kind.length} cap and stopped`)

  const kinds = new Set(Array.from(ev.Kind).slice(0, ev.Count))
  // 0 EnemyHit, 1 EnemyDied, 2 BoltFired, 4 GemPickup.
  check(
    [0, 1, 2, 4].every((k) => kinds.has(k)),
    `saw hit, death, fire and pickup events (kinds: ${[...kinds].sort().join(',')})`
  )

  // Draining is the client's job; a drained buffer must refill.
  ev.Count = 0
  for (let t = 0; t < TICKS_PER_SECOND; t++) {
    if (g.Player >= 0) g.World.Hp[g.Player] = g.World.MaxHp[g.Player]
    step(g, input)
  }
  check(ev.Count > 0, `buffer refills after a drain (${ev.Count} in one second)`)
  check(ev.Count < ev.Kind.length, 'one second of play stays well inside the cap')
}

console.log('\n=== 2. The player can actually die ===')
{
  // No god mode: standing still in the open should eventually be fatal.
  const g = createGame(777)
  startRun(g, 777)
  const input = emptyInput()
  let ticks = 0
  while (g.Phase !== 2 && ticks < 300 * TICKS_PER_SECOND) {
    input.MoveX = 0
    input.MoveY = 0
    step(g, input)
    if (g.Phase === 1) applyUpgrade(g, g.Offers[0])
    ticks++
  }
  check(g.Phase === 2, `player died after ${(ticks / TICKS_PER_SECOND).toFixed(1)}s of standing still`)
}

console.log('\n=== 2b. How long a first run lasts ===')
{
  // The god-mode runs above prove the systems work; they say nothing about
  // whether the game is beatable. This plays it the way someone would on a
  // first attempt - keeps moving, but orbits through the crowd rather than
  // kiting cleanly, and takes whichever upgrade is offered first.
  //
  // Before this existed the balance was only ever checked with god mode on,
  // and an actual first run lasted 39 seconds.
  const seeds = [11, 222, 3333, 44444]
  const runs = seeds.map((seed) => {
    const g = createGame(seed)
    startRun(g, seed)
    const input = emptyInput()
    let t = 0
    while (g.Phase !== 2 && t < 600 * TICKS_PER_SECOND) {
      const a = (t / TICKS_PER_SECOND) * 0.45
      input.MoveX = Math.cos(a)
      input.MoveY = Math.sin(a)
      step(g, input)
      if (g.Phase === 1) applyUpgrade(g, g.Offers[0])
      t++
    }
    return { secs: g.Time, level: g.Level }
  })

  const secs = runs.map((r) => r.secs).sort((a, b) => a - b)
  const median = secs[Math.floor(secs.length / 2)]
  console.log(`  runs: ${runs.map((r) => `${r.secs.toFixed(0)}s L${r.level}`).join('  ')}`)

  // A wide band on purpose: this guards against a balance change that makes the
  // game unplayable or trivial, not against ordinary tuning.
  check(median > 70 && median < 330, `median first-run survival ${median.toFixed(0)}s`)
  check(secs[0] > 40, `worst run still lasted ${secs[0].toFixed(0)}s`)
  check(
    runs.every((r) => r.level >= 4),
    `every run reached at least level 4 (${runs.map((r) => r.level).join(',')})`
  )
}

console.log('\n=== 2c. Elites and bosses ===')
{
  const g = createGame(9090)
  startRun(g, 9090)
  const input = emptyInput()
  const w = g.World

  let bossSpawns = 0
  let bossDeaths = 0
  let eliteDeaths = 0
  let maxBossesAlive = 0
  let sawEliteAlive = false

  for (let t = 0; t < 330 * TICKS_PER_SECOND; t++) {
    const a = (t / TICKS_PER_SECOND) * 0.45
    input.MoveX = Math.cos(a)
    input.MoveY = Math.sin(a)
    if (g.Player >= 0) w.Hp[g.Player] = w.MaxHp[g.Player]
    step(g, input)
    if (g.Phase === 1) applyUpgrade(g, g.Offers[0])

    const ev = g.Events
    for (let i = 0; i < ev.Count; i++) {
      if (ev.Kind[i] === 8) bossSpawns++
      if (ev.Kind[i] === 9) bossDeaths++
      if (ev.Kind[i] === 7) eliteDeaths++
    }
    ev.Count = 0 // stand in for the client drain

    // Comp.Boss = 32768, Comp.Elite = 16384, Comp.Alive = 1, Comp.Dead = 2048
    let alive = 0
    for (let i = 0; i < w.Count; i++) {
      const f = w.Flags[i]
      if ((f & 1) === 0 || (f & 2048) !== 0) continue
      if ((f & 32768) !== 0) alive++
      if ((f & 16384) !== 0) sawEliteAlive = true
    }
    if (alive > maxBossesAlive) maxBossesAlive = alive
  }

  // bossTimes starts at 120s and 300s, both inside this run.
  check(bossSpawns >= 2, `${bossSpawns} bosses arrived`)
  check(maxBossesAlive <= 1, `never more than one boss at a time (peak ${maxBossesAlive})`)
  check(bossDeaths >= 1, `${bossDeaths} bosses were actually killed`)
  check(bossDeaths === bossSpawns || bossSpawns - bossDeaths === (g.Boss >= 0 ? 1 : 0),
    `boss spawns and deaths reconcile (${bossSpawns} in, ${bossDeaths} down)`)
  check(sawEliteAlive, 'elites appeared in the crowd')
  check(eliteDeaths > 0, `${eliteDeaths} elites were killed`)
}

console.log('\n=== 3. Entity slots are recycled, not leaked ===')
{
  const { w, maxLive } = playRun({ seconds: 300, seed: 999, godMode: true })
  const live = liveCount(w)
  check(w.Count <= 8192, `high-water slot mark ${w.Count} stayed within the 8192 cap`)
  check(maxLive < 3000, `peak live entities ${maxLive} stayed well under the cap`)
  check(w.FreeCount > 0, `${w.FreeCount} slots sat on the free list for reuse`)
  check(live === w.Live, `live counter (${w.Live}) agrees with a flag scan (${live})`)
}

console.log('\n=== 4. Determinism: same seed, same run ===')
{
  const a = playRun({ seconds: 90, seed: 4242, godMode: true })
  const b = playRun({ seconds: 90, seed: 4242, godMode: true })
  check(a.g.Kills === b.g.Kills, `kills match (${a.g.Kills})`)
  check(a.g.Level === b.g.Level, `level matches (${a.g.Level})`)
  check(a.w.Px[a.g.Player] === b.w.Px[b.g.Player], 'player position matches bit for bit')
}

console.log('\n=== 5. Simulation cost at load ===')
{
  const { g, samples } = playRun({ seconds: 300, seed: 31337, godMode: true, sample: true })

  // Warm-up ticks are meaningless; the last minute is peak difficulty.
  const tail = samples.slice(-60 * TICKS_PER_SECOND)
  tail.sort((x, y) => x - y)
  const pct = (p) => tail[Math.min(tail.length - 1, Math.floor(tail.length * p))]
  const mean = tail.reduce((s, x) => s + x, 0) / tail.length

  console.log(`  entities at end : ${g.EnemyCount} enemies`)
  console.log(`  mean            : ${mean.toFixed(3)} ms/tick`)
  console.log(`  p50             : ${pct(0.5).toFixed(3)} ms/tick`)
  console.log(`  p95             : ${pct(0.95).toFixed(3)} ms/tick`)
  console.log(`  p99             : ${pct(0.99).toFixed(3)} ms/tick`)
  console.log(`  worst           : ${tail[tail.length - 1].toFixed(3)} ms/tick`)

  // Desktop Node is far faster than a phone, so this is a regression guard,
  // not the mobile budget. The mobile number has to come from a real device.
  check(pct(0.95) < 4.0, `p95 tick cost ${pct(0.95).toFixed(3)}ms is within the desktop guard rail`)
}

console.log(process.exitCode ? '\nFAILED\n' : '\nAll checks passed\n')
