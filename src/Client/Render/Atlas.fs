/// Sprite atlas.
///
/// Right now every texture is generated procedurally at boot. That is a
/// deliberate seam, not the end state: the renderer only ever asks this module
/// for `SpriteSet`s, so swapping in a real CC0 spritesheet means rewriting
/// `build` to call `Assets.load` and slice frames - no renderer changes, no
/// gameplay changes. See README "Replacing the placeholder art".
///
/// Directional sets hold 8 frames in screen-facing order:
///   0 = E, 1 = SE, 2 = S, 3 = SW, 4 = W, 5 = NW, 6 = N, 7 = NE
module Vss.Client.Atlas

open Browser
open Browser.Types
open Fable.Core
open Vss.Client.Pixi
open Vss.Shared.Core
open Vss.Shared.Content

[<NoComparison; NoEquality>]
type SpriteSet =
    { /// Indexed by the entity's `Facing` field, modulo length.
      Textures: ITexture[]
      /// Fraction of the texture height that sits at the entity's ground point.
      AnchorY: float
      /// White silhouettes, same frame order, used for the hit flash. Empty
      /// where a kind has none. A texture swap beats an additive second pass:
      /// same draw call count, and it reads as a crisp impact rather than a
      /// tint shift.
      Flash: ITexture[]
      /// Rotate the sprite by its facing angle instead of swapping frames.
      RotateByFacing: bool
      /// Pixels-per-texture scaling applied on top of the entity's own scale.
      BaseScale: float }

[<NoComparison; NoEquality>]
type Atlas = { Sets: SpriteSet[]; Ground: ITexture }

// Canvas used for character textures. The ground point - where the entity
// "stands" - sits at (CharW/2, CharFoot).
let private CharW = 64.0
let private CharH = 64.0
let private CharFoot = 52.0

/// Pin the texture bounds to a known rectangle. Without this, Pixi sizes the
/// generated texture to the drawn geometry and the anchor maths drifts per
/// sprite.
let private pinBounds (g: IGraphics) (w: float) (h: float) =
    g.rect (0.0, 0.0, w, h) |> ignore
    fillAlpha g 0x000000 0.0 |> ignore

let private darken (c: int) (f: float) =
    let r = float ((c >>> 16) &&& 0xFF) * f |> int
    let gg = float ((c >>> 8) &&& 0xFF) * f |> int
    let b = float (c &&& 0xFF) * f |> int
    (min 255 r <<< 16) ||| (min 255 gg <<< 8) ||| min 255 b

/// One humanoid frame, facing screen-direction `dir` (0..7).
/// `silhouette` flattens every feature to white for the hit flash, keeping only
/// the ground shadow so the figure stays planted.
let private drawCharacter (g: IGraphics) (dir: int) (body: int) (head: int) (scale: float) (silhouette: bool) =
    pinBounds g CharW CharH

    let a = float dir * System.Math.PI / 4.0
    let dx = cos a
    let dy = sin a
    // dy > 0 points down-screen, i.e. toward the viewer.
    let facingCamera = dy > -0.35

    let cx = CharW / 2.0
    let bodyR = 11.0 * scale
    let bodyH = 15.0 * scale
    let bodyCy = CharFoot - bodyH
    let headR = 8.5 * scale
    let headCy = bodyCy - bodyH * 0.72

    // Ground shadow, baked in so depth sorting carries it along for free.
    g.ellipse (cx, CharFoot, 15.0 * scale, 6.5 * scale) |> ignore
    fillAlpha g 0x000000 0.32 |> ignore

    // Trailing leg, offset away from the facing direction.
    g.ellipse (cx - dx * 4.0 * scale, CharFoot - 4.0 * scale, 4.0 * scale, 5.0 * scale) |> ignore
    fillColor g (if silhouette then 0xFFFFFF else darken body 0.7) |> ignore

    // Torso, leaning slightly into the direction of travel.
    g.ellipse (cx + dx * 1.5 * scale, bodyCy, bodyR, bodyH) |> ignore
    fillColor g (if silhouette then 0xFFFFFF else body) |> ignore

    // Shoulder highlight catches an implied light from up-screen.
    g.ellipse (cx + dx * 1.5 * scale, bodyCy - bodyH * 0.45, bodyR * 0.72, bodyH * 0.32) |> ignore
    fillAlpha g 0xFFFFFF 0.14 |> ignore

    // Head.
    g.circle (cx + dx * 2.2 * scale, headCy, headR) |> ignore
    fillColor g (if silhouette then 0xFFFFFF else head) |> ignore

    if silhouette then
        ()
    elif facingCamera then
        // Eyes, offset along the facing direction with a perpendicular spread.
        let ex = cx + dx * 2.2 * scale + dx * headR * 0.34
        let ey = headCy + dy * headR * 0.26
        let px = -dy * headR * 0.36
        let py = dx * headR * 0.36
        g.circle (ex + px, ey + py, 1.6 * scale) |> ignore
        g.circle (ex - px, ey - py, 1.6 * scale) |> ignore
        fillColor g 0x141821 |> ignore
    else
        // Back of the head: a darker cap so the silhouette still reads.
        g.circle (cx + dx * 2.2 * scale, headCy - headR * 0.18, headR * 0.82) |> ignore
        fillColor g (darken head 0.58) |> ignore

let private buildDirectional (r: IRenderer) (body: int) (head: int) (scale: float) =
    let bake8 (silhouette: bool) =
        Array.init 8 (fun dir ->
            let g = newGraphics ()
            drawCharacter g dir body head scale silhouette
            let t = bake r g 2.0
            g.destroy ()
            t)

    { Textures = bake8 false
      Flash = bake8 true
      AnchorY = CharFoot / CharH
      RotateByFacing = false
      BaseScale = 1.0 }

let private buildGem (r: IRenderer) =
    let colors = [| 0x49B6FF; 0x5BD66F; 0xFFCC4D |]
    let tex = Array.zeroCreate colors.Length
    for i in 0 .. colors.Length - 1 do
        let g = newGraphics ()
        let s = 24.0
        pinBounds g s s
        g.ellipse (s / 2.0, s / 2.0 + 5.0, 6.0, 2.5) |> ignore
        fillAlpha g 0x000000 0.3 |> ignore
        // Iso-proportioned diamond: twice as wide as it is tall.
        g.poly (ResizeArray [ s / 2.0; 3.0; s / 2.0 + 7.0; s / 2.0; s / 2.0; s - 6.0; s / 2.0 - 7.0; s / 2.0 ]) |> ignore
        fillColor g colors.[i] |> ignore
        g.poly (ResizeArray [ s / 2.0; 3.0; s / 2.0 + 7.0; s / 2.0; s / 2.0; s / 2.0 ]) |> ignore
        fillAlpha g 0xFFFFFF 0.35 |> ignore
        tex.[i] <- bake r g 2.0
        g.destroy ()
    { Textures = tex
      Flash = Array.empty
      AnchorY = 0.62
      RotateByFacing = false
      BaseScale = 1.0 }

let private buildBolt (r: IRenderer) =
    let g = newGraphics ()
    pinBounds g 28.0 12.0
    g.ellipse (14.0, 6.0, 11.0, 3.6) |> ignore
    fillAlpha g 0x9AD8FF 0.55 |> ignore
    g.ellipse (15.0, 6.0, 7.0, 2.0) |> ignore
    fillColor g 0xFFFFFF |> ignore
    let t = bake r g 2.0
    g.destroy ()
    { Textures = [| t |]
      Flash = Array.empty
      AnchorY = 0.5
      RotateByFacing = true
      BaseScale = 1.0 }

let private buildBlade (r: IRenderer) =
    let g = newGraphics ()
    pinBounds g 34.0 20.0
    g.ellipse (17.0, 10.0, 15.0, 6.0) |> ignore
    fillAlpha g 0x7FE7FF 0.35 |> ignore
    g.ellipse (17.0, 10.0, 11.0, 3.4) |> ignore
    fillColor g 0xE8FBFF |> ignore
    let t = bake r g 2.0
    g.destroy ()
    { Textures = [| t |]
      Flash = Array.empty
      AnchorY = 0.5
      RotateByFacing = true
      BaseScale = 1.0 }

/// Unit ring, scaled per cast. Drawn as an ellipse so it lies flat on the
/// isometric ground plane rather than hovering as a circle.
let private buildNova (r: IRenderer) =
    let g = newGraphics ()
    let w = 140.0
    let h = 76.0
    pinBounds g w h
    g.ellipse (w / 2.0, h / 2.0, 64.0, 32.0) |> ignore
    strokeStyle g 0xFFE9A8 5.0 0.95 |> ignore
    g.ellipse (w / 2.0, h / 2.0, 58.0, 29.0) |> ignore
    fillAlpha g 0xFFCC4D 0.12 |> ignore
    let t = bake r g 1.5
    g.destroy ()
    { Textures = [| t |]
      Flash = Array.empty
      AnchorY = 0.5
      RotateByFacing = false
      // Texture is 128px across for a world radius of 1, so the renderer
      // divides by this to get world-accurate scaling.
      BaseScale = 64.0 }

/// Seamlessly tiling isometric floor: a diamond at the tile centre plus the
/// four quarter-diamonds at its corners, which together tile the plane.
///
/// Drawn onto a plain 2D canvas rather than through Pixi's Graphics pipeline.
/// `generateTexture` yields a RenderTexture, and a TilingSprite fed a
/// RenderTexture draws nothing at all - verified by reading the texture back
/// (correct pixels) while the screen stayed at the clear colour. A texture
/// built from a canvas element tiles correctly.
let private buildGround () =
    let w = int IsoHalfW * 2
    let h = int IsoHalfH * 2
    let hw = float w / 2.0
    let hh = float h / 2.0

    let canvas = document.createElement "canvas" :?> HTMLCanvasElement
    canvas.width <- float w
    canvas.height <- float h
    let ctx = canvas.getContext_2d ()

    let diamond (cx: float) (cy: float) =
        ctx.beginPath ()
        ctx.moveTo (cx, cy - hh)
        ctx.lineTo (cx + hw, cy)
        ctx.lineTo (cx, cy + hh)
        ctx.lineTo (cx - hw, cy)
        ctx.closePath ()

    ctx.fillStyle <- U3.Case1 "#111728"
    ctx.fillRect (0.0, 0.0, float w, float h)

    // Centre diamond, then the four corner quarters, which together give the
    // alternating checkerboard of an isometric floor.
    diamond hw hh
    ctx.fillStyle <- U3.Case1 "#18203a"
    ctx.fill ()
    ctx.strokeStyle <- U3.Case1 "#273354"
    ctx.lineWidth <- 1.0
    ctx.stroke ()

    for cx, cy in [ 0.0, 0.0; float w, 0.0; 0.0, float h; float w, float h ] do
        diamond cx cy
        ctx.fillStyle <- U3.Case1 "#0f1422"
        ctx.fill ()

    makeRepeating (textureFrom (box canvas))


/// Digit glyphs 0-9, drawn on a 2D canvas so they get real font rasterisation
/// rather than hand-plotted paths. Indexed by `Facing`, which damage-number
/// entities use to carry which digit they are.
let private buildDigits () =
    let w = 20
    let h = 26
    Array.init 10 (fun d ->
        let canvas = document.createElement "canvas" :?> HTMLCanvasElement
        canvas.width <- float w
        canvas.height <- float h
        let ctx = canvas.getContext_2d ()
        ctx.font <- "bold 20px ui-monospace, SFMono-Regular, Menlo, monospace"
        ctx.textAlign <- "center"
        ctx.textBaseline <- "middle"
        // Outline first, so a number stays readable over a pale sprite.
        ctx.lineWidth <- 4.0
        ctx.strokeStyle <- U3.Case1 "rgba(6,8,13,0.92)"
        ctx.strokeText (string d, float w / 2.0, float h / 2.0)
        ctx.fillStyle <- U3.Case1 "#ffffff"
        ctx.fillText (string d, float w / 2.0, float h / 2.0)
        textureFrom (box canvas))

/// Soft radial burst left where something died.
let private buildPuff (r: IRenderer) =
    let g = newGraphics ()
    let s = 48.0
    pinBounds g s s
    g.ellipse (s / 2.0, s / 2.0, 22.0, 11.0) |> ignore
    fillAlpha g 0xFFFFFF 0.30 |> ignore
    g.ellipse (s / 2.0, s / 2.0, 13.0, 6.5) |> ignore
    fillAlpha g 0xFFFFFF 0.55 |> ignore
    let t = bake r g 2.0
    g.destroy ()
    t

let build (r: IRenderer) : Atlas =
    let sets = Array.zeroCreate Sprites.Count
    sets.[Sprites.Player] <- buildDirectional r 0x4F8EF7 0xF2D0A4 1.0
    sets.[Sprites.Grunt] <- buildDirectional r 0x7A5CC4 0x9B86D8 0.92
    sets.[Sprites.Runner] <- buildDirectional r 0xC44F6A 0xE08A9A 0.82
    sets.[Sprites.Brute] <- buildDirectional r 0x4A7C4E 0x6FA374 1.15
    sets.[Sprites.Gem] <- buildGem r
    sets.[Sprites.Bolt] <- buildBolt r
    sets.[Sprites.Blade] <- buildBlade r
    sets.[Sprites.Nova] <- buildNova r

    sets.[Sprites.Digit] <-
        { Textures = buildDigits ()
          Flash = Array.empty
          AnchorY = 0.5
          RotateByFacing = false
          BaseScale = 1.0 }

    sets.[Sprites.Puff] <-
        { Textures = [| buildPuff r |]
          Flash = Array.empty
          AnchorY = 0.5
          RotateByFacing = false
          BaseScale = 1.0 }
    { Sets = sets; Ground = buildGround () }
