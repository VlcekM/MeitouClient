// Benchmarks page: fetches the launcher's benchmark sessions from the Reports service (GET /bench/data, GET /bench/targets) at
// runtime and draws one metric over the targets, one line per machine and platform, plus a table of the machines. Every text in
// the data is submitted by players, so it only ever reaches the page through textContent or esc().
(function () {
  "use strict";

  // #region Definitions

  /** Quality tiers in their order; legacy kit data uses "baseline". */
  const TIERS = ["low", "medium", "high", "ultra", "extreme", "baseline"];

  /** Scenarios in their order and names; the old kit's two (version 0) have names of their own. Others sort after, by id. */
  const SCENARIOS = { bases: "Bases", "shark-flyby": "Shark flyby" };
  const LEGACY_SCENARIOS = { play: "Play benchmark", bases: "Base switch" };

  /** Groups of a scenario's segments as result.json names them (they differ per scenario); others sort before "all", by id. */
  const GROUPS = {
    camera: "Camera", streaming: "Streaming", simulation: "Simulation", ui: "UI", views: "Views", flight: "Flight", all: "All segments"
  };

  /** What the legacy view shows, under the controls. */
  const LEGACY_NOTE = "Legacy kit: the earlier Python kit at its baseline settings and each machine's own resolution, over commits; " +
    "not comparable with the launcher's versions.";

  /** Metrics: key in result.json, column of the old kit's results.csv, label, unit; higher = a rise is good. */
  const METRICS = [
    { key: "avgFps", legacy: "avg_fps", label: "Average fps", unit: " fps", higher: true },
    { key: "p99Ms", legacy: "p99_ms", label: "p99 frame time (ms)", unit: " ms" },
    { key: "p50Ms", legacy: "p50_ms", label: "p50 frame time (ms)", unit: " ms" },
    { key: "maxMs", legacy: "max_ms", label: "Max frame (ms)", unit: " ms" },
    { key: "over33Pct", legacy: "frames_over_33ms_pct", label: "Frames over 33 ms (%)", unit: "%" },
    { key: "over100", legacy: "frames_over_100ms", label: "Frames over 100 ms", unit: "" },
    { key: "slowWorldP99Ms", legacy: "slow_world_p99_ms", label: "Slow-world tick p99 (ms)", unit: " ms" },
    { key: "slowWorldMaxMs", legacy: "slow_world_max_ms", label: "Slow-world tick max (ms)", unit: " ms" },
    { key: "vramPeakMb", legacy: "vram_peak_mb", label: "Peak video memory (MB)", unit: " MB" },
    { key: "sysmemPeakMb", legacy: "sysmem_peak_mb", label: "Peak system memory (MB)", unit: " MB" }
  ];

  /** Platforms the launcher submits. */
  const PLAT = { native: "Native", proton: "Proton", windows: "Windows", macos: "macOS" };

  const NS = "http://www.w3.org/2000/svg";
  const root = document.getElementById("bench-root");
  const tip = document.getElementById("tip");

  /** Everything drawn comes from these: the loaded data and the current selection. */
  let D = null;
  const state = { version: null, scenario: "", fingerprint: "", tier: "", group: "", metric: "avgFps", hidden: {}, search: "" };

  // #endregion

  // #region Helpers

  function el(tag, attrs, text) {
    const e = document.createElement(tag);
    for (const k in attrs || {}) e.setAttribute(k, attrs[k]);
    if (text != null) e.textContent = text;
    return e;
  }

  function sv(tag, attrs) {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs || {}) e.setAttribute(k, attrs[k]);
    return e;
  }

  function esc(s) {
    return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
  }

  function fmt(v) {
    if (v == null) return "n/a";
    return Math.abs(v) >= 100 ? String(Math.round(v)) : String(Math.round(v * 10) / 10);
  }

  /** A number from JSON or a CSV cell; null for blank or non-numeric. */
  function num(v) {
    if (v == null || v === "") return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }

  /** A map keyed by submitted text: no inherited keys such as "toString" or "__proto__". */
  function dict() { return Object.create(null); }

  function own(o, k) { return Object.prototype.hasOwnProperty.call(o, k); }

  function str(v) { return v == null ? "" : String(v); }

  function niceTicks(v) {
    if (v <= 0) v = 1;
    const raw = v / 4, p = Math.pow(10, Math.floor(Math.log10(raw))), n = raw / p;
    const st = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p, out = [];
    for (let x = 0; x < v + st * 0.999; x += st) out.push(Math.round(x / st * 1e6) / 1e6 * st);
    return out;
  }

  function showTip(html, ev) {
    tip.innerHTML = html;
    tip.hidden = false;
    const w = tip.offsetWidth;
    let x = ev.clientX + 14, y = ev.clientY + 14;
    if (x + w > window.innerWidth - 8) x = ev.clientX - w - 14;
    if (y + tip.offsetHeight > window.innerHeight - 8) y = ev.clientY - tip.offsetHeight - 14;
    tip.style.left = Math.max(4, x) + "px";
    tip.style.top = Math.max(4, y) + "px";
  }

  function hideTip() { tip.hidden = true; }

  function setStatus(text) {
    const s = document.getElementById("status");
    s.hidden = !text;
    s.textContent = text || "";
  }

  // #endregion

  // #region Loading

  /** The service; ?data=<base url> replaces it only while the page itself runs on this computer (local testing). */
  function serviceBase() {
    const local = location.hostname === "localhost" || location.hostname === "127.0.0.1";
    const override = new URLSearchParams(location.search).get("data");
    return (local && override ? override : root.dataset.service).replace(/\/+$/, "");
  }

  function getJson(url) {
    return fetch(url, { cache: "no-cache" }).then((r) => {
      if (!r.ok) throw new Error("HTTP " + r.status);
      return r.json();
    });
  }

  /** Targets are optional: without them the targets are ordered by when they were first benchmarked. */
  function load() {
    const base = serviceBase();
    Promise.allSettled([getJson(base + "/bench/data"), getJson(base + "/bench/targets")]).then((res) => {
      if (res[0].status !== "fulfilled") {
        setStatus("The benchmark service can't be reached right now (" + res[0].reason.message + "). Please try again later.");
        return;
      }
      const targets = res[1].status === "fulfilled" && res[1].value ? res[1].value.targets : [];
      init(res[0].value && res[0].value.sessions, Array.isArray(targets) ? targets : []);
    }).catch((e) => setStatus("The benchmark results couldn't be read (" + e.message + ")."));
  }

  // #endregion

  // #region Data

  /** Normalises the service's sessions into one entry per (session, scenario): runs[0] = run 1, runs[1] = run 2, metrics by key. */
  function normalise(sessions, targets) {
    const entries = [];
    sessions.forEach((s) => {
      if (!s || typeof s !== "object") return;
      try {
        if (s.legacy || s.version === 0) {
          const scen = legacyScenarios(s);
          Object.keys(scen).forEach((id) => entries.push(entry(s, id, "all", scen[id])));
        } else {
          const groups = resultGroups(s), id = scenarioOf(s);
          Object.keys(groups).forEach((g) => entries.push(entry(s, id, g, groups[g])));
        }
      } catch (e) {
        console.warn("Benchmark session " + s.id + " skipped:", e);
      }
    });
    return { entries: entries, targets: orderTargets(entries, targets), lines: lineList(entries) };
  }

  function entry(s, scenario, group, scen) {
    const m = s.machine && typeof s.machine === "object" ? s.machine : {}, platform = str(s.platform);
    return {
      id: s.id, at: str(s.at), handle: str(s.handle), machine: m, platform: platform, target: str(s.target).replace(/^v/, ""),
      tier: str(s.tier), version: num(s.version) || 0, fingerprint: str(s.fingerprint), scenario: scenario, group: group,
      line: str(m.id || m.name) + "|" + platform, runs: scen.runs, info: scen.info, when: str(scen.when || scen.info.when || s.at)
    };
  }

  function runsOf(s) { return (s.data && Array.isArray(s.data.runs) ? s.data.runs : []).slice(0, 2); }

  /** The session's scenario, else its runs', else "bases" (the only scenario before there were several). */
  function scenarioOf(s) {
    const r = runsOf(s).filter((x) => x && x.scenario)[0];
    return str(s.scenario || (r && r.scenario)) || "bases";
  }

  /** result.json runs: each group's metrics per run, the game build and hardware from the newest run. */
  function resultGroups(s) {
    const out = dict();
    runsOf(s).forEach((r, i) => {
      const scs = r && typeof r.groups === "object" && r.groups ? r.groups : {};
      Object.keys(scs).forEach((g) => {
        if (!scs[g] || typeof scs[g] !== "object" || !Object.keys(scs[g]).length) return;
        const o = out[g] || (out[g] = { runs: [null, null], info: {} });
        o.runs[i] = metricsOf(scs[g], (mt) => mt.key);
        o.info = { game: r.game || {}, hardware: r.hardware || {}, resolution: r.resolution, when: r.startedAt };
      });
    });
    return out;
  }

  /** Old kit rows (results.csv columns): scenario from the row, else bases when segments start with "bases:", else play. */
  function legacyScenarios(s) {
    const rows = s.data && Array.isArray(s.data.rows) ? s.data.rows : [];
    const out = dict();
    rows.forEach((r) => {
      if (!r || typeof r !== "object") return;
      const g = str(r.scenario || s.scenario || (str(r.segments).indexOf("bases:") === 0 ? "bases" : "play"));
      if (!own(LEGACY_SCENARIOS, g)) return;
      const run = num(r.run) === 1 ? 0 : 1;
      const o = out[g] || (out[g] = { runs: [null, null], info: {} });
      o.runs[run] = metricsOf(r, (mt) => mt.legacy);
      o.info = { game: { commit: str(r.commit) }, hardware: { gpu: r.gpu, driver: r.nvidia_driver }, resolution: r.resolution };
      const when = (str(r.date) + "T" + str(r.time)).replace(/T$/, "");
      if (when && (!o.when || when < o.when)) o.when = when;
    });
    return out;
  }

  function metricsOf(src, key) {
    const m = {};
    METRICS.forEach((mt) => { m[mt.key] = num(src[key(mt)]); });
    return m;
  }

  /** Targets oldest first: those of /bench/targets by publish date, any other by its first benchmark (legacy commits). */
  function orderTargets(entries, targets) {
    const known = dict(), first = dict();
    targets.forEach((t) => {
      if (!t || typeof t !== "object") return;
      const v = str(t.version || t.tag).replace(/^v/, "");
      known[v] = { id: v, kind: str(t.kind), time: str(t.publishedAt), label: v };
      known[str(t.tag).replace(/^v/, "")] = known[v];
    });
    entries.forEach((e) => {
      if (!first[e.target] || e.when < first[e.target]) first[e.target] = e.when;
    });
    const out = Object.keys(first).map((id) => known[id]
      ? Object.assign({}, known[id], { id: id })
      : { id: id, kind: "", time: first[id], label: id });
    return out.sort((a, b) => (a.time < b.time ? -1 : a.time > b.time ? 1 : 0));
  }

  /** Every machine+platform line, in order of first appearance, so its colour stays the same whatever is selected. */
  function lineList(entries) {
    const seen = dict(), out = [];
    entries.forEach((e) => {
      if (seen[e.line]) return;
      seen[e.line] = 1;
      out.push({ key: e.line, machine: e.machine, platform: e.platform, handle: e.handle });
    });
    return out;
  }

  function versions() {
    const vs = dict();
    D.entries.forEach((e) => { vs[e.version] = 1; });
    return Object.keys(vs).map(Number).sort((a, b) => b - a);
  }

  /** Fingerprints of the selected version and scenario, newest session's first; legacy data has none. */
  function fingerprints() {
    const out = [];
    D.entries.forEach((e) => {
      if (e.version !== state.version || e.scenario !== state.scenario) return;
      if (e.fingerprint && out.indexOf(e.fingerprint) < 0) out.push(e.fingerprint);
    });
    return out;
  }

  function inScenario() {
    return D.entries.filter((e) => e.version === state.version && e.scenario === state.scenario &&
      (!state.fingerprint || e.fingerprint === state.fingerprint));
  }

  /** The newest session per (machine, platform, target) of the selected version, scenario, fingerprint, tier and group. */
  function selected() {
    const seen = dict();
    return inScenario().filter((e) => {
      if (e.tier !== state.tier || e.group !== state.group) return false;
      const k = e.line + "|" + e.target;
      if (seen[k]) return false;
      seen[k] = 1;
      return true;
    });
  }

  /** Run 2 when it has the value, else run 1 (some old kit sessions have one run). */
  function shownRun(e, key) {
    if (e.runs[1] && e.runs[1][key] != null) return 1;
    return e.runs[0] && e.runs[0][key] != null ? 0 : -1;
  }

  // #endregion

  // #region Labels

  function lineOf(key) { return D.lines.filter((l) => l.key === key)[0]; }

  function lcolor(key) { return "var(--m" + (D.lines.indexOf(lineOf(key)) % 8) + ")"; }

  function ldot(key) { return '<span class="dot" style="background:' + lcolor(key) + '"></span>'; }

  function lname(line) {
    const m = line.machine;
    const name = str(m.name || m.id) || "unnamed";
    return name + " (" + (PLAT[line.platform] || line.platform || "?") + ")";
  }

  function targetOf(id) { return D.targets.filter((t) => t.id === id)[0]; }

  /** Axis label, two lines: v<version> or "daily" (a hash for legacy commits), then the date. */
  function axisLabel(t) {
    const top = t.kind === "release" ? "v" + t.label : t.kind === "daily" ? "daily" : t.label.slice(0, 8);
    return [top, t.time.slice(5, 10)];
  }

  function targetTitle(t) {
    const kind = t.kind === "release" ? "release " : t.kind === "daily" ? "daily build " : state.version === 0 ? "commit " : "";
    return kind + t.label + (t.time ? " (" + t.time.slice(0, 10) + ")" : "");
  }

  function metricDef() { return METRICS.filter((m) => m.key === state.metric)[0] || METRICS[1]; }

  // #endregion

  // #region Controls

  function init(sessions, targets) {
    if (!Array.isArray(sessions) || !sessions.length) {
      setStatus("No benchmark results yet.");
      return;
    }
    D = normalise(sessions, targets);
    if (!D.entries.length) {
      setStatus("No benchmark results yet.");
      return;
    }
    setStatus("");
    selectVersion(versions()[0]);
    window.addEventListener("resize", debounce(renderChart, 150));
  }

  function debounce(fn, ms) {
    let t;
    return () => {
      clearTimeout(t);
      t = setTimeout(fn, ms);
    };
  }

  /** A version change picks a scenario the version has (bases when it has it). */
  function selectVersion(v) {
    state.version = v;
    const ids = scenariosOf();
    selectScenario(ids.indexOf("bases") >= 0 ? "bases" : ids[0] || "");
  }

  /** A scenario change resets the fingerprint, tier and group to ones the scenario has. */
  function selectScenario(id) {
    state.scenario = id;
    state.fingerprint = fingerprints()[0] || "";
    resetTierAndGroup();
    render();
  }

  function scenariosOf() {
    const have = dict(), names = scenarioNames();
    D.entries.forEach((e) => { if (e.version === state.version) have[e.scenario] = 1; });
    return Object.keys(names).filter((id) => have[id]).concat(Object.keys(have).filter((id) => !own(names, id)).sort());
  }

  function scenarioNames() { return state.version === 0 ? LEGACY_SCENARIOS : SCENARIOS; }

  function scenarioName(id) { return own(scenarioNames(), id) ? scenarioNames()[id] : id; }

  function resetTierAndGroup() {
    const tiers = tiersOf(), groups = groupsOf();
    if (tiers.indexOf(state.tier) < 0) state.tier = tiers.indexOf("medium") >= 0 ? "medium" : tiers[0] || "";
    if (groups.indexOf(state.group) < 0) state.group = groups.indexOf("all") >= 0 ? "all" : groups[0] || "";
  }

  function tiersOf() {
    const have = dict();
    inScenario().forEach((e) => { have[e.tier] = 1; });
    return TIERS.filter((t) => have[t]).concat(Object.keys(have).filter((t) => TIERS.indexOf(t) < 0).sort());
  }

  function groupsOf() {
    const have = dict();
    inScenario().forEach((e) => { have[e.group] = 1; });
    const known = Object.keys(GROUPS).filter((g) => have[g] && g !== "all");
    const other = Object.keys(have).filter((g) => !own(GROUPS, g)).sort();
    return known.concat(other, have.all ? ["all"] : []);
  }

  function groupName(g) { return own(GROUPS, g) ? GROUPS[g] : g; }

  function render() {
    renderControls();
    renderCaveat();
    renderChart();
    renderMachines();
  }

  function renderControls() {
    const host = document.getElementById("controls");
    host.innerHTML = "";
    const groups = groupsOf();
    host.appendChild(control("Benchmark", versionSelect()));
    host.appendChild(control("Scenario", segmented(scenariosOf(), state.scenario, scenarioName, selectScenario)));
    if (fingerprints().length > 1) host.appendChild(control("Fingerprint", fingerprintSelect()));
    host.appendChild(control("Tier", segmented(tiersOf(), state.tier, tierName, pickTier)));
    if (groups.length > 1) host.appendChild(control("Segments", segmented(groups, state.group, groupName, pickGroup)));
    host.appendChild(control("Metric", metricSelect()));
  }

  function tierName(t) { return t ? t[0].toUpperCase() + t.slice(1) : t; }

  function pickTier(t) {
    state.tier = t;
    render();
  }

  function pickGroup(g) {
    state.group = g;
    render();
  }

  function control(label, input) {
    const l = el("label", { class: "ctl" }, label);
    l.appendChild(input);
    return l;
  }

  function versionSelect() {
    const opts = versions().map((v) => [String(v), v === 0 ? "Legacy kit" : "Version " + v]);
    return select(opts, String(state.version), (v) => selectVersion(Number(v)));
  }

  function fingerprintSelect() {
    const opts = fingerprints().map((f, i) => [f, f.slice(0, 12) + (i === 0 ? " (newest)" : "")]).concat([["", "all"]]);
    return select(opts, state.fingerprint, (f) => {
      state.fingerprint = f;
      resetTierAndGroup();
      render();
    });
  }

  function metricSelect() {
    return select(METRICS.map((m) => [m.key, m.label]), state.metric, (k) => {
      state.metric = k;
      renderChart();
      renderMachines();
    });
  }

  function select(opts, value, onChange) {
    const s = el("select");
    opts.forEach((o) => s.appendChild(el("option", { value: o[0] }, o[1])));
    s.value = value;
    s.addEventListener("change", () => onChange(s.value));
    return s;
  }

  function segmented(items, current, label, onPick) {
    const seg = el("div", { class: "seg", role: "group" });
    items.forEach((it) => {
      const b = el("button", { type: "button", "aria-pressed": it === current ? "true" : "false" }, label(it));
      b.addEventListener("click", () => onPick(it));
      seg.appendChild(b);
    });
    return seg;
  }

  /** One benchmark version should have one workload: more than one fingerprint means builds disagree on what it measures. */
  function renderCaveat() {
    const host = document.getElementById("caveat"), fps = fingerprints();
    host.innerHTML = "";
    if (state.version === 0) host.appendChild(el("p", { class: "muted small" }, LEGACY_NOTE));
    if (fps.length < 2) return;
    const what = "Benchmark version " + state.version + " of scenario " + scenarioName(state.scenario);
    host.appendChild(el("p", { class: "caveat" }, what + " has " + fps.length +
      " different fingerprints: builds that claim this version measure a different workload, so their numbers may not be comparable." +
      (state.fingerprint ? " The newest fingerprint is shown; pick another or \"all\" above." : " All of them are shown.")));
  }

  // #endregion

  // #region Chart

  function renderChart() {
    const host = document.getElementById("chart");
    host.innerHTML = "";
    if (!D) return;
    const rows = selected().filter((e) => !state.hidden[e.line] && matches(e.line));
    const targets = D.targets.filter((t) => rows.some((e) => e.target === t.id));
    host.appendChild(searchBox());
    host.appendChild(picker());
    if (!rows.some((e) => shownRun(e, state.metric) >= 0)) {
      host.appendChild(el("p", { class: "box muted" }, "No results for this selection."));
      return;
    }
    host.appendChild(legend());
    chart(host, rows, targets);
  }

  /** Lines that can be hidden: every machine+platform of the version with results for this tier and group. */
  function picker() {
    const keys = dict(), box = el("div", { class: "mpick", role: "group", "aria-label": "Machines shown" });
    selected().forEach((e) => { keys[e.line] = 1; });
    D.lines.filter((l) => keys[l.key] && matches(l.key)).forEach((l) => {
      const lab = el("label", { class: "mchip" }), cb = el("input", { type: "checkbox" });
      cb.checked = !state.hidden[l.key];
      cb.addEventListener("change", () => {
        state.hidden[l.key] = !cb.checked;
        renderChart();
      });
      lab.appendChild(cb);
      lab.insertAdjacentHTML("beforeend", ldot(l.key) + esc(lname(l)));
      box.appendChild(lab);
    });
    return box;
  }

  /** Whether machine+platform line <key> matches the search text (case-insensitive, in its name). */
  function matches(key) {
    const q = state.search.trim().toLowerCase();
    return !q || lname(lineOf(key)).toLowerCase().indexOf(q) >= 0;
  }

  /** The machine search: narrows the chips and the chart's lines; keeps its focus and caret across the redraw it causes. */
  function searchBox() {
    const box = el("input", { type: "search", class: "msearch", placeholder: "Search machines", "aria-label": "Search machines" });
    box.value = state.search;
    box.addEventListener("input", () => {
      state.search = box.value;
      renderChart();
      const again = document.querySelector("#chart .msearch");
      again.focus();
      again.setSelectionRange(again.value.length, again.value.length);
    });
    return box;
  }

  function legend() {
    const d = el("div", { class: "legend" });
    const mark = '<rect x="4" y="3" width="8" height="8" transform="rotate(45 8 7)" fill="var(--panel)" stroke="var(--text)" stroke-width="2"/>';
    d.innerHTML = '<span><svg width="16" height="14">' + mark + "</svg> run 2 (warm)</span>" +
      '<span><svg width="16" height="14"><circle cx="8" cy="7" r="4.5" fill="var(--text)" opacity=".35"/></svg> run 1 (may be cache-cold)</span>' +
      "<span>" + (metricDef().higher ? "higher" : "lower") + " is better</span>";
    return d;
  }

  function chart(host, rows, targets) {
    const metric = metricDef(), box = el("div", { class: "chart" });
    box.appendChild(el("h3", {}, metric.label + " - " + chartTitle()));
    host.appendChild(box);
    const g = geometry(box, targets.length, maxOf(rows, metric.key));
    const svg = sv("svg", { width: g.W, height: g.H, viewBox: "0 0 " + g.W + " " + g.H, role: "img", "aria-label": metric.label + " per build" });
    drawGrid(svg, g);
    drawAxis(svg, g, targets);
    const pts = points(rows, targets, metric.key);
    drawLines(svg, g, pts);
    drawMarkers(svg, g, pts, metric);
    box.appendChild(svg);
  }

  function chartTitle() {
    const group = groupsOf().length > 1 ? ", " + groupName(state.group) : "";
    return scenarioName(state.scenario) + group + ", " + state.tier;
  }

  function maxOf(rows, key) {
    let max = 0;
    rows.forEach((e) => e.runs.forEach((r) => { if (r && r[key] != null && r[key] > max) max = r[key]; }));
    return max;
  }

  function geometry(box, n, max) {
    const W = Math.max(280, Math.floor(box.clientWidth - 20)), H = 320, m = { l: 50, r: 26, t: 14, b: 48 };
    const ticks = niceTicks(max), top = ticks[ticks.length - 1] || 1, pw = W - m.l - m.r, ph = H - m.t - m.b;
    return {
      W: W, H: H, m: m, pw: pw, ph: ph, n: n, ticks: ticks,
      X: (i) => m.l + (n === 1 ? pw / 2 : (pw * i) / (n - 1)),
      Y: (v) => m.t + ph - (v / top) * ph
    };
  }

  function drawGrid(svg, g) {
    g.ticks.forEach((v) => {
      const y = g.Y(v);
      svg.appendChild(sv("line", { class: "grid", x1: g.m.l, x2: g.W - g.m.r, y1: y, y2: y }));
      const t = sv("text", { x: g.m.l - 5, y: y + 4, "text-anchor": "end" });
      t.textContent = fmt(v);
      svg.appendChild(t);
    });
  }

  function drawAxis(svg, g, targets) {
    const step = Math.max(1, Math.ceil(g.n / Math.max(1, Math.floor(g.pw / 70))));
    targets.forEach((t, i) => {
      if (i % step && i !== g.n - 1) return;
      const lab = axisLabel(t);
      [lab[0], lab[1]].forEach((s, k) => {
        const a = sv("text", { x: g.X(i), y: g.H - 24 + 13 * k, "text-anchor": "middle" });
        a.textContent = s;
        svg.appendChild(a);
      });
    });
  }

  /** Points per line: the target's index and both runs of the newest session there. */
  function points(rows, targets, key) {
    const byLine = dict();
    rows.forEach((e) => {
      const i = targets.findIndex((t) => t.id === e.target), shown = shownRun(e, key);
      if (shown < 0) return;
      const v1 = shown === 1 && e.runs[0] ? e.runs[0][key] : null;
      (byLine[e.line] = byLine[e.line] || []).push({ i: i, e: e, shown: shown, v: e.runs[shown][key], v1: v1 });
    });
    Object.keys(byLine).forEach((k) => byLine[k].sort((a, b) => a.i - b.i));
    return byLine;
  }

  function drawLines(svg, g, pts) {
    Object.keys(pts).forEach((k) => {
      if (pts[k].length < 2) return;
      svg.appendChild(sv("polyline", { class: "ml", style: "stroke:" + lcolor(k), points: pts[k].map((p) => g.X(p.i) + "," + g.Y(p.v)).join(" ") }));
    });
  }

  function drawMarkers(svg, g, pts, metric) {
    const guide = sv("line", { class: "guide", x1: 0, x2: 0, y1: g.m.t, y2: g.m.t + g.ph });
    svg.appendChild(guide);
    Object.keys(pts).forEach((k) => pts[k].forEach((p) => {
      const x = g.X(p.i), col = lcolor(k);
      if (p.v1 != null) svg.appendChild(sv("circle", { class: "p1", cx: x, cy: g.Y(p.v1), r: 5, style: "fill:" + col }));
      const y = g.Y(p.v);
      svg.appendChild(p.shown === 1 ? diamond(x, y, col) : sv("circle", { class: "p1 only", cx: x, cy: y, r: 5, style: "fill:" + col }));
      svg.appendChild(hitTarget(x, y, guide, p, metric));
    }));
  }

  function diamond(x, y, col) {
    const attrs = { class: "p2", x: x - 4.5, y: y - 4.5, width: 9, height: 9, transform: "rotate(45 " + x + " " + y + ")" };
    attrs.style = "stroke:" + col;
    return sv("rect", attrs);
  }

  function hitTarget(x, y, guide, p, metric) {
    const hit = sv("circle", { class: "hit", cx: x, cy: y, r: 11, tabindex: 0 }), html = tipHtml(p, metric);
    const on = () => {
      guide.setAttribute("x1", x);
      guide.setAttribute("x2", x);
      guide.classList.add("on");
    };
    const off = () => {
      hideTip();
      guide.classList.remove("on");
    };
    hit.addEventListener("mouseenter", on);
    hit.addEventListener("mousemove", (ev) => showTip(html, ev));
    hit.addEventListener("mouseleave", off);
    hit.addEventListener("focus", () => {
      on();
      const b = hit.getBoundingClientRect();
      showTip(html, { clientX: b.left, clientY: b.top });
    });
    hit.addEventListener("blur", off);
    const ti = sv("title");
    ti.textContent = lname(lineOf(p.e.line)) + " " + p.e.target + ": " + fmt(p.v) + metric.unit;
    hit.appendChild(ti);
    return hit;
  }

  function tipHtml(p, metric) {
    const e = p.e, t = targetOf(e.target), game = e.info.game || {};
    const runs = p.shown === 1
      ? "run 2: <b>" + fmt(p.v) + "</b>" + metric.unit + (p.v1 != null ? ", run 1: " + fmt(p.v1) + metric.unit : "")
      : "run 1: <b>" + fmt(p.v) + "</b>" + metric.unit + " (no run 2)";
    const commit = game.commit && state.version !== 0 ? ' <span class="muted">' + esc(str(game.commit).slice(0, 8)) + "</span>" : "";
    return "<b>" + esc(t ? targetTitle(t) : e.target) + "</b>" + commit +
      "<br>" + ldot(e.line) + esc(lname(lineOf(e.line))) + (e.handle ? " by " + esc(e.handle) : "") +
      "<br>" + esc(metric.label) + "<br>" + runs +
      "<br><span class=\"muted\">" + esc(e.when.slice(0, 16).replace("T", " ")) + "</span>";
  }

  // #endregion

  // #region Machines

  /** One row per machine+platform of the selected version, with its newest session's hardware and the metric at its newest target. */
  function renderMachines() {
    const host = document.getElementById("machines");
    host.innerHTML = "";
    if (!D) return;
    const rows = selected(), metric = metricDef(), lines = dict();
    rows.forEach((e) => { (lines[e.line] = lines[e.line] || []).push(e); });
    const keys = D.lines.map((l) => l.key).filter((k) => lines[k]);
    if (!keys.length) return;
    const wrap = el("div", { class: "tablewrap" }), table = el("table", { class: "mtable" });
    table.innerHTML = "<thead><tr><th>Machine</th><th class=\"l\">Platform</th><th class=\"l\">CPU</th><th class=\"l\">GPU</th><th>RAM</th>" +
      "<th class=\"l\">OS</th><th>Resolution</th><th>Builds</th><th class=\"l\">Newest</th><th>" + esc(metric.label) + "</th></tr></thead>";
    const body = el("tbody");
    keys.forEach((k) => body.appendChild(machineRow(k, lines[k], metric)));
    table.appendChild(body);
    wrap.appendChild(table);
    host.appendChild(wrap);
    const note = "Values: run 2 (run 1 in brackets), at the machine's newest build for this scenario and tier.";
    host.appendChild(el("p", { class: "muted small" }, note));
  }

  function machineRow(key, entries, metric) {
    const newest = entries.slice().sort((a, b) => D.targets.findIndex((t) => t.id === b.target) - D.targets.findIndex((t) => t.id === a.target))[0];
    const m = newest.machine, hw = newest.info.hardware || {}, tr = el("tr");
    const name = el("td");
    const by = newest.handle ? ' <span class="muted">by ' + esc(newest.handle) + "</span>" : "";
    name.innerHTML = ldot(key) + "<b>" + esc(str(m.name || m.id) || "unnamed") + "</b>" + by;
    if (m.notes) name.title = str(m.notes);
    tr.appendChild(name);
    const t = targetOf(newest.target);
    const ram = num(m.ramGb != null ? m.ramGb : hw.ramGb);
    [
      [PLAT[newest.platform] || newest.platform, "l"],
      [str(m.cpu || hw.cpu), "l"],
      [str(m.gpu || hw.gpu), "l", hw.driver ? "Driver " + str(hw.driver) : ""],
      [ram != null ? ram + " GB" : "", ""],
      [str(m.os || hw.os), "l", hw.kernel ? "Kernel " + str(hw.kernel) : ""],
      [str(m.resolution || newest.info.resolution), ""],
      [String(entries.length), ""],
      [t ? t.label : newest.target, "l", t ? targetTitle(t) : ""],
      [valueText(newest, metric), ""]
    ].forEach((c) => tr.appendChild(cell(c[0], c[1], c[2])));
    return tr;
  }

  function cell(text, cls, title) {
    const td = el("td", cls ? { class: cls } : {}, text);
    if (title) td.title = title;
    return td;
  }

  function valueText(e, metric) {
    const shown = shownRun(e, metric.key);
    if (shown < 0) return "n/a";
    const v = fmt(e.runs[shown][metric.key]) + metric.unit;
    if (shown === 0) return v + " (run 1 only)";
    const v1 = e.runs[0] ? e.runs[0][metric.key] : null;
    return v + (v1 != null ? " (" + fmt(v1) + ")" : "");
  }

  // #endregion

  load();
})();
