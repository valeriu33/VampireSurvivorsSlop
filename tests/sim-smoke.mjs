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
