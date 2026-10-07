---
title: Advanced usage
---

# Advanced usage

Back to [Getting started](getting-started.md) · up to the [index](../README.md).

## Palettes

`Ctrl+P` opens the quick-open palette over every Markdown file under the root — type a few
letters of the path (`refc` → `reference/config.md`). `Ctrl+G` opens the project search palette:
each hit shows `file:line` and a snippet, and `Enter` opens the page at that line. Search this
wiki for **palette** to land back here.

## Folding

Sections and `<details>` blocks fold with `za`; `zM` folds everything so a long page becomes an
outline, `zR` opens it again.

<details>
<summary>Folded by default — press Enter or za to open</summary>

Folds survive live reloads of the file, so you can keep a reference page collapsed while editing
it in another window.

</details>

## Key remapping

Every action has a name you can bind in the config (see the
[configuration reference](../reference/config.md#keys)). A few favourites:

```jsonc
"keys": {
  "quit": ["q", "ctrl+q"],
  "quickOpen": "ctrl+o",
  "toggleTheme": "F2"
}
```

## Slides

Any document whose sections are separated by `---` can be presented: `readmd deck.md --slides`,
or `Ctrl+S` inside the viewer. See `samples/slides.md` for the markers (`<!-- pause -->`,
`<!-- notes -->`).

## Remote documents

```bash
readmd gh:MoaidHathot/readmd                 # a repository's README
readmd https://github.com/owner/repo/blob/main/docs/guide.md
```

Relative links and images inside a remote document are rewritten against its source, so they
keep working.
