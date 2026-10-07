---
title: Configuration reference
---

# Configuration reference

Index: [wiki home](../README.md). The authoritative list lives in the JSON Schema
(`readmd config schema`); this page is the short tour. A complete example file is at
`samples/config.example.json` — validate it with `readmd config validate samples/config.example.json`.

## Files

| File | Scope |
| --- | --- |
| `~/.config/readmd/config.json` (`%APPDATA%\readmd\config.json`) | user |
| `.readmd.json` (found walking up from the document) | project — overrides the user file |

`readmd config init` writes a commented starter; `readmd config path` shows what is in effect.

## Themes

`theme` picks a built-in (`dark`, `light`, `nord`, `dracula`, `gruvbox-dark`, `solarized-dark`,
`solarized-light`, `catppuccin-mocha`, `one-dark`, `tokyo-night`, `monokai`), `auto`, or a name
from `themes`. A custom theme inherits from a built-in with `base`:

```json
{
  "theme": "ink",
  "themeCycle": ["ink", "dark", "light"],
  "themes": {
    "ink": { "base": "tokyo-night", "link": "#7dcfff", "accent": "#ff9e64", "codeTheme": "one-dark" }
  }
}
```

## Keys

`keys` maps an action name to a chord or a list of chords; the defaults for that action are
replaced. Chords are single keys, sequences (`gg`), modifiers (`ctrl+q`, `alt+f`, `shift+tab`),
named keys (`enter`, `f1`–`f12`, `pageup`) or Vim style (`<C-d>`).

```json
{ "keys": { "pageDown": ["space", "<C-f>"], "slides": "F5", "followTail": "alt+f" } }
```

## Behaviour

| Key | Default | Meaning |
| --- | --- | --- |
| `network` | `true` | `false` = offline: no remote images, Kroki or remote documents |
| `krokiUrl` | `https://kroki.io` | diagram server for kinds without a local tool |
| `maxWidth` | `0` | centre content at this width (0 = full) |
| `rememberPosition` | `true` | resume where you left off |
| `watch` | `true` | live reload |
| `graphics` | `auto` | `sixel`, `kitty`, `iterm2`, `half-block`, `none` |

TODO: document `ambiguousWide` for CJK terminals.
