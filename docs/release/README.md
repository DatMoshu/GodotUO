# GUO versions and releases

Feature branches go through reviewed PRs into `integration`. Functional tests and
Godot sessions may run concurrently. FPS measurements and performance tuning
come after feature acceptance, with one measured Godot instance at a time.
Promote accepted integration work to `main` through a reviewed PR.

## Version source

`version.json` is authoritative. The starting development version is
`0.1.0-alpha.1`; it does not mean a release has shipped. Use
`MAJOR.MINOR.PATCH`, optionally followed by `-alpha.N`, `-beta.N`, or `-rc.N`.
Increment `android_version_code` for every distributed Android update, including
prereleases; never reuse a code with different release contents.

After editing the version, run `python tools/release/run.py sync`, then
`python tools/release/run.py check`. Commit the generated MSBuild, Godot and
export-template fields together. Windows numeric versions omit the prerelease
suffix; .NET, Godot and Android retain it. Development artifacts are identified
by their Actions run and exact commit, not by a new release tag on each push.

## CI and downloads

CI runs on `integration`, `main`, work/release branches and PRs. It checks privacy,
documentation, launchers, version consistency, tool tests and client builds.
The synthetic performance contracts do not launch Godot or measure FPS.
Platform compilation runs when its inputs change. The release export workflow
calls CI on the same commit before exporting. Integration exports are temporary
Actions artifacts, retained for seven days. They do not create GitHub releases.

The website's releases page reads published GitHub Releases, labels prereleases,
and links directly to their assets. It never exposes drafts. GitHub remains the
download source even if the API is unavailable. The site deploys from main.

## First release hold

Owner instruction: setup only until cleanup, debloat and performance work are
complete. Do not create the first tag or draft release until that hold is lifted.

## Prepare a release

1. Complete cleanup, debloat, feature acceptance and the final performance pass before proposing
   a public release. Promote the reviewed commit to main.
2. Update the version and Android code, sync them, and write
   `docs/release/v<version>.md`: changes, supported platforms, known issues and
   required user setup. Review and merge that preparation PR.
3. Once the owner lifts the first-release hold, set the GitHub repository variable
   `GUO_RELEASE_ENABLED` to `true`. With owner authorization, create and push the matching `v<version>` tag.
   The workflow rejects a mismatched tag, missing notes or a commit outside main.
4. CI and both export jobs must pass. The workflow creates a **draft**, with
   versioned platform ZIPs and SHA256 checksums. Prerelease versions are marked
   as prereleases. Inspect and install-test the exact artifacts before the owner
   publishes. No workflow automatically publishes a release.

Android release signing uses the existing repository secrets. When absent, only
the explicitly named debug APK is available; that is not a release-signed APK.
Never put credentials or UO client data in release assets.

Recommended repository rules: require PR review and passing CI checks on both
main and integration, prohibit force pushes/deletion, and restrict release-tag
creation to maintainers. Repository rules are separate from workflow files;
verify them in GitHub settings before relying on enforcement.
