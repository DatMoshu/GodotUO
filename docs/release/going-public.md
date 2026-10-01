# Going public: checklist

For the owner. Each item says whether it's done, and what's left is yours. Nothing here makes the
repository public; that switch is only ever the owner's.

## Already done

- [x] **Full-history scan, refreshed 2026-10-01:** every commit on `main` (888) and every pull-request ref
      GitHub keeps (`refs/pull/*`). Results:
  - no tokens, keys, LAN addresses, device serials or personal emails;
  - no material from other projects;
  - every identity is a GitHub noreply address.

  The pre-scrub commits are not on GitHub (checked by SHA). Two harmless remnants stay in old diffs:
  - an old checkout folder name, without a user name;
  - the Thor's SurfaceFlinger display id, which identifies a device model and port, not a person.
- [x] **Agent working files out of the tree (2026-10-01):**
  - `production/` keeps only `session-state/.gitkeep`, as in Claude Code Game Studios upstream. Sprint
    plans, session state and logs are gitignored.
  - The overnight sprint plan and the Discord post tool for Codex's dungeons are removed.
- [x] **`.gitignore` covers** every `config.local.bat*` (backups too), `.playwright-mcp/`, `.claude/worktrees/`,
      `.claude/settings.local.json` and `tools/privacy_scan/deny.local.txt`.
- [x] **CI guards on every push:** no client data, no machine paths, `tools/privacy_scan`, docs links.
      The private deny list exists only on the director's machine, so the full privacy grep is a local
      gate before every push to `main`.
- [x] **Licences:**
  - ClassicUO's BSD 2-Clause headers are kept, with provenance in `docs/upstream/`.
  - CCGS (MIT) and Kenney's input prompts (CC0) are recorded there too.
  - The built-in backgrounds and the store's sample packs are CC0.
  - The old press kit's Godot-logo derivative is credited in `docs/upstream/BRAND.md`.
- [x] **Project files:** README, CHANGELOG, CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, the issue and PR
      templates, the wiki and Known Issues.
- [x] **Downloadable builds** come from the `release` workflow: a `v*` tag makes one draft release with all
      three platforms.
- [x] **The pack is about 200 MB.** The largest blobs are the built-in background videos (2–5 MB each),
      the README image and the brand sigil.

## Owner, on the machine

- [ ] **Run `build/cleanup_2026-09-28/cleanup.bat` in the main checkout.** It backs up the stale edits,
      resets to `origin/main`, deletes the stray files and removes the idle worktrees.
- [ ] **Then run `build/cleanup_2026-09-28/prune_branches.bat`.** It deletes:
  - the 110 merged local branches;
  - the pre-scrub branches (`backup/*`, `work/director-ci`, `work/background`, `work/editor`, `work/ui`);
  - the stale `uoport/*` refs.

  It first checks a backup bundle kept outside the repo (the script names it), which holds every
  branch it deletes.

  Six pre-scrub branches are still checked out in worktrees, so the script can't delete them:
  `work/docs`, `work/dual-launch`, `work/icon`, `work/steamdeck`, `work/trial-render` and
  `work/video-content`. Never push them. Remove those worktrees when their agents are done.
- [ ] **Never `git push --all`.** Push only `main` and named work branches.

## Owner decisions before the switch

- [ ] **The CI Android APK is debug-signed with a keystore made fresh on each run,** so a newer download
      can't install over an older one (uninstall first). Say so in the release notes, or add a stable
      signing key as a GitHub secret before the first public release.
- [ ] **The Android download's shard address** (`127.0.0.1`): ship as is with a note, or add an in-app
      shard address before release.
- [ ] **Five open Dependabot PRs** (#6–#10, GitHub Actions bumps): merge them after CI passes, or close
      them.

## The switch and after (commands ready)

- [ ] **Repository visibility:** Settings → General → Change visibility → Public.
- [ ] **Security settings:**
  - `gh api -X PUT repos/DatMoshu/GodotUO/private-vulnerability-reporting`
  - `gh api -X PUT repos/DatMoshu/GodotUO/vulnerability-alerts`
  - secret scanning and push protection: Settings → Code security.
- [ ] **Branch protection:**
      `gh api -X PUT repos/DatMoshu/GodotUO/branches/main/protection --input docs/release/branch-protection.json`
      (requires `CI / guard` and `CI / build`; admins may still push, so the director workflow keeps
      working).
- [ ] **Pages:** Settings → Pages → Source: GitHub Actions, then `gh workflow run pages`. Add a push
      trigger in `.github/workflows/pages.yml` if Pages should follow `main`.
- [ ] **First release:** tag `v0.1.0` on a green `main`, check the draft release's three downloads and
      notes, then publish it.
- [ ] **Announce:** the showcase post drafts are ready to adapt.
