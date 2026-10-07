---
title: readmd Demo
author: readmd
date: 2026-01-01
---

# readmd Demo

[[_TOC_]]

Welcome to **readmd**, a terminal-first Markdown viewer with an optional browser mode.[^intro]

[^intro]: This sentence has a footnote; its definition appears in the Footnotes section at the bottom.

## Features

- Live reload on file change (scroll position preserved)
- `[[_TOC_]]` table of contents (Azure DevOps style)
- Search and navigation
- Mermaid and D2 diagrams
- Math via KaTeX
- Multi-file wiki navigation

> [!NOTE]
> This document exercises every major feature. Edit it while `readmd` is running to see live reload.

## Text formatting

This paragraph mixes **bold**, *italic*, ***bold italic***, `inline code`, ~~strikethrough~~, and a
[regular link](https://opencode.ai). Emoji shortcodes work too: :rocket: :sparkles: :tada:.

Extended emphasis: water is H~2~O (subscript), Einstein wrote E = mc^2^ (superscript),
this is ++inserted++ text, and this is ==highlighted== text. Abbreviations like HTML and CSS
keep their term and are underlined.

*[HTML]: HyperText Markup Language
*[CSS]: Cascading Style Sheets

Colour values in inline code get a swatch: the brand colour is `#ff8800`, links use `#58a6ff`,
and `rgb(80, 200, 120)` / `hsl(280, 60%, 60%)` work as well.

Two trailing spaces force a hard line break —
this text starts on a brand-new line.

Ordered lists can start at any number:

5. Fifth step
6. Sixth step
7. Seventh step

## GitHub alerts

All five alert kinds are recognized and styled with their own icon, title, and color:

> [!NOTE]
> Useful information that users should know, even when skimming.

> [!TIP]
> Helpful advice for doing things better or more easily.

> [!IMPORTANT]
> Key information users need to know to achieve their goal.

> [!WARNING]
> Urgent info that needs immediate user attention to avoid problems.

> [!CAUTION]
> Advises about risks or negative outcomes of certain actions.

### Code with highlighting

Inline code like `dotnet build`, `Get-ChildItem`, and `npm run dev` renders distinctly. Below are
full snippets across several languages so you can see the syntax highlighting and the `bat`-style
gutter with line numbers.

#### Fence options

A fence's info string can carry options: a `title="…"` header, highlighted lines (`{1,3-5}` or
`hl_lines="2"`), and `nonums` / `showLineNumbers` to override the line-number gutter per block.

```csharp title="Program.cs" {2,5-6}
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Clock>();          // highlighted
var app = builder.Build();

app.MapGet("/", () => "Hello");                   // highlighted
app.MapGet("/time", (Clock c) => c.Now);          // highlighted
app.Run();
```

```text nonums
A plain block with the gutter switched off: handy for ASCII art and command output.
```

#### C#

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Readmd.Sample;

public sealed record Person(string Name, int Age)
{
    public bool IsAdult => Age >= 18;
}

public static class Program
{
    public static async Task Main()
    {
        var people = new[]
        {
            new Person("Ada", 36),
            new Person("Linus", 17),
            new Person("Grace", 85),
        };

        var adults = people
            .Where(p => p.IsAdult)
            .OrderByDescending(p => p.Age)
            .Select(p => $"{p.Name} ({p.Age})");

        foreach (var line in adults)
            Console.WriteLine(line);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
    }
}
```

#### PowerShell

```powershell
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [int]$MinSizeKB = 100
)

# Find large files and group them by extension
Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Length -gt ($MinSizeKB * 1KB) } |
    Group-Object Extension |
    Sort-Object { ($_.Group | Measure-Object Length -Sum).Sum } -Descending |
    ForEach-Object {
        $totalMB = [math]::Round((($_.Group | Measure-Object Length -Sum).Sum) / 1MB, 2)
        [pscustomobject]@{
            Extension = if ($_.Name) { $_.Name } else { '(none)' }
            Count     = $_.Count
            TotalMB   = $totalMB
        }
    } | Format-Table -AutoSize
```

#### JavaScript

```javascript
// Debounce an async function and cache the latest result
function debounceAsync(fn, delayMs = 200) {
  let timer;
  let pending;
  return (...args) => {
    clearTimeout(timer);
    return new Promise((resolve, reject) => {
      timer = setTimeout(async () => {
        try {
          pending = await fn(...args);
          resolve(pending);
        } catch (err) {
          reject(err);
        }
      }, delayMs);
    });
  };
}

const search = debounceAsync(async (query) => {
  const res = await fetch(`/api/search?q=${encodeURIComponent(query)}`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
});

search("markdown").then((hits) => console.log(`${hits.length} results`));
```

#### TypeScript

```typescript
type Result<T, E = Error> =
  | { ok: true; value: T }
  | { ok: false; error: E };

async function tryFetch<T>(url: string): Promise<Result<T>> {
  try {
    const res = await fetch(url);
    if (!res.ok) return { ok: false, error: new Error(`HTTP ${res.status}`) };
    return { ok: true, value: (await res.json()) as T };
  } catch (error) {
    return { ok: false, error: error as Error };
  }
}

interface User { id: number; name: string; roles: readonly string[] }

const result = await tryFetch<User>("/api/user/1");
if (result.ok) {
  console.log(result.value.name.toUpperCase());
} else {
  console.error(result.error.message);
}
```

#### Python

```python
from dataclasses import dataclass
from functools import lru_cache


@dataclass(frozen=True)
class Point:
    x: float
    y: float

    def distance_to(self, other: "Point") -> float:
        return ((self.x - other.x) ** 2 + (self.y - other.y) ** 2) ** 0.5


@lru_cache(maxsize=None)
def fib(n: int) -> int:
    return n if n < 2 else fib(n - 1) + fib(n - 2)


if __name__ == "__main__":
    origin = Point(0, 0)
    print(f"distance: {origin.distance_to(Point(3, 4)):.1f}")
    print([fib(i) for i in range(10)])
```

#### Rust

```rust
use std::collections::HashMap;

fn word_count(text: &str) -> HashMap<&str, usize> {
    let mut counts = HashMap::new();
    for word in text.split_whitespace() {
        *counts.entry(word).or_insert(0) += 1;
    }
    counts
}

fn main() {
    let text = "the quick brown fox the lazy dog the end";
    let mut pairs: Vec<_> = word_count(text).into_iter().collect();
    pairs.sort_by(|a, b| b.1.cmp(&a.1));
    for (word, count) in pairs.iter().take(3) {
        println!("{word}: {count}");
    }
}
```

#### Go

```go
package main

import (
	"fmt"
	"sort"
)

func main() {
	scores := map[string]int{"ada": 92, "linus": 88, "grace": 99}

	names := make([]string, 0, len(scores))
	for name := range scores {
		names = append(names, name)
	}
	sort.Slice(names, func(i, j int) bool {
		return scores[names[i]] > scores[names[j]]
	})

	for _, name := range names {
		fmt.Printf("%-6s %d\n", name, scores[name])
	}
}
```

#### SQL

```sql
WITH ranked AS (
    SELECT
        customer_id,
        order_total,
        ROW_NUMBER() OVER (
            PARTITION BY customer_id
            ORDER BY order_total DESC
        ) AS rn
    FROM orders
    WHERE order_date >= '2026-01-01'
)
SELECT customer_id, order_total
FROM ranked
WHERE rn = 1
ORDER BY order_total DESC
LIMIT 10;
```

#### Bash

```bash
#!/usr/bin/env bash
set -euo pipefail

# Back up files modified in the last day into a timestamped archive
backup_dir="${1:-$HOME/backups}"
stamp="$(date +%Y%m%d-%H%M%S)"
archive="${backup_dir}/recent-${stamp}.tar.gz"

mkdir -p "$backup_dir"
find . -type f -mtime -1 -print0 \
  | tar --null -czf "$archive" --files-from=-

echo "Wrote $(du -h "$archive" | cut -f1) to $archive"
```

#### JSON

```json
{
  "name": "readmd",
  "version": "0.1.0",
  "features": ["mermaid", "d2", "math", "live-reload"],
  "engines": { "dotnet": ">=10.0" },
  "keybindings": {
    "scroll": ["j", "k"],
    "search": "/",
    "quit": "q"
  }
}
```

#### YAML

```yaml
name: ci
on:
  push:
    branches: [main]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - run: dotnet build --configuration Release
```

#### Diff

```diff
 public static int Add(int a, int b)
 {
-    return a + b;
+    checked { return a + b; }
 }
```

### Tables

Column alignment (`:--`, `:-:`, `--:`) is honored, and **wide cells wrap** instead of being truncated.

| Feature   | Terminal | Browser |
| --------- | :------: | ------: |
| Mermaid   |    ✓     |       ✓ |
| D2        |    ✓     |       ✓ |
| Live edit |    ✓     |       ✓ |

| Option        | What it does                                                                                          |
| ------------- | ----------------------------------------------------------------------------------------------------- |
| `--browser`   | Opens the document in your browser for full-fidelity rendering of mermaid, D2, and math via KaTeX.    |
| `--best-effort` | Skips the one-time headless-browser download in the terminal; mermaid diagrams open in the browser on demand instead of rendering inline as Sixel. |
| `--theme`     | Chooses dark, light, or auto. Auto honors `COLORFGBG` when present and otherwise defaults to dark.    |

### Long links wrap

Long, unbreakable URLs wrap to the next line instead of being clipped at the screen edge:
https://example.com/some/really/long/path/that/keeps/going/and/going/until/it/exceeds/the/terminal/width/and/needs/to/wrap.html

### Task list

- [x] Parse markdown
- [x] Render diagrams
- [x] Tables, alerts, footnotes, definition lists
- [ ] Conquer the world

### Nested & loose lists

Unordered bullets change with nesting depth, and loose lists get spacing:

- Top level
  - Second level
    - Third level
- Back to top

1. First (loose)

2. Second, with a blank line between items

3. Third

## Diagrams

### Mermaid

```mermaid
graph TD
  A[Start] --> B{Works?}
  B -- yes --> C[Ship it]
  B -- no --> D[Fix it]
  D --> B
```

Azure DevOps wikis write mermaid as a `::: mermaid` container; that renders the same way:

::: mermaid
pie title Where the time goes
  "Rendering" : 45
  "Diagrams" : 30
  "Coffee" : 25
:::

### D2

```d2
client -> server: request
server -> db: query
db -> server: rows
server -> client: response
```

### Graphviz

```dot
digraph G {
  rankdir=LR;
  Parse -> Render -> Display;
  Render -> Cache [style=dashed];
}
```

### PlantUML

```plantuml
@startuml
actor User
User -> readmd : open file
readmd -> Renderer : parse + render
Renderer --> User : view
@enduml
```

## Math

The quadratic formula is $x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}$.

GitHub-style ```` ```math ```` fences are display math too:

```math
\sum_{k=1}^{n} k = \frac{n(n+1)}{2}
```

$$
\int_0^\infty e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}
$$

Environments (matrices, cases, aligned) lay out across rows and columns:

$$
\begin{pmatrix} a & b \\ c & d \end{pmatrix}
$$

$$
\begin{cases} x + y = 1 \\ 2x - y = 0 \end{cases}
$$

## Images & media

Images can be local or remote. In the terminal they render inline (Sixel) where supported;
in the browser they display normally.

### Local PNG

![readmd logo](./logo.png)

### Local SVG

![readmd logo (svg)](./logo.svg)

### Remote image

![Markdown mark](https://upload.wikimedia.org/wikipedia/commons/4/48/Markdown-mark.svg)

### Image as a link

[![Click me](./logo.png)](https://opencode.ai)

### Badges (inline images)

![MIT](https://img.shields.io/badge/license-MIT-green) ![dotnet](https://img.shields.io/badge/.NET-10-blue) ![status](https://img.shields.io/badge/status-demo-orange)

### Sequence diagram (mermaid)

```mermaid
sequenceDiagram
  autonumber
  participant U as User
  participant M as readmd
  participant B as Browser
  U->>M: open report.md
  M->>M: render (terminal)
  U->>M: press o
  M->>B: serve + open
  B-->>U: full-fidelity view
```

### Class diagram (mermaid)

```mermaid
classDiagram
  class Renderer {
    +Parse(md) Document
    +Render() void
  }
  class Diagram {
    +Kind kind
    +string source
  }
  Renderer --> Diagram : produces
```

### Gantt (mermaid)

```mermaid
gantt
  title Roadmap
  dateFormat YYYY-MM-DD
  section Core
  Parser      :done,    p1, 2026-01-01, 7d
  Diagrams    :active,  p2, 2026-01-08, 10d
  section UI
  Terminal    :         u1, 2026-01-12, 8d
  Browser     :         u2, 2026-01-15, 6d
```

### D2 (grid)

```d2
direction: right
ui: UI {
  terminal
  browser
}
core: Core {
  parser
  diagrams
}
ui.terminal -> core.parser
ui.browser -> core.parser
core.parser -> core.diagrams
```

## Navigation

See the [second page](./page2.md) for multi-file navigation.

## HTML in Markdown

The markup that READMEs rely on is interpreted, not stripped:

<p align="center">
  <img src="./logo.png" alt="readmd logo" width="96">
  <br>
  <b>readmd</b> &mdash; <i>a terminal-first Markdown viewer</i>
</p>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./logo.png">
  <source media="(prefers-color-scheme: light)" srcset="./logo.svg">
  <img alt="Theme-aware logo" src="./logo.svg">
</picture>

Press <kbd>Ctrl</kbd> + <kbd>C</kbd> to copy. Water is H<sub>2</sub>O, E = mc<sup>2</sup>, <mark>highlighted</mark>, <u>underlined</u>, <s>struck</s>, <span style="color:#e5484d">colored</span>, and <a href="https://github.com/MoaidHathot/readmd">an HTML link</a>.

<details>
<summary>Click to expand (collapsible section)</summary>

The body of a `<details>` block is regular Markdown — including **formatting** and lists:

- it is indented under its summary
- so the structure stays visible

</details>

<table>
  <tr><th>Name</th><th align="right">Size</th></tr>
  <tr><td>alpha</td><td align="right">10 KB</td></tr>
  <tr><td>beta <b>(bold)</b></td><td align="right">200 KB</td></tr>
</table>

## Glossary (definition list)

Sixel
:   A terminal graphics protocol used to draw diagrams and images inline.

Front matter
:   A leading YAML metadata block (delimited by `---`) that is recognized and stripped from the rendered output.

## Edge cases

These exercise rendering details that are easy to get subtly wrong.

- A long list item wraps onto several lines, and the bullet appears only once: Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco.
- Entities decode: Tom &amp; Jerry, &copy; 2026, 5 &lt; 6, a&nbsp;non-breaking&nbsp;space.
- An inline line break<br>continues on the next line.
- Emoji and CJK keep columns aligned: 🚀 日本語 한국어 — search for "日本" to see the highlight land exactly.

a. Alpha-numbered lists keep their letters
b. and continue correctly
c. all the way down.

Roman numerals are preserved as well:

i. one
ii. two
iii. three

A YouTube "image" is a media embed, so it renders as a playable link instead of a broken picture:
![readmd intro](https://www.youtube.com/watch?v=dQw4w9WgXcQ)

| Wide table | Column two | Column three | Column four | Column five | Column six |
| ---------- | ---------- | ------------ | ----------- | ----------- | ---------- |
| Cells wrap to fit the terminal width instead of being clipped off the right edge of the screen | two | three | four | five | six |

```go
func tabs() {
	// Tabs are expanded to spaces so wrapping and clipping stay aligned.
	fmt.Println("done")
}
```

## Footnotes

Footnotes let you add references[^demo] and asides[^aside] without breaking the flow of reading.
They render as superscript markers in the text and a numbered list at the bottom of the document.

[^demo]: A numbered footnote definition. Markers in the text link to these entries.
[^aside]: Footnotes can be referenced by any label (here, "aside") and are renumbered in order.

^^ Generated by readmd — a terminal-first Markdown viewer.

