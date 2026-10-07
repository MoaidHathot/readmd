# Project wiki

A small folder of linked Markdown files to try readmd's **multi-file** features. Open it as a
directory and readmd lists every page, newest first:

```bash
readmd samples/wiki            # directory mode: pick a page, then ]f / [f step through them
readmd samples/wiki/README.md  # or start here and follow the links
```

| Try | Keys |
| --- | --- |
| Quick-open any page by fuzzy name (`gs` finds *getting-started*) | `Ctrl+P` |
| Search every page for a word — try `palette` or `TODO` | `Ctrl+G` |
| Next / previous page in the folder | `]f` / `[f` |
| Follow a link, then come back | `Enter` or `f` + label, then `←` |
| Jump to the section a link points at | links with `#fragments` below |

## Pages

- [Getting started](guide/getting-started.md) — install and first run
- [Advanced usage](guide/advanced.md#key-remapping) — jumps straight to the *Key remapping* section
- [Configuration reference](reference/config.md)
- [Ideas & TODOs](notes/ideas.md)

Links are **sandboxed** to the root you opened (`--root` widens it), so `../../README.md`
from here is refused, while every page inside `samples/wiki/` is reachable.

<details>
<summary>Why a wiki sample?</summary>

Quick-open, project search and file stepping only make sense with several files around. These
pages cross-link with relative paths and anchors so each of those features has something to do.

</details>
