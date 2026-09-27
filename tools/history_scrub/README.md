# Unpublished-history scrub

Version 1.0.0, authored in this repository; Python standard library and Git
plumbing only. No third-party rewriting library or pip dependency.

The tool clones with `git clone --no-local --bare` into a unique directory
under `<source>/build/scrub/`. All object rewrites and temporary branch refs
happen in that clone. It preserves the `origin/main` boundary and its entire
ancestry. Merge topology, timestamps, executable bits and symlinks survive;
invalidated commit signatures and mergetags are removed. Submodule pointers
are unchanged. Annotated tags are not republished.

Supply a private JSON map of exact text replacements outside tracked files.
The same byte replacements cover blobs, filenames, commit messages and
identity headers. Inputs must be single-line strings. Do not put private
values in this README, tests or command examples.

```text
python tools/history_scrub/run.py --source <checkout> --replacements <private-map.json> work/example
python tools/history_scrub/run.py --source <checkout> --replacements <private-map.json> --publish --expected-main <full-sha> main
```

The first command is a dry run: it writes `dry-run-commit-map.txt` and
`dry-run-result.json` but publishes no ref. The second verifies the exact
source main SHA, writes `commit-map.txt` and `result.json`, and fetches only
`refs/scrub/main` into the source repository. It never moves `main`, branches,
`origin/main` or worktrees. The director performs any later main swap.

Verification scans every rewritten commit with `git grep -a -F` and then
checks all reachable rewritten objects, including metadata and filenames.
No sensitive match is printed. The old-to-new commit map supports downstream
rebases. Each run retains its throwaway clone for review; it deliberately
performs no recursive cleanup. The source still holds old history until its
owner explicitly retires old refs/reflogs and performs garbage collection.
