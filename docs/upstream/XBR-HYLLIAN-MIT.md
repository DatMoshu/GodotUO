# xBR edge rule: provenance and licence

`godot/GUO/assets/pregame/shaders/xbr.gdshaderinc` (used by the pad-first pregame, PR #16 by
Speeko) is a reduced, single-pass corner blend. Its edge rule follows Hyllian's xBR shaders: the
weights 48/7/6 on the Y/U/V distance, the equality threshold 15, and the wd1/wd2 diagonal edge
weights. It is not a port of xBR-lv1/lv2/lv3 themselves.

- Author: Hyllian, `sergiogdb@gmail.com`
- Source read: libretro `glsl-shaders`, `xbr/shaders/xbr-lv2.glsl`
  (https://github.com/libretro/glsl-shaders/blob/master/xbr/shaders/xbr-lv2.glsl), header
  "Hyllian's xBR-lv2 Shader, Copyright (C) 2011-2016 Hyllian". libretro `slang-shaders`
  carries the same author's shaders (for example `edge-smoothing/ddt/shaders/ddt-xbr-lv1.slang`,
  Copyright (C) 2011-2022 Hyllian/Jararaca) under the same notice.
- Licence: MIT (the notice below), checked 2026-10-02.

```
Copyright (C) 2011-2016 Hyllian - sergiogdb@gmail.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```
