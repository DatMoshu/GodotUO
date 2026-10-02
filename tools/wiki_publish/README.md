# wiki_publish

Publishes `docs/wiki/` to the repository's GitHub Wiki. The pages in
`docs/wiki/` are the source: edit them there, in a pull request. The wiki on
GitHub is a generated copy, and the `wiki` workflow rewrites it on every push
to `main` that touches `docs/wiki/`.

GitHub's wiki is its own git repository with its own link rules, so `run.py`
converts the links:

| In `docs/wiki/` | On the wiki |
|---|---|
| `[Text](Page-Name.md)`, `[Text](Page-Name.md#part)` | `[Text](Page-Name)`, `[Text](Page-Name#part)` |
| `[Text](../data_formats.md)` | `https://github.com/<repo>/blob/main/docs/data_formats.md` |
| `![alt](../images/x.png)` | `https://github.com/<repo>/raw/main/docs/images/x.png` |

`README.md` describes the folder, so it's left out. A page deleted from
`docs/wiki/` is deleted from the wiki too.

```
python tools\wiki_publish\run.py --check          every relative link resolves; writes nothing
python tools\wiki_publish\run.py --out DIR        write the converted pages into DIR
```

The wiki's repository exists only after its first page is saved once on
GitHub (Wiki → Create the first page). Python standard library only.
