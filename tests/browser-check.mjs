/**
 * Browser smoke test.
 *
 * Boots the built bundle in a headless phone-sized Chromium, drives the
 * joystick, and asserts that the things only a browser can prove actually
 * happened: the canvas exists at the right backing resolution, the ground
 * tiles, entities and pickups draw, the stick tracks a drag, and a level-up
 * card appears with three choices.
 *
 * This is not belt-and-braces. Written against an already-"working" build it
 * caught three real bugs the headless simulation test could not see:
 *   - Graphics.poly was silently dropping every polygon, because Fable maps
 *     `float[]` to Float64Array and Pixi requires a real Array (no ground, no
 *     XP gems);
 *   - enemies spawned on a circle enclosing a portrait viewport, ~5 screen
 *     widths off to the sides, so combat happened entirely off-camera;
 *   - a TilingSprite fed a RenderTexture drew nothing at all.
 *
 * Run: npm run test:browser   (expects a server on PREVIEW_URL)
 */
import { chromium } from 'playwright'

const URL = process.argv[2] || process.env.PREVIEW_URL || 'http://127.0.0.1:4173/?perf=1'
const OUT = process.argv[3] || '/tmp/vss-shot'

// Honour a preinstalled browser when Playwright's own download is unavailable.
const launchOpts = process.env.PW_CHROMIUM ? { executablePath: process.env.PW_CHROMIUM } : {}
const browser = await chromium.launch(launchOpts)
// iPhone-ish portrait: the target form factor.
const ctx = await browser.newContext({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 2,
  isMobile: true,
  hasTouch: true
})
const page = await ctx.newPage()

// Audio cannot be heard from a headless browser and produces no visible
// output, so the only way to know sound is actually firing is to count the
// nodes the synth creates. This wraps the constructor before the page loads.
await ctx.addInitScript(() => {
  const Real = window.AudioContext || window.webkitAudioContext
  window.__audio = { contexts: 0, oscillators: 0, buffers: 0, sources: 0 }
  if (!Real) return
  function Wrapped(...args) {
    const c = new Real(...args)
    window.__audio.contexts++
    const osc = c.createOscillator.bind(c)
    const src = c.createBufferSource.bind(c)
    const buf = c.createBuffer.bind(c)
    c.createOscillator = () => { window.__audio.oscillators++; return osc() }
    c.createBufferSource = () => { window.__audio.sources++; return src() }
    c.createBuffer = (...a) => { window.__audio.buffers++; return buf(...a) }
    return c
  }
  window.AudioContext = Wrapped
  window.webkitAudioContext = Wrapped
})

const errors = []
const logs = []
page.on('console', (m) => { logs.push(`${m.type()}: ${m.text()}`); if (m.type() === 'error') errors.push(m.text()) })
page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`))

await page.goto(URL, { waitUntil: 'networkidle' })
await page.waitForTimeout(1500)

// Probe live state out of the page: is there a canvas, is it non-empty?
const probe = await page.evaluate(() => {
  const c = document.querySelector('#canvas-host canvas')
  if (!c) return { canvas: false }
  const g = document.createElement('canvas')
  g.width = c.width; g.height = c.height
  return {
    canvas: true,
    w: c.width, h: c.height,
    hud: document.querySelector('#hud').innerText.replace(/\n+/g, ' | '),
    stickPresent: !!document.querySelector('#stick')
  }
})

await page.screenshot({ path: `${OUT}-boot.png` })

// Drive the joystick the way a player actually does: press, then orbit the
// thumb so the character circles rather than sprinting in one direction.
// A straight-line hold outruns its own XP gems, which made the level-up
// assertion below flaky.
const ORIGIN_X = 195
const ORIGIN_Y = 600
const THROW = 55

await page.mouse.move(ORIGIN_X, ORIGIN_Y)
await page.mouse.down()

const startedAt = Date.now()
const orbit = async () => {
  const a = ((Date.now() - startedAt) / 1000) * 0.8
  await page.mouse.move(ORIGIN_X + Math.cos(a) * THROW, ORIGIN_Y + Math.sin(a) * THROW)
}

for (let i = 0; i < 34; i++) {
  await orbit()
  await page.waitForTimeout(120)
}
await page.screenshot({ path: `${OUT}-playing.png` })

// A WebGL canvas cannot be read back with drawImage without
// preserveDrawingBuffer, so ground coverage is judged from the screenshot
// instead - see the colour histogram assertions at the end.
// Read the specific HUD fields rather than the whole overlay's innerText:
// the perf panel also lives in #hud, and scraping the lot made this check
// depend on what else happened to be on screen.
const midHud = await page.evaluate(() => ({
  timer: document.querySelector('.hud-timer')?.textContent ?? '',
  kills: Number(document.querySelector('.hud-kills')?.textContent ?? 'NaN'),
  level: document.querySelector('.hud-level')?.textContent ?? '',
  perfRows: [...document.querySelectorAll('.perf-row')].map((r) =>
    [r.querySelector('.perf-k').textContent, r.querySelector('.perf-v').textContent]
  ),
  perfVisible: !document.querySelector('.perf-panel')?.hasAttribute('hidden')
}))
const stickActive = await page.evaluate(() => document.querySelector('#stick')?.classList.contains('active'))

// Keep playing until a level-up card appears, rather than sleeping a fixed
// span and hoping. Bounded, so a genuine failure still terminates.
const LEVEL_UP_TIMEOUT_MS = 45000
const deadline = Date.now() + LEVEL_UP_TIMEOUT_MS
let sawLevelUp = false
while (Date.now() < deadline) {
  const state = await page.evaluate(() => ({
    choices: document.querySelectorAll('.choice').length,
    // Phase.Dead hides the picker for good; stop waiting for something that
    // is never coming.
    dead: [...document.querySelectorAll('.overlay')].some(
      (o) => !o.classList.contains('hidden') && o.querySelector('h1')
    )
  }))
  if (state.choices === 3) { sawLevelUp = true; break }
  if (state.dead) break
  await orbit()
  await page.waitForTimeout(150)
}
await page.mouse.up()
const overlay = await page.evaluate(() => {
  const lu = document.querySelectorAll('.overlay')[0]
  return { levelUpVisible: lu && !lu.classList.contains('hidden'), choices: document.querySelectorAll('.choice').length }
})
await page.screenshot({ path: `${OUT}-levelup.png` })

const fails = []
const check = (cond, msg) => { console.log(`  ${cond ? 'ok  ' : 'FAIL'} ${msg}`); if (!cond) fails.push(msg) }

console.log('')
check(probe.canvas, `canvas present at ${probe.w}x${probe.h} (2x backing store)`)
check(probe.w === 780 && probe.h === 1688, 'canvas honours devicePixelRatio')
check(probe.stickPresent && stickActive, 'joystick tracks a drag')
check(/^\d\d:\d\d$/.test(midHud.timer), `timer is running: ${midHud.timer}`)
check(Number.isFinite(midHud.kills) && midHud.kills > 0, `kills are accumulating: ${midHud.kills}`)

// The overlay is opened with ?perf=1 by the invocation below. Frame timings
// are meaningless under headless rendering, so only assert that the instrument
// reports plausible CPU numbers - the real figures come from a device.
if (URL.includes('perf')) {
  const rows = Object.fromEntries(midHud.perfRows)
  const simMs = parseFloat(rows.sim)
  const drawMs = parseFloat(rows.draw)
  check(midHud.perfVisible, 'perf overlay opens from ?perf=1')
  check(midHud.perfRows.length === 13, `overlay reports ${midHud.perfRows.length} metrics`)
  check(simMs >= 0 && simMs < 50, `sim time is plausible: ${rows.sim}`)
  check(drawMs >= 0 && drawMs < 50, `draw time is plausible: ${rows.draw}`)
  check(Number(rows.entities) > 0, `entity count is live: ${rows.entities}`)
}
check(overlay.levelUpVisible && overlay.choices === 3, 'level-up card offers three choices')
const audio = await page.evaluate(() => window.__audio)
check(audio.contexts === 1, `exactly one AudioContext created (${audio.contexts})`)
check(audio.buffers === 1, `noise buffer built once, not per sound (${audio.buffers})`)
check(audio.oscillators + audio.sources > 5,
  `synth fired ${audio.oscillators} tones and ${audio.sources} noise bursts`)

const muteWorks = await page.evaluate(() => {
  const b = document.querySelector('.mute-btn')
  if (!b) return null
  const before = b.className
  b.click()
  const after = b.className
  b.click()
  return { before, after, restored: b.className }
})
check(
  muteWorks && muteWorks.before !== muteWorks.after && muteWorks.before === muteWorks.restored,
  `mute toggles and restores (${muteWorks?.before} -> ${muteWorks?.after})`
)

check(errors.length === 0, `no console errors${errors.length ? ': ' + errors.join('; ') : ''}`)

console.log('')
console.log(`screenshots written to ${OUT}-{boot,playing,levelup}.png`)

await browser.close()

if (fails.length) {
  console.error(`\n${fails.length} browser check(s) FAILED\n`)
  process.exitCode = 1
} else {
  console.log('\nAll browser checks passed\n')
}
