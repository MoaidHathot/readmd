using System.Text;
using Readmd.Core;
using SkiaSharp;

namespace Readmd.Terminal;

public sealed partial class TerminalViewer
{
    private int ViewportHeight => _screen.Height - 1; // last row is the status bar

    // ---------------- main loop ----------------
    private void RenderLoop()
    {
        // Guaranteed escape hatch: Ctrl+C always quits cleanly (the screen is restored in
        // RunAsync's finally). We keep ENABLE_PROCESSED_INPUT on so this still fires.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;     // don't hard-kill; let the loop exit and restore the screen
            _running = false;
        };
        Console.CancelKeyPress += onCancel;

        // Note: we deliberately do NOT synchronously query the terminal for its pixel size here.
        // That reply (CSI 4;h;w t) ends in 't' and, if not perfectly drained, could leak in as a
        // keystroke (opening the TOC). The input reader swallows such report sequences instead, and
        // the per-cell size falls back to a sensible estimate (refined on resize).

        // Read keys on a dedicated background thread via the platform key reader (ReadConsoleInput
        // on Windows, Console.ReadKey elsewhere) and hand them to the loop through a queue. This is
        // the reliable path on Windows Terminal and can't go deaf to 'q'.
        var keyQueue = new System.Collections.Concurrent.ConcurrentQueue<KeyEvent>();
        Thread? reader = null;
        if (!Console.IsInputRedirected)
        {
            reader = new Thread(() =>
            {
                foreach (var ev in KeyReader.Read(() => _running))
                    keyQueue.Enqueue(ev);
            })
            {
                IsBackground = true,
                Name = "readmd-input",
            };
            reader.Start();
        }

        // Ask the terminal for its pixel size (CSI 14t). The reply is captured + swallowed by the
        // input reader (so it never leaks as a keystroke); we read it back a moment later.
        if (!Console.IsInputRedirected)
            _screen.WriteEscape("\e[14t").Flush();

        try
        {
            RefreshCellSize();
            int lastW = _screen.Width, lastH = _screen.Height;
            bool pixelApplied = _windowPixelWidth > 0;
            var pixelDeadline = DateTime.UtcNow.AddSeconds(2);
            while (_running)
            {
                if (_screen.Width != lastW || _screen.Height != lastH)
                {
                    lastW = _screen.Width; lastH = _screen.Height;
                    RefreshCellSize();
                    ReflowForResize();
                }

                // The CSI 14t reply arrives shortly after startup; apply it once it's available.
                if (!pixelApplied && DateTime.UtcNow < pixelDeadline)
                {
                    if (KeyReader.WindowPixelSize is not null) { RefreshCellSize(); pixelApplied = _windowPixelWidth > 0; }
                }

                if (_dirty || _forceRepaintNextFrame)
                {
                    _forceRepaintNextFrame = false;
                    Draw();
                    _dirty = false;
                }

                bool handledAny = false;
                while (keyQueue.TryDequeue(out var key))
                {
                    HandleKeyEvent(key);
                    handledAny = true;
                    if (!_running) break;
                }

                if (!handledAny)
                {
                    Thread.Sleep(16);
                    if (_statusMessage is not null && DateTime.UtcNow > _statusUntil)
                    {
                        _statusMessage = null;
                        _dirty = true;
                    }
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// Recomputes the per-cell pixel size from the terminal's window pixel size divided by the
    /// current rows/cols. Because the window pixel size is constant across font zoom (Ctrl +/-),
    /// this picks up zoom automatically (the grid changes). When the cell size changes, diagram
    /// bitmaps/reservations are recomputed and the screen is hard-cleared so images don't overlap.
    /// </summary>
    private void RefreshCellSize()
    {
        // Prefer the terminal-reported pixel size (captured by the input reader from a CSI 14t
        // reply); it stays constant across font zoom, so dividing by the live grid tracks zoom.
        if (KeyReader.WindowPixelSize is { } wp)
        {
            _windowPixelWidth = wp.WidthPx;
            _windowPixelHeight = wp.HeightPx;
        }

        int cols = _screen.Width, rows = _screen.Height;
        int cw, ch;
        if (_windowPixelWidth > 0 && _windowPixelHeight > 0 && cols > 0 && rows > 0)
        {
            cw = Math.Max(1, _windowPixelWidth / cols);
            ch = Math.Max(1, _windowPixelHeight / rows);
        }
        else
        {
            cw = 10; ch = 20; // estimate (non-Windows / no reply)
        }

        if (cw == _cellWidthPx && ch == _cellHeightPx) return;

        lock (_stateLock)
        {
            _cellWidthPx = cw;
            _cellHeightPx = ch;
            ClearDiagramCaches();
            _forceHardClear = true;
            // Re-reserve rows for all ready diagrams at the new cell size.
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready)
                    ReserveDiagramRows(key, result);
            _dirty = true;
        }
    }

    private void ReflowForResize()
    {
        lock (_stateLock)
        {
            // Width changed: scaled diagram bitmaps and their crops are no longer valid.
            ClearDiagramCaches();
            _forceHardClear = true;
            // Re-render lines at the new width.
            try
            {
                var markdown = File.ReadAllText(_currentPath);
                var parsed = ParseToLines(_currentPath, markdown);
                _lines = parsed.Lines;
                _links = parsed.Links;
                _pendingDiagrams = parsed.Diagrams;
                _pendingImages = parsed.Images;
            }
            catch { /* keep old layout */ }

            // Re-parsing produced fresh anchors with NO reservation rows; re-reserve for every
            // already-rendered diagram/image so the layout (and images) stay intact after resize.
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready)
                    ReserveDiagramRows(key, result);
            _dirty = true;
        }
        RecomputeSearch();
    }

    // ---------------- drawing ----------------
    private bool _diagramsOnScreenLastFrame;
    private bool _overlayOpenLastFrame;
    private int _lastDrawScroll = -1;
    private volatile bool _forceHardClear;
    // Set only by an explicit refresh ('r') to perform a clean wipe: one diagram-free frame followed
    // by a repaint, guaranteeing stubborn Sixel artifacts are cleared. Not used for theme/bg/zoom
    // toggles (those would blink). See the clean-wipe logic in Draw().
    private volatile bool _refreshWipe;
    // Forces one extra repaint after a clean-wipe frame (which actually draws the diagrams) even
    // though the loop resets _dirty after Draw().
    private bool _forceRepaintNextFrame;

    private void Draw()
    {
        lock (_stateLock)
        {
            _screen.BeginFrame();

            // Focused single-image view takes over the whole screen (its own clear + hint bar).
            if (_focusMode)
            {
                DrawFocusView();
                _screen.Reset();
                _screen.Flush();
                return;
            }

            int height = ViewportHeight;
            int width = _screen.Width;

            // Sixel graphics aren't erased by per-line text clears, so when the view scrolled and a
            // diagram is (or just was) on screen, hard-clear once to wipe leftover image pixels.
            // Also hard-clear when an overlay (help/TOC) opens or closes so diagrams don't bleed
            // through the panel, or when the cell size changed (font zoom).
            bool anyDiagramVisible = AnyDiagramVisible(height);
            bool overlayOpen = _helpMode || _tocMode;
            bool scrolled = _scroll != _lastDrawScroll;
            bool overlayChanged = overlayOpen != _overlayOpenLastFrame;
            // An explicit refresh ('r') performs a "clean wipe": render ONE diagram-free frame (text
            // + hard clear only), flush it, then repaint the images on the next frame over a
            // verified-empty buffer. This reliably clears stubborn Sixel artifacts on Windows
            // Terminal that ED (\e[2J) leaves behind. We DON'T do this on theme/background/zoom
            // toggles — there it would cause a visible one-frame blink of the diagrams/images, and a
            // normal hard clear is enough for those. The user can press 'r' if anything lingers.
            bool cleanWipe = _refreshWipe;
            _refreshWipe = false;
            bool suppressDiagramsThisFrame = false;
            if (_forceHardClear || cleanWipe || ((scrolled || overlayChanged) && (anyDiagramVisible || _diagramsOnScreenLastFrame)))
            {
                _screen.HardClear();
                _forceHardClear = false;
                if (cleanWipe)
                {
                    suppressDiagramsThisFrame = true;
                    _forceRepaintNextFrame = true;   // follow-up frame paints the diagrams over the cleared buffer
                }
            }
            _diagramsOnScreenLastFrame = anyDiagramVisible;
            _overlayOpenLastFrame = overlayOpen;
            _lastDrawScroll = _scroll;

            // Paint the whole screen with the solid background first (overrides terminal
            // transparency, like OpenCode). When solid mode is off, we just clear normally so
            // the terminal's own background shows through.
            if (_solidBackground)
                _screen.FillBackground(_theme.Background, _screen.Height, _screen.Width);
            _screen.MoveTo(0, 0);

            for (int row = 0; row < height; row++)
            {
                _screen.MoveTo(row, 0);
                if (_solidBackground) { _screen.SetBackground(_theme.Background); _screen.ClearLineToEnd(); }
                else _screen.ClearLine();
                int lineIndex = _scroll + row;
                if (lineIndex < _lines.Count)
                    DrawLine(_lines[lineIndex], lineIndex, row, width);
            }

            // Don't draw Sixel diagrams while an overlay panel is open — they'd bleed through it.
            // Also skip on the single clear-only frame after a deep clear (see above).
            if (!_helpMode && !_tocMode && !suppressDiagramsThisFrame)
                DrawDiagrams(height);
            DrawStatusBar();

            if (_tocMode) DrawTocOverlay();
            if (_helpMode) DrawHelpOverlay();

            _screen.Reset();
            _screen.Flush();
        }
    }

    private bool AnyDiagramVisible(int height)
    {
        foreach (var (key, result) in _diagramResults)
        {
            if (result.Status != DiagramStatus.Ready || result.Png is null) continue;
            int anchorIndex = FindDiagramLine(key);
            if (anchorIndex < 0) continue;
            int rowOnScreen = anchorIndex - _scroll;
            int rowsTall = DiagramRows(result);
            if (rowOnScreen < height && rowOnScreen + rowsTall > 0) return true;
        }
        return false;
    }

    private void DrawLine(DisplayLine line, int lineIndex, int row, int width)
    {
        // If the line wants a full-width background (e.g. code blocks), paint it first.
        if (line.LineBackground is { } lineBg)
        {
            _screen.MoveTo(row, 0).SetBackground(lineBg).ClearLineToEnd();
        }

        _screen.MoveTo(row, 0);
        int col = 0;
        bool lineSelected = _selAnchor is not null && LineInSelection(lineIndex);
        foreach (var span in line.Spans)
        {
            if (col >= width) break;
            var text = span.Text;
            // Clip by display width so wide/emoji glyphs (which occupy two terminal cells) don't
            // overrun the viewport, and so `col` stays aligned with the real terminal cursor.
            int spanWidth = TextWidth.Of(text);
            if (col + spanWidth > width) { text = TextWidth.TrimToWidth(text, Math.Max(0, width - col)); spanWidth = TextWidth.Of(text); }

            // Search highlight?
            if (_searchHits.Count > 0 && SpanHasHit(lineIndex))
            {
                DrawSpanWithHighlight(line, lineIndex, span, ref col, width);
                continue;
            }

            // Selection highlight (mark mode): render char-by-char so we can flip the background on
            // the selected cells.
            if (lineSelected)
            {
                DrawSpanWithSelection(lineIndex, span, text, ref col, width);
                continue;
            }

            _screen.Reset();
            var bg = span.Background ?? line.LineBackground ?? (_solidBackground ? _theme.Background : (Rgb?)null);
            if (bg is { } b) _screen.SetBackground(b);
            if (span.Color is { } c) _screen.SetForeground(c);
            else _screen.SetForeground(_theme.Text);
            if (span.Style != CellStyle.None) _screen.SetStyle(span.Style);
            _screen.Write(text);
            col += spanWidth;
        }

        // Pad the rest of a backgrounded line so the block fills the full width.
        if (line.LineBackground is { } fill && col < width)
        {
            _screen.Reset();
            _screen.SetBackground(fill);
            _screen.Write(new string(' ', width - col));
        }
        _screen.Reset();
    }

    private bool SpanHasHit(int lineIndex) => _searchHits.Any(h => h.Line == lineIndex);

    private void DrawSpanWithHighlight(DisplayLine line, int lineIndex, StyledSpan span, ref int col, int width)
    {
        // Render grapheme-by-grapheme so we can flip background on hits within this line, advancing
        // by display width so wide glyphs stay aligned with the terminal cursor and hit columns.
        var hits = _searchHits.Where(h => h.Line == lineIndex).ToList();
        var active = _searchHitIndex >= 0 && _searchHitIndex < _searchHits.Count ? _searchHits[_searchHitIndex] : (Line: -1, Col: -1, Len: 0);
        foreach (var ch in TextWidth.Graphemes(span.Text))
        {
            int gw = TextWidth.ElementWidth(ch);
            if (col + gw > width) break;
            int here = col;
            bool isHit = hits.Any(h => here >= h.Col && here < h.Col + h.Len);
            bool isActive = active.Line == lineIndex && here >= active.Col && here < active.Col + active.Len;
            _screen.Reset();
            if (isHit)
            {
                _screen.SetBackground(isActive ? _theme.SearchActiveBg : _theme.SearchBg);
                _screen.SetForeground(new Rgb(0, 0, 0));
            }
            else
            {
                var bg = span.Background ?? line.LineBackground ?? (_solidBackground ? _theme.Background : (Rgb?)null);
                if (bg is { } b) _screen.SetBackground(b);
                _screen.SetForeground(span.Color ?? _theme.Text);
                if (span.Style != CellStyle.None) _screen.SetStyle(span.Style);
            }
            _screen.Write(ch);
            col += gw;
        }
        _screen.Reset();
    }

    // ---------------- diagrams (Sixel) ----------------
    // Diagrams render at a FIXED size (scaled once to fit). When scrolling, we crop the source
    // bitmap to just the visible row-window and draw that — so the image scrolls smoothly like a
    // browser instead of resizing. Scaled bitmaps are cached per (key, theme, width).
    private readonly Dictionary<string, SKBitmap> _scaledDiagramCache = new();
    private readonly Dictionary<string, string> _sixelCache = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _halfBlockCache = new();

    // Focused-view scaled bitmap: a single entry, rebuilt when the key/size/theme/zoom changes.
    private SKBitmap? _focusScaled;
    private string _focusScaledKey = "";

    private void ClearDiagramCaches()
    {
        foreach (var bmp in _scaledDiagramCache.Values) bmp.Dispose();
        _scaledDiagramCache.Clear();
        _sixelCache.Clear();
        _halfBlockCache.Clear();
        _focusScaled?.Dispose();
        _focusScaled = null;
        _focusScaledKey = "";
    }

    // High-quality resampler for the PNG fallback (Mitchell cubic): far smoother than the default
    // nearest/box sampling when a raster diagram/image has to be scaled up for a zoomed view.
    private static readonly SKSamplingOptions _highQualitySampling = new(SKCubicResampler.Mitchell);

    private SKBitmap GetScaledDiagram(string key, DiagramResult result)
    {
        var cacheKey = $"{key}-{(_theme.IsDark ? "d" : "l")}-{_screen.Width}-{_cellHeightPx}-{_diagramZoom}";
        if (_scaledDiagramCache.TryGetValue(cacheKey, out var cached) && !cached.IsNull) return cached;

        // Size from the source pixel dimensions (same aspect as the SVG, when present).
        int srcW = result.PixelWidth > 0 ? result.PixelWidth : 1;
        int srcH = result.PixelHeight > 0 ? result.PixelHeight : 1;
        var (w, h) = ScaledSize(srcW, srcH, MaxDiagramRows);
        // Snap height UP to a whole number of cell rows so every scroll crop aligns exactly to a
        // row boundary — otherwise the partial last row makes the image jitter while scrolling.
        int rows = Math.Max(1, (int)Math.Ceiling(h / (double)_cellHeightPx));
        int snappedH = rows * _cellHeightPx;

        bool isImage = key.StartsWith("img-") || key.StartsWith("imgrp-");
        var scaled = ComposeScaledBitmap(key, result, w, h, snappedH, isImage);
        _scaledDiagramCache[cacheKey] = scaled;
        return scaled;
    }

    /// <summary>
    /// Renders a diagram/image's content at (<paramref name="w"/>, <paramref name="h"/>) and composes
    /// it — centered, over the theme (or light-card) backdrop — onto a (w × snappedH) canvas. Content
    /// is rasterized fresh from the SVG when available (crisp at any zoom), otherwise the PNG is
    /// resized with high-quality cubic sampling.
    /// </summary>
    private SKBitmap ComposeScaledBitmap(string key, DiagramResult result, int w, int h, int snappedH, bool isImage)
    {
        var content = RenderContentToFit(result, w, h);

        // Dark, transparent images (logos/icons, e.g. a black SVG mark) would vanish when flattened
        // onto the dark theme background. For *images* (not diagrams, which are theme-aware), give
        // such content a light "card" backdrop so it stays visible — like GitHub does in dark mode.
        var backdrop = (_theme.IsDark && isImage && content is not null && NeedsLightCard(key, content))
            ? new Rgb(0xf6, 0xf8, 0xfa)
            : _theme.Background;

        var scaled = new SKBitmap(w, snappedH);
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(ToSkColor(backdrop));
            if (content is not null)
            {
                // Center within the snapped canvas (fit may leave a hair of slack on one axis).
                float left = (w - content.Width) / 2f;
                float top = (snappedH - content.Height) / 2f;
                canvas.DrawBitmap(content, left, top);
            }
        }
        content?.Dispose();
        return scaled;
    }

    /// <summary>
    /// Produces the diagram/image content bitmap at ~(<paramref name="w"/>, <paramref name="h"/>):
    /// crisp SVG rasterization when a vector source exists, else a cubic-resampled PNG. Caller disposes.
    /// </summary>
    private static SKBitmap? RenderContentToFit(DiagramResult result, int w, int h)
    {
        if (result.Svg is { Length: > 0 } svg)
        {
            var svgBmp = Readmd.Diagrams.SvgRasterizer.RenderToFit(svg, w, h);
            if (svgBmp is not null) return svgBmp;   // fall through to PNG on parse failure
        }
        if (result.Png is null) return null;
        using var bmp = SKBitmap.Decode(result.Png);
        if (bmp is null) return null;
        return bmp.Resize(new SKImageInfo(w, h), _highQualitySampling);
    }


    private readonly Dictionary<string, bool> _lightCardDecision = new();

    /// <summary>
    /// Decides whether an image needs a light backdrop in dark mode: true when it has meaningful
    /// transparency AND its visible (opaque) pixels are predominantly dark, so flattening onto the
    /// dark theme background would make it disappear. Sampled and cached per image key.
    /// </summary>
    private bool NeedsLightCard(string key, SKBitmap bmp)
    {
        if (_lightCardDecision.TryGetValue(key, out var cachedDecision)) return cachedDecision;

        long opaque = 0, transparent = 0;
        double lumaSum = 0;
        int stepX = Math.Max(1, bmp.Width / 64);
        int stepY = Math.Max(1, bmp.Height / 64);
        for (int y = 0; y < bmp.Height; y += stepY)
        for (int x = 0; x < bmp.Width; x += stepX)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha < 32) { transparent++; continue; }
            opaque++;
            // Rec. 601 luma, weighted by alpha (semi-transparent edges count less).
            double luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            lumaSum += luma * (c.Alpha / 255.0);
        }

        long total = opaque + transparent;
        bool decision = false;
        if (total > 0 && opaque > 0)
        {
            double transparentFrac = transparent / (double)total;
            double avgLuma = lumaSum / opaque;
            // Significant transparency (it's a logo/icon, not a photo) AND dark visible content.
            decision = transparentFrac > 0.15 && avgLuma < 110;
        }
        _lightCardDecision[key] = decision;
        return decision;
    }


    private static SKColor ToSkColor(Rgb c) => new(c.R, c.G, c.B);

    /// <summary>Maximum on-screen height (in rows) a diagram is allowed to occupy at its fixed size.</summary>
    private int MaxDiagramRows => Math.Max(8, (int)(Math.Min(ViewportHeight - 2, 30) * Math.Pow(1.25, Math.Max(0, _diagramZoom))));

    private void DrawDiagrams(int height)
    {
        if (_graphicsMode == GraphicsMode.None) return; // captions only; nothing to draw

        foreach (var (key, result) in _diagramResults)
        {
            if (result.Status != DiagramStatus.Ready || result.Png is null) continue;

            var scaled = GetScaledDiagram(key, result);
            int imgRows = (int)Math.Ceiling(scaled.Height / (double)_cellHeightPx);

            // The same image/diagram can appear multiple times in a document (e.g. the same logo.png
            // used both standalone and inside a link). Draw at EVERY anchor with this key, not just
            // the first, so duplicate references all render.
            foreach (int anchorIndex in FindAllDiagramLines(key))
            {
                int imageTopRow = (anchorIndex + 1) - _scroll;
                int imageBottomRow = imageTopRow + imgRows;     // exclusive

                if (imageBottomRow <= 0 || imageTopRow >= height) continue;

                int topCropRows = imageTopRow < 0 ? -imageTopRow : 0;          // rows hidden above
                int drawRow = imageTopRow < 0 ? 0 : imageTopRow;              // first on-screen row
                int visibleRows = Math.Min(imageBottomRow, height) - drawRow; // rows visible on screen
                if (visibleRows < 1) continue;

                int srcY = Math.Clamp(topCropRows * _cellHeightPx, 0, scaled.Height);
                int srcH = Math.Clamp(visibleRows * _cellHeightPx, 1, scaled.Height - srcY);
                if (srcH < 1) continue;

                if (_graphicsMode == GraphicsMode.HalfBlock)
                {
                    DrawHalfBlockSlice(key, scaled, srcY, srcH, drawRow, topCropRows);
                    continue;
                }

                var cacheKey = $"{key}-{(_theme.IsDark ? "d" : "l")}-{_screen.Width}-{srcY}-{srcH}";
                if (!_sixelCache.TryGetValue(cacheKey, out var sixel))
                {
                    sixel = BuildCroppedSixel(scaled, srcY, srcH);
                    _sixelCache[cacheKey] = sixel;
                }

                _screen.MoveTo(drawRow, 1);
                _screen.WriteEscape(sixel);
            }
        }
    }

    /// <summary>
    /// Draws a vertical slice of a scaled diagram as half-block text. Each terminal row maps to one
    /// cell height of pixels; the slice is rendered to two-pixels-per-row half-block lines and the
    /// visible rows are placed starting at <paramref name="drawRow"/>.
    /// </summary>
    private void DrawHalfBlockSlice(string key, SKBitmap scaled, int srcY, int srcH, int drawRow, int topCropRows)
    {
        var cacheKey = $"{key}-{(_theme.IsDark ? "d" : "l")}-{_screen.Width}-{_cellHeightPx}-{_diagramZoom}-hb";
        if (!_halfBlockCache.TryGetValue(cacheKey, out var rowLines))
        {
            rowLines = BuildHalfBlockRows(scaled);
            _halfBlockCache[cacheKey] = rowLines;
        }

        int visibleRows = (srcH + _cellHeightPx - 1) / _cellHeightPx;
        for (int r = 0; r < visibleRows; r++)
        {
            int rowIndex = topCropRows + r;
            if (rowIndex < 0 || rowIndex >= rowLines.Count) continue;
            _screen.MoveTo(drawRow + r, 1);
            _screen.WriteEscape(rowLines[rowIndex]);
        }
    }

    /// <summary>
    /// Builds one half-block text line per terminal row for a scaled diagram. The bitmap is
    /// re-sampled to two pixel rows per terminal row so each ▀ cell shows the right vertical detail.
    /// </summary>
    private IReadOnlyList<string> BuildHalfBlockRows(SKBitmap scaled)
    {
        int rows = Math.Max(1, scaled.Height / _cellHeightPx);
        int targetH = rows * 2;                 // two pixel rows per terminal row
        int targetW = Math.Max(1, scaled.Width * targetH / Math.Max(1, scaled.Height));
        targetW = Math.Min(targetW, Math.Max(1, _screen.Width - 1));

        using var resized = scaled.Resize(new SKImageInfo(targetW, targetH), SKSamplingOptions.Default) ?? scaled;
        return HalfBlockEncoder.Encode(resized, _theme.Background);
    }

    /// <summary>
    /// Computes the scaled pixel size of a diagram so it fits the available width and a given number
    /// of terminal rows of height.
    /// </summary>
    private (int Width, int Height) ScaledSize(int pxWidth, int pxHeight, int maxRows)
    {
        double zoom = Math.Pow(1.25, _diagramZoom);   // Ctrl+wheel zoom steps
        int maxWidthPx = Math.Max(_cellWidthPx, (_screen.Width - 2) * _cellWidthPx);
        int maxHeightPx = Math.Max(_cellHeightPx, maxRows * _cellHeightPx);
        // Base fit scale (never upscales past the source at zoom 0), then apply the zoom factor.
        double fit = Math.Min(maxWidthPx / (double)pxWidth, maxHeightPx / (double)pxHeight);
        double scale = Math.Min(1.0, fit) * zoom;
        // Still clamp so an enlarged image can't exceed the usable width.
        scale = Math.Min(scale, maxWidthPx / (double)pxWidth);
        int w = Math.Max(1, (int)(pxWidth * scale));
        int h = Math.Max(1, (int)(pxHeight * scale));
        return (w, h);
    }

    /// <summary>Encodes a vertical slice [srcY, srcY+srcH) of a pre-scaled bitmap as Sixel.</summary>
    private string BuildCroppedSixel(SKBitmap scaled, int srcY, int srcH)
    {
        if (srcY == 0 && srcH == scaled.Height)
            return SixelEncoder.Encode(scaled, _theme.Background);

        using var crop = new SKBitmap(scaled.Width, srcH);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.DrawBitmap(scaled, new SKRect(0, srcY, scaled.Width, srcY + srcH),
                new SKRect(0, 0, scaled.Width, srcH));
        }
        return SixelEncoder.Encode(crop, _theme.Background);
    }

    private int DiagramRows(DiagramResult result)
    {
        if (result.Png is null || result.PixelWidth <= 0 || result.PixelHeight <= 0) return 1;
        var (_, h) = ScaledSize(result.PixelWidth, result.PixelHeight, MaxDiagramRows);
        return Math.Max(1, (int)Math.Ceiling(h / (double)_cellHeightPx));
    }

    private int FindDiagramLine(string key)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].DiagramKey == key) return i;
        return -1;
    }

    /// <summary>Yields every anchor line index for a key (a diagram/image may appear more than once).</summary>
    private IEnumerable<int> FindAllDiagramLines(string key)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].DiagramKey == key) yield return i;
    }

    // ---------------- status bar ----------------
    private void DrawStatusBar()
    {
        int row = _screen.Height - 1;
        _screen.MoveTo(row, 0).ClearLine();
        _screen.SetBackground(_theme.IsDark ? new Rgb(22, 27, 34) : new Rgb(246, 248, 250));
        _screen.SetForeground(_theme.Muted);

        string left;
        if (_searchMode)
        {
            left = $" /{_searchQuery}";
            if (_searchHits.Count > 0) left += $"  [{_searchHitIndex + 1}/{_searchHits.Count}]";
            else if (_searchQuery.Length > 0) left += "  (no matches)";
        }
        else if (_statusMessage is not null)
        {
            left = " " + _statusMessage;
        }
        else
        {
            int pct = _lines.Count <= ViewportHeight ? 100
                : (int)(100.0 * _scroll / Math.Max(1, _lines.Count - ViewportHeight));
            left = $" {Path.GetFileName(_currentPath)}  {pct}%";
            if (_selectionMode) left += "  [SELECT]";
        }

        // Pick a help hint that fits the available width (vim keys shown when there's room).
        int width = _screen.Width;
        string help = width >= 112
            ? "j/k·gg/G·mouse move  Ctrl+f/b·d/u page  /:search n/N  t:toc  [:theme ]:bg  r:refresh  o:browser  ?:help  q:quit "
            : width >= 72
                ? "j/k gg/G  Ctrl+f/b page  /:find  t:toc  [/]:theme/bg  q:quit "
                : "j/k /:find t:toc q:quit ";
        var sb = new StringBuilder();
        sb.Append(left);
        int padding = width - left.Length - help.Length;
        if (padding > 0) sb.Append(new string(' ', padding));
        if (left.Length + help.Length <= width) sb.Append(help);
        var text = sb.ToString();
        if (text.Length > width) text = text[..width];
        _screen.Write(text);
        _screen.Reset();
    }

    // ---------------- focused image view ----------------
    // Renders ONE image/diagram scaled to fill the whole viewport (bypassing the inline row cap),
    // centered, with zoom (_focusZoom) and pan (_focusPanRows/_focusPanCols). Both axes can be
    // cropped, so it uses a general rectangle crop rather than the inline vertical-only slice path.
    private void DrawFocusView()
    {
        _forceHardClear = false;
        _screen.HardClear();
        if (_solidBackground) _screen.FillBackground(_theme.Background, _screen.Height, _screen.Width);

        DiagramResult? baseResult = null;
        if (_focusKey is not null) _diagramResults.TryGetValue(_focusKey, out baseResult);

        if (baseResult is null || baseResult.Status != DiagramStatus.Ready || baseResult.Png is null)
        {
            _screen.MoveTo(0, 0).SetForeground(_theme.Muted).Write("Image not available — press esc to close.");
            DrawFocusHint();
            return;
        }
        if (_graphicsMode == GraphicsMode.None)
        {
            _screen.MoveTo(0, 0).SetForeground(_theme.Muted).Write("Inline graphics are disabled in this terminal.");
            DrawFocusHint();
            return;
        }

        // Use the highest-resolution render we currently have for this diagram; if the display is
        // still upscaling it, kick off a crisper re-render in the background (mermaid only).
        var result = FocusSource(_focusKey!, baseResult);
        var scaled = GetFocusScaled(_focusKey!, result);
        MaybeRequestFocusHiRes(_focusKey!, baseResult, scaled.Width);

        int imgRows = Math.Max(1, scaled.Height / _cellHeightPx);
        int imgCols = Math.Max(1, (int)Math.Ceiling(scaled.Width / (double)_cellWidthPx));

        int viewRows = ViewportHeight;
        int viewCols = _screen.Width;

        // Where the image's top-left sits on screen. Centered when smaller than the view; when larger,
        // the pan value slides the visible window (clamped so the image can't leave the screen).
        int marginRows = (viewRows - imgRows) / 2;
        int marginCols = (viewCols - imgCols) / 2;

        int minTop, maxTop, minLeft, maxLeft;
        if (imgRows <= viewRows) { minTop = maxTop = marginRows; }
        else { minTop = viewRows - imgRows; maxTop = 0; }
        if (imgCols <= viewCols) { minLeft = maxLeft = marginCols; }
        else { minLeft = viewCols - imgCols; maxLeft = 0; }

        // Clamp the pan to the valid range and persist it, so a big mouse drag (or key pan) can't
        // push the pan state past the edge and leave a "dead zone" on the way back.
        _focusPanRows = Math.Clamp(_focusPanRows, marginRows - maxTop, marginRows - minTop);
        _focusPanCols = Math.Clamp(_focusPanCols, marginCols - maxLeft, marginCols - minLeft);

        int screenTopRow = marginRows - _focusPanRows;
        int screenLeftCol = marginCols - _focusPanCols;

        // Visible crop in image-cell space.
        int srcCellRow = Math.Max(0, -screenTopRow);
        int drawRow = Math.Max(0, screenTopRow);
        int visRows = Math.Min(imgRows - srcCellRow, viewRows - drawRow);

        int srcCellCol = Math.Max(0, -screenLeftCol);
        int drawCol = Math.Max(0, screenLeftCol);
        int visCols = Math.Min(imgCols - srcCellCol, viewCols - drawCol);

        if (visRows < 1 || visCols < 1) { DrawFocusHint(); return; }

        int srcX = Math.Clamp(srcCellCol * _cellWidthPx, 0, scaled.Width);
        int srcY = Math.Clamp(srcCellRow * _cellHeightPx, 0, scaled.Height);
        int srcW = Math.Min(visCols * _cellWidthPx, scaled.Width - srcX);
        int srcH = Math.Min(visRows * _cellHeightPx, scaled.Height - srcY);
        if (srcW < 1 || srcH < 1) { DrawFocusHint(); return; }

        using var crop = new SKBitmap(srcW, srcH);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.Clear(ToSkColor(_theme.Background));
            canvas.DrawBitmap(scaled, new SKRect(srcX, srcY, srcX + srcW, srcY + srcH),
                new SKRect(0, 0, srcW, srcH));
        }

        if (_graphicsMode == GraphicsMode.HalfBlock)
        {
            // Two pixel rows per terminal row; width is one pixel per cell.
            using var resized = crop.Resize(new SKImageInfo(Math.Max(1, visCols), Math.Max(1, visRows * 2)),
                _highQualitySampling) ?? crop;
            var lines = HalfBlockEncoder.Encode(resized, _theme.Background);
            for (int i = 0; i < lines.Count && drawRow + i < viewRows; i++)
            {
                _screen.MoveTo(drawRow + i, drawCol);
                _screen.WriteEscape(lines[i]);
            }
        }
        else
        {
            _screen.MoveTo(drawRow, drawCol);
            _screen.WriteEscape(SixelEncoder.Encode(crop, _theme.Background));
        }

        DrawFocusHint();
    }

    /// <summary>Returns the best-available render for the focused key: the hi-res one when ready, else the base.</summary>
    private DiagramResult FocusSource(string key, DiagramResult baseResult)
    {
        if (_focusHiResKey == key && _focusHiResResult is { Status: DiagramStatus.Ready, Png: not null } hi)
            return hi;
        return baseResult;
    }

    /// <summary>
    /// When the focused mermaid diagram would be upscaled at the current zoom (mermaid is PNG-only),
    /// kicks off a crisper re-render at a higher device scale in the background and swaps it in when
    /// ready. Vector diagrams (SVG) and raster images don't need this — the former re-rasterize
    /// locally, the latter can't exceed their native resolution.
    /// </summary>
    private void MaybeRequestFocusHiRes(string key, DiagramResult baseResult, int displayWidthPx)
    {
        if (baseResult.Svg is not null || baseResult.PixelWidth <= 0) return;
        if (!_pendingDiagrams.TryGetValue(key, out var req) || req.Kind != DiagramKind.Mermaid) return;

        int haveWidth = (_focusHiResKey == key && _focusHiResResult is { PixelWidth: > 0 } hi)
            ? hi.PixelWidth : baseResult.PixelWidth;
        if (haveWidth >= displayWidthPx) return;   // already sharp enough for this zoom

        double needed = displayWidthPx / (double)baseResult.PixelWidth;
        double bucket = NextScaleBucket(needed);
        if (bucket <= _focusHiResScale || bucket <= _focusHiResInFlight) return; // have/getting this or better

        _focusHiResInFlight = bucket;
        var theme = _diagramTheme;
        _ = Task.Run(async () =>
        {
            try
            {
                var hires = await _diagrams.RenderAtScaleAsync(req, theme, bucket, _lifetimeCts.Token);
                lock (_stateLock)
                {
                    _focusHiResInFlight = 0;
                    if (hires.Status == DiagramStatus.Ready && hires.Png is not null
                        && _focusMode && _focusKey == key)
                    {
                        _focusHiResKey = key;
                        _focusHiResResult = hires;
                        _focusHiResScale = bucket;
                        _focusScaledKey = "";       // force the scaled cache to rebuild from the hi-res source
                        _forceHardClear = true;
                        _dirty = true;
                    }
                }
            }
            catch { lock (_stateLock) { _focusHiResInFlight = 0; } }
        });
    }

    /// <summary>Rounds a required scale up to the next discrete bucket, so we re-render a bounded number of times.</summary>
    private static double NextScaleBucket(double needed)
    {
        foreach (var b in FocusScaleBuckets)
            if (b >= needed) return b;
        return FocusScaleBuckets[^1];
    }
    private static readonly double[] FocusScaleBuckets = { 1.5, 2, 3, 4, 6, 8 };

    /// <summary>Scales the focused image to fit the viewport (contain) times the zoom factor, cached.</summary>
    private SKBitmap GetFocusScaled(string key, DiagramResult result)
    {
        // Include the source resolution in the key so switching from the base to a hi-res render
        // (same key/theme/zoom) rebuilds instead of returning the stale, softer bitmap.
        string ck = $"{key}-{(_theme.IsDark ? "d" : "l")}-{_screen.Width}-{ViewportHeight}-{_cellWidthPx}-{_cellHeightPx}-{_focusZoom}-{result.PixelWidth}x{result.PixelHeight}";
        if (_focusScaledKey == ck && _focusScaled is { IsNull: false }) return _focusScaled;

        _focusScaled?.Dispose();

        // Size from the source pixel dimensions (same aspect as the SVG, when present) so we don't
        // have to decode the PNG just to measure it.
        int srcW = result.PixelWidth > 0 ? result.PixelWidth : 1;
        int srcH = result.PixelHeight > 0 ? result.PixelHeight : 1;
        double zoom = Math.Pow(1.25, _focusZoom);
        int maxW = Math.Max(_cellWidthPx, (_screen.Width - 1) * _cellWidthPx);
        int maxH = Math.Max(_cellHeightPx, ViewportHeight * _cellHeightPx);
        double fit = Math.Min(maxW / (double)srcW, maxH / (double)srcH);
        double scale = fit * zoom;
        int w = Math.Max(1, (int)(srcW * scale));
        int h = Math.Max(1, (int)(srcH * scale));

        // OOM guard: extreme zoom could otherwise ask for a multi-gigabyte bitmap (and the compose
        // step briefly holds two of them). Cap the total pixel budget and scale the target down to
        // fit — the view crops to the viewport anyway.
        const long maxPixels = 32_000_000; // ~128 MB RGBA
        if ((long)w * h > maxPixels)
        {
            double k = Math.Sqrt(maxPixels / ((double)w * h));
            w = Math.Max(1, (int)(w * k));
            h = Math.Max(1, (int)(h * k));
        }

        int rows = Math.Max(1, (int)Math.Ceiling(h / (double)_cellHeightPx));
        int snappedH = rows * _cellHeightPx;

        bool isImage = key.StartsWith("img-") || key.StartsWith("imgrp-");
        var scaled = ComposeScaledBitmap(key, result, w, h, snappedH, isImage);
        _focusScaled = scaled;
        _focusScaledKey = ck;
        return scaled;
    }

    private void DrawFocusHint()
    {
        int row = _screen.Height - 1;
        _screen.MoveTo(row, 0).ClearLine();
        _screen.SetBackground(_theme.IsDark ? new Rgb(22, 27, 34) : new Rgb(246, 248, 250));
        _screen.SetForeground(_theme.Muted);

        // Surface a transient status message (e.g. "Opened in browser") on the left; otherwise show
        // the focus label + zoom level. The render loop clears expired messages and marks dirty.
        string left;
        if (_statusMessage is not null && DateTime.UtcNow <= _statusUntil)
        {
            left = " " + _statusMessage;
        }
        else
        {
            string kind = _focusKey is not null ? FocusLabel(_focusKey) : "image";
            string zoom = _focusZoom == 0 ? "fit" : $"+{_focusZoom}";
            string sharpening = _focusHiResInFlight > 0 ? "  sharpening…" : "";
            left = $" Focus {kind}  [{zoom}]{sharpening}";
        }
        string help = " +/-·wheel zoom  drag·hjkl pan  0 reset  o app  b browser  esc close ";

        int width = _screen.Width;
        var sb = new StringBuilder();
        sb.Append(left);
        int padding = width - left.Length - help.Length;
        if (padding > 0) sb.Append(new string(' ', padding));
        if (left.Length + help.Length <= width) sb.Append(help);
        var text = sb.ToString();
        if (text.Length > width) text = text[..width];
        _screen.Write(text);
        _screen.Reset();
    }

    private static string FocusLabel(string key) =>
        key.StartsWith("img-") || key.StartsWith("imgrp-") ? "image" : "diagram";

}