// readmd browser front-end: live reload, diagrams, search, TOC, multi-file nav.
// mermaid is loaded as a global (window.mermaid) via a <script> tag in the shell, because the
// bundled build is UMD/IIFE, not an ES module.
const mermaid = window.mermaid;

const content = document.getElementById("readmd-content");
const tocEl = document.getElementById("readmd-toc");
const statusEl = document.getElementById("readmd-status");
const statusText = document.getElementById("readmd-status-text");

let currentTheme = document.documentElement.getAttribute("data-theme") || "dark";

// ---------------- persisted UI preferences ----------------
// Theme and chrome toggles persist across reloads/navigations via localStorage (best-effort:
// private-mode failures are ignored). The server still provides the initial theme, but a saved
// user preference wins after the first visit.
const PREFS_KEY = "readmd:prefs";
function loadPrefs() {
  try { return JSON.parse(localStorage.getItem(PREFS_KEY) || "{}") || {}; } catch { return {}; }
}
function savePref(key, value) {
  try {
    const p = loadPrefs();
    p[key] = value;
    localStorage.setItem(PREFS_KEY, JSON.stringify(p));
  } catch { /* storage unavailable */ }
}

// Mermaid theme variables are injected by the server (window.__READMD_MERMAID__) from the same
// C# source the terminal Playwright render uses, so both front-ends share one palette.
function mermaidConfig(theme) {
  const themes = window.__READMD_MERMAID__ || {};
  const vars = themes[theme === "dark" ? "dark" : "light"] || {};
  return {
    startOnLoad: false,
    theme: "base",
    securityLevel: "loose",
    themeVariables: vars,
    flowchart: { curve: "basis", htmlLabels: true, padding: 12 },
    sequence: { useMaxWidth: true, mirrorActors: false },
  };
}

if (mermaid && mermaid.initialize) {
  mermaid.initialize(mermaidConfig(currentTheme));
}

// ---------------- rendering passes ----------------
async function renderMermaid(root) {
  if (!mermaid || !mermaid.render) return;
  const blocks = root.querySelectorAll("figure.readmd-diagram-mermaid pre.mermaid:not([data-readmd-done])");
  for (const pre of blocks) {
    const src = pre.textContent;
    const id = "m" + Math.random().toString(36).slice(2);
    try {
      const { svg } = await mermaid.render(id, src);
      const fig = pre.closest("figure");
      fig.innerHTML = svg;
      fig.setAttribute("data-readmd-done", "1");
    } catch (e) {
      pre.setAttribute("data-readmd-done", "1");
      pre.outerHTML = `<div class="readmd-diagram-error">Mermaid error: ${escapeHtml(String(e))}</div>`;
    }
  }
}

async function renderD2(root) {
  const slots = root.querySelectorAll(".readmd-d2-slot:not([data-readmd-done])");
  for (const slot of slots) {
    const key = slot.getAttribute("data-readmd-key");
    slot.setAttribute("data-readmd-done", "1");
    try {
      const resp = await fetch(`/_readmd/diagram/${key}?theme=${currentTheme}&format=svg`);
      if (!resp.ok) throw new Error(await resp.text());
      slot.innerHTML = await resp.text();
    } catch (e) {
      slot.innerHTML = `<div class="readmd-diagram-error">D2 error: ${escapeHtml(String(e))}</div>`;
    }
  }
}

function renderMath(root) {
  if (window.renderMathInElement) {
    window.renderMathInElement(root, {
      delimiters: [
        { left: "$$", right: "$$", display: true },
        { left: "$", right: "$", display: false },
        { left: "\\(", right: "\\)", display: false },
        { left: "\\[", right: "\\]", display: true },
      ],
      throwOnError: false,
    });
  }
}

function highlight(root) {
  if (!window.hljs) return;
  root.querySelectorAll("pre code").forEach((block) => {
    if (block.closest(".readmd-diagram")) return;
    window.hljs.highlightElement(block);
  });
}

async function renderAll(root) {
  highlight(root);
  renderMath(root);
  await renderMermaid(root);
  await renderD2(root);
  addCopyButtons(root);
}

// Adds a hover "Copy" button to every code block (skipping diagram containers).
function addCopyButtons(root) {
  root.querySelectorAll("pre:not([data-readmd-copy])").forEach((pre) => {
    if (pre.closest(".readmd-diagram")) return;
    const code = pre.querySelector("code") || pre;
    pre.setAttribute("data-readmd-copy", "1");
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "readmd-copy-btn";
    btn.textContent = "Copy";
    btn.setAttribute("aria-label", "Copy code to clipboard");
    btn.addEventListener("click", async (e) => {
      e.preventDefault();
      try {
        await navigator.clipboard.writeText(code.innerText.replace(/\n$/, ""));
        btn.textContent = "Copied!";
        btn.classList.add("readmd-copied");
      } catch {
        btn.textContent = "Failed";
      }
      setTimeout(() => { btn.textContent = "Copy"; btn.classList.remove("readmd-copied"); }, 1400);
    });
    pre.appendChild(btn);
  });
}

// Appends a "#" permalink anchor to a heading; clicking copies the full URL with the fragment.
function addHeadingAnchor(h) {
  if (h.querySelector(".readmd-anchor")) return;
  const link = document.createElement("a");
  link.className = "readmd-anchor";
  link.href = "#" + h.id;
  link.textContent = "#";
  link.setAttribute("aria-label", "Link to this section");
  link.addEventListener("click", (e) => {
    e.preventDefault();
    h.scrollIntoView({ behavior: "smooth", block: "start" });
    history.replaceState(history.state, "", "#" + h.id);
    const url = location.origin + location.pathname + location.search + "#" + h.id;
    navigator.clipboard?.writeText(url).then(() => flashStatus("Link copied"), () => {});
  });
  h.appendChild(link);
}

// ---------------- table of contents ----------------
function buildToc() {
  const headings = content.querySelectorAll("h1, h2, h3, h4, h5, h6");
  tocEl.innerHTML = "";
  const entries = [];
  headings.forEach((h) => {
    if (!h.id) h.id = slugify(h.textContent);
    addHeadingAnchor(h);
    const level = Number(h.tagName.substring(1));
    const a = document.createElement("a");
    a.href = "#" + h.id;
    a.textContent = h.textContent;
    a.className = "lvl-" + level;
    a.addEventListener("click", (e) => {
      e.preventDefault();
      h.scrollIntoView({ behavior: "smooth", block: "start" });
      history.replaceState(history.state, "", "#" + h.id);
    });
    tocEl.appendChild(a);
    entries.push({ h, a });
  });
  // also fill inline [[_TOC_]] blocks
  content.querySelectorAll("[data-readmd-toc-inline]").forEach((nav) => {
    nav.innerHTML = "";
    entries.forEach(({ h }) => {
      const level = Number(h.tagName.substring(1));
      const a = document.createElement("a");
      a.href = "#" + h.id;
      a.textContent = h.textContent;
      a.className = "lvl-" + level;
      a.addEventListener("click", (e) => { e.preventDefault(); h.scrollIntoView({ behavior: "smooth" }); });
      nav.appendChild(a);
    });
  });
  return entries;
}

let tocEntries = [];
function setupScrollSpy() {
  const observer = new IntersectionObserver((obs) => {
    obs.forEach((entry) => {
      if (entry.isIntersecting) {
        const id = entry.target.id;
        tocEntries.forEach(({ h, a }) => a.classList.toggle("active", h.id === id));
      }
    });
  }, { rootMargin: "0px 0px -80% 0px", threshold: 0 });
  tocEntries.forEach(({ h }) => observer.observe(h));
}

// ---------------- search ----------------
const searchInput = document.getElementById("readmd-search");
const searchCount = document.getElementById("readmd-search-count");
let hits = [];
let hitIndex = -1;

function clearSearch() {
  content.querySelectorAll("mark.readmd-hit").forEach((m) => {
    const parent = m.parentNode;
    parent.replaceChild(document.createTextNode(m.textContent), m);
    parent.normalize();
  });
  hits = [];
  hitIndex = -1;
  searchCount.textContent = "";
}

function runSearch(query) {
  clearSearch();
  if (!query || query.length < 2) return;
  const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      if (!node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
      if (node.parentElement.closest("script,style,mark")) return NodeFilter.FILTER_REJECT;
      return NodeFilter.FILTER_ACCEPT;
    },
  });
  const lower = query.toLowerCase();
  const targets = [];
  let n;
  while ((n = walker.nextNode())) {
    if (n.nodeValue.toLowerCase().includes(lower)) targets.push(n);
  }
  for (const node of targets) {
    const text = node.nodeValue;
    const frag = document.createDocumentFragment();
    let i = 0, idx;
    const l = text.toLowerCase();
    while ((idx = l.indexOf(lower, i)) !== -1) {
      if (idx > i) frag.appendChild(document.createTextNode(text.slice(i, idx)));
      const mark = document.createElement("mark");
      mark.className = "readmd-hit";
      mark.textContent = text.slice(idx, idx + query.length);
      frag.appendChild(mark);
      hits.push(mark);
      i = idx + query.length;
    }
    if (i < text.length) frag.appendChild(document.createTextNode(text.slice(i)));
    node.parentNode.replaceChild(frag, node);
  }
  if (hits.length) { hitIndex = 0; focusHit(); }
  searchCount.textContent = hits.length ? `${hitIndex + 1}/${hits.length}` : "0 results";
}

function focusHit() {
  hits.forEach((m, i) => m.classList.toggle("current", i === hitIndex));
  if (hitIndex >= 0 && hits[hitIndex]) {
    hits[hitIndex].scrollIntoView({ behavior: "smooth", block: "center" });
    searchCount.textContent = `${hitIndex + 1}/${hits.length}`;
  }
}
function nextHit(dir) {
  if (!hits.length) return;
  hitIndex = (hitIndex + dir + hits.length) % hits.length;
  focusHit();
}

let searchTimer;
searchInput.addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => runSearch(searchInput.value), 150);
  scheduleProjectSearch(searchInput.value);
});
searchInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") { e.preventDefault(); nextHit(e.shiftKey ? -1 : 1); }
  if (e.key === "Escape") { searchInput.value = ""; clearSearch(); hideSearchResults(); searchInput.blur(); }
});
document.getElementById("readmd-search-next").addEventListener("click", () => nextHit(1));
document.getElementById("readmd-search-prev").addEventListener("click", () => nextHit(-1));

// ---------------- project-wide search (other files) ----------------
const searchResults = document.getElementById("readmd-search-results");
let projectSearchTimer;
function scheduleProjectSearch(q) {
  clearTimeout(projectSearchTimer);
  if (!q || q.length < 2) { hideSearchResults(); return; }
  projectSearchTimer = setTimeout(() => runProjectSearch(q), 280);
}
async function runProjectSearch(q) {
  try {
    const resp = await fetch(`/_readmd/search?q=${encodeURIComponent(q)}`);
    if (!resp.ok) { hideSearchResults(); return; }
    const hits = await resp.json();
    // Don't show hits for the page we're already viewing (the in-page search covers those).
    const other = hits.filter((h) => normalize(h.path) !== normalize(currentPath));
    renderSearchResults(other, q);
  } catch { hideSearchResults(); }
}
function renderSearchResults(hits, q) {
  searchResults.innerHTML = "";
  if (!hits.length) { hideSearchResults(); return; }
  const header = document.createElement("div");
  header.className = "readmd-sr-header";
  header.textContent = `${hits.length} match${hits.length === 1 ? "" : "es"} in other files`;
  searchResults.appendChild(header);
  for (const h of hits) {
    const row = document.createElement("button");
    row.type = "button";
    row.className = "readmd-sr-item";
    row.setAttribute("role", "option");
    const title = document.createElement("span");
    title.className = "readmd-sr-title";
    title.textContent = `${h.title} · ${h.relative}:${h.line}`;
    const snip = document.createElement("span");
    snip.className = "readmd-sr-snippet";
    snip.innerHTML = highlightSnippet(h.snippet, q);
    row.appendChild(title);
    row.appendChild(snip);
    row.addEventListener("click", () => { hideSearchResults(); navigate(h.path); });
    searchResults.appendChild(row);
  }
  searchResults.classList.remove("readmd-hidden");
}
function highlightSnippet(text, q) {
  const safe = escapeHtml(text);
  const i = safe.toLowerCase().indexOf(escapeHtml(q).toLowerCase());
  if (i < 0) return safe;
  const len = escapeHtml(q).length;
  return safe.slice(0, i) + "<mark>" + safe.slice(i, i + len) + "</mark>" + safe.slice(i + len);
}
function hideSearchResults() { searchResults.classList.add("readmd-hidden"); searchResults.innerHTML = ""; }
document.addEventListener("click", (e) => {
  if (!searchResults.classList.contains("readmd-hidden") &&
      !e.target.closest("#readmd-search-results") && e.target !== searchInput) {
    hideSearchResults();
  }
});

// ---------------- quick-open palette (Ctrl+P) ----------------
const quickOpen = document.getElementById("readmd-quickopen");
const quickInput = document.getElementById("readmd-quickopen-input");
const quickList = document.getElementById("readmd-quickopen-list");
let fileList = null;        // cached [{path, relative, title}]
let quickFiltered = [];
let quickActive = 0;
let quickLastFocus = null;

async function ensureFileList() {
  if (fileList) return fileList;
  try {
    const resp = await fetch("/_readmd/tree");
    fileList = resp.ok ? await resp.json() : [];
  } catch { fileList = []; }
  return fileList;
}
// Subsequence fuzzy match: every char of the query appears in order in the candidate.
function fuzzyMatch(query, text) {
  if (!query) return true;
  const q = query.toLowerCase(), t = text.toLowerCase();
  let i = 0;
  for (let j = 0; j < t.length && i < q.length; j++) if (t[j] === q[i]) i++;
  return i === q.length;
}
async function openQuickOpen() {
  await ensureFileList();
  quickLastFocus = document.activeElement;
  quickOpen.classList.remove("readmd-hidden");
  quickInput.value = "";
  renderQuickList("");
  quickInput.focus();
}
function closeQuickOpen() {
  quickOpen.classList.add("readmd-hidden");
  if (quickLastFocus && quickLastFocus.focus) quickLastFocus.focus();
  quickLastFocus = null;
}
function renderQuickList(query) {
  quickFiltered = (fileList || []).filter((f) => fuzzyMatch(query, f.relative) || fuzzyMatch(query, f.title)).slice(0, 50);
  quickActive = 0;
  quickList.innerHTML = "";
  quickFiltered.forEach((f, i) => {
    const li = document.createElement("li");
    li.className = "readmd-qo-item" + (i === 0 ? " active" : "");
    li.setAttribute("role", "option");
    li.innerHTML = `<span class="readmd-qo-title">${escapeHtml(f.title)}</span><span class="readmd-qo-path">${escapeHtml(f.relative)}</span>`;
    li.addEventListener("click", () => { closeQuickOpen(); navigate(f.path); });
    quickList.appendChild(li);
  });
}
function quickMove(delta) {
  if (!quickFiltered.length) return;
  quickActive = (quickActive + delta + quickFiltered.length) % quickFiltered.length;
  [...quickList.children].forEach((li, i) => li.classList.toggle("active", i === quickActive));
  quickList.children[quickActive]?.scrollIntoView({ block: "nearest" });
}
quickInput?.addEventListener("input", () => renderQuickList(quickInput.value));
quickInput?.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { e.preventDefault(); closeQuickOpen(); }
  else if (e.key === "ArrowDown") { e.preventDefault(); quickMove(1); }
  else if (e.key === "ArrowUp") { e.preventDefault(); quickMove(-1); }
  else if (e.key === "Enter") {
    e.preventDefault();
    const f = quickFiltered[quickActive];
    if (f) { closeQuickOpen(); navigate(f.path); }
  }
});
quickOpen?.addEventListener("click", (e) => { if (e.target === quickOpen) closeQuickOpen(); });

// ---------------- navigation (multi-file wiki) ----------------
async function navigate(path, push = true) {
  showStatus("Loading…");
  try {
    const resp = await fetch(`/_readmd/doc?path=${encodeURIComponent(path)}`);
    if (!resp.ok) throw new Error(await resp.text());
    const data = await resp.json();
    morphContent(data.html, { morph: false });
    document.title = data.title;
    document.getElementById("readmd-doc-title").textContent = data.title;
    if (push) history.pushState({ path }, "", `/?path=${encodeURIComponent(path)}`);
    currentPath = path;
    await afterContentChange(true);
    window.scrollTo({ top: 0 });
    hideStatus();
  } catch (e) {
    showStatus("Failed to load: " + e.message, true);
  }
}

function interceptLinks() {
  content.addEventListener("click", (e) => {
    const a = e.target.closest("a");
    if (!a) return;
    const href = a.getAttribute("href");
    if (!href) return;
    if (href.startsWith("#")) return; // anchor handled by browser/scroll
    const local = a.getAttribute("data-readmd-local");
    if (local) {
      e.preventDefault();
      navigate(local);
    }
    // external links keep default behavior (open normally)
  });
}

window.addEventListener("popstate", (e) => {
  const path = e.state?.path || new URLSearchParams(location.search).get("path");
  if (path) navigate(path, false);
});

// ---------------- live reload via SSE ----------------
let currentPath = new URLSearchParams(location.search).get("path") || "";
function connectLiveReload() {
  const es = new EventSource("/_readmd/events");
  es.addEventListener("reload", async (ev) => {
    try {
      const payload = JSON.parse(ev.data);
      if (payload.path && currentPath && normalize(payload.path) !== normalize(currentPath)) return;
      const resp = await fetch(`/_readmd/doc?path=${encodeURIComponent(currentPath || payload.path)}`);
      if (!resp.ok) return;
      const data = await resp.json();
      morphContent(data.html);
      document.title = data.title;
      document.getElementById("readmd-doc-title").textContent = data.title;
      await afterContentChange(false);
      flashStatus("Reloaded");
    } catch { /* ignore transient */ }
  });
  es.onerror = () => { /* browser auto-reconnects */ };
}

function morphContent(html, { morph = true } = {}) {
  // Remove enhancement nodes we injected (copy buttons, heading permalinks) first so they don't
  // confuse the diff / leak into a replace; they're re-added afterwards by afterContentChange.
  content.querySelectorAll(".readmd-copy-btn, .readmd-anchor").forEach((el) => el.remove());
  content.querySelectorAll("[data-readmd-copy]").forEach((el) => el.removeAttribute("data-readmd-copy"));
  if (!morph) {
    // Navigation to a different file: a clean replace is correct (nothing to preserve) and avoids
    // idiomorph edge cases when diffing already-rendered diagram subtrees against fresh markup.
    content.innerHTML = html;
    return;
  }
  // Live reload of the same file: idiomorph diffs the new HTML into the live DOM, preserving scroll
  // position and untouched nodes (already-rendered diagrams) instead of replacing innerHTML.
  const next = document.createElement("article");
  next.id = "readmd-content";
  next.innerHTML = html;
  window.Idiomorph.morph(content, next, { morphStyle: "innerHTML" });
}

// ---------------- shared post-render ----------------
async function afterContentChange(resetScroll) {
  await renderAll(content);
  tocEntries = buildToc();
  setupScrollSpy();
  if (searchInput.value) runSearch(searchInput.value);
}

// ---------------- theme ----------------
async function setTheme(theme, { persist = true, rerender = true } = {}) {
  currentTheme = theme;
  document.documentElement.setAttribute("data-theme", currentTheme);
  document.getElementById("hljs-theme").href = currentTheme === "dark"
    ? "/_readmd/vendor/github-dark.min.css" : "/_readmd/vendor/github.min.css";
  if (mermaid && mermaid.initialize) {
    mermaid.initialize(mermaidConfig(currentTheme));
  }
  if (persist) savePref("theme", currentTheme);
  if (rerender) {
    // re-render diagrams for the new theme
    content.querySelectorAll("figure.readmd-diagram-mermaid[data-readmd-done]").forEach((f) => f.removeAttribute("data-readmd-done"));
    content.querySelectorAll(".readmd-d2-slot[data-readmd-done]").forEach((s) => { s.removeAttribute("data-readmd-done"); s.innerHTML = '<div class="readmd-diagram-placeholder">Rendering D2 diagram…</div>'; });
    await renderAll(content);
  }
}
document.getElementById("readmd-theme-toggle").addEventListener("click", () =>
  setTheme(currentTheme === "dark" ? "light" : "dark"));

document.getElementById("readmd-back").addEventListener("click", () => history.back());
document.getElementById("readmd-forward").addEventListener("click", () => history.forward());

// ---------------- export (HTML / PDF) ----------------
const exportBtn = document.getElementById("readmd-export");
const exportMenu = document.getElementById("readmd-export-menu");
function toggleExportMenu(force) {
  const show = force ?? exportMenu.classList.contains("readmd-hidden");
  exportMenu.classList.toggle("readmd-hidden", !show);
  exportBtn.setAttribute("aria-expanded", String(show));
  if (show) exportMenu.querySelector("button")?.focus();
}
function exportUrl(format) {
  const params = new URLSearchParams({ format });
  if (currentPath) params.set("path", currentPath);
  return `/_readmd/export?${params.toString()}`;
}
// Downloads a Blob as a file without navigating away.
function saveBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename || "document";
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 4000);
}
function filenameFromDisposition(res, fallback) {
  const cd = res.headers.get("Content-Disposition") || "";
  const m = /filename="?([^";]+)"?/i.exec(cd);
  return m ? m[1] : fallback;
}
function baseName() {
  const p = (currentPath || "document").replace(/\\/g, "/");
  const name = p.slice(p.lastIndexOf("/") + 1).replace(/\.[^.]+$/, "");
  return name || "document";
}
// Fetches an export (html/pdf) and triggers a download, surfacing any server error in the status bar.
async function fetchAndSave(format) {
  const isPdf = format === "pdf";
  showStatus(isPdf ? "Preparing PDF…" : "Exporting…");
  try {
    const res = await fetch(exportUrl(format));
    if (res.ok) {
      const blob = await res.blob();
      saveBlob(blob, filenameFromDisposition(res, `${baseName()}.${isPdf ? "pdf" : "html"}`));
      hideStatus();
      return { ok: true };
    }
    // Non-OK: try to extract a structured/plain error to show the user.
    const ct = res.headers.get("Content-Type") || "";
    if (ct.includes("application/json")) {
      const data = await res.json().catch(() => null);
      if (data && data.error === "pdf_not_provisioned") return { ok: false, provision: data };
      showStatus(`Export failed: ${(data && data.message) || res.statusText}`, true);
    } else {
      showStatus(`${(await res.text()) || "Export failed"}`, true);
    }
    return { ok: false };
  } catch (e) {
    showStatus("Export failed: " + e.message, true);
    return { ok: false };
  }
}
// Uses the browser's own print dialog to produce a PDF from the current on-screen rendering.
// Zero install and always available; the export CSS already has @media print rules.
function printToPdf() {
  toggleExportMenu(false);
  flashStatus("Opening print dialog… choose “Save as PDF”.");
  setTimeout(() => window.print(), 150);
}
async function exportPdf() {
  // Try the high-quality server render; if the tooling isn't provisioned, prompt to install it or
  // offer the instant Print fallback.
  const first = await fetchAndSave("pdf");
  if (first.ok || !first.provision) return;

  const p = first.provision;
  if (p.reason === "NodeMissing") {
    showStatus("High-quality PDF needs Node.js installed. Use “Print to PDF”, or install Node.js from nodejs.org.", true);
    return;
  }
  const ok = window.confirm(
    "High-quality PDF export needs a one-time download of a headless browser (~150 MB).\n\n" +
    "Download it now? (Or cancel and use “Print to PDF” instead.)");
  if (!ok) return;

  showStatus("Downloading the PDF browser (one-time, ~150 MB)… this may take a minute.");
  try {
    const res = await fetch("/_readmd/install-pdf", { method: "POST" });
    const data = await res.json().catch(() => null);
    if (res.ok && data && data.ready) {
      await fetchAndSave("pdf");
    } else {
      showStatus("Couldn’t set up PDF export: " + ((data && data.message) || res.statusText) +
        " You can still use “Print to PDF”.", true);
    }
  } catch (e) {
    showStatus("Couldn’t set up PDF export: " + e.message + " You can still use “Print to PDF”.", true);
  }
}
function doExport(format) {
  toggleExportMenu(false);
  if (format === "print") { printToPdf(); return; }
  if (format === "pdf") { exportPdf(); return; }
  fetchAndSave("html");
}
exportBtn?.addEventListener("click", (e) => { e.stopPropagation(); toggleExportMenu(); });
exportMenu?.addEventListener("click", (e) => {
  const item = e.target.closest("button[data-format]");
  if (item) doExport(item.getAttribute("data-format"));
});
document.addEventListener("click", (e) => {
  if (!exportMenu.classList.contains("readmd-hidden") && !e.target.closest("#readmd-export-wrap")) toggleExportMenu(false);
});
exportMenu?.addEventListener("keydown", (e) => { if (e.key === "Escape") { e.preventDefault(); toggleExportMenu(false); exportBtn.focus(); } });

// ---------------- diagram lightbox (zoom / pan) ----------------
const lightbox = document.getElementById("readmd-lightbox");
const lbCanvas = document.getElementById("readmd-lightbox-canvas");
const lbStage = document.getElementById("readmd-lightbox-stage");
let lbState = { scale: 1, x: 0, y: 0, dragging: false, sx: 0, sy: 0, svg: "" };
let lbLastFocus = null;

function lbApply() {
  lbCanvas.style.transform = `translate(${lbState.x}px, ${lbState.y}px) scale(${lbState.scale})`;
}
function lbZoom(factor, cx, cy) {
  const prev = lbState.scale;
  // High ceiling so a wide/long diagram can be enlarged far past "fit"; vector stays crisp.
  const next = Math.min(80, Math.max(0.02, prev * factor));
  if (cx != null) {
    // Keep the point under the cursor stationary while zooming (center-relative, matching the
    // canvas's center transform-origin and the flex-centered stage).
    const rect = lbStage.getBoundingClientRect();
    const ox = cx - rect.left - rect.width / 2;
    const oy = cy - rect.top - rect.height / 2;
    lbState.x = ox - (ox - lbState.x) * (next / prev);
    lbState.y = oy - (oy - lbState.y) * (next / prev);
  }
  lbState.scale = next;
  lbApply();
}
function lbReset() {
  // Reset to the fit-to-window baseline (not 1:1), which is the useful "see the whole thing" view.
  lbState.scale = lbState.base || 1;
  lbState.x = 0;
  lbState.y = 0;
  lbApply();
}
// Serialize an <svg> to well-formed XML. mermaid's htmlLabels emit <foreignObject> HTML (e.g. an
// unclosed <br>); outerHTML preserves that HTML flavor, which is invalid SVG/XML. XMLSerializer
// self-closes void elements so Copy produces valid SVG and Open can parse it.
function serializeSvg(svgEl) {
  try { return new XMLSerializer().serializeToString(svgEl); }
  catch { return svgEl.outerHTML; }
}
// Reads an SVG's natural size from its viewBox (falling back to width/height attrs or the rendered
// box), used to size the lightbox reliably instead of relying on CSS auto-sizing (which collapses
// very wide/short diagrams to a sliver or a 300x150 default).
function svgNaturalSize(svg) {
  const vb = svg.viewBox && svg.viewBox.baseVal;
  const w = (vb && vb.width) || parseFloat(svg.getAttribute("width")) || svg.getBoundingClientRect().width || 800;
  const h = (vb && vb.height) || parseFloat(svg.getAttribute("height")) || svg.getBoundingClientRect().height || 600;
  return { w, h };
}
function openLightbox(svgEl) {
  const svgText = serializeSvg(svgEl);
  lbCanvas.innerHTML = svgText;
  const inner = lbCanvas.querySelector("svg");
  let natW = 800, natH = 600;
  if (inner) {
    const s = svgNaturalSize(inner);
    natW = s.w; natH = s.h;
    // Pin the SVG to its natural pixel size and let the canvas transform do ALL sizing/zooming. This
    // is robust for extreme aspect ratios where CSS max-width/height auto-sizing renders nothing.
    inner.removeAttribute("style");
    inner.setAttribute("width", natW);
    inner.setAttribute("height", natH);
    inner.style.maxWidth = "none";
    inner.style.maxHeight = "none";
    inner.style.display = "block";
  }
  // Fit the natural size within the stage (contain).
  const rect = lbStage.getBoundingClientRect();
  let base = Math.min((rect.width * 0.94) / natW, (rect.height * 0.9) / natH);
  if (!isFinite(base) || base <= 0) base = 1;
  lbState = { scale: base, x: 0, y: 0, dragging: false, sx: 0, sy: 0, svg: svgText, base, natW, natH };
  lbApply();
  lbLastFocus = document.activeElement;
  lightbox.classList.remove("readmd-hidden");
  lightbox.querySelector('[data-act="close"]')?.focus();
}
function closeLightbox() {
  lightbox.classList.add("readmd-hidden");
  lbCanvas.innerHTML = "";
  if (lbLastFocus && lbLastFocus.focus) lbLastFocus.focus();
  lbLastFocus = null;
}
// Open when a rendered diagram is clicked.
content.addEventListener("click", (e) => {
  const fig = e.target.closest("figure.readmd-diagram, .readmd-d2-slot");
  if (!fig) return;
  const svg = fig.querySelector("svg");
  if (svg) { e.preventDefault(); openLightbox(svg); }
});
// Inline Ctrl+wheel zoom: enlarge a diagram in place on the page (matches the terminal's Ctrl+wheel).
// Plain wheel keeps scrolling the page; only Ctrl+wheel zooms, and we preventDefault so the browser's
// own page zoom doesn't fire. Growing the SVG's width reflows the layout (no overlap with following
// content); the figure scrolls if the enlarged diagram exceeds the column. Wheel back down to reset.
const inlineZoom = new WeakMap(); // figure -> scale
function applyInlineZoom(fig, svg, scale) {
  inlineZoom.set(fig, scale);
  if (scale <= 1.001) {
    svg.style.width = "";
    svg.style.maxWidth = "";
    fig.classList.remove("readmd-diagram-zoomed");
  } else {
    svg.style.maxWidth = "none";
    svg.style.width = Math.round(scale * 100) + "%";
    fig.classList.add("readmd-diagram-zoomed");
  }
}
content.addEventListener("wheel", (e) => {
  if (!e.ctrlKey) return; // plain wheel scrolls the page
  const fig = e.target.closest("figure.readmd-diagram, .readmd-d2-slot");
  if (!fig) return;
  const svg = fig.querySelector("svg");
  if (!svg) return;
  e.preventDefault();
  const prev = inlineZoom.get(fig) || 1;
  const next = Math.min(8, Math.max(1, prev * (e.deltaY < 0 ? 1.15 : 1 / 1.15)));
  applyInlineZoom(fig, svg, next);
}, { passive: false });
lbStage?.addEventListener("wheel", (e) => { e.preventDefault(); lbZoom(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX, e.clientY); }, { passive: false });
lbStage?.addEventListener("dblclick", (e) => lbZoom(1.5, e.clientX, e.clientY));
lbStage?.addEventListener("pointerdown", (e) => { lbState.dragging = true; lbState.sx = e.clientX - lbState.x; lbState.sy = e.clientY - lbState.y; lbStage.setPointerCapture(e.pointerId); lbStage.style.cursor = "grabbing"; });
lbStage?.addEventListener("pointermove", (e) => { if (!lbState.dragging) return; lbState.x = e.clientX - lbState.sx; lbState.y = e.clientY - lbState.sy; lbApply(); });
lbStage?.addEventListener("pointerup", (e) => { lbState.dragging = false; lbStage.style.cursor = "grab"; try { lbStage.releasePointerCapture(e.pointerId); } catch {} });
document.getElementById("readmd-lightbox-toolbar")?.addEventListener("click", async (e) => {
  const act = e.target.closest("button")?.getAttribute("data-act");
  if (act === "in") lbZoom(1.25);
  else if (act === "out") lbZoom(1 / 1.25);
  else if (act === "reset") lbReset();
  else if (act === "close") closeLightbox();
  else if (act === "copy") { try { await navigator.clipboard.writeText(lbState.svg); flashStatus("SVG copied"); } catch { flashStatus("Copy failed", true); } }
  else if (act === "open") {
    // Open in a new tab as a self-contained pan/zoom viewer so the diagram can be enlarged far
    // beyond the browser's own zoom limit (~500%) and stays crisp (it's vector). Wrapping in HTML
    // (not image/svg+xml) also avoids the strict-XML parse error on mermaid's <foreignObject> labels.
    const url = URL.createObjectURL(new Blob([buildSvgViewerHtml(lbState.svg)], { type: "text/html" }));
    window.open(url, "_blank", "noopener");
    setTimeout(() => URL.revokeObjectURL(url), 15000);
  }
});
lightbox?.addEventListener("click", (e) => { if (e.target === lightbox) closeLightbox(); });

// Builds a self-contained HTML page that shows an SVG string in a pan/zoom viewer (wheel = zoom to
// cursor, drag = pan, buttons/keys, no browser-zoom ceiling). Used by the lightbox "Open" action.
function buildSvgViewerHtml(svgText) {
  return `<!doctype html><html><head><meta charset="utf-8"><title>Diagram</title>
<style>html,body{margin:0;height:100%;overflow:hidden;background:#fff}
#s{position:fixed;inset:0;overflow:hidden;cursor:grab;touch-action:none;display:flex;align-items:center;justify-content:center}
#c{transform-origin:center center;will-change:transform}#c svg{display:block;background:#fff}
#b{position:fixed;top:10px;left:50%;transform:translateX(-50%);display:flex;gap:6px;z-index:5}
#b button{border:1px solid #d0d7de;background:#f6f8fa;border-radius:6px;min-width:36px;padding:6px 10px;cursor:pointer;font:14px system-ui}</style></head>
<body><div id="b"><button data-a="out">&#8722;</button><button data-a="fit">Fit</button><button data-a="in">+</button></div>
<div id="s"><div id="c">${svgText}</div></div>
<script>
var s=document.getElementById('s'),c=document.getElementById('c'),svg=c.querySelector('svg');
var natW=800,natH=600;
if(svg){var vb=svg.viewBox&&svg.viewBox.baseVal;natW=(vb&&vb.width)||parseFloat(svg.getAttribute('width'))||800;natH=(vb&&vb.height)||parseFloat(svg.getAttribute('height'))||600;svg.removeAttribute('style');svg.setAttribute('width',natW);svg.setAttribute('height',natH);svg.style.maxWidth='none';svg.style.maxHeight='none';}
var st={scale:1,x:0,y:0};
function ap(){c.style.transform='translate('+st.x+'px,'+st.y+'px) scale('+st.scale+')';}
function fit(){var b=Math.min((s.clientWidth*0.94)/natW,(s.clientHeight*0.9)/natH);if(!isFinite(b)||b<=0)b=1;st.scale=b;st.x=0;st.y=0;ap();}
function zoom(f,cx,cy){var p=st.scale,n=Math.min(80,Math.max(0.02,p*f));var r=s.getBoundingClientRect();var ox=(cx!=null?cx:r.left+r.width/2)-r.left-r.width/2;var oy=(cy!=null?cy:r.top+r.height/2)-r.top-r.height/2;st.x=ox-(ox-st.x)*(n/p);st.y=oy-(oy-st.y)*(n/p);st.scale=n;ap();}
s.addEventListener('wheel',function(e){e.preventDefault();zoom(e.deltaY<0?1.15:1/1.15,e.clientX,e.clientY);},{passive:false});
s.addEventListener('dblclick',function(e){zoom(1.6,e.clientX,e.clientY);});
var dg=false,dx=0,dy=0;
s.addEventListener('pointerdown',function(e){dg=true;dx=e.clientX-st.x;dy=e.clientY-st.y;s.setPointerCapture(e.pointerId);s.style.cursor='grabbing';});
s.addEventListener('pointermove',function(e){if(!dg)return;st.x=e.clientX-dx;st.y=e.clientY-dy;ap();});
s.addEventListener('pointerup',function(e){dg=false;s.style.cursor='grab';});
document.getElementById('b').addEventListener('click',function(e){var a=e.target.getAttribute('data-a');if(a==='in')zoom(1.25);else if(a==='out')zoom(1/1.25);else if(a==='fit')fit();});
window.addEventListener('keydown',function(e){if(e.key==='+'||e.key==='=')zoom(1.25);else if(e.key==='-'||e.key==='_')zoom(1/1.25);else if(e.key==='0')fit();});
window.addEventListener('resize',fit);fit();
</script></body></html>`;
}

// ---------------- view toggles (sidebar / toolbar / zen) ----------------
const layout = document.getElementById("readmd-layout");
const toolbar = document.getElementById("readmd-toolbar");
function toggleSidebar() {
  const hidden = document.body.classList.toggle("readmd-no-sidebar");
  savePref("noSidebar", hidden);
  flashStatus(hidden ? "Sidebar hidden" : "Sidebar shown");
}
function toggleToolbar() {
  const hidden = document.body.classList.toggle("readmd-no-toolbar");
  savePref("noToolbar", hidden);
  flashStatus(hidden ? "Toolbar hidden" : "Toolbar shown");
}
function toggleZen() {
  const on = !document.body.classList.contains("readmd-zen");
  document.body.classList.toggle("readmd-zen", on);
  document.body.classList.toggle("readmd-no-sidebar", on);
  document.body.classList.toggle("readmd-no-toolbar", on);
  savePref("zen", on);
  flashStatus(on ? "Zen mode (z to exit)" : "Zen mode off");
}

// Hook up toolbar buttons that may exist in the shell.
document.getElementById("readmd-toggle-sidebar")?.addEventListener("click", toggleSidebar);
document.getElementById("readmd-toggle-zen")?.addEventListener("click", toggleZen);

// ---------------- help overlay (which-key style) ----------------
const helpOverlay = document.getElementById("readmd-help-overlay");
const HELP_SECTIONS = [
  { title: "Move", items: [
    ["j / k", "down / up a line"],
    ["Ctrl+e / Ctrl+y", "scroll a line"],
    ["Ctrl+d / Ctrl+u", "half page"],
    ["Ctrl+f / Ctrl+b", "full page"],
    ["Space / PageUp", "page down / up"],
    ["gg / G", "top / bottom"],
  ]},
  { title: "Find & navigate", items: [
    ["/ or Ctrl+F", "search (this page + other files)"],
    ["n / N", "next / prev match"],
    ["Ctrl+P", "quick-open a file"],
    ["Alt+← / Alt+→", "back / forward"],
  ]},
  { title: "View", items: [
    ["t", "toggle sidebar (TOC)"],
    ["s", "toggle toolbar"],
    ["z", "zen mode"],
    ["[", "toggle theme"],
    ["e", "export (HTML / PDF)"],
    ["r", "reload (refresh)"],
    ["?", "this help"],
  ]},
];
function buildHelp() {
  const body = document.getElementById("readmd-help-body");
  if (body.childElementCount) return; // build once
  for (const section of HELP_SECTIONS) {
    const group = document.createElement("div");
    group.className = "readmd-help-group";
    const h = document.createElement("h4");
    h.textContent = section.title;
    group.appendChild(h);
    for (const [keys, desc] of section.items) {
      const row = document.createElement("div");
      row.className = "readmd-help-row";
      const k = document.createElement("span");
      k.innerHTML = keys.split(" / ").map((part) =>
        part.split("+").map((p) => `<kbd>${escapeHtml(p)}</kbd>`).join("+")
      ).join(" / ");
      const d = document.createElement("span");
      d.className = "desc";
      d.textContent = desc;
      row.appendChild(k); row.appendChild(d);
      group.appendChild(row);
    }
    body.appendChild(group);
  }
}
let helpLastFocus = null;
function toggleHelp(force) {
  buildHelp();
  const show = force ?? helpOverlay.classList.contains("readmd-hidden");
  helpOverlay.classList.toggle("readmd-hidden", !show);
  if (show) {
    helpLastFocus = document.activeElement;
    document.getElementById("readmd-help-close")?.focus();
  } else if (helpLastFocus && helpLastFocus.focus) {
    helpLastFocus.focus();
    helpLastFocus = null;
  }
}
// Trap focus inside the help dialog while it's open.
helpOverlay?.addEventListener("keydown", (e) => {
  if (e.key !== "Tab" || helpOverlay.classList.contains("readmd-hidden")) return;
  const focusable = helpOverlay.querySelectorAll("button, [href], input, [tabindex]:not([tabindex='-1'])");
  if (focusable.length === 0) return;
  const first = focusable[0], last = focusable[focusable.length - 1];
  if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
});
helpOverlay?.addEventListener("click", (e) => { if (e.target === helpOverlay) toggleHelp(false); });
document.getElementById("readmd-help-close")?.addEventListener("click", () => toggleHelp(false));

// ---------------- terminal-like keybindings ----------------
let gPending = 0;
function scrollByLines(n) { window.scrollBy({ top: n * 40, behavior: "instant" in window ? "instant" : "auto" }); }
function pageHeight() { return window.innerHeight - 40; }

document.addEventListener("keydown", (e) => {
  const typing = document.activeElement === searchInput || document.activeElement === quickInput;

  // Quick-open palette swallows its own keys (handled on the input); just stop global shortcuts.
  if (!quickOpen.classList.contains("readmd-hidden")) {
    if (e.key === "Escape" && document.activeElement !== quickInput) { e.preventDefault(); closeQuickOpen(); }
    return;
  }

  // Lightbox: Esc closes; +/-/0 zoom while it's open.
  if (!lightbox.classList.contains("readmd-hidden")) {
    if (e.key === "Escape") { e.preventDefault(); closeLightbox(); return; }
    if (e.key === "+" || e.key === "=") { e.preventDefault(); lbZoom(1.25); return; }
    if (e.key === "-" || e.key === "_") { e.preventDefault(); lbZoom(1 / 1.25); return; }
    if (e.key === "0") { e.preventDefault(); lbReset(); return; }
    return; // swallow other keys while the lightbox is open
  }

  // Close help on Esc (works even while typing).
  if (e.key === "Escape" && !helpOverlay.classList.contains("readmd-hidden")) { e.preventDefault(); toggleHelp(false); return; }

  // Ctrl+F always focuses search (browser-native find is replaced by ours).
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") { e.preventDefault(); searchInput.focus(); searchInput.select(); return; }
  // Ctrl+P opens the quick-open file palette.
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "p") { e.preventDefault(); openQuickOpen(); return; }
  if (e.altKey && e.key === "ArrowLeft") { e.preventDefault(); history.back(); return; }
  if (e.altKey && e.key === "ArrowRight") { e.preventDefault(); history.forward(); return; }

  if (typing) return; // don't hijack keys while typing in the search box

  // Ctrl combos (vim-style paging).
  if (e.ctrlKey && !e.altKey && !e.metaKey) {
    const k = e.key.toLowerCase();
    if (k === "d") { e.preventDefault(); window.scrollBy({ top: pageHeight() / 2 }); return; }
    if (k === "u") { e.preventDefault(); window.scrollBy({ top: -pageHeight() / 2 }); return; }
    if (k === "f") { e.preventDefault(); window.scrollBy({ top: pageHeight() }); return; }
    if (k === "b") { e.preventDefault(); window.scrollBy({ top: -pageHeight() }); return; }
    if (k === "e") { e.preventDefault(); scrollByLines(1); return; }
    if (k === "y") { e.preventDefault(); scrollByLines(-1); return; }
    return;
  }
  if (e.metaKey || e.altKey) return;

  switch (e.key) {
    case "j": case "ArrowDown": e.preventDefault(); scrollByLines(1); break;
    case "k": case "ArrowUp": e.preventDefault(); scrollByLines(-1); break;
    case " ": case "PageDown": e.preventDefault(); window.scrollBy({ top: pageHeight() }); break;
    case "PageUp": e.preventDefault(); window.scrollBy({ top: -pageHeight() }); break;
    case "G": e.preventDefault(); window.scrollTo({ top: document.body.scrollHeight }); break;
    case "g":
      if (gPending && Date.now() - gPending < 700) { e.preventDefault(); window.scrollTo({ top: 0 }); gPending = 0; }
      else gPending = Date.now();
      break;
    case "Home": e.preventDefault(); window.scrollTo({ top: 0 }); break;
    case "End": e.preventDefault(); window.scrollTo({ top: document.body.scrollHeight }); break;
    case "/": e.preventDefault(); searchInput.focus(); searchInput.select(); break;
    case "n": nextHit(1); break;
    case "N": nextHit(-1); break;
    case "t": e.preventDefault(); toggleSidebar(); break;
    case "s": e.preventDefault(); toggleToolbar(); break;
    case "z": e.preventDefault(); toggleZen(); break;
    case "[": e.preventDefault(); document.getElementById("readmd-theme-toggle").click(); break;
    case "e": e.preventDefault(); toggleExportMenu(true); break;
    case "r": e.preventDefault(); location.reload(); break;
    case "?": e.preventDefault(); toggleHelp(); break;
  }
  if (e.key !== "g") gPending = 0;
});

// ---------------- helpers ----------------
function slugify(t) {
  return t.toLowerCase().trim().replace(/[^\w\s-]/g, "").replace(/\s+/g, "-");
}
function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function normalize(p) { return (p || "").replace(/\\/g, "/").toLowerCase(); }
let statusHideTimer;
function showStatus(msg, isError) {
  statusText.textContent = msg;
  statusEl.classList.remove("readmd-hidden");
  statusEl.style.borderColor = isError ? "#da3633" : "var(--border)";
}
function hideStatus() { statusEl.classList.add("readmd-hidden"); }
function flashStatus(msg) {
  showStatus(msg, false);
  clearTimeout(statusHideTimer);
  statusHideTimer = setTimeout(hideStatus, 1200);
}

// ---------------- boot ----------------
function applySavedPrefs() {
  const p = loadPrefs();
  // Theme: the <head> inline script already set data-theme to avoid a flash; sync the rest here.
  const docTheme = document.documentElement.getAttribute("data-theme") || "dark";
  setTheme(docTheme, { persist: false, rerender: false });
  // Chrome toggles.
  if (p.zen) {
    document.body.classList.add("readmd-zen", "readmd-no-sidebar", "readmd-no-toolbar");
  } else {
    if (p.noSidebar) document.body.classList.add("readmd-no-sidebar");
    if (p.noToolbar) document.body.classList.add("readmd-no-toolbar");
  }
}
(async function init() {
  if (!currentPath) {
    currentPath = document.body.getAttribute("data-readmd-initial-path") || "";
  }
  applySavedPrefs();
  interceptLinks();
  await afterContentChange(false);
  connectLiveReload();
})();
