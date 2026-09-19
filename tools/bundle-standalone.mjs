/**
 * Bundle the game into ONE self-contained HTML fragment for hosts that serve a
 * single file (e.g. a published Artifact, which wraps the file in its own
 * document skeleton and therefore cannot take Vite's multi-chunk output).
 *
 * Emits page CONTENT only - no doctype/html/head/body - with the stylesheet and
 * the whole JS bundle inlined. Requires a build made with
 * `inlineDynamicImports`, so there is exactly one script to inline.
 *
 * Run: npm run build:standalone
 */
import { readFileSync, writeFileSync, readdirSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'

const DIST = 'dist'
const OUT_DIR = 'dist-standalone'
const OUT = join(OUT_DIR, 'game.html')
const TITLE = process.env.GAME_TITLE || 'Bolt &amp; Blade'

const assets = readdirSync(join(DIST, 'assets')).filter((f) => f.endsWith('.js'))
if (assets.length !== 1) {
  console.error(
    `Expected exactly one JS chunk in ${DIST}/assets, found ${assets.length}: ${assets.join(', ')}\n` +
      'Build with inlineDynamicImports (npm run build:standalone).'
  )
  process.exit(1)
}

const css = readFileSync(join(DIST, 'styles.css'), 'utf8')
const js = readFileSync(join(DIST, 'assets', assets[0]), 'utf8')

// A closing tag inside a JS string would end the script element early.
const safe = (s) => s.replace(/<\/script/gi, '<\\/script')

const html = `<title>${TITLE}</title>
<style>
${css}
</style>

<div id="game-root">
  <div id="canvas-host"></div>
  <div id="hud"></div>
</div>

<script type="module">
${safe(js)}
</script>
`

mkdirSync(OUT_DIR, { recursive: true })
writeFileSync(OUT, html)

const kb = (n) => `${(n / 1024).toFixed(0)} KB`
console.log(`${OUT}  ${kb(Buffer.byteLength(html))}  (css ${kb(css.length)}, js ${kb(js.length)})`)
