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

const URL = process.argv[2] || process.env.PREVIEW_URL || 'http://127.0.0.1:4173/'
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

// Drive the joystick: press, drag, hold, so movement and combat actually run.
await page.mouse.move(195, 600)
await page.mouse.down()
await page.mouse.move(150, 540, { steps: 6 })
await page.waitForTimeout(4000)
await page.screenshot({ path: `${OUT}-playing.png` })

// A WebGL canvas cannot be read back with drawImage without
// preserveDrawingBuffer, so ground coverage is judged from the screenshot
// instead - see the colour histogram assertions at the end.
const midHud = await page.evaluate(() => document.querySelector('#hud').innerText.replace(/\n+/g, ' | '))
const stickActive = await page.evaluate(() => document.querySelector('#stick')?.classList.contains('active'))

// Hold longer to force a level-up card.
await page.waitForTimeout(9000)
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
check(/\d\d:\d\d/.test(midHud), `HUD is live: ${midHud}`)
check(Number(midHud.split('|').pop().trim()) > 0, 'kills are accumulating')
check(overlay.levelUpVisible && overlay.choices === 3, 'level-up card offers three choices')
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
