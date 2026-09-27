# docs/wiki

The project wiki, kept in the repository so it is reviewed, versioned and
merged like everything else. GitHub renders these pages in place; the same
files are what the repository's GitHub Wiki is published from.

## Layout

| File | Role |
|---|---|
| `Home.md` | The wiki's front page (GitHub Wiki serves it at `/wiki`). |
| `_Sidebar.md` | The navigation column GitHub Wiki shows beside every page. |
| `<Page-Name>.md` | One page each. File names use hyphens, as the wiki's URL slugs do. |
| `README.md` | This file. It is not a wiki page. |

Pages link to each other with plain relative links, `[Text](Page-Name.md)`,
so they resolve when browsed in the repository. Links into the rest of the
repository (`docs/`, `tools/`, `launchers/`) are written as paths in code
spans, not as links, because the wiki and the repository are not served from
the same root.

## Publishing to the GitHub Wiki

The wiki is a separate git repository, `<repo>.wiki.git`. GitHub Wiki
resolves page links without the `.md` suffix, so strip it on the way over:

```sh
git clone https://github.com/<owner>/<repo>.wiki.git ../wiki
cp docs/wiki/*.md ../wiki/
rm ../wiki/README.md
sed -i -E 's/\]\(([A-Za-z0-9_-]+)\.md\)/](\1)/g' ../wiki/*.md
cd ../wiki && git add -A && git commit -m "Sync from docs/wiki" && git push
```

Only the repository owner pushes (see [Contributing](Contributing.md)).
Edit the pages here, in a `work/*` branch; never edit the wiki clone
directly, or the next sync overwrites it.

## Rules for these pages

- Every claim that something works must trace to an ADR validation section,
  a tool README run log or a commit message. Where the sources say a thing
  is written but has not been run, say **written, not verified**.
- No personal machine details: no LAN addresses, device serials, key paths,
  passwords or private account names. The three game master test accounts
  may be named; the owner and trade-partner accounts are referred to by
  their `config.bat` variable only.
- No game data, and no captures rendered from game data.
