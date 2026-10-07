# readmd — terminal Markdown viewer

A **terminal-first, feature-rich Markdown viewer** with an optional browser mode. It renders Markdown right in your terminal — including **mermaid** and **D2** diagrams (as real images via Sixel), syntax-highlighted code, tables, GitHub-style alerts, math, and an Azure DevOps `[[_TOC_]]` table of contents — and live-reloads as you edit. When you want full fidelity, the same document opens in your browser with one keypress.

```bash
dnx readmd report.md                 # view in the terminal (TUI)
dnx readmd report.md --browser       # view in the browser (full fidelity)
```

> Requires the **.NET 10 SDK** (for `dnx`). `d2` on your `PATH` is needed for D2 diagrams, and a local `mmdc` (mermaid-cli) for mermaid. **High-quality PDF export** additionally needs **Node.js** and a one-time headless-browser download (`readmd install-pdf`, or accept the prompt in the browser); everything else works with no extra setup.

## Features

- **Runs in the terminal** as a full TUI (alternate screen, scrolling, overlays).
- **One-shot launch** via `dnx readmd <file>` (or `npx`-style global install).
- **GitHub-Flavored Markdown**: headings, **bold/italic/strikethrough**, inline code, links, tables, lists, blockquotes, hard breaks, emoji shortcodes.
- **GitHub alerts** (`[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]`, `[!CAUTION]`) with colored icons and titles.
- **Task lists** (`- [x]` / `- [ ]`) rendered as ☑ / ☐, **footnotes** with linked markers, and **definition lists**.
- **Tables** honor column alignment (left/center/right) and **wrap** wide cells instead of truncating; long URLs wrap rather than clipping.
- **YAML front matter** is recognized and used for document metadata: the `title` sets the document/window title, and `title`/`author`/`date`/`tags` render as a compact header (the raw block never leaks into the body).
- **Live reload** on file change. The browser preserves scroll position (DOM-morphing instead of full reload); the terminal repaints in place and caches diagrams so they don't flicker.
- **Search & navigation**: `/` to search, `n`/`N` to jump, `t` for a table-of-contents overlay, `?` for keybindings.
- **Azure DevOps `[[_TOC_]]`** marker support, plus a sticky TOC sidebar in the browser.
- **Diagrams** from fenced code blocks: **mermaid**, **D2**, **Graphviz** (`graphviz`/`dot`), and **PlantUML** (`plantuml`/`puml`) — rendered to images inline in the terminal (via Sixel) and shown natively in the browser. Render failures are reported inline with an actionable message.
- **Math** via KaTeX (browser) / a Unicode approximation (terminal), **code** via TextMate (terminal) / highlight.js (browser).
- **Multi-file wiki navigation**: follow local `.md` links with back/forward history (sandboxed to the document's directory). Opening an external link asks for confirmation first.
- **Browser mode** (`--browser`) for pixel-perfect rendering, served locally (loopback only) from an embedded web server. The browser view adds: **export** the current document to a self-contained `.html`, a **high-quality WYSIWYG `.pdf`** (matches the on-screen theme — dark stays dark — on a single page auto-sized to the content, nothing cropped), or **Print to PDF** (the browser's own print dialog); **copy buttons** on code blocks and **permalink anchors** on headings; a **diagram lightbox** (click any mermaid/D2 diagram to zoom/pan, copy or open its SVG); **project-wide search** across the wiki and a **`Ctrl+P` quick-open** file palette; and **persisted** theme/zen/sidebar preferences.
- **Export & piping**: `--export` writes a self-contained `.html` or `.pdf`; with a redirected stdout (or stdin via `-`) it renders to text, so `readmd file.md | less -R` and `cat file.md | readmd -` just work.

## Usage

```
readmd <file> [options]
readmd -            # read Markdown from standard input

Options:
  -b, --browser        Open in the browser instead of the terminal.
  -p, --port <port>    Port for browser mode (0 = pick a free port; 0–65535).
      --no-open        In browser mode, start the server but don't launch a browser.
      --best-effort    Terminal mode: skip the headless-browser download; mermaid
                       diagrams open in the browser instead of rendering inline.
  -e, --export <path>  Export to a self-contained file and exit; format from the
        (-o)           extension: .html (single file, assets inlined) or .pdf.
      --print          Render to stdout and exit (plain text, or ANSI on a TTY).
                       Implied when stdout is a pipe/file, or when reading stdin.
      --color <mode>   auto (default; honors NO_COLOR / FORCE_COLOR), always
                       (e.g. `readmd f.md --color always | less -R`), or never.
      --root <dir>     Directory local links/images may reach (default: the
                       document's folder), so ../sibling.md links work in a wiki.
      --theme <theme>  Color theme: dark, light, or auto (auto honors COLORFGBG,
                       else defaults to dark).
      --background <b>  'solid' paints a solid themed background (overrides terminal
       (--bg)          transparency, like OpenCode); 'terminal' shows the terminal
                       background through. Toggle live with ].
      --d2-path <p>    Explicit path to the d2 executable.
  -v, --version        Print the version and exit.
  -h, --help           Show help.

Subcommands:
  readmd install-pdf         Provision the headless browser for high-quality PDF export.
  readmd completions <shell> Print a completion script (bash, zsh, fish, pwsh).
  readmd man                 Print a man page (troff).
```

### Export & piping

readmd is pipeline-friendly. When stdout is redirected (or you read from stdin),
it renders to text instead of starting the interactive UI:

```bash
readmd report.md --color always | less -R   # paged, ANSI-colored
readmd report.md > report.txt           # plain text (styles stripped)
cat report.md | readmd -                # render from stdin
readmd report.md --export report.html   # one self-contained HTML file (assets inlined)
readmd report.md --export report.pdf    # WYSIWYG PDF (see below)
readmd install-pdf                      # one-time: provision the browser used for PDF export
```

Plain (non-TTY) output skips syntax highlighting entirely, so piping a large document is fast.
Set `READMD_TIMING=1` to print per-phase timings (config, parse, layout, highlighting) to stderr.

Exported HTML is fully standalone: CSS, JavaScript and KaTeX fonts are inlined,
local images become data URIs, D2 diagrams are pre-rendered to inline SVG, and
mermaid/KaTeX run client-side from the embedded libraries — so the file opens
anywhere with no server.

**PDF export** renders that same HTML through a headless Chromium to produce a
**WYSIWYG** PDF: it keeps the document's on-screen theme (a dark document stays
dark with its colorful text and dark-themed diagrams) and emits a single
continuous page sized to fit the content, so wide tables and long code aren't
cropped or reflowed to A4. It needs a **Node.js** runtime on your machine plus a
one-time Chromium download — run `readmd install-pdf` once (or, in the browser,
click **Export → PDF (high quality)** and accept the prompt). If Node.js isn't
available, use **Print to PDF** in the browser instead (the OS print dialog).

### Terminal keybindings

Vim-style motions are supported throughout, plus the mouse wheel scrolls.

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| `j` / `k` (or arrows) | Scroll one line | mouse wheel | Scroll |
| `Ctrl` + mouse wheel | Zoom diagrams in / out | `/` | Search |
| `Ctrl+e` / `Ctrl+y` | Scroll one line (vim) | `n` / `N` | Next / previous match |
| `Ctrl+d` / `Ctrl+u` | Half page | `←` / `→` (or `Backspace`) | Back / forward (history) |
| `Ctrl+f` / `Ctrl+b` | Full page | `t` | Table-of-contents overlay |
| `Space` / `b` | Page down / up | `Enter`, `1`–`9` | Follow a link (first visible / link #n by its superscript) |
| `gg` / `G` | Top / bottom | click / `Ctrl`+click | Follow a link (Ctrl skips the confirm) |
| `Home` / `End` | Top / bottom | `[` | Toggle light / dark theme |
| `o` | Open in browser | `]` | Toggle solid / transparent background |
| `m` | Mark mode (select + copy) | `q` (or `Ctrl+C`) | Quit |
| `?` | Keybindings overlay | | |

On Windows, mouse-wheel scrolling captures the mouse, which disables the terminal's native
click-drag text selection. Press `m` to enter **mark mode** (the status bar shows `[SELECT]`):
**drag** with the left button to select text, then **right-click** to copy the selection to the
clipboard. `Esc` clears the selection; press `m` again to leave mark mode and restore wheel
scrolling. On macOS/Linux the mouse is always reported (SGR), so drag-select and right-click-copy
work directly. Copy uses the Win32 clipboard on Windows and OSC 52 elsewhere (Windows Terminal,
WezTerm, kitty, iTerm2, …).

Theme and background can also be set at launch with `--theme` and `--background`.

### Browser keybindings

The browser view uses the same vim-style motions, plus toggles to hide chrome:

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| `j` / `k` (or arrows) | Scroll | `/` or `Ctrl+F` | Search (this page + other files) |
| `Ctrl+d` / `Ctrl+u` | Half page | `n` / `N` | Next / previous match |
| `Ctrl+f` / `Ctrl+b` | Full page | `Ctrl+P` | Quick-open a file |
| `Space` / `PageUp` | Page down / up | `t` / `s` | Toggle sidebar / toolbar |
| `gg` / `G` | Top / bottom | `z` | Zen mode (hide all chrome) |
| `Alt+←` / `Alt+→` | Back / forward | `[` / `e` | Toggle theme / export |

## Configuration

readmd reads an optional JSON config so you don't have to repeat flags, and can
define custom color themes and remap keys. It looks for a user-level file —
`~/.config/readmd/config.json` (honoring `XDG_CONFIG_HOME`) on macOS/Linux, or
`%APPDATA%\readmd\config.json` on Windows — and a project-level `.readmd.json`
found by walking up from the document's directory. Project settings override
user settings; command-line flags override both. A missing or malformed file is
ignored.

```jsonc
{
  // Defaults used when the matching flag isn't passed:
  "theme": "nord",            // "dark" | "light" | "auto" | a custom theme name below
  "background": "solid",      // "solid" | "terminal"
  "d2Path": "/usr/local/bin/d2",
  "graphics": "auto",         // "sixel" | "half-block" | "none" | "auto" (inline image mode)

  // Custom terminal color themes (any omitted color falls back to the dark/light base):
  "themes": {
    "nord": {
      "dark": true,
      "text": "#d8dee9", "background": "#2e3440", "backgroundElevated": "#3b4252",
      "h1": "#88c0d0", "h2": "#81a1c1", "h3": "#8fbcbb",
      "link": "#5e81ac", "accent": "#b48ead", "rule": "#434c5e"
    }
  },

  // Remap the common single-key actions (others keep their defaults):
  "keys": {
    "quit": "x",
    "search": "s",
    "pagedown": "space"
  }
}
```

Remappable actions: `quit`, `scrollDown`, `scrollUp`, `pageDown`, `pageUp`,
`goBottom`, `search`, `searchNext`, `searchPrev`, `toc`, `openInBrowser`,
`selectionMode`, `rerender`, `toggleTheme`, `toggleBackground`, `help`. (Arrows,
mouse, `Ctrl`-combinations, the `gg` prefix, and `1`–`9` link-following are fixed.)

### Shell completions & man page

`readmd` can print completion scripts for bash, zsh, fish, and PowerShell, plus a
man page:

```bash
# bash / zsh (load now, or add the line to your rc file)
source <(readmd completions bash)
source <(readmd completions zsh)

# fish
readmd completions fish > ~/.config/fish/completions/readmd.fish

# PowerShell (add to $PROFILE)
readmd completions pwsh | Out-String | Invoke-Expression

# man page
readmd man > ~/.local/share/man/man1/readmd.1
```

## How it works

`readmd` is a single .NET 10 tool composed of five projects:

| Project | Responsibility |
| --- | --- |
| **Readmd.Core** | Markdig pipeline, `[[_TOC_]]` extension, TOC/diagram extraction, file watching, link resolution, diagram cache contracts. |
| **Readmd.Diagrams** | Renders mermaid (local `mmdc`, else headless Chromium via Playwright with mermaid bundled), D2, Graphviz (`dot`), and PlantUML — each external tool → SVG → PNG via SkiaSharp — cached by content hash. Also drives **PDF export** (Playwright + a system Node.js, Chromium provisioned on demand). |
| **Readmd.Web** | Kestrel server: HTML rendering, server-rendered D2 SVG, SSE live-reload with `idiomorph`, multi-file SPA navigation, embedded assets. |
| **Readmd.Terminal** | Hand-rolled TUI: ANSI/truecolor renderer, Markdown→styled-line layout, TextMate highlighting, Sixel image output, search/TOC/links/history. |
| **Readmd.Cli** | `System.CommandLine` entry point; packaged as the `readmd` .NET tool. |

The Markdig pipeline and diagram cache are shared by both front-ends, so the terminal and browser views stay consistent and diagrams are only rendered once per content hash.

**PDF export** takes the self-contained export HTML, loads it in a headless Chromium with *screen* styles (so the theme is preserved), measures the fully-rendered content, and prints it as one page sized to fit — no A4 pagination and nothing cropped. To keep the packaged tool small, only Playwright's ~12 MB JavaScript driver ships with it; Chromium and a Node.js runtime are located/downloaded on demand (`readmd install-pdf`).

## Terminal image support

Inline diagrams and images in the terminal use the **Sixel** graphics protocol where available (Windows Terminal ≥ 1.22, WezTerm, xterm, mintty, foot, …). On terminals without Sixel, readmd automatically falls back to **Unicode half-block (▀) rendering**, which works on any truecolor terminal (kitty, iTerm2, the Linux console, tmux, …) — diagrams and images still render inline, just at lower resolution. Force a mode with `READMD_GRAPHICS=sixel|half-block|none` (or `"graphics"` in config); `none` shows only the diagram caption. You can also run with `--best-effort` and press `o` to view diagrams in the browser.

## Platform support

`readmd` targets .NET 10 and is cross-platform (Windows, macOS, Linux). Mouse support — **wheel scrolling, click-to-follow-link, and drag-select in `m` mark mode** — works on all three: Windows uses the console API, while macOS/Linux use SGR mouse reporting (with the terminal put in raw mode via `termios`). Every keybinding works on all platforms, and the browser front-end is identical everywhere.

> **Mermaid rendering:** if a local `mmdc` (mermaid-cli, `npm i -g @mermaid-js/mermaid-cli`) is on your `PATH` — or set via `mermaidCliPath` in config — readmd uses it and skips its own browser download entirely. Otherwise the first mermaid diagram triggers a one-time headless-browser (Chromium) download via Playwright, which can be large; use `--best-effort` to skip it (mermaid then opens in your browser on demand). D2 requires the `d2` binary on your `PATH` (or `--d2-path`); if it's missing, the diagram area shows an actionable message. **Graphviz** blocks require `dot` on your `PATH`, and **PlantUML** blocks require `plantuml` (and Java); both can also be pointed at via `graphvizPath`/`plantUmlPath` in config.

## Building from source

```bash
git clone https://github.com/MoaidHathot/readmd
cd readmd
dotnet build Readmd.slnx
dotnet test tests/Readmd.Tests/Readmd.Tests.csproj                   # run the test suite
dotnet run --project src/Readmd.Cli -- samples/demo.md            # terminal
dotnet run --project src/Readmd.Cli -- samples/demo.md --browser  # browser

# Pack & install the tool locally
dotnet pack src/Readmd.Cli -c Release -o artifacts/nupkg
dotnet tool install --global --add-source artifacts/nupkg readmd
```

`samples/demo.md` exercises every feature (TOC, tables, task lists, alerts, code, mermaid, D2, math, multi-file links). CI (GitHub Actions) builds and tests on Windows, macOS, and Linux, and packs the tool.

`tools/SixelView` is a dev-only diagnostic that decodes the terminal's Sixel/ANSI output back to PNG for visual verification; it is not part of the shipped tool.

## License

MIT
