/**
 * Play the built game for a while and screenshot it mid-combat.
 *
 * The browser smoke test proves things work; this is for *looking* at them.
 * A boot screenshot shows an empty field, which is exactly the state that
 * hides every problem worth seeing - an unreadable crowd, damage numbers that
 * collide, gems carpeting the floor.
 *
 * Auto-picks the first upgrade on every level-up so the run progresses.
 *
 *   node tools/capture.mjs <url> <out-prefix> [seconds]
 */
import { chromium } from 'playwright'
const launchOpts = process.env.PW_CHROMIUM ? { executablePath: process.env.PW_CHROMIUM } : {}
const b = await chromium.launch(launchOpts)
const ctx = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true })
const p = await ctx.newPage()
const errs = []
p.on('pageerror', (e) => errs.push(e.message))
p.on('console', (m) => { if (m.type() === 'error') errs.push(m.text()) })

await p.goto(process.argv[2], { waitUntil: 'networkidle' })
await p.waitForTimeout(800)

const OX = 195, OY = 620, R = 55
await p.mouse.move(OX, OY)
await p.mouse.down()

const t0 = Date.now()
const SECONDS = Number(process.argv[4] || 50)
let picks = 0
while (Date.now() - t0 < SECONDS * 1000) {
  const a = ((Date.now() - t0) / 1000) * 0.7
  await p.mouse.move(OX + Math.cos(a) * R, OY + Math.sin(a) * R)
  const n = await p.evaluate(() => document.querySelectorAll('.choice').length)
  if (n === 3) {
    await p.mouse.up()
    await p.evaluate(() => document.querySelectorAll('.choice')[0].click())
    picks++
    await p.mouse.move(OX, OY); await p.mouse.down()
  }
  await p.waitForTimeout(90)
}

// Burst of frames, to catch damage numbers and flashes mid-flight.
for (let i = 0; i < 4; i++) {
  await p.screenshot({ path: `${process.argv[3]}-combat${i}.png` })
  await p.waitForTimeout(160)
}
const hud = await p.evaluate(() => ({
  kills: document.querySelector('.hud-kills')?.textContent,
  level: document.querySelector('.hud-level')?.textContent,
  time: document.querySelector('.hud-timer')?.textContent,
  perf: [...document.querySelectorAll('.perf-row')].map(r => `${r.querySelector('.perf-k').textContent}=${r.querySelector('.perf-v').textContent}`).join(' ')
}))
await p.mouse.up()
console.log(`picks=${picks} time=${hud.time} level=${hud.level} kills=${hud.kills}`)
console.log(hud.perf)
console.log('errors:', errs.length ? errs.slice(0, 3) : 'none')
await b.close()
