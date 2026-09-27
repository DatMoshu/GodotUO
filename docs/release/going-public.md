# Going public: checklist

For the owner. Each item says whether it's done, and what's left is yours. Nothing here makes the
repository public; that switch is only ever the owner's.

## Already done

- [x] Full-history scan of `main` (2026-09-27): no tokens, keys, private addresses, device serials or
      personal emails. The only emails are GitHub noreply, Anthropic's co-author noreply, ModernUO's
      contact in its file headers, and example domains. (The Thor's SurfaceFlinger display id remains in
      two historical diffs; it identifies a device model and port, not a person.)
- [x] CI guards on every push: no client data, no machine paths, `tools/privacy_scan`, docs links.
- [x] Licences: ClassicUO BSD 2-Clause headers kept; provenance in `docs/upstream/`; CC0 for the
      built-in backgrounds and the store's sample packs.
- [x] README, CHANGELOG, CONTRIBUTING, SECURITY, issue and PR templates, the wiki, Known Issues.
- [x] Downloadable builds from the `release` workflow; a `v*` tag makes one draft release with all three
      platforms and uses `docs/release/<tag>.md` as its notes.

## Readiness review, 2026-09-27 midday

- [x] LICENSE: BSD 2-Clause, crediting ClassicUO (andreakarasho) and the GUO contributors.
- [x] CONTRIBUTING, SECURITY, CODE_OF_CONDUCT (added), issue and PR templates.
- [x] README status section brought up to date (Android on two devices, Steam Deck, touch features,
      the Store and editor, the web build in progress).
- [x] Repository size: the pack is 54 MB. The largest blobs are the built-in background videos (2–5 MB
      each), the README image, and the brand sigil.
- [x] `.claude/` (agents, skills, rules, hooks) is public by design, with MIT provenance in
      `docs/upstream/`. The privacy scan covers it.
- [x] The web engine fork and its private .NET SDK stay out of git (`tools/godot_web/` holds only a README).
- [ ] **The press kit in history:** `design/press-kit/` was committed once and then removed ("Keep the press
      kit out of the repo for now"), so its files, including `guo-godot-mark-*`, remain in history. If that
      mark is derived from the Godot logo (CC BY 4.0, Andrea Calabró), either credit it or rewrite that
      path out of history before going public. Owner call.
- [ ] **The stale remote branch** `codex/guo-brand-icons` on GitHub: delete it, or merge anything left.
- [ ] **The CI Android APK is debug-signed with a keystore made fresh on each run,** so a newer download
      can't install over an older one (uninstall first). Say so in the release notes, or add a stable signing
      key as a GitHub secret before the first public release.

## The owner decides

- [ ] **Local backup branches** with the pre-scrub history (`backup/main-unscrubbed`,
      `backup/director-ci-old`, `backup/main-pre-night`): delete them, or keep them and never push them.
- [ ] **The Android download's shard address** (`127.0.0.1`): ship as is with the note in the release
      notes, or add an in-app shard address before release.
- [ ] **Repository visibility**: Settings → General → Change visibility → Public.
- [ ] **Pages**: Settings → Pages → Source: GitHub Actions, then run the `pages` workflow (it's manual
      now). Add a push trigger in `.github/workflows/pages.yml` if you want it to follow main.
- [ ] **Security settings** once public: enable private vulnerability reporting (SECURITY.md points to
      it), Dependabot alerts, and secret scanning with push protection.
- [ ] **Branch protection on main**: require the `CI` and `build` checks; decide whether the director
      keeps direct push or moves to PRs.
- [ ] **First release**: tag `v0.1.0` on a green main, check the draft release's three downloads and
      notes, then publish it.
- [ ] **Announce**: the #godot-uo-client showcase posts are ready to adapt.
