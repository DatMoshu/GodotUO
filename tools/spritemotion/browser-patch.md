# Browser editor patch

The upstream Godot CEF 1.16.2 `CefTexture` is not a tool class. Its lifecycle and
input callbacks do not run when it is hosted by an EditorPlugin. GUO uses this
one-line patch against upstream commit 2be21490057b5edfd3836f1632d577263bbac2c2:

```diff
-#[class(base=TextureRect)]
+#[class(base=TextureRect, tool)]
```

File: crates/gdcef/src/cef_texture/mod.rs. No changes to CEF, Chromium or the
helper executable. Upstream: https://github.com/dsh0416/godot-cef (MIT).

Maintainer build: in a Visual C++ x64 developer environment, install Rust
nightly and `export-cef-dir` version 154.1.0+154.0.26. Export the Windows x64
CEF SDK version 154.0.26, set CEF_PATH to it, and run:

    cargo +nightly build --locked --release --package gdcef

Use the upstream release's other Windows runtime files. Replace only gdcef.dll.
Record the resulting SHA-256 with the packaged release. End users must receive a
prebuilt, checksum-verified runtime; they must never need Rust or Visual C++.
The prebuilt Windows x64 library is packaged in runtime/gdcef.dll (6.5 MB), with its MIT license. run.py verifies its SHA-256 before installation. Chromium itself is fetched from the pinned upstream release. Rebuilds must update EDITOR_HASH after verification.
