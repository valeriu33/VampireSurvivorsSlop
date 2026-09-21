/**
 * Joystick unit tests.
 *
 * `readInto` is pure — it reads a stick's state and writes screen-space intent
 * — so it can be driven directly from the compiled module without a browser or
 * any DOM. The fields it touches are plain record fields, so a literal stands
 * in for a real stick.
 *
 * Run: npm run test:input
 */

import { readInto } from '../build/client/Input/Joystick.js'
import { emptyInput } from '../build/client/Shared/Sim.js'

let failures = 0
const check = (cond, msg) => {
  console.log(`  ${cond ? 'ok  ' : 'FAIL'} ${msg}`)
  if (!cond) failures++
}

/** A stick held at (originX, originY) with the thumb currently at (curX, curY). */
const stick = (originX, originY, curX, curY) => ({
  Active: true,
  PointerId: 1,
  OriginX: originX,
  OriginY: originY,
  CurX: curX,
  CurY: curY,
  KeyUp: false,
  KeyDown: false,
  KeyLeft: false,
  KeyRight: false
})

const read = (j) => {
  const input = emptyInput()
  readInto(j, input)
  return input
}

const mag = (i) => Math.hypot(i.MoveX, i.MoveY)

console.log('\n=== Throttle does not depend on how far the stick is pushed ===')
{
  // Same direction, wildly different deflections.
  const small = read(stick(200, 600, 200 + 12, 600))
  const mid = read(stick(200, 600, 200 + 40, 600))
  const past = read(stick(200, 600, 200 + 400, 600))

  check(Math.abs(mag(small) - 1) < 1e-4, `a slight push reads full throttle (${mag(small).toFixed(4)})`)
  check(Math.abs(mag(mid) - 1) < 1e-4, `a half push reads full throttle (${mag(mid).toFixed(4)})`)
  check(Math.abs(mag(past) - 1) < 1e-4, `a push past the rim reads full throttle (${mag(past).toFixed(4)})`)

  // ...and all point the same way.
  check(
    Math.abs(small.MoveX - past.MoveX) < 1e-4 && Math.abs(small.MoveY - past.MoveY) < 1e-4,
    'a slight push and a shove point exactly the same way'
  )
}

console.log('\n=== The dead zone still absorbs thumb jitter ===')
{
  const jitter = read(stick(200, 600, 203, 602))
  check(mag(jitter) === 0, `a 3.6px wobble produces no input (${mag(jitter)})`)

  const past = read(stick(200, 600, 210, 600))
  check(mag(past) === 1, 'just past the dead zone is already full speed')
}

console.log('\n=== Direction is analogue, not snapped to eight ways ===')
{
  // A survivors-like needs smooth aiming of the body even with digital speed.
  const a = read(stick(200, 600, 200 + 100, 600 + 7))
  const angle = (Math.atan2(a.MoveY, a.MoveX) * 180) / Math.PI
  check(angle > 1 && angle < 10, `a shallow push keeps its shallow angle (${angle.toFixed(1)} deg)`)
}

console.log('\n=== Idle and inactive sticks produce nothing ===')
{
  const idle = read({ ...stick(200, 600, 200, 600) })
  check(mag(idle) === 0, 'a stick at rest is silent')

  const off = read({ ...stick(200, 600, 400, 600), Active: false })
  check(mag(off) === 0, 'a released stick is silent even with a stale position')
}

console.log('\n=== Keyboard fallback ===')
{
  const j = { ...stick(0, 0, 0, 0), Active: false, KeyRight: true }
  const right = read(j)
  check(Math.abs(mag(right) - 1) < 1e-4, 'one key reads full throttle')

  const diag = read({ ...stick(0, 0, 0, 0), Active: false, KeyRight: true, KeyUp: true })
  check(Math.abs(mag(diag) - 1) < 1e-4, `two keys are not faster than one (${mag(diag).toFixed(4)})`)
}

console.log(failures ? `\n${failures} FAILED\n` : '\nAll input checks passed\n')
process.exitCode = failures ? 1 : 0
