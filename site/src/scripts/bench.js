// Benchmarks page: fetches the launcher's benchmark sessions from the Reports service (GET /bench/data, GET /bench/targets) at
// runtime and draws one metric for one version range, a dot per machine, platform and build, plus a table of the machines. Every text in
// the data is submitted by players, so it only ever reaches the page through textContent or esc().
(function () {
  "use strict";

  // #region Definitions

  /** Quality tiers in their order; legacy kit data uses "baseline". */
  const TIERS = ["low", "medium", "high", "ultra", "extreme", "baseline"];

  /** Scenarios in their order and names; the old kit's two (version 0) have names of their own. Others sort after, by id. */
  const SCENARIOS = { bases: "Bases", "shark-flyby": "Shark flyby" };
  const LEGACY_SCENARIOS = { play: "Play benchmark", bases: "Base switch" };

  /** Groups of a scenario's segments as result.json names them (they differ per scenario); "all" sorts first, others by id. */
  const GROUPS = {
    all: "All segments", camera: "Camera", streaming: "Streaming", simulation: "Simulation", ui: "UI", views: "Views", flight: "Flight"
  };

  /** Human-readable descriptions for each segment group, shown under the controls and as button tooltips. */
  const GROUP_DESC = {
    camera: "Basic camera movement around the starting base: stationary idle, 360\u00B0 orbit, zooming out and in, and steady panning.",
    streaming: "Fast camera travel across the map and snapping back, stressing terrain, foliage, and zone streaming.",
    simulation: "World simulation and gameplay under load: 2x/5x speed, dialogue, trading, containers, doors, picking up items, pathfinding bursts, walking, combat, and looting.",
    ui: "Opening and switching between in-game interface panels (inventory, stats, overview, limbs, and build menu), cold and warm.",
    views: "Heavy draw-distance angles (low grazing horizon sweep and high top-down overview) plus camera flights hopping between squad members.",
    flight: "Fixed camera flyby around the busy swamp town of Shark: a high 360\u00B0 orbit, a closer low orbit, and a low pass through the centre.",
    all: "Combined results across every measured segment in the scenario (excluding the initial uncounted settle)."
  };

  /** What the legacy view shows, under the controls. */
  const LEGACY_NOTE = "Legacy kit: the earlier Python kit at its baseline settings and each machine's own resolution, all its commits together; " +
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

  /**
   * Version ranges of launcher benchmarks (benchmark version bench), newest first, kept by hand: every build from since (a target's version)
   * up to the next newer range of the same benchmark version performs alike, so the page compares them as one. A performance regression or
   * optimisation (or a new benchmark version) starts a new range: add an item with its first build on top. since "" is every older build.
   * The legacy kit (benchmark version 0) is always the last item.
   */
  const RANGES = [
    { bench: 1, since: "" }
  ];

  /** Machines the table shows per page. */
  const PAGE = 20;

  /** Platforms the launcher submits. */
  const PLAT = { native: "Linux", proton: "Linux (Proton)", windows: "Windows", macos: "macOS" };

  const NS = "http://www.w3.org/2000/svg";
  const root = document.getElementById("bench-root");
  const tip = document.getElementById("tip");

  /** Everything drawn comes from these: the loaded data and the current selection. */
  let D = null;
  const state = {
    range: 0, scenario: "", tier: "", group: "all", metric: "avgFps", checked: Object.create(null), search: "", page: 0, offTarget: false,
    sort: { col: "metric", dir: 0 }
  };

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
    s.classList.remove("is-loading");
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
    const known = knownTargets(targets), ordered = orderTargets(entries, known);
    const ranges = rangeList(entries, known, ordered);
    return { entries: entries, targets: ordered, lines: lineList(entries), ranges: ranges, haveTargets: targets.length > 0 };
  }

  function entry(s, scenario, group, scen) {
    const m = s.machine && typeof s.machine === "object" ? s.machine : {}, platform = str(s.platform);
    return {
      id: s.id, at: str(s.at), handle: str(s.handle), machine: m, platform: platform, target: str(s.target).replace(/^v/, ""),
      tier: str(s.tier), version: num(s.version) || 0, scenario: scenario, group: group, range: -1,
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

  /** The targets of /bench/targets by version and by tag (both without the v). */
  function knownTargets(targets) {
    const known = dict();
    targets.forEach((t) => {
      if (!t || typeof t !== "object") return;
      const v = str(t.version || t.tag).replace(/^v/, "");
      known[v] = { id: v, kind: str(t.kind), time: str(t.publishedAt), label: v };
      known[str(t.tag).replace(/^v/, "")] = known[v];
    });
    return known;
  }

  /** Targets oldest first: the known ones by publish date, any other by its first benchmark (legacy commits). */
  function orderTargets(entries, known) {
    const first = dict();
    entries.forEach((e) => {
      if (!first[e.target] || e.when < first[e.target]) first[e.target] = e.when;
    });
    const out = Object.keys(first).map((id) => known[id]
      ? Object.assign({}, known[id], { id: id })
      : { id: id, kind: "", time: first[id], label: id });
    return out.sort((a, b) => (a.time < b.time ? -1 : a.time > b.time ? 1 : 0));
  }

  /**
   * The ranges that hold a session, newest first, each with its benchmark version and its builds oldest first; every entry gets its range's
   * index. A range's start is compared by publish time, as versions don't sort by text (a nightly of 0.22.2 is older than 0.23.0).
   */
  function rangeList(entries, known, ordered) {
    const defs = RANGES.map((r) => ({ bench: r.bench, start: r.since && known[r.since] ? known[r.since].time : "" }))
      .concat([{ bench: 0, start: "" }]);
    const time = dict();
    ordered.forEach((t) => { time[t.id] = t.time; });
    const used = defs.map(() => []);
    entries.forEach((e) => {
      const i = defs.findIndex((d) => d.bench === e.version && time[e.target] >= d.start);
      if (i < 0) return;
      e.range = i;
      if (used[i].indexOf(e.target) < 0) used[i].push(e.target);
    });
    return defs.map((d, i) => ({ index: i, bench: d.bench, builds: ordered.filter((t) => used[i].indexOf(t.id) >= 0) }))
      .filter((r) => r.builds.length);
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

  function range() { return D.ranges.filter((r) => r.index === state.range)[0]; }

  function legacy() { return range().bench === 0; }

  function inScenarioOf(scenario) { return D.entries.filter((e) => e.range === state.range && e.scenario === scenario); }

  function inScenario() { return inScenarioOf(state.scenario); }

  /** The newest session per (machine, platform, target) for a given scenario, tier and group. */
  function selectedFor(scenario, tier, group) {
    const seen = dict();
    return inScenarioOf(scenario).filter((e) => {
      if (e.tier !== tier || e.group !== group) return false;
      if (!state.offTarget && !onTarget(e)) return false;
      const k = e.line + "|" + e.target;
      if (seen[k]) return false;
      seen[k] = 1;
      return true;
    });
  }

  /**
   * Whether the session's build is a benchmark target (/bench/targets: full releases and the nightlies whose notes say "Daily build"). Legacy kit
   * commits are never targets there, and without the target list nothing can be told apart, so both count as on target.
   */
  function onTarget(e) {
    if (e.version === 0 || !D.haveTargets) return true;
    const t = targetOf(e.target);
    return !!(t && t.kind);
  }

  /** Whether the selected range and scenario have a session on a build that is not a target. */
  function hasOffTarget() { return inScenario().some((e) => !onTarget(e)); }

  /** The newest session per (machine, platform, target) of the selected range, scenario, tier and group. */
  function selected() { return selectedFor(state.scenario, state.tier, state.group); }

  /** How many plotted results would show for a hypothetical (scenario, tier, group) under the active metric and machine selection. */
  function countFor(scenario, tier, group) {
    return selectedFor(scenario, tier, group)
      .filter((e) => plotted(e.line) && shownRun(e, state.metric) >= 0).length;
  }

  /** How many plotted results would show if <scenario> were selected (resolving its default tier and group the same way). */
  function countForScenario(scenario) {
    const entries = inScenarioOf(scenario);
    const haveTiers = dict(), haveGroups = dict();
    entries.forEach((e) => { haveTiers[e.tier] = 1; haveGroups[e.group] = 1; });
    const tiers = TIERS.filter((t) => haveTiers[t]).concat(Object.keys(haveTiers).filter((t) => TIERS.indexOf(t) < 0).sort());
    const groups = (haveGroups.all ? ["all"] : [])
      .concat(Object.keys(GROUPS).filter((g) => haveGroups[g] && g !== "all"), Object.keys(haveGroups).filter((g) => !own(GROUPS, g)).sort());
    const tier = tiers.indexOf(state.tier) >= 0 ? state.tier : (tiers.indexOf("medium") >= 0 ? "medium" : tiers[0] || "");
    const group = groups.indexOf(state.group) >= 0 ? state.group : (groups.indexOf("all") >= 0 ? "all" : groups[0] || "");
    return countFor(scenario, tier, group);
  }

  /** Whether line <key> is drawn: the checked machines when any are, else every one matching the table's filter. */
  function plotted(key) {
    if (anyChecked()) return !!state.checked[key];
    return matches(key);
  }

  function anyChecked() { return Object.keys(state.checked).some((k) => state.checked[k]); }

  /** Whether machine+platform line <key> matches the filter text (machine id, platform, handle, CPU, GPU or OS). */
  function matches(key) {
    const q = state.search.trim().toLowerCase();
    if (!q) return true;
    const line = lineOf(key), m = line.machine || {};
    const text = [lname(line), line.handle, m.cpu, m.gpu, m.os].join(" ").toLowerCase();
    return text.indexOf(q) >= 0;
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

  function targetTitle(t) {
    const kind = t.kind === "release" ? "release " : t.kind === "daily" ? "daily build " : legacy() ? "commit " : "";
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
    selectRange(D.ranges[0].index);
    window.addEventListener("resize", debounce(renderChart, 150));
  }

  function debounce(fn, ms) {
    let t;
    return () => {
      clearTimeout(t);
      t = setTimeout(fn, ms);
    };
  }

  /** A range change picks a scenario the range has (bases when it has it). */
  function selectRange(i) {
    state.range = i;
    const ids = scenariosOf();
    selectScenario(ids.indexOf("bases") >= 0 ? "bases" : ids[0] || "");
  }

  /** A scenario change resets the tier and group to ones the scenario has. */
  function selectScenario(id) {
    state.scenario = id;
    resetTierAndGroup();
    render();
  }

  function scenariosOf() {
    const have = dict(), names = scenarioNames();
    D.entries.forEach((e) => { if (e.range === state.range) have[e.scenario] = 1; });
    return Object.keys(names).filter((id) => have[id]).concat(Object.keys(have).filter((id) => !own(names, id)).sort());
  }

  function scenarioNames() { return legacy() ? LEGACY_SCENARIOS : SCENARIOS; }

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
    return (have.all ? ["all"] : []).concat(known, other);
  }

  function groupName(g) { return own(GROUPS, g) ? GROUPS[g] : g; }

  function groupDesc(g) { return own(GROUP_DESC, g) ? GROUP_DESC[g] : ""; }

  function render() {
    document.getElementById("toolbar").hidden = false;
    renderControls();
    renderGroupDesc();
    renderCaveat();
    renderChart();
    renderMachines();
  }

  function renderControls() {
    const host = document.getElementById("controls");
    host.innerHTML = "";
    const groups = groupsOf();

    const row1 = el("div", { class: "tb-row" });
    row1.appendChild(control("Scenario", segmented(scenariosOf(), state.scenario, scenarioName, selectScenario, null, countForScenario)));
    row1.appendChild(control("Tier", segmented(tiersOf(), state.tier, tierName, pickTier, null,
      (t) => countFor(state.scenario, t, state.group))));
    row1.appendChild(control("Versions", rangeSelect()));
    if (hasOffTarget()) row1.appendChild(control("Builds", buildsToggle()));
    const metric = control("Metric", metricSelect());
    metric.classList.add("tb-metric");
    row1.appendChild(metric);
    host.appendChild(row1);

    if (groups.length > 1) {
      const row2 = el("div", { class: "tb-row" });
      row2.appendChild(control("Segments", segmented(groups, state.group, groupName, pickGroup, groupDesc,
        (g) => countFor(state.scenario, state.tier, g))));
      host.appendChild(row2);
    }
  }

  function renderGroupDesc() {
    const host = document.getElementById("group-desc");
    const desc = groupsOf().length > 1 ? groupDesc(state.group) : "";
    host.hidden = !desc;
    host.innerHTML = "";
    if (!desc) return;
    host.appendChild(el("strong", {}, groupName(state.group) + ": "));
    host.appendChild(document.createTextNode(desc));
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

  function rangeSelect() {
    const opts = D.ranges.map((r) => [String(r.index), rangeName(r)]);
    return select(opts, String(state.range), (i) => selectRange(Number(i)));
  }

  /** v<first build> to v<last build> of a range (with its benchmark version past 1), or the legacy kit. */
  function rangeName(r) {
    if (r.bench === 0) return "Legacy kit (commits)";
    const first = r.builds[0].label, last = r.builds[r.builds.length - 1].label;
    const span = first === last ? "v" + first : "v" + first + " \u2013 v" + last;
    return r.bench > 1 ? span + " (benchmark " + r.bench + ")" : span;
  }

  /** Off-target builds (neither a full release nor a daily nightly) are hidden until "All builds" is picked. */
  function buildsToggle() {
    const names = { targets: "Targets only", all: "All builds" };
    return segmented(["targets", "all"], state.offTarget ? "all" : "targets", (k) => names[k], (k) => {
      state.offTarget = k === "all";
      render();
    }, (k) => (k === "targets" ? "Full releases and daily nightlies only" : "Also the other nightlies"));
  }

  function metricSelect() {
    return select(METRICS.map((m) => [m.key, m.label]), state.metric, (k) => {
      state.metric = k;
      state.page = 0;
      renderControls();
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

  function segmented(items, current, label, onPick, titleOf, countOf) {
    const seg = el("div", { class: "seg", role: "group" });
    items.forEach((it) => {
      const attrs = { type: "button", "aria-pressed": it === current ? "true" : "false" };
      const title = titleOf ? titleOf(it) : "";
      if (title) attrs.title = title;
      const b = el("button", attrs, label(it));
      if (countOf) {
        const n = countOf(it);
        b.appendChild(el("span", { class: n === 0 ? "seg-n is-zero" : "seg-n", "aria-label": n + " results" }, String(n)));
      }
      b.addEventListener("click", () => onPick(it));
      seg.appendChild(b);
    });
    return seg;
  }

  function renderCaveat() {
    const host = document.getElementById("caveat");
    host.innerHTML = "";
    if (legacy()) host.appendChild(el("p", { class: "muted small" }, LEGACY_NOTE));
  }

  // #endregion

  // #region Chart

  function renderChart() {
    const host = document.getElementById("chart");
    host.innerHTML = "";
    if (!D) return;
    const rows = selected().filter((e) => plotted(e.line));
    if (!rows.some((e) => shownRun(e, state.metric) >= 0)) {
      host.appendChild(el("p", { class: "box muted" }, "No results for this selection."));
      return;
    }
    chart(host, rows);
  }

  function legend() {
    const d = el("div", { class: "legend" });
    const mark = '<rect x="4" y="3" width="8" height="8" transform="rotate(45 8 7)" fill="var(--panel)" stroke="var(--text)" stroke-width="2"/>';
    d.innerHTML = '<span><svg width="16" height="14">' + mark + "</svg> run 2 (warm)</span>" +
      '<span><svg width="16" height="14"><circle cx="8" cy="7" r="4.5" fill="var(--text)" opacity=".35"/></svg> run 1 (may be cache-cold)</span>' +
      "<span>" + (metricDef().higher ? "higher" : "lower") + " is better</span>";
    return d;
  }

  /** One dot per machine, platform and build: the builds of a range perform alike, so X only spreads the machines apart. */
  function chart(host, rows) {
    const metric = metricDef(), box = el("div", { class: "chart" });
    const head = el("div", { class: "chart-head" });
    head.appendChild(el("h3", {}, metric.label + " - " + chartTitle()));
    head.appendChild(legend());
    box.appendChild(head);
    host.appendChild(box);
    const g = geometry(box, maxOf(rows, metric.key));
    const svg = sv("svg", { width: g.W, height: g.H, viewBox: "0 0 " + g.W + " " + g.H, role: "img", "aria-label": metric.label + " per machine" });
    drawGrid(svg, g);
    drawMarkers(svg, g, points(rows, metric.key), metric);
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

  function geometry(box, max) {
    const W = Math.max(280, Math.floor(box.clientWidth - 20)), H = 320, m = { l: 50, r: 16, t: 14, b: 14 };
    const ticks = niceTicks(max), top = ticks[ticks.length - 1] || 1, pw = W - m.l - m.r, ph = H - m.t - m.b;
    return {
      W: W, H: H, m: m, pw: pw, ph: ph, ticks: ticks,
      X: (f) => m.l + pw * (0.03 + 0.94 * f),
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

  /** Each plotted session's both runs and its place along X, from its machine alone: a PC keeps its X in every scenario, tier and segment. */
  function points(rows, key) {
    const out = [];
    rows.forEach((e) => {
      const shown = shownRun(e, key);
      if (shown < 0) return;
      const v1 = shown === 1 && e.runs[0] ? e.runs[0][key] : null;
      out.push({ x: spread(str(e.machine.id || e.machine.name)), e: e, shown: shown, v: e.runs[shown][key], v1: v1 });
    });
    return out;
  }

  /** A fraction in [0, 1) from text: FNV-1a, then a final mix, so close ids land far apart. */
  function spread(text) {
    let h = 2166136261;
    for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 16777619);
    h = Math.imul(h ^ (h >>> 15), 2246822507);
    h ^= h >>> 13;
    return (h >>> 0) / 4294967296;
  }

  function drawMarkers(svg, g, pts, metric) {
    pts.forEach((p) => {
      const x = g.X(p.x), col = lcolor(p.e.line);
      if (p.v1 != null) svg.appendChild(sv("circle", { class: "p1", cx: x, cy: g.Y(p.v1), r: 5, style: "fill:" + col }));
      const y = g.Y(p.v);
      svg.appendChild(p.shown === 1 ? diamond(x, y, col) : sv("circle", { class: "p1 only", cx: x, cy: y, r: 5, style: "fill:" + col }));
      svg.appendChild(hitTarget(x, y, p, metric));
    });
  }

  function diamond(x, y, col) {
    const attrs = { class: "p2", x: x - 4.5, y: y - 4.5, width: 9, height: 9, transform: "rotate(45 " + x + " " + y + ")" };
    attrs.style = "stroke:" + col;
    return sv("rect", attrs);
  }

  function hitTarget(x, y, p, metric) {
    const hit = sv("circle", { class: "hit", cx: x, cy: y, r: 11, tabindex: 0 }), html = tipHtml(p, metric);
    hit.addEventListener("mousemove", (ev) => showTip(html, ev));
    hit.addEventListener("mouseleave", hideTip);
    hit.addEventListener("focus", () => {
      const b = hit.getBoundingClientRect();
      showTip(html, { clientX: b.left, clientY: b.top });
    });
    hit.addEventListener("blur", hideTip);
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
    const commit = game.commit && !legacy() ? ' <span class="muted">' + esc(str(game.commit).slice(0, 8)) + "</span>" : "";
    return "<b>" + esc(t ? targetTitle(t) : e.target) + "</b>" + commit +
      "<br>" + ldot(e.line) + esc(lname(lineOf(e.line))) + (e.handle ? " by " + esc(e.handle) : "") +
      "<br>" + esc(metric.label) + "<br>" + runs +
      "<br><span class=\"muted\">" + esc(e.when.slice(0, 16).replace("T", " ")) + "</span>";
  }

  // #endregion

  // #region Machines

  /**
   * The machines of the selection, one row per machine+platform: filtered by the text box, sorted by any column (the metric, best first, by
   * default), PAGE at a time. Checked rows are the only ones the chart draws; none checked draws every row the filter keeps.
   */
  function renderMachines() {
    const host = document.getElementById("machines");
    if (!D) return;
    const bar = machineBar(host), body = host.querySelector(".mbody");
    body.innerHTML = "";

    const metric = metricDef(), items = machineItems(metric);
    updateMachineBar(bar, items.length);
    if (!items.length) {
      body.appendChild(el("p", { class: "muted small" }, selected().length ? "No machines match this filter." : "No machines."));
      return;
    }

    sortMachines(items, metric);
    const pages = Math.ceil(items.length / PAGE);
    state.page = Math.min(state.page, pages - 1);
    body.appendChild(machineTable(items.slice(state.page * PAGE, (state.page + 1) * PAGE), metric));
    if (pages > 1) body.appendChild(pager(pages, items.length));
    const note = "Values: run 2 (run 1 in brackets), at the machine's newest build in this range for this scenario and tier. " +
      "Launcher benchmarks always render at 1080p (1920×1080 windowed).";
    body.appendChild(el("p", { class: "muted small" }, note));
  }

  /** Every machine+platform line of the selection that matches the filter, with its sessions. */
  function machineItems(metric) {
    const lines = dict();
    selected().forEach((e) => { (lines[e.line] = lines[e.line] || []).push(e); });
    return D.lines.map((l) => l.key)
      .filter((k) => lines[k] && matches(k))
      .map((k) => machineData(k, lines[k], metric));
  }

  /** The filter box and the selection's count and clear button, made once so typing never loses focus. */
  function machineBar(host) {
    const have = host.querySelector(".mbar");
    if (have) return have;
    const bar = el("div", { class: "mbar" });
    const box = el("input", { type: "search", class: "msearch", placeholder: "Filter machines (GPU, CPU, OS, user)", "aria-label": "Filter machines" });
    box.addEventListener("input", () => {
      state.search = box.value;
      state.page = 0;
      refresh();
    });
    const clear = el("button", { type: "button", class: "mclear" });
    clear.addEventListener("click", () => {
      state.checked = dict();
      refresh();
    });
    bar.appendChild(box);
    bar.appendChild(el("span", { class: "mcount muted" }));
    bar.appendChild(clear);
    host.appendChild(bar);
    host.appendChild(el("div", { class: "mbody" }));
    return bar;
  }

  function updateMachineBar(bar, shown) {
    bar.dataset.shown = shown;
    const n = Object.keys(state.checked).filter((k) => state.checked[k]).length;
    const clear = bar.querySelector(".mclear");
    bar.querySelector(".mcount").textContent = shown + (shown === 1 ? " machine" : " machines") +
      (n ? ", " + n + " checked: only those are drawn" : ", check some to draw only those");
    clear.textContent = "Clear checks";
    clear.hidden = !n;
  }

  /** What depends on the machine selection: the counts in the controls, the chart and the table. */
  function refresh() {
    renderControls();
    renderChart();
    renderMachines();
  }

  function machineColumns(metric) {
    const showRes = legacy();
    if (!showRes && state.sort.col === "res") state.sort = { col: "metric", dir: 0 };
    return [
      { id: "machine", label: "Machine", cls: "", num: false },
      { id: "platform", label: "Platform", cls: "l wide", num: false },
      { id: "cpu", label: "CPU", cls: "l wide", num: false },
      { id: "gpu", label: "GPU", cls: "l wide", num: false },
      { id: "ram", label: "RAM", cls: "wide", num: true },
      { id: "os", label: "OS", cls: "l wide", num: false },
      ...(showRes ? [{ id: "res", label: "Resolution", cls: "wide", num: true }] : []),
      { id: "builds", label: "Builds", cls: "wide", num: true },
      { id: "newest", label: "Newest", cls: "l wide", num: true },
      { id: "metric", label: metric.label, cls: "", num: true }
    ];
  }

  function machineTable(items, metric) {
    const cols = machineColumns(metric);
    const wrap = el("div", { class: "tablewrap" }), table = el("table", { class: "mtable" });
    const head = el("tr"), body = el("tbody");
    head.appendChild(el("th", { class: "ck" }, ""));
    cols.forEach((c) => head.appendChild(sortHeader(c, metric)));
    items.forEach((item) => body.appendChild(machineRow(item, metric, legacy())));
    const thead = el("thead");
    thead.appendChild(head);
    table.appendChild(thead);
    table.appendChild(body);
    wrap.appendChild(table);
    return wrap;
  }

  function sortHeader(c, metric) {
    const active = state.sort.col === c.id, dir = sortDir(metric);
    const th = el("th", c.cls ? { class: c.cls } : {});
    th.setAttribute("aria-sort", active ? (dir > 0 ? "ascending" : "descending") : "none");
    const btn = el("button", { type: "button", class: active ? "sort-btn is-active" : "sort-btn" }, c.label);
    btn.appendChild(el("span", { class: "sort-ind", "aria-hidden": "true" }, active ? (dir > 0 ? "▲" : "▼") : "⇅"));
    btn.addEventListener("click", () => {
      if (active) state.sort = { col: c.id, dir: -dir };
      else state.sort = { col: c.id, dir: c.id === "metric" ? 0 : (c.num ? -1 : 1) };
      state.page = 0;
      renderMachines();
    });
    th.appendChild(btn);
    return th;
  }

  /** The sort direction; 0 is the metric's best first. */
  function sortDir(metric) {
    if (state.sort.dir) return state.sort.dir;
    return metric.higher ? -1 : 1;
  }

  function pager(pages, total) {
    const nav = el("div", { class: "pager", role: "navigation", "aria-label": "Machine pages" });
    const from = state.page * PAGE + 1, to = Math.min(total, (state.page + 1) * PAGE);
    nav.appendChild(pageButton("‹ Previous", state.page - 1, state.page === 0));
    nav.appendChild(el("span", { class: "muted" }, from + "–" + to + " of " + total));
    nav.appendChild(pageButton("Next ›", state.page + 1, state.page >= pages - 1));
    return nav;
  }

  function pageButton(label, page, disabled) {
    const b = el("button", { type: "button" }, label);
    b.disabled = disabled;
    b.addEventListener("click", () => {
      state.page = page;
      renderMachines();
    });
    return b;
  }

  function machineData(key, entries, metric) {
    const newest = entries.slice().sort((a, b) => D.targets.findIndex((t) => t.id === b.target) - D.targets.findIndex((t) => t.id === a.target))[0];
    const m = newest.machine, hw = newest.info.hardware || {}, t = targetOf(newest.target);
    const ram = num(m.ramGb != null ? m.ramGb : hw.ramGb);
    const res = !legacy() ? str(newest.info.resolution || "1920x1080") : str(m.resolution || newest.info.resolution);
    const resMatch = res.match(/^(\d+)\s*[x×]\s*(\d+)$/i);
    const resPixels = resMatch ? Number(resMatch[1]) * Number(resMatch[2]) : null;
    const shown = shownRun(newest, metric.key);
    const metricVal = shown >= 0 ? num(newest.runs[shown][metric.key]) : null;
    return {
      key: key,
      entries: entries,
      newest: newest,
      m: m,
      hw: hw,
      t: t,
      ram: ram,
      res: res,
      sort: {
        machine: str(m.name || m.id).toLowerCase(),
        platform: (PLAT[newest.platform] || newest.platform || "").toLowerCase(),
        cpu: str(m.cpu || hw.cpu).toLowerCase(),
        gpu: str(m.gpu || hw.gpu).toLowerCase(),
        ram: ram,
        os: str(m.os || hw.os).toLowerCase(),
        res: resPixels,
        builds: entries.length,
        newest: D.targets.findIndex((x) => x.id === newest.target),
        metric: metricVal
      }
    };
  }

  function sortMachines(items, metric) {
    const col = state.sort.col, dir = sortDir(metric);
    items.sort((a, b) => {
      const av = a.sort[col], bv = b.sort[col];
      const aEmpty = av == null || av === "", bEmpty = bv == null || bv === "";
      if (aEmpty && bEmpty) return 0;
      if (aEmpty) return 1;
      if (bEmpty) return -1;
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
  }

  /** A row checks its machine when clicked anywhere; narrow screens show the platform and GPU under the name instead of their columns. */
  function machineRow(item, metric, showRes) {
    const key = item.key, entries = item.entries, newest = item.newest, m = item.m, hw = item.hw, t = item.t, ram = item.ram;
    const tr = el("tr", { class: state.checked[key] ? "is-checked" : "" });
    tr.appendChild(checkCell(key, tr));
    tr.appendChild(nameCell(item));
    [
      [PLAT[newest.platform] || newest.platform, "l wide"],
      [str(m.cpu || hw.cpu), "l wide"],
      [str(m.gpu || hw.gpu), "l wide", hw.driver ? "Driver " + str(hw.driver) : ""],
      [ram != null ? ram + " GB" : "", "wide"],
      [str(m.os || hw.os), "l wide", hw.kernel ? "Kernel " + str(hw.kernel) : ""],
      ...(showRes ? [[item.res, "wide"]] : []),
      [String(entries.length), "wide"],
      [t ? t.label : newest.target, "l wide", t ? targetTitle(t) : ""],
      [valueText(newest, metric), ""]
    ].forEach((c) => tr.appendChild(cell(c[0], c[1], c[2])));
    tr.addEventListener("click", (ev) => {
      if (ev.target.closest("input")) return;
      const cb = tr.querySelector("input");
      cb.checked = !cb.checked;
      cb.dispatchEvent(new Event("change"));
    });
    return tr;
  }

  function checkCell(key, tr) {
    const td = el("td", { class: "ck" }), cb = el("input", { type: "checkbox", "aria-label": "Draw only the checked machines" });
    cb.checked = !!state.checked[key];
    cb.addEventListener("change", () => {
      state.checked[key] = cb.checked;
      tr.classList.toggle("is-checked", cb.checked);
      renderControls();
      renderChart();
      const bar = document.querySelector("#machines .mbar");
      updateMachineBar(bar, Number(bar.dataset.shown));
    });
    td.appendChild(cb);
    return td;
  }

  function nameCell(item) {
    const m = item.m, newest = item.newest, td = el("td");
    const by = newest.handle ? ' <span class="muted">by ' + esc(newest.handle) + "</span>" : "";
    const sub = [PLAT[newest.platform] || newest.platform, str(m.gpu || item.hw.gpu)].filter(Boolean).map(esc).join(" · ");
    td.innerHTML = ldot(item.key) + "<b>" + esc(str(m.name || m.id) || "unnamed") + "</b>" + by + '<span class="msub">' + sub + "</span>";
    if (m.notes) td.title = str(m.notes);
    return td;
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
