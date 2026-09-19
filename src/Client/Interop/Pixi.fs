/// Minimal hand-written bindings for PixiJS 8.
///
/// Deliberately narrow: only the surface the renderer actually touches. A full
/// binding of Pixi would be a maintenance burden with no payoff, and there is
/// no maintained Fable binding for v8 to lean on.
module Vss.Client.Pixi

open Fable.Core
open Fable.Core.JsInterop

type ITexture =
    /// The underlying `TextureSource`; reached for addressing mode.
    abstract source: obj

type IPoint =
    abstract x: float with get, set
    abstract y: float with get, set
    abstract set: float * float -> unit

type IContainer =
    abstract x: float with get, set
    abstract y: float with get, set
    abstract alpha: float with get, set
    abstract visible: bool with get, set
    abstract rotation: float with get, set
    abstract zIndex: int with get, set
    abstract scale: IPoint
    abstract position: IPoint
    abstract addChild: IContainer -> IContainer
    abstract removeChildren: unit -> unit
    abstract destroy: unit -> unit

type ISprite =
    inherit IContainer
    abstract texture: ITexture with get, set
    abstract anchor: IPoint
    abstract tint: int with get, set
    abstract width: float with get, set
    abstract height: float with get, set

type IGraphics =
    inherit IContainer
    abstract circle: float * float * float -> IGraphics
    abstract ellipse: float * float * float * float -> IGraphics
    abstract rect: float * float * float * float -> IGraphics
    /// Takes a ResizeArray, NOT a float[]. Fable maps `float[]` to
    /// `Float64Array`, which fails Pixi's `Array.isArray` check on the points
    /// argument - the call then silently draws nothing. ResizeArray compiles to
    /// a plain JS Array.
    abstract poly: ResizeArray<float> -> IGraphics
    abstract moveTo: float * float -> IGraphics
    abstract lineTo: float * float -> IGraphics
    abstract closePath: unit -> IGraphics
    abstract fill: obj -> IGraphics
    abstract stroke: obj -> IGraphics
    abstract clear: unit -> IGraphics

type ITilingSprite =
    inherit IContainer
    abstract tilePosition: IPoint
    abstract width: float with get, set
    abstract height: float with get, set

type IRenderer =
    abstract resize: float * float -> unit
    abstract generateTexture: obj -> ITexture
    abstract width: float
    abstract height: float

type IApplication =
    abstract init: obj -> JS.Promise<unit>
    abstract stage: IContainer
    abstract renderer: IRenderer
    abstract canvas: Browser.Types.HTMLElement
    abstract render: unit -> unit

// ---------------------------------------------------------------------------
// Constructors
// ---------------------------------------------------------------------------

[<Import("Application", "pixi.js")>]
let private ApplicationClass: obj = jsNative

[<Import("Container", "pixi.js")>]
let private ContainerClass: obj = jsNative

[<Import("Sprite", "pixi.js")>]
let private SpriteClass: obj = jsNative

[<Import("Graphics", "pixi.js")>]
let private GraphicsClass: obj = jsNative

[<Import("TilingSprite", "pixi.js")>]
let private TilingSpriteClass: obj = jsNative

[<Import("Texture", "pixi.js")>]
let private TextureClass: obj = jsNative

/// `Texture.from(source)`. A texture built from a real canvas element tiles
/// correctly in a TilingSprite; one produced by `generateTexture` (a
/// RenderTexture) renders as nothing, whatever its address mode.
let textureFrom (source: obj) : ITexture = TextureClass?from (source)

[<Emit("new $0()")>]
let private ctor0 (c: obj) : 'T = jsNative

[<Emit("new $0($1)")>]
let private ctor1 (c: obj) (a: obj) : 'T = jsNative

let newApplication () : IApplication = ctor0 ApplicationClass
let newContainer () : IContainer = ctor0 ContainerClass
let newGraphics () : IGraphics = ctor0 GraphicsClass
let newSprite (t: ITexture) : ISprite = ctor1 SpriteClass t
let newTilingSprite (opts: obj) : ITilingSprite = ctor1 TilingSpriteClass opts

let emptyTexture: ITexture = TextureClass?EMPTY

// ---------------------------------------------------------------------------
// Style helpers
// ---------------------------------------------------------------------------

let inline fillColor (g: IGraphics) (color: int) = g.fill (createObj [ "color" ==> color ])

let inline fillAlpha (g: IGraphics) (color: int) (alpha: float) =
    g.fill (createObj [ "color" ==> color; "alpha" ==> alpha ])

let inline strokeStyle (g: IGraphics) (color: int) (width: float) (alpha: float) =
    g.stroke (createObj [ "color" ==> color; "width" ==> width; "alpha" ==> alpha ])

/// Textures default to clamp-to-edge addressing; a tiled texture must opt into
/// repeating or its edges smear.
let makeRepeating (t: ITexture) =
    t.source?addressMode <- "repeat"
    t.source?style?addressMode <- "repeat"
    t

/// Rasterise a Graphics into a texture. Done once at boot so nothing in the
/// frame loop ever touches the geometry pipeline.
let bake (renderer: IRenderer) (g: IGraphics) (resolution: float) : ITexture =
    renderer.generateTexture (createObj [ "target" ==> g; "resolution" ==> resolution; "antialias" ==> true ])
