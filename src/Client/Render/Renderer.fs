/// Pixi-backed isometric renderer.
///
/// Two things here matter for phone performance:
///
///  1. **No per-frame allocation.** The render list lives in preallocated typed
///     arrays and the sprites live in a pool that only ever grows.
///
///  2. **No comparison sort.** Depth in an isometric view is exactly screen Y,
///     and screen Y is already bounded by the viewport, so entities are bucketed
///     by integer scanline in O(n). Sprites are then written to the pool in that
///     order - and since pool slot k is child k, Pixi's own child order *is* the
///     depth order. `sortableChildren` is never enabled.
module Vss.Client.Renderer

open Fable.Core.JsInterop
open Vss.Client.Pixi
open Vss.Client.Atlas
open Vss.Client.Iso
open Vss.Shared.Core
open Vss.Shared.Ecs
open Vss.Shared.Content
open Vss.Shared.Sim

[<NoComparison; NoEquality>]
type Renderer =
    { App: IApplication
      Atlas: Atlas
      Ground: ITilingSprite
      Layer: IContainer
      Cam: Camera
      mutable Pool: ISprite[]
      mutable PoolUsed: int

      // Render list, parallel arrays indexed 0 .. ListCount-1.
      ListEntity: int[]
      ListX: float32[]
      ListY: float32[]
      ListBin: int[]
      /// Render-list positions, depth-ordered.
      ListOrder: int[]
      mutable ListCount: int

      /// Counting-sort buckets, one per screen scanline (plus cull padding).
      mutable Bins: int[]
      mutable BinCount: int }

let private growPool (r: Renderer) (needed: int) =
    if needed > r.Pool.Length then
        let next = max needed (r.Pool.Length * 2)
        let arr = Array.zeroCreate next
        Array.blit r.Pool 0 arr 0 r.Pool.Length
        for i in r.Pool.Length .. next - 1 do
            let s = newSprite emptyTexture
            s.anchor.set (0.5, 0.5)
            s.visible <- false
            r.Layer.addChild (s :> IContainer) |> ignore
            setIx arr i (s)
        r.Pool <- arr

let private resizeBins (r: Renderer) =
    let n = int r.Cam.H + int (CullPad * 2.0f) + 4
    if n <> r.BinCount then
        r.BinCount <- n
        r.Bins <- Array.zeroCreate (n + 1)

let create (app: IApplication) (atlas: Atlas) (w: float32) (h: float32) =
    let layer = newContainer ()

    let ground =
        newTilingSprite (
            createObj [ "texture" ==> atlas.Ground
                        "width" ==> float w
                        "height" ==> float h ]
        )

    app.stage.addChild (ground :> IContainer) |> ignore
    app.stage.addChild layer |> ignore

    let cam = createCamera ()
    cam.W <- w
    cam.H <- h

    let r =
        { App = app
          Atlas = atlas
          Ground = ground
          Layer = layer
          Cam = cam
          Pool = Array.empty
          PoolUsed = 0
          ListEntity = Array.zeroCreate MaxEntities
          ListX = Array.zeroCreate MaxEntities
          ListY = Array.zeroCreate MaxEntities
          ListBin = Array.zeroCreate MaxEntities
          ListOrder = Array.zeroCreate MaxEntities
          ListCount = 0
          Bins = Array.empty
          BinCount = -1 }

    resizeBins r
    growPool r 512
    r

let resize (r: Renderer) (w: float32) (h: float32) =
    r.Cam.W <- w
    r.Cam.H <- h
    r.Ground.width <- float w
    r.Ground.height <- float h
    resizeBins r

/// Depth-order the render list by integer scanline. O(n + bins).
let private countingSort (r: Renderer) =
    let bins = r.Bins
    let n = r.ListCount
    Array.fill bins 0 (r.BinCount + 1) 0

    let mutable i = 0
    while i < n do
        let b = (ix r.ListBin i)
        setIx bins b ((ix bins b) + 1)
        i <- i + 1

    // Prefix sum, turning counts into write cursors.
    let mutable acc = 0
    let mutable b = 0
    while b < r.BinCount do
        let c = (ix bins b)
        setIx bins b (acc)
        acc <- acc + c
        b <- b + 1

    i <- 0
    while i < n do
        let bi = (ix r.ListBin i)
        setIx r.ListOrder (ix bins bi) (i)
        setIx bins bi ((ix bins bi) + 1)
        i <- i + 1

/// `alpha` is the fraction of the way from the previous tick to the current
/// one, so movement stays smooth when the display rate and the 30 Hz
/// simulation rate disagree.
let draw (r: Renderer) (g: GameState) (alpha: float32) (nowSec: float32) =
    let w = g.World
    let cam = r.Cam

    // ---- camera ----
    let p = g.Player
    if p >= 0 && hasAny (ix w.Flags p) Comp.Alive then
        let px = lerpf (ix w.Prevx p) (ix w.Px p) alpha
        let py = lerpf (ix w.Prevy p) (ix w.Py p) alpha
        centerOn cam px py

    r.Ground.tilePosition.set (float (-cam.X + cam.W * 0.5f), float (-cam.Y + cam.H * 0.5f))

    // ---- build the render list, culling off-screen entities ----
    let minX = -CullPad
    let maxX = cam.W + CullPad
    let minY = -CullPad
    let maxY = cam.H + CullPad
    let binBase = int CullPad

    let mutable count = 0
    let mutable i = 0
    while i < w.Count do
        let f = (ix w.Flags i)
        if hasAll f (Comp.Alive ||| Comp.Renderable) && not (hasAny f Comp.Dead) then
            let wx = lerpf (ix w.Prevx i) (ix w.Px i) alpha
            let wy = lerpf (ix w.Prevy i) (ix w.Py i) alpha
            let sx = toScreenX cam wx wy
            let sy = toScreenY cam wx wy
            if sx >= minX && sx <= maxX && sy >= minY && sy <= maxY then
                setIx r.ListEntity count (i)
                setIx r.ListX count (sx)
                setIx r.ListY count (sy)
                // Screen Y *is* isometric depth. Clamp defensively: a rounding
                // slip past the cull bounds would corrupt the bucket array.
                let bin = int sy + binBase
                setIx r.ListBin count ((if bin < 0 then 0 elif bin >= r.BinCount then r.BinCount - 1 else bin))
                count <- count + 1
        i <- i + 1

    r.ListCount <- count
    countingSort r
    growPool r count

    // ---- write sprites in depth order ----
    let mutable k = 0
    while k < count do
        let li = (ix r.ListOrder k)
        let e = (ix r.ListEntity li)
        let sprite = (ix r.Pool k)
        let kind = (ix w.Sprite e)
        let set = (ix r.Atlas.Sets kind)

        let frame =
            if set.Textures.Length = 1 then 0
            else
                let f = (ix w.Facing e)
                if f < 0 then 0 else f % set.Textures.Length

        sprite.texture <- (ix set.Textures frame)
        sprite.anchor.set (0.5, set.AnchorY)
        sprite.visible <- true
        sprite.x <- float (ix r.ListX li)
        sprite.y <- float (ix r.ListY li)

        if set.RotateByFacing then
            // Rotate by the entity's actual heading in *screen* space, which is
            // not the same angle as its world heading under an iso projection.
            let mutable dx = (ix w.Vx e)
            let mutable dy = (ix w.Vy e)
            if hasAny (ix w.Flags e) Comp.Orbiter then
                // Blades are positioned directly, so use the orbit tangent.
                let a = (ix w.OrbitAngle e)
                dx <- -sin a
                dy <- cos a
            if dx <> 0.0f || dy <> 0.0f then
                sprite.rotation <- atan2 (float (isoY dx dy)) (float (isoX dx dy))
        else
            sprite.rotation <- 0.0

        if kind = Sprites.Nova then
            // Expanding, fading ring. Texture is authored at 64px per world
            // unit of radius, so world radius maps to scale directly.
            let t = 1.0f - clampf 0.0f 1.0f ((ix w.Life e) / NovaVisual)
            let s = float ((ix w.Radius e) * IsoHalfW) / set.BaseScale * float (0.45f + 0.55f * t)
            sprite.scale.set (s, s)
            sprite.alpha <- float (1.0f - t)
            sprite.tint <- 0xFFFFFF
        else
            let mutable s = float (ix w.Scale e)
            let mutable a = 1.0
            let mutable tint = 0xFFFFFF

            if (ix w.Flash e) > 0.0f then
                // Hit feedback: a brief pink tint plus a size pop. A true white
                // flash would need an additive pass, which is not worth a second
                // draw call per hit on mobile.
                tint <- 0xFF9AA0
                s <- s * 1.12

            // Blink through the player's invulnerability window.
            if hasAny (ix w.Flags e) Comp.Player && (ix w.Cooldown e) > 0.0f then
                a <- (if (int (nowSec * 18.0f)) % 2 = 0 then 0.45 else 1.0)

            if kind = Sprites.Gem then
                // Gentle bob so gems read as pickups rather than scenery.
                sprite.y <- sprite.y + sin (float (nowSec * 3.4f + float32 e)) * 1.6

            sprite.scale.set (s, s)
            sprite.alpha <- a
            sprite.tint <- tint

        k <- k + 1

    // Retire whatever the previous frame used and this one did not.
    let mutable j = count
    while j < r.PoolUsed do
        (ix r.Pool j).visible <- false
        j <- j + 1

    r.PoolUsed <- count
    r.App.render ()
