# Fonts: bitmap and TrueType text (`#import "graphics"`)

`Renderer.DrawText` draws text with the **console font** of the renderer (the built-in 8x14 bitmap font, or 8x8) unless it is told another font. A font can be

- one of the two **built-in bitmap fonts**: `"8x14"` (also written `"14x8"`) and `"8x8"` - fixed size, the size argument is ignored,
- a **TrueType font** (`.ttf`, outlines in the `glyf` format; OpenType fonts with PostScript outlines are not read): any size in pixels, anti-aliased, with kerning. The rasteriser is
  [libschrift](https://github.com/tomolt/libschrift) 0.10.2 (ISC license, `src/fire.Terminal/TrueType/LICENSE-libschrift.md`), ported to C# for the virtual machine and to C++ for the native build -
  the tests compare both with the original: the glyph images are equal, and so are the pixels of a text in the VM and in a native program.

```
#import "graphics"

var fb = new Framebuffer(320, 100)
var r = new Renderer(fb)
var ink = new SolidBrush(0xFFFFFFFF)

Fonts.Add(new Resource("fonts/Roboto-Regular.ttf"))            // embedded in the program (docs/RESOURCES.md), now findable by name
r.DrawText(8, 8, "Hello, world", ink, undefined, Fonts.Get("Roboto-Regular"), 24)
r.DrawText(8, 48, "Hello, world", ink, undefined, Fonts.Get("DejaVu Sans"), 16)   // a font installed on the system
r.DrawText(8, 72, "Hello, world", ink)                                           // the console font, as before
```

## Finding a font: `Fonts.Get(name)`

`Fonts.Get` looks the name up in this order (names are compared **without case, spaces and punctuation**: `"DejaVu Sans"` = `"dejavusans"` = `"DejaVu-Sans"`):

1. `""` (or `"console"`) - the font of the renderer;
2. `"8x14"`, `"14x8"`, `"8x8"` - the built-in bitmap fonts;
3. a font that was **added with `Fonts.Add`** anywhere in the program: by its alias (the second argument), by the **file name** of the resource without folder and extension (`fonts/Roboto-Regular.ttf` ->
   `Roboto-Regular`), by the family name or by the full name stored in the font;
4. a font **installed on the system**: the files `*.ttf`/`*.otf` in the font folders - `%WINDIR%\Fonts` and `%LOCALAPPDATA%\Microsoft\Windows\Fonts` (Windows), `/System/Library/Fonts`,
   `/System/Library/Fonts/Supplemental`, `/Library/Fonts`, `~/Library/Fonts` (macOS), `/usr/share/fonts`, `/usr/local/share/fonts`, `~/.fonts`, `~/.local/share/fonts` (Linux) - looked up by the file name first, then
   by the full name, then by the family name. The environment variable **`FIRE_FONT_DIRS`** (folders separated by `;`) adds folders in front of these - handy for a portable program that ships its fonts next to it.
   A board without a file system (ESP32 without a mounted partition) has no system fonts: embed the font with `Fonts.Add`;
5. otherwise the **console font** - text never fails because a font is missing. `Fonts.Has(name)` tells whether the name was found.

```
class Fonts {
    static Font Add(resource, string alias = "")   // an embedded .ttf; GraphicsException if it is not a TrueType font
    static Font FromBytes(data)                    // the bytes of a .ttf (a byte buffer); no name to find it by
    static Font Get(string name)                   // see above
    static bool Has(string name)
    static Font Console()                          // the font of the renderer
}
class Font { string Name(); bool IsBitmap(); Release() }    // Release frees a font from Add/FromBytes; the built-in ones stay
```

A `Font` is only a handle: you do not need to keep it or destroy it. The fonts are shared by the whole program.

## Drawing and measuring

```
renderer.DrawText(x, y, text, foreground, background = undefined, font = undefined, size = 0)
renderer.TextWidth(text, font = undefined, size = 0)     // pixels
renderer.TextHeight(font = undefined, size = 0)          // the height of a line of text
renderer.TextAscent(font = undefined, size = 0)          // from the top of the line to the baseline
```

`(x, y)` is the **upper left corner of the line of text**; `size` is the height of the letters in pixels (the em of the font; `0` or less = 14, at most 512). With a `background` brush the line box (width of the text,
height of a line) is filled first. The glyph pixels are blended with the coverage (anti-aliasing) times the alpha of the brush, so the usual rules of `AlphaBlending` apply: with blending off the
pixels are copied with their alpha, in a **palette** framebuffer a pixel is drawn from alpha 128 on (no anti-aliasing - text on a palette framebuffer is better set in a bitmap font).

The layout is exact and the same in the VM and natively: the line is `Ascent + Descent + LineGap` pixels high (each rounded half up from the `hhea` table scaled to `size`); the baseline is `Ascent + LineGap / 2` below the top;
the pen starts at `x` and moves on by the advance width of each glyph plus the kerning of the pair (the `kern` table; fractions of a pixel are kept), and each glyph is drawn at the pen position rounded to
whole pixels. Characters below U+0020 are skipped; a character the font does not have is drawn as glyph 0 of the font (usually an empty box); a pair of surrogates is one character.
A damaged font gives "no glyph" instead of an error.

## In the UI

Every `UI.Element` has `font` (a name for `Fonts.Get`, `""` = inherit) and `fontSize` (pixels, `0` = inherit): a font set on a panel applies to everything inside it, a menu opened from an element takes the font of that
element, and `root.theme.font` / `root.theme.fontSize` (default `""` and `14`) are the last resort. Without any of them the console font is used, exactly as before. In markup: `font="Roboto-Regular" fontSize="18"`.
Labels, buttons, check boxes, text fields, lists, trees, radio buttons and menus measure and draw their text with the font of the element; a text field with a proportional font measures the text up to the
caret and under the mouse.

## Limits

- TrueType collections (`.ttc`) and OpenType fonts with PostScript outlines (`CFF`) are not read; hinting, bold/italic synthesis and sub-pixel positioning are not done (pick the bold file instead:
  `Fonts.Get("DejaVu Sans Bold")`).
- Text is laid out left to right, one line, without shaping (no ligatures, no right-to-left, no combining marks beyond what the font's `cmap` offers).
- The console font stays CP437 (characters above 255 show as `?`); a TrueType font shows what its `cmap` has.
