# Optional pre-push checks

Run `launchers/dev/install_git_hooks.bat` to opt in, or
`python tools/git_hooks/install.py` from the checkout. Pass `--uninstall`
to remove only this tool's managed hook. Installation is never automatic.

The installer preserves existing hooks and refuses an existing
`core.hooksPath` configuration. With a hook manager, integrate
`python tools/git_hooks/check.py` yourself and forward the pre-push stdin.
Git worktrees share the repository's hooks. The generated, untracked hook
remembers the installing Python interpreter; `UO_PYTHON` can override it.

For each distinct pushed commit tip, the hook makes a temporary local clone
under `build/git_hooks` and runs that commit's docs lint and privacy scanner.
Dirty working files cannot hide a committed problem; different branches and
commit tags are checked independently. Deletion-only pushes do no scanning.
The user's ignored privacy deny list is copied into the temporary checkout,
never committed. Failures stop the push and leave the user's index and
working files unchanged. No network access, push or remote mutation is made
by the checker itself.

Before the tip checks, every commit the push would publish (remote..local) is read from
the object store and refused if it adds a compiled program (.dll, .exe, .so, ...), matches
the CI machine-path guard outside `docs/`, or contains a deny-list value, binaries included.
A clean tip does not excuse an earlier commit: GitHub publishes them all. The full docs lint
and privacy scan still run on pushed **tips** only.
It is a contributor convenience, not a security boundary against a malicious
author changing the checks. CI and review remain necessary; use the history
scrub workflow separately when an earlier commit contains private material.

Run `python tools/git_hooks/test_hooks.py` for disposable-repository tests.
