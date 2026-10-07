---
title: Getting started
---

# Getting started

Part of the [wiki sample](../README.md). Next page: [Advanced usage](advanced.md) (`]f` gets you there too).

## Install

Pick whichever fits (see the project README for the full table):

```bash
dnx readmd README.md                 # run once with the .NET 10 SDK
dotnet tool install -g readmd        # or install the tool
scoop install readmd                 # Windows, from a release manifest
```

## First run

1. `readmd README.md` opens the interactive viewer.
2. Press `?` for the keybinding overlay, `t` for the table of contents.
3. Press `f` and type a label to follow a link — try it on the link to the
   [configuration reference](../reference/config.md).
4. `q` quits; your position is remembered for next time.

> [!TIP]
> `readmd doctor` tells you which graphics protocol and colour depth your terminal supports
> and which diagram tools were found.

## Where to next

- [Advanced usage](advanced.md) — palettes, folding, slides
- [Configuration](../reference/config.md) — themes and keys
