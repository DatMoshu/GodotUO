# The world depth pass against the canvas (PERF-GPU, step 1)

Step 1 of the depth-buffer world pass (ADR-0007, amendment 2026-10-19):
`WorldDepthPass` draws quads into a 3D `SubViewport`, and the GPU depth test
orders them. `launchers\dev\depth_probe.bat` checks it against the canvas
world pass on synthetic art, so it needs neither a UO install nor a shard.
Nothing in the client draws through the pass yet.

## Results, 2026-10-19 (RTX 4090)

| Check | Forward+ (Vulkan) | Compatibility (OpenGL 3) |
|---|---|---|
| Coverage, 11 scales x 3 origins | identical | identical |
| Colour, same cases | identical (0 px) | every pixel off: written linear (see below) |
| Adjacent 24-bit keys, bottom / middle / top, both submission orders | ordered, one page and two | bottom and middle lose in one page; top ordered |
| Adjacent 28-bit keys | ordered where the floats differ | not ordered in one page |
| Equal keys in one page | later wins (the sort's insertion order) | later wins |
| **Probe** | **PASS, 39/39** | FAIL, 4/39 |

The scales are the camera's own (it scales the world by the zoom): 0.5,
0.75, 1, 1.25, 1.5, 2 and 2.5, plus 0.8, 0.4, 1.333 and 0.667. The origins
are (0, 0), (0.37, 0.5) and (0.5, 0.25). At 1.25 and 2.5, integer positions
land on half pixels, so quad edges and texel boundaries fall exactly on pixel
centres. Those exact cases are what took four runs to match.

## How it matches the canvas

A pixel centre exactly on an edge or a texel boundary is decided by float
rounding. Guessing a rule did not work: an earlier version took the lower
texel at boundaries, and the canvas turned out to take the upper one at other
scales. So the pass repeats Godot 4.7.2's own rect maths step for step
(`renderer_rd/shaders/canvas.glsl`, `renderer_canvas_render_rd.cpp`):

- **Corners and triangles:** corners in the order top-left, bottom-left,
  bottom-right, top-right, drawn as triangles 0-1-2 and 0-2-3.
- **Source rect:** the pixel region times `1 / size`, rounded to float on the
  CPU as Godot does.
- **UV:** `src.xy + src.zw * corner`.
- **Transforms:** the item transform and the screen transform as the same
  `mat4` products, with the same float constants (`translate_local(-size / 2)`
  then `scale(2 / size)`).
- **Sampling:** the hardware's nearest filter.

The texel is read raw and decoded from sRGB with the exact formula, so
Forward+'s output encode gives the texel's bytes back.

## Compatibility: what is left

The switch stays off on Compatibility (the web build), as the amendment says.
Two causes are known, neither fixed yet:

- **Colour.** The 3D pass in a Compatibility `SubViewport` writes linear
  values with no sRGB encode: 128 comes out as 54, which is the sRGB-to-linear
  value. On that renderer the decode must be skipped: a uniform, off on
  Compatibility.
- **Depth precision.** Adjacent keys collide at the bottom and middle of the
  range but order at the top. That fits a 24-bit depth buffer under OpenGL's
  −1..1 clip range, which halves the precision. On Compatibility the key
  would get 22 bits, or the ties would go to submission order. Both are
  unconfirmed until they are measured.

## Running it

```
launchers\dev\depth_probe.bat
launchers\dev\depth_probe.bat --rendering-method gl_compatibility
```

Set `GUO_DEPTH_PROBE_DUMP` to a folder to save the canvas and depth images of
every failing case. The probe needs a real window, not `--headless`.
