(function () {
  "use strict";
  var REPO = "https://github.com/VlcekM/MeitouClient";
  var METRICS = [
    { key: "p99_ms", label: "p99 frame time (ms)", unit: " ms", primary: true },
    { key: "over33_pct", label: "Frames over 33 ms (%)", unit: "%", primary: true },
    { key: "p50_ms", label: "p50 frame time (ms)", unit: " ms", primary: true },
    { key: "slow_world_p99_ms", label: "Slow-world tick p99 (ms)", unit: " ms" },
    { key: "avg_fps", label: "Average fps", unit: " fps", higher: true, optional: true },
    { key: "vram_peak_mb", label: "Peak video memory (MB)", unit: " MB", primary: true, optional: true }
  ];
  var ALLM = METRICS.concat([
    { key: "max_ms", label: "Max frame (ms)", unit: " ms" },
    { key: "over100_count", label: "Frames over 100 ms", unit: "" },
    { key: "slow_world_max_ms", label: "Slow-world tick max (ms)", unit: " ms" }
  ]);
  var NS = "http://www.w3.org/2000/svg";
  var D = null;
  var state = { tlAll: false, selected: {}, hm: "", runMode: "warm", relative: false, more: false, bdAll: {}, ref: "", bdStat: 0, bdMachine: "", bdOff: {}, bdOn: {} };
  var tip = document.getElementById("tip");

  function el(tag, attrs, text) {
    var e = document.createElement(tag);
    for (var k in (attrs || {})) e.setAttribute(k, attrs[k]);
    if (text != null) e.textContent = text;
    return e;
  }
  function sv(tag, attrs) {
    var e = document.createElementNS(NS, tag);
    for (var k in (attrs || {})) e.setAttribute(k, attrs[k]);
    return e;
  }
  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]; });
  }
  function linkify(s) {
    return esc(s).replace(/#(\d+)/g, '<a href="' + REPO + '/pull/$1">#$1</a>');
  }
  function fmt(v) {
    if (v == null) return "n/a";
    return Math.abs(v) >= 100 ? String(Math.round(v)) : String(Math.round(v * 10) / 10);
  }
  function short(c) { return c.slice(0, 8); }
  // Commits are ordered by upstream history (D.commits is in that order), never by the date a run was made:
  // a commit benchmarked late (a backfill) still sits where it belongs. cdate is the commit's own date.
  function cdate(c) { return c.commit_date || c.date; }
  function crank(c) { return D.commits.indexOf(c); }
  function targetOf(c) { return D.targets.filter(function (t) { return t.commit === c.target; })[0]; }
  // Version tag of a release target, e.g. v0.16.0; "" for a daily target or an off-target commit.
  function ctag(c) {
    var t = targetOf(c);
    return t && t.kind === "release" && /^v?\d/.test(t.label) ? t.label : "";
  }
  // Dropdown label: commit date, version tag if any, then the short hash.
  function clabel(c) { return cdate(c) + (ctag(c) ? " " + ctag(c) : "") + "  " + short(c.commit); }
  function mname(m) { return m.label || m.github || m.id; }
  // Platform of a machine: native = the Linux build on Linux, proton = the Windows build under Proton on Linux.
  var PLAT = { native: "Native Linux", proton: "Proton", windows: "Win", macos: "macOS" };
  var PLAT_LONG = { native: "Native Linux build", proton: "Windows build under Proton on Linux", windows: "Windows build", macos: "macOS build" };
  // Shown resolution: the screen size when the machine file gives one, else the size the benchmark rendered at; the tooltip keeps the render size.
  function mres(m) { return m.screen || m.resolution; }
  function mresTip(m) { return m.screen && m.screen !== m.resolution ? m.screen + " screen, renders at " + m.resolution : m.resolution; }
  function platLabel(m) { return PLAT[m.platform] || m.platform; }
  // Does this machine have values of an optional metric at all? (older kit versions record neither average fps nor video memory)
  function hasMetric(m, key) { return D.rows.some(function (r) { return r.machine === m.id && r.metrics[key] != null; }); }
  function anyMetric(ms, key) { return ms.some(function (m) { return hasMetric(m, key); }); }
  // The one machine the headline and the timeline numbers come from: the one named by ?m= in the address, else the maintainer, else the first selected.
  function pageMachine() {
    var sel = selectedMachines();
    return sel.filter(function (m) { return m.id === state.hm; })[0] || sel.filter(function (m) { return m.maintainer; })[0] || sel[0];
  }
  function metricsFor(mlist, list) { return list.filter(function (mt) { return !mt.optional || anyMetric(mlist, mt.key); }); }
  function mcolor(m) { return "var(--m" + (D.machines.indexOf(m) % 8) + ")"; }
  function mdot(m) { return '<span class="dot" style="background:' + mcolor(m) + '"></span>'; }
  function machineById(id) { return D.machines.filter(function (m) { return m.id === id; })[0]; }
  function selectedMachines() { return D.machines.filter(function (m) { return state.selected[m.id]; }); }
  function multi() { return D.machines.length > 1; }

  function rowsFor(m, commit, scen) {
    return D.rows.filter(function (r) {
      return r.commit === commit && r.scenario === scen && r.machine === m.id;
    }).sort(function (a, b) { return a.run - b.run; });
  }
  // Rows of one machine that count for a target (the target commit itself, else a code-identical variant).
  function targetRows(m, t, scen) {
    var rs = D.rows.filter(function (r) { return r.machine === m.id && r.scenario === scen && r.target === t.commit; });
    var ex = rs.filter(function (r) { return r.commit_full === t.commit_full; }), use = ex.length ? ex : rs;
    if (!use.length) return [];
    return use.filter(function (r) { return r.commit === use[0].commit; }).sort(function (a, b) { return a.run - b.run; });
  }
  function currentTarget() { return D.targets[D.targets.length - 1]; }
  function commitsShown() {
    var sel = selectedMachines().map(function (m) { return m.id; });
    return D.commits.filter(function (c) {
      return D.rows.some(function (r) { return r.commit === c.commit && sel.indexOf(r.machine) >= 0; });
    });
  }
  function seriesName(id) {
    var s = D.series.filter(function (x) { return x.id === id; })[0];
    return s ? s.label : id == null ? "no series" : "Series " + id;
  }
  // The runs of one (machine, commit, scenario) that the run selector wants drawn.
  function pick(rs) {
    var r1 = rs.filter(function (r) { return r.run === 1; })[0], r2 = rs.filter(function (r) { return r.run === 2; })[0];
    if (state.runMode === "run1") return r1 ? [r1] : [];
    if (state.runMode === "both") return [r1, r2].filter(Boolean);
    return r2 ? [r2] : r1 ? [r1] : [];
  }

  // One basis for every comparison on the page: the "Runs shown" setting (warm = run 2, else run 1; both = median of the runs).
  function val(m, commit, sid, key) {
    var xs = pick(rowsFor(m, commit, sid)).map(function (r) { return r.metrics[key]; }).filter(function (v) { return v != null; });
    return xs.length ? median(xs) : null;
  }
  // higher = true for metrics where a rise is good (avg fps); every other metric is lower-is-better.
  function delta(x, y, higher) {
    var d = y - x, pct = x ? d / x * 100 : null, rd = Math.round(d * 10) / 10;
    return { d: d, pct: pct, cls: rd === 0 || Math.abs(pct == null ? d : pct) < 2 ? "" : (d < 0) !== !!higher ? "good" : "bad" };
  }
  function pctFmt(p) {
    var r = Math.round(p * 10) / 10;
    return (r > 0 ? "+" : r < 0 ? "\u2212" : "") + Math.abs(r) + "%";
  }
  function dFmt(d) { var r = Math.round(d * 10) / 10; return (r > 0 ? "+" : r < 0 ? "\u2212" : "") + fmt(Math.abs(d)); }

  function showTip(html, ev) {
    tip.innerHTML = html; tip.hidden = false;
    var w = tip.offsetWidth, x = ev.clientX + 14, y = ev.clientY + 14;
    if (x + w > window.innerWidth - 8) x = ev.clientX - w - 14;
    if (y + tip.offsetHeight > window.innerHeight - 8) y = ev.clientY - tip.offsetHeight - 14;
    tip.style.left = Math.max(4, x) + "px"; tip.style.top = Math.max(4, y) + "px";
  }
  function hideTip() { tip.hidden = true; }

  function niceTicks(v) {
    if (v <= 0) v = 1;
    var raw = v / 4, p = Math.pow(10, Math.floor(Math.log10(raw))), n = raw / p;
    var st = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p, out = [];
    for (var x = 0; x < v + st * 0.999; x += st) out.push(Math.round(x / st * 1e6) / 1e6 * st);
    return out;
  }

  // Reference value for relative mode: this machine's value at its reference commit.
  function baseValue(m, scen, key, run) {
    var refs = state.ref ? D.targets.filter(function (t) { return t.commit === state.ref; }) : D.targets;
    for (var i = 0; i < refs.length; i++) {
      var rs = targetRows(m, refs[i], scen).filter(function (r) { return r.metrics[key] != null; });
      if (!rs.length) continue;
      var same = rs.filter(function (r) { return r.run === run; })[0];
      var r = same || rs[rs.length - 1];
      return r.metrics[key];
    }
    return null;
  }

  function chart(host, metric, scen, commits, labelBreaks) {
    var box = el("div", { class: "chart" });
    box.appendChild(el("h3", {}, metric.label + (state.relative ? " - % of own baseline" : "")));
    host.appendChild(box);
    var W = Math.max(240, Math.floor(box.clientWidth - 20)), H = 250;
    var m = { l: 46, r: 26, t: 14, b: 48 };
    var pw = W - m.l - m.r, ph = H - m.t - m.b;
    var svg = sv("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, role: "img",
      "aria-label": metric.label + " per benchmarked commit" });
    var n = commits.length;

    // collect points per machine and line group
    var lines = [], max = 0;
    selectedMachines().forEach(function (mc) {
      var groups = {};
      commits.forEach(function (c, i) {
        pick(rowsFor(mc, c.commit, scen)).forEach(function (r) {
          var v = r.metrics[metric.key];
          if (v == null) { var g0 = state.runMode === "both" ? r.run : 0; (groups[g0] = groups[g0] || []).push({ i: i, gap: true }); return; }
          var rel = null;
          if (state.relative) {
            var b = baseValue(mc, scen, metric.key, r.run);
            if (b == null || b === 0) return;
            rel = v / b * 100;
          }
          var shown = state.relative ? rel : v;
          if (shown > max) max = shown;
          var g = state.runMode === "both" ? r.run : 0;
          (groups[g] = groups[g] || []).push({ i: i, c: c, r: r, v: v, shown: shown, m: mc, off: !!c.off_target });
        });
      });
      Object.keys(groups).forEach(function (g) { lines.push({ m: mc, g: +g, pts: groups[g] }); });
    });
    if (state.relative && max < 110) max = 110;
    var tk = niceTicks(max);
    max = tk[tk.length - 1];
    function X(i) { return m.l + (n === 1 ? pw / 2 : (pw * i) / (n - 1)); }
    function Y(v) { return m.t + ph - (v / max) * ph; }
    for (var g = 0; g < tk.length; g++) {
      var gv = tk[g], gy = Y(gv);
      svg.appendChild(sv("line", { class: "grid", x1: m.l, x2: W - m.r, y1: gy, y2: gy }));
      var t = sv("text", { x: m.l - 5, y: gy + 4, "text-anchor": "end" });
      t.textContent = fmt(gv) + (state.relative ? "%" : ""); svg.appendChild(t);
    }
    if (state.relative) {
      var ry = Y(100);
      svg.appendChild(sv("line", { class: "ref100", x1: m.l, x2: W - m.r, y1: ry, y2: ry }));
    }
    commits.forEach(function (c, i) {
      if (i > 0 && c.series != null && commits[i - 1].series != null && c.series !== commits[i - 1].series) {
        var bx = (X(i) + X(i - 1)) / 2;
        svg.appendChild(sv("line", { class: "brk", x1: bx, x2: bx, y1: m.t, y2: m.t + ph }));
        if (labelBreaks) {
          var lt = sv("text", { class: "brklabel", x: bx + 4, y: m.t + 10 });
          lt.textContent = seriesName(c.series).toLowerCase() + " starts"; svg.appendChild(lt);
        }
      }
    });
    var step = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(pw / 62))));
    commits.forEach(function (c, i) {
      if (i % step && i !== n - 1) return;
      var tx = X(i), a = sv("text", { x: tx, y: H - 24, "text-anchor": "middle" });
      a.textContent = short(c.commit).slice(0, 7); svg.appendChild(a);
      var b = sv("text", { x: tx, y: H - 11, "text-anchor": "middle" });
      b.textContent = (c.commit_date || c.date).slice(5); svg.appendChild(b);
    });

    lines.forEach(function (ln) {
      var col = mcolor(ln.m);
      var segs = [[]];
      ln.pts.forEach(function (p) { if (p.gap) { if (segs[segs.length - 1].length) segs.push([]); } else if (!p.off) segs[segs.length - 1].push(p); });
      segs.forEach(function (on) {
        if (on.length > 1) {
          svg.appendChild(sv("polyline", { class: "ml" + (ln.g === 2 ? " dashed" : ""), style: "stroke:" + col,
            points: on.map(function (p) { return X(p.i) + "," + Y(p.shown); }).join(" ") }));
        }
      });
    });
    var guide = sv("line", { class: "guide", x1: 0, x2: 0, y1: m.t, y2: m.t + ph });
    svg.appendChild(guide);
    lines.forEach(function (ln) {
      var col = mcolor(ln.m);
      ln.pts.forEach(function (p) {
        if (p.gap) return;
        var x = X(p.i), y = Y(p.shown), mk;
        if (p.r.run === 1) mk = sv("circle", { class: "p1" + (p.off ? " off" : ""), cx: x, cy: y, r: 5.5, style: "fill:" + col });
        else mk = sv("rect", { class: "p2" + (p.off ? " off" : ""), x: x - 4.5, y: y - 4.5, width: 9, height: 9,
          transform: "rotate(45 " + x + " " + y + ")", style: "stroke:" + col });
        svg.appendChild(mk);
        var hit = sv("circle", { class: "hit", cx: x, cy: y, r: 11, tabindex: 0 });
        var html = "<b>" + esc(short(p.c.commit)) + "</b> - " + esc(p.r.date + " " + p.r.time) + "<br>" +
          (multi() ? mdot(p.m) + esc(mname(p.m)) + "<br>" : "") +
          esc(metric.label) + ": <b>" + fmt(p.v) + "</b>" +
          (state.relative ? " (" + fmt(p.shown) + "% of own baseline)" : "") +
          "<br>run " + p.r.run + ", " + esc(seriesName(p.r.series)) +
          (p.off ? "<br><i>off-target commit: not a benchmark target</i>" : "");
        function gOn() { guide.setAttribute("x1", x); guide.setAttribute("x2", x); guide.classList.add("on"); }
        function gOff() { guide.classList.remove("on"); }
        hit.addEventListener("mouseenter", gOn);
        hit.addEventListener("mousemove", function (ev) { showTip(html, ev); });
        hit.addEventListener("mouseleave", function () { hideTip(); gOff(); });
        hit.addEventListener("focus", function () {
          gOn(); var b = hit.getBoundingClientRect(); showTip(html, { clientX: b.left, clientY: b.top });
        });
        hit.addEventListener("blur", function () { hideTip(); gOff(); });
        var ti = sv("title");
        ti.textContent = (multi() ? mname(p.m) + " " : "") + short(p.c.commit) + " run " + p.r.run + ": " + fmt(p.v) + metric.unit;
        hit.appendChild(ti);
        svg.appendChild(hit);
      });
    });
    box.appendChild(svg);
  }

  // Only what appears in the data: machines (when several), the run markers when both kinds are drawn, relative baseline, off-target.
  function legend(commits) {
    var used = {}, off = false;
    selectedMachines().forEach(function (m) {
      commits.forEach(function (c) {
        Object.keys(D.scenarios).forEach(function (sid) {
          pick(rowsFor(m, c.commit, sid)).forEach(function (r) { used[r.run] = 1; if (c.off_target) off = true; });
        });
      });
    });
    var h = "";
    if (multi()) selectedMachines().forEach(function (m) { h += "<span>" + mdot(m) + esc(mname(m)) + "</span>"; });
    if (used[1] && used[2]) {
      h += '<span><svg width="16" height="14"><circle cx="8" cy="7" r="4.5" fill="var(--text)"/></svg> run 1</span>';
      h += '<span><svg width="16" height="14"><rect x="4" y="3" width="8" height="8" transform="rotate(45 8 7)" fill="var(--panel)" stroke="var(--text)" stroke-width="2"/></svg> run 2</span>';
    }
    if (state.relative) h += '<span><svg width="22" height="14"><line x1="0" x2="22" y1="7" y2="7" stroke="var(--muted)" stroke-width="1.5" stroke-dasharray="2 2"/></svg> 100% = own baseline</span>';
    if (off) h += '<span title="Not a benchmark target; drawn faded and not connected">faded = off-target</span>';
    if (!h) return null;
    var d = el("div", { class: "legend" });
    d.innerHTML = h;
    return d;
  }

  // A before/after table (A = earlier commit, B = later), Change = B - A. Used by the headline's "All metrics" and by Compare, so both agree.
  function diffTable(m, ca, cb, sid) {
    var hasA = pick(rowsFor(m, ca.commit, sid)).length, hasB = pick(rowsFor(m, cb.commit, sid)).length;
    if (!hasA || !hasB) return null;
    var t = el("table", { class: "cmp" });
    t.innerHTML = "<thead><tr><th>Metric</th><th>" + esc(short(ca.commit)) + "</th><th>" + esc(short(cb.commit)) + "</th><th>Change</th><th>%</th></tr></thead>";
    var tb = el("tbody");
    metricsFor([m], ALLM).forEach(function (mt) {
      var x = val(m, ca.commit, sid, mt.key), y = val(m, cb.commit, sid, mt.key), tr = el("tr");
      tr.appendChild(el("td", {}, mt.label));
      tr.appendChild(el("td", {}, x == null ? "n/a" : fmt(x)));
      tr.appendChild(el("td", {}, y == null ? "n/a" : fmt(y)));
      if (x == null || y == null) { tr.appendChild(el("td", {}, "")); tr.appendChild(el("td", {}, "")); }
      else {
        var dl = delta(x, y, mt.higher);
        tr.appendChild(el("td", { class: dl.cls }, dFmt(dl.d)));
        tr.appendChild(el("td", { class: dl.cls }, dl.pct == null ? "n/a" : pctFmt(dl.pct)));
      }
      tb.appendChild(tr);
    });
    t.appendChild(tb);
    var w = el("div", { class: "tablewrap" }); w.appendChild(t);
    return w;
  }

  // ---- Frame-time breakdown: the stages of the game's F12 profiler (CPU = render thread, GPU = timestamp queries) ----
  function bdStages(kind) {
    var st = D.stages, out = st.order.filter(function (s) { return kind === "cpu" ? st.gpu_only.indexOf(s) < 0 : st.cpu_only.indexOf(s) < 0; });
    if (kind === "gpu") out.push("other");
    out.push("total");
    return out;
  }
  function bdColor(kind, s) {
    if (s === "total") return "var(--text)";
    var i = D.stages.order.indexOf(s); if (s === "other") i = D.stages.order.length;
    return "hsl(" + Math.round((i * 137.5) % 360) + ",var(--bs),var(--bl))";
  }
  function bdRows(mc, c, scen) {
    return pick(rowsFor(mc, c.commit, scen)).filter(function (r) { return r.breakdown; });
  }
  // The 5 stages whose value changed most between the last two benchmarked commits (or the biggest ones when there is only one).
  function bdMovers(kind, scen, commits, mc) {
    var have = commits.filter(function (c) { return bdRows(mc, c, scen).length; });
    var cur = have[have.length - 1], prev = have[have.length - 2];
    if (!cur) return [];
    function v(c, s) { var r = bdRows(mc, c, scen)[0], d = r && r.breakdown[kind]; return d && d[s] ? d[s][state.bdStat] : null; }
    return bdStages(kind).filter(function (s) { return s !== "total" && v(cur, s) != null; }).map(function (s) {
      var a = v(cur, s), b = prev ? v(prev, s) : 0;
      return { s: s, m: Math.abs(a - (b == null ? 0 : b)) };
    }).sort(function (x, y) { return y.m - x.m; }).slice(0, 5).map(function (x) { return x.s; });
  }
  // A stage is drawn when the user turned it on, or by default when it is the total or one of the top movers.
  function bdVisible(kind, s, movers) {
    var k = kind + "/" + s;
    if (state.bdOn[k]) return true;
    if (state.bdOff[k]) return false;
    return s === "total" || movers.indexOf(s) >= 0;
  }
  function bdChart(host, kind, scen, commits, mc, movers) {
    var title = kind === "cpu" ? "CPU time by stage (render thread, includes driver waits)" : "GPU time by stage";
    var box = el("div", { class: "chart" });
    box.appendChild(el("h3", {}, title + " - " + (state.bdStat ? "p99" : "median") + ", ms"));
    host.appendChild(box);
    var W = Math.max(240, Math.floor(box.clientWidth - 20)), H = 280, m = { l: 46, r: 26, t: 14, b: 48 };
    var pw = W - m.l - m.r, ph = H - m.t - m.b, n = commits.length;
    var svg = sv("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, role: "img", "aria-label": title + " per benchmarked commit" });
    var stages = bdStages(kind).filter(function (s) { return bdVisible(kind, s, movers); });
    var lines = [], max = 0;
    stages.forEach(function (s) {
      var groups = {};
      commits.forEach(function (c, i) {
        bdRows(mc, c, scen).forEach(function (r) {
          var d = r.breakdown[kind]; if (!d || !d[s]) return;
          var v = d[s][state.bdStat]; if (v > max) max = v;
          var g = state.runMode === "both" ? r.run : 0;
          (groups[g] = groups[g] || []).push({ i: i, c: c, r: r, v: v, off: !!c.off_target });
        });
      });
      Object.keys(groups).forEach(function (g) { lines.push({ s: s, g: +g, pts: groups[g] }); });
    });
    var tk = niceTicks(max); max = tk[tk.length - 1];
    function X(i) { return m.l + (n === 1 ? pw / 2 : (pw * i) / (n - 1)); }
    function Y(v) { return m.t + ph - (v / max) * ph; }
    tk.forEach(function (gv) {
      var gy = Y(gv);
      svg.appendChild(sv("line", { class: "grid", x1: m.l, x2: W - m.r, y1: gy, y2: gy }));
      var t = sv("text", { x: m.l - 5, y: gy + 4, "text-anchor": "end" }); t.textContent = fmt(gv); svg.appendChild(t);
    });
    commits.forEach(function (c, i) {
      if (i > 0 && c.series != null && commits[i - 1].series != null && c.series !== commits[i - 1].series) {
        var bx = (X(i) + X(i - 1)) / 2;
        svg.appendChild(sv("line", { class: "brk", x1: bx, x2: bx, y1: m.t, y2: m.t + ph }));
      }
    });
    var step = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(pw / 62))));
    commits.forEach(function (c, i) {
      if (i % step && i !== n - 1) return;
      var a = sv("text", { x: X(i), y: H - 24, "text-anchor": "middle" }); a.textContent = short(c.commit).slice(0, 7); svg.appendChild(a);
      var b = sv("text", { x: X(i), y: H - 11, "text-anchor": "middle" }); b.textContent = (c.commit_date || c.date).slice(5); svg.appendChild(b);
    });
    lines.forEach(function (ln) {
      var on = ln.pts.filter(function (p) { return !p.off; });
      if (on.length > 1) svg.appendChild(sv("polyline", { class: "bl" + (ln.s === "total" ? " tot" : "") + (ln.g === 2 ? " dashed" : ""),
        style: "stroke:" + bdColor(kind, ln.s), points: on.map(function (p) { return X(p.i) + "," + Y(p.v); }).join(" ") }));
    });
    lines.forEach(function (ln) {
      ln.pts.forEach(function (p) {
        var x = X(p.i), y = Y(p.v);
        svg.appendChild(sv("circle", { class: "bp" + (p.off ? " off" : ""), cx: x, cy: y, r: 2.5, style: "fill:" + bdColor(kind, ln.s) }));
        var hit = sv("circle", { class: "hit", cx: x, cy: y, r: 8 });
        var html = "<b>" + esc(short(p.c.commit)) + "</b> - " + esc(p.r.date + " " + p.r.time) + "<br>" + kind.toUpperCase() + " " + esc(ln.s) +
          ": <b>" + fmt(p.v) + " ms</b> (" + (state.bdStat ? "p99" : "median") + ")<br>run " + p.r.run;
        hit.addEventListener("mousemove", function (ev) { showTip(html, ev); });
        hit.addEventListener("mouseleave", hideTip);
        svg.appendChild(hit);
      });
    });
    box.appendChild(svg);
  }
  function bdSection(sec, scen, commits) {
    var avail = D.machines.filter(function (mc) { return state.selected[mc.id] && D.rows.some(function (r) { return r.machine === mc.id && r.scenario === scen && r.breakdown; }); });
    var box = el("div", { class: "bd" });
    box.appendChild(el("h3", {}, D.scenarios[scen].title));
    sec.appendChild(box);
    if (!avail.length) { box.appendChild(el("p", { class: "muted" }, "No stage timings for the selected machines.")); return; }
    var mc = avail.filter(function (x) { return x.id === state.bdMachine; })[0] || avail[0];
    var nobd = selectedMachines().filter(function (x) { return avail.indexOf(x) < 0; });
    if (nobd.length) box.appendChild(el("p", { class: "muted small" }, "No stage timings for: " + nobd.map(mname).join(", ") + " (results from an older kit, without them)."));
    var tb = el("div", { class: "toolbar" });
    if (avail.length > 1) {
      var ms = el("select"); avail.forEach(function (x) { var o = el("option", { value: x.id }, mname(x)); if (x === mc) o.selected = true; ms.appendChild(o); });
      ms.addEventListener("change", function () { state.bdMachine = ms.value; renderScenarios(); });
      tb.appendChild(ms);
    }
    [["Median", 0], ["p99", 1]].forEach(function (o) {
      var lab = el("label", { class: "radio" }), r = el("input", { type: "radio", name: "bdstat-" + scen, "data-key": "bdstat/" + scen + "/" + o[1] });
      r.checked = state.bdStat === o[1];
      r.addEventListener("change", function () { state.bdStat = o[1]; renderScenarios(); });
      lab.appendChild(r); lab.appendChild(document.createTextNode(" " + o[0] + " ")); tb.appendChild(lab);
    });
    box.appendChild(tb);
    var mv = {};
    var lg = el("div", { class: "legend bdlegend" });
    ["cpu", "gpu"].forEach(function (kind) {
      var grp = el("div", { class: "bdgrp" });
      grp.appendChild(el("b", {}, kind.toUpperCase() + ": "));
      mv[kind] = bdMovers(kind, scen, commits, mc);
      var all = !!state.bdAll[scen];
      bdStages(kind).filter(function (s) { return all || bdVisible(kind, s, mv[kind]); }).forEach(function (s) {
        var on = bdVisible(kind, s, mv[kind]), b = el("button", { type: "button", class: "bdchip" + (on ? "" : " off"), "aria-pressed": on ? "true" : "false",
          "data-key": "bd/" + scen + "/" + kind + "/" + s, title: "show / hide this " + kind.toUpperCase() + " line" });
        b.innerHTML = '<svg width="16" height="8"><line x1="0" x2="16" y1="4" y2="4" stroke-width="3" style="stroke:' + bdColor(kind, s) + '"/></svg>' + esc(s);
        b.addEventListener("click", function () {
          var k = kind + "/" + s;
          if (on) { delete state.bdOn[k]; state.bdOff[k] = 1; } else { delete state.bdOff[k]; state.bdOn[k] = 1; }
          renderScenarios();
        });
        grp.appendChild(b);
      });
      lg.appendChild(grp);
    });
    var more = el("button", { type: "button", class: "linkbtn", "data-key": "bdall/" + scen }, state.bdAll[scen] ? "Top movers only" : "All stages");
    more.addEventListener("click", function () { state.bdAll[scen] = !state.bdAll[scen]; renderScenarios(); });
    lg.appendChild(more);
    box.appendChild(lg);
    var grid = el("div", { class: "charts" });
    box.appendChild(grid);
    bdChart(grid, "cpu", scen, commits, mc, mv.cpu);
    bdChart(grid, "gpu", scen, commits, mc, mv.gpu);
  }

  // Rebuilding the charts empties both sections for a moment, which made the browser jump; keep the scroll position and the focused control.
  function renderScenarios() {
    var y = window.scrollY, a = document.activeElement, fk = a && a.getAttribute ? a.getAttribute("data-key") : null;
    renderScenariosNow();
    window.scrollTo(window.scrollX, y);
    if (fk) { var f = document.querySelector('[data-key="' + fk + '"]'); if (f) f.focus({ preventScroll: true }); }
  }

  function renderScenariosNow() {
    var host = document.getElementById("scenarios"), bhost = document.getElementById("breakdown");
    host.innerHTML = ""; bhost.innerHTML = "";
    var commits = commitsShown();
    if (!commits.length) { host.appendChild(el("p", { class: "box muted" }, "No machine selected.")); return; }
    var lg = legend(commits);
    if (lg) host.appendChild(lg);
    var first = true;
    Object.keys(D.scenarios).forEach(function (sid) {
      var sc = D.scenarios[sid];
      var sec = el("section", { class: "scen" });
      sec.appendChild(el("h3", { class: "scen-h", title: sc.subtitle }, sc.title));
      var grid = el("div", { class: "charts" });
      sec.appendChild(grid);
      host.appendChild(sec);
      metricsFor(selectedMachines(), METRICS).filter(function (mt) { return mt.primary || state.more; }).forEach(function (mt) { chart(grid, mt, sid, commits, first); first = false; });
      var bsec = el("section", { class: "scen" });
      bhost.appendChild(bsec);
      bdSection(bsec, sid, commits);
    });
  }

  // Headline: the newest benchmarked commit against the one before it (upstream order), for the primary selected machine.
  // Tiles show the change first; every number uses the same basis as Compare (see val()).
  var TILES = [["p99_ms", "p99 frame time", " ms"], ["over33_pct", "Frames over 33 ms", "%"], ["slow_world_p99_ms", "Slow-world tick p99", " ms"], ["avg_fps", "Average fps", " fps", true], ["vram_peak_mb", "Peak video memory", " MB"]];
  function renderHeadline() {
    var host = document.getElementById("headline");
    host.innerHTML = "";
    var commits = commitsShown(), sel = selectedMachines();
    var mc = pageMachine();
    if (!mc) return;
    var shown = commits.filter(function (c) { return D.rows.some(function (r) { return r.commit === c.commit && r.machine === mc.id; }); });
    var cur = shown[shown.length - 1], prev = shown[shown.length - 2];
    if (!cur) return;
    function sha(c) { return '<a class="sha" title="' + esc(cdate(c)) + '" href="' + REPO + "/commit/" + esc(c.commit_full) + '">' + esc(c.commit) + "</a>"; }
    var cap = el("p", { class: "hl-sub" });
    cap.innerHTML = (prev ? sha(prev) + " \u2192 " : "") + sha(cur) + '<span class="muted small"> ' + (prev ? esc(cdate(prev)) + " \u2192 " : "") + esc(cdate(cur)) +
      (multi() ? " &middot; " + esc(mname(mc)) : "") + "</span>";
    host.appendChild(cap);
    var strip = el("div", { class: "hl" });
    Object.keys(D.scenarios).forEach(function (sid) {
      var card = el("div", { class: "box hlcard" });
      card.appendChild(el("h3", {}, D.scenarios[sid].title));
      var hasB = pick(rowsFor(mc, cur.commit, sid)).length;
      if (!hasB) { card.appendChild(el("p", { class: "muted" }, "Not measured for this commit.")); strip.appendChild(card); return; }
      var tiles = el("div", { class: "tiles" }), verdict = el("p", { class: "verdict" });
      var pa = prev ? val(mc, prev.commit, sid, "p99_ms") : null, pb = val(mc, cur.commit, sid, "p99_ms");
      if (pa == null || pb == null) verdict.textContent = prev ? "No p99 to compare." : "No previous commit to compare.";
      else {
        var dl = delta(pa, pb);
        verdict.className = "verdict " + dl.cls;
        verdict.textContent = (dl.cls === "" ? "No clear change" : dl.d < 0 ? "Faster" : "Slower") + ": p99 " + (dl.pct == null ? dFmt(dl.d) : pctFmt(dl.pct));
      }
      card.appendChild(verdict);
      TILES.filter(function (t) { return (t[0] !== "avg_fps" && t[0] !== "vram_peak_mb") || hasMetric(mc, t[0]); }).forEach(function (t) {
        var v = val(mc, cur.commit, sid, t[0]), pv = prev ? val(mc, prev.commit, sid, t[0]) : null, big = "", cls = "", small = "";
        if (v != null && pv != null) {
          var d2 = delta(pv, v, t[3]);
          cls = d2.cls; big = d2.pct == null ? dFmt(d2.d) : pctFmt(d2.pct);
          small = fmt(pv) + " \u2192 " + fmt(v) + t[2];
        } else small = (v == null ? "n/a" : fmt(v) + t[2]);
        var tile = el("div", { class: "tile" });
        tile.innerHTML = '<div class="l">' + esc(t[1]) + '</div><div class="d ' + cls + '">' + esc(big || "\u2013") + '</div><div class="v">' + esc(small) + "</div>";
        tiles.appendChild(tile);
      });
      card.appendChild(tiles);
      strip.appendChild(card);
    });
    host.appendChild(strip);
    var ch = cur.changes;
    if (prev && ch && ch.bucket_counts) {
      var bc = ch.bucket_counts, p = bc["Performance"] || 0, g = bc["Rendering/graphics"] || 0;
      var why = el("p", { class: "muted small" });
      why.innerHTML = "Likely cause: " + (p + g ? '<a href="#tl-' + esc(cur.commit) + '">' + p + " Performance and " + g + " Rendering/graphics commits</a> in this range." : "no Performance or Rendering/graphics commits in this range.");
      host.appendChild(why);
    }
    var det = el("details", { class: "allm" });
    det.appendChild(el("summary", {}, "All metrics"));
    if (prev) {
      if (prev.series !== cur.series) det.appendChild(el("p", { class: "caveat" }, "The two commits are in different series (" + seriesName(prev.series) + " vs " + seriesName(cur.series) + "), so the procedure differs."));
      Object.keys(D.scenarios).forEach(function (sid) {
        var t = diffTable(mc, prev, cur, sid);
        if (!t) return;
        det.appendChild(el("h3", { class: "cmp-h" }, D.scenarios[sid].title));
        det.appendChild(t);
      });
    }
    host.appendChild(det);
  }

  // Latest benchmark target that at least two selected machines ran. Off-target commits are not compared.
  function renderSameCommit() {
    var host = document.getElementById("samecommit");
    host.innerHTML = ""; host.hidden = true;
    var sel = selectedMachines();
    if (sel.length < 2) return;
    var found = null;
    for (var i = D.targets.length - 1; i >= 0 && !found; i--) {
      var t = D.targets[i];
      var ms = sel.filter(function (m) { return t.machines.indexOf(m.id) >= 0; });
      if (ms.length > 1) found = { t: t, ms: ms };
    }
    if (!found) return;
    host.hidden = false;
    var h2 = el("h2", {}, "Same commit, different machines ");
    h2.appendChild(el("span", { class: "q", tabindex: "0", title: "Different GPU, CPU, resolution or platform change every number, so read this as a hardware comparison, not a code comparison. A commit that is code-identical to the target counts for it." }, "(?)"));
    host.appendChild(h2);
    var p = el("p", { class: "muted small" });
    p.innerHTML = '<a class="sha" href="' + REPO + "/commit/" + esc(found.t.commit_full) + '">' + esc(found.t.commit) + "</a> (" + esc(found.t.label) + "), " + esc(found.t.date);
    host.appendChild(p);
    var cols = metricsFor(found.ms, METRICS);
    Object.keys(D.scenarios).forEach(function (sid) {
      var any = found.ms.some(function (m) { return targetRows(m, found.t, sid).length; });
      if (!any) return;
      var html = "<h3>" + esc(D.scenarios[sid].title) + "</h3>" +
        '<div class="tablewrap"><table><thead><tr><th>Machine</th><th>Runs</th>';
      cols.forEach(function (mt) { html += "<th>" + esc(mt.label) + "</th>"; });
      html += "</tr></thead><tbody>";
      found.ms.forEach(function (m) {
        var all = targetRows(m, found.t, sid), rs = pick(all);
        if (!rs.length) return;
        html += '<tr><td title="' + esc(m.cpu + "; " + m.gpu + "; " + mresTip(m) + "; " + (PLAT_LONG[m.platform] || m.platform)) + '">' + mdot(m) + esc(mname(m)) +
          "</td><td>" + rs.map(function (r) { return r.run; }).join(", ") + (all[0].commit !== found.t.commit ? ' <span class="muted" title="code-identical commit ' + esc(all[0].commit) + '">*</span>' : "") + "</td>";
        cols.forEach(function (mt) {
          html += "<td>" + rs.map(function (r) { return r.metrics[mt.key] == null ? "\u2013" : fmt(r.metrics[mt.key]); }).join(" / ") + "</td>";
        });
        html += "</tr>";
      });
      html += "</tbody></table></div>";
      var box = el("div", { class: "box" }); box.innerHTML = html; host.appendChild(box);
    });
  }

  // Machines x commits: which machine ran which benchmark target. Columns are in upstream commit order; a
  // code-identical variant shares the column of its target; off-target commits get their own faded column.
  function renderCoverage() {
    var host = document.getElementById("coverage");
    var cur = currentTarget();
    var cols = [];
    D.commits.forEach(function (c) {
      var key = c.off_target || !c.target ? c.commit : c.target;
      var col = cols.filter(function (x) { return x.key === key; })[0];
      if (!col) { col = { key: key, c: c, t: c.off_target ? null : targetOf(c), off: !!c.off_target || !c.target, rank: crank(c) }; cols.push(col); }
      if (c.target_exact) col.c = c;
    });
    D.targets.forEach(function (t) {
      if (!cols.some(function (x) { return x.t === t; })) cols.push({ key: t.commit, c: null, t: t, off: false, rank: 1e9 });
    });
    cols.sort(function (x, y) { return x.rank - y.rank; });
    var html = '<div class="tablewrap"><table class="cov"><thead><tr><th>Machine</th>';
    cols.forEach(function (x) {
      var full = x.t ? x.t.commit_full : x.c.commit_full, hash = x.t ? x.t.commit : x.c.commit;
      var dt = x.c ? cdate(x.c) : x.t.date;
      html += '<th class="' + (x.t && x.t.kind === "release" ? "rel " : "") + (x.off ? "offcol" : "") + '" title="' +
        esc((x.t ? x.t.label + " (" + x.t.kind + ")" : "off-target commit, not a benchmark target") + ", commit date " + dt) + '">' +
        esc(dt.slice(5)) + '<br><a class="sha" href="' + REPO + "/commit/" + esc(full) + '">' + esc(hash) + "</a><br>" +
        (x.t ? esc(x.t.label) : "off-target") + (x.t === cur ? "<br><b>current</b>" : "") + "</th>";
    });
    html += "</tr></thead><tbody>";
    D.machines.forEach(function (m) {
      html += "<tr><td>" + mdot(m) + esc(mname(m)) + "</td>";
      cols.forEach(function (x) {
        var has, variant = false;
        if (x.off) has = D.rows.some(function (r) { return r.machine === m.id && r.commit === x.c.commit; });
        else {
          has = x.t.machines.indexOf(m.id) >= 0;
          variant = has && !D.rows.some(function (r) { return r.machine === m.id && r.commit_full === x.t.commit_full; });
        }
        html += '<td class="' + (has ? "yes" : "no") + (x.off ? " offcol" : "") + '" title="' + (has ? (variant ? "code-identical commit counts for this target" : "benchmarked") : "not benchmarked") + '">' +
          (has ? (variant ? "&#10003;*" : "&#10003;") : "&middot;") + "</td>";
      });
      html += "</tr>";
    });
    html += "</tbody></table></div>";
    host.innerHTML = html + '<p class="muted small">Upstream commit order. Bold = release target; * = code-identical commit; faded = off-target.</p>';
  }

  function renderStandard() {
    document.getElementById("standard").innerHTML = "<pre><code>cd bench/kit &amp;&amp; python3 build.py &amp;&amp; python3 bench.py --machine &lt;id&gt;</code></pre>";
  }

  function metricsTable(commit) {
    var out = "", sel = selectedMachines();
    Object.keys(D.scenarios).forEach(function (sid) {
      var block = "";
      sel.forEach(function (m) {
        rowsFor(m, commit, sid).forEach(function (r) {
          block += "<tr><td>" + (multi() ? mdot(m) + esc(mname(m)) + ", " : "") + "run " + r.run +
            (r.series === 2 ? (r.run === 1 ? " (cold?)" : " (warm)") : "") +
            (r.note ? ' <span class="muted">(' + esc(r.note) + ")</span>" : "") + "</td><td>" +
            fmt(r.metrics.over33_pct) + "%</td><td>" + fmt(r.metrics.p50_ms) + "</td><td>" + fmt(r.metrics.p99_ms) +
            "</td><td>" + fmt(r.metrics.slow_world_p99_ms) + "</td><td>" + esc(r.date + " " + r.time) + "</td></tr>";
        });
      });
      if (block) out += "<tr><th colspan=\"6\" style=\"text-align:left\">" + esc(D.scenarios[sid].title) + "</th></tr>" + block;
    });
    return '<div class="tablewrap"><table><thead><tr><th>Run</th><th>&gt;33 ms</th><th>p50 ms</th><th>p99 ms</th><th>slow-world p99 ms</th><th>when</th></tr></thead><tbody>' +
      out + "</tbody></table></div>";
  }

  // p99 on the page's basis (see val()) of the page machine only: machines are never mixed in one number.
  function pageP99(commit, sid) {
    var m = pageMachine();
    return m ? val(m, commit, sid, "p99_ms") : null;
  }
  var SCEN_SHORT = { play: "play", bases: "base switch" };
  function summaryLine(c, prev) {
    var parts = [];
    Object.keys(D.scenarios).forEach(function (key) {
      var sc = D.scenarios[key], sid = key;
      var v = pageP99(c.commit, sid);
      if (v == null) return;
      var txt = (SCEN_SHORT[sid] || sc.title.toLowerCase()) + " p99 " + fmt(v) + " ms";
      var pv = prev && prev.series === c.series ? pageP99(prev.commit, sid) : null;
      if (pv != null && pv > 0) {
        var dl = delta(pv, v), cls = dl.cls || "tl-flat";
        txt += ' <span class="tl-d ' + cls + '" title="vs ' + esc(prev.commit) + ' (' + esc(fmt(pv)) + ' ms)">' + pctFmt(dl.pct) + "</span>";
      }
      parts.push('<span class="tl-m">' + txt + "</span>");
    });
    return parts.join("");
  }

  function renderTimeline() {
    var host = document.getElementById("timeline");
    host.innerHTML = "";
    var tq = document.getElementById("tlq"), pm = pageMachine();
    if (tq) tq.title = "From upstream commit messages, unreviewed." + (multi() && pm ? " Numbers: " + mname(pm) + "." : "");
    var asc = commitsShown();
    var list = asc.slice().reverse();
    var LIMIT = 5;
    list.forEach(function (c, idx) {
      var prev = asc[asc.indexOf(c) - 1] || null;
      var box = el("details", { class: "tl", id: "tl-" + c.commit });
      if (idx === 0) box.open = true;
      if (idx >= LIMIT) { box.className += " tl-old"; box.hidden = !state.tlAll; }
      var link = c.commit_full ? REPO + "/commit/" + c.commit_full : null;
      var ch = c.changes;
      var tg = D.targets.filter(function (t) { return t.commit === c.target; })[0];
      var tinfo = c.off_target ? "off-target: not a benchmark target"
        : tg ? (c.target_exact ? "benchmark target" : "counts for target " + tg.commit) + " (" + tg.label + ")" : "";
      var sum = '<summary class="tl-sum"><span class="sha"' + (tinfo ? ' title="' + esc(tinfo) + '"' : "") + ">" + esc(c.commit) + "</span>" +
        '<span class="when">' + esc(cdate(c)) + (ctag(c) ? " " + esc(ctag(c)) : "") + "</span>" +
        summaryLine(c, prev) +
        (ch ? '<span class="tl-n" title="since ' + esc(ch.since) + '">' + ch.commit_count + (ch.commit_count === 1 ? " commit" : " commits") + "</span>" : "") +
        (c.off_target ? '<span class="chip off">off-target</span>' : "") + "</summary>";
      var html = sum + '<div class="tl-body">';
      if (link) html += '<p class="small"><a href="' + link + '">Commit on GitHub</a></p>';
      html += '<details class="runs"><summary class="small">Run details</summary>' + metricsTable(c.commit) + "</details>";
      if (c.first_benchmark) {
        html += '<p class="muted">First benchmarked commit: no change list.</p>';
      } else if (!ch) {
        html += '<p class="muted">Change list unavailable for this commit.</p>';
      } else {
        if (ch.merged_prs.length) {
          html += "<details><summary>Merged pull requests (" + ch.merged_prs.length + ")</summary><ul>";
          ch.merged_prs.forEach(function (p) {
            html += '<li><a href="' + REPO + "/pull/" + p.number + '">#' + p.number + "</a> " +
              (p.title ? esc(p.title) : "") + (p.author ? ' <span class="muted">by ' + esc(p.author) + "</span>" : "") + "</li>";
          });
          html += "</ul></details>";
        }
        D.buckets.forEach(function (b) {
          var l = ch.commits.filter(function (x) { return x.bucket === b; });
          if (!l.length) return;
          html += "<details" + (b === "Performance" || b === "Rendering/graphics" ? " open" : "") + "><summary>" + esc(b) + " (" + l.length + ")</summary><ul>";
          l.forEach(function (x) {
            html += '<li><a class="sha" href="' + REPO + "/commit/" + x.hash + '">' + esc(x.short) + "</a> " + linkify(x.subject) + "</li>";
          });
          html += "</ul></details>";
        });
      }
      box.innerHTML = html + "</div>";
      host.appendChild(box);
    });
    if (list.length > LIMIT) {
      var btn = el("button", { class: "tl-more", type: "button" });
      var upd = function () { btn.textContent = state.tlAll ? "Show fewer" : "Show older (" + (list.length - LIMIT) + ")"; };
      upd();
      btn.addEventListener("click", function () {
        state.tlAll = !state.tlAll; upd();
        Array.prototype.forEach.call(host.querySelectorAll(".tl-old"), function (n) { n.hidden = !state.tlAll; });
      });
      host.appendChild(btn);
    }
  }

  function renderSeries() {
    var ul = document.getElementById("series-list"); ul.innerHTML = "";
    D.series.forEach(function (s) {
      var li = el("li");
      li.innerHTML = "<b>" + esc(s.label) + "</b> from " + esc(s.start.slice(0, 10)) + ": " + esc(s.description);
      ul.appendChild(li);
    });
  }

  // Commit date of the newest (by upstream order) commit this machine benchmarked.
  function lastCommitDate(m) {
    var last = null;
    D.commits.forEach(function (c) { if (D.rows.some(function (r) { return r.commit === c.commit && r.machine === m.id; })) last = c; });
    return last ? cdate(last) : m.last_date;
  }
  function renderMachines() {
    var host = document.getElementById("machines");
    host.innerHTML = "";
    D.machines.forEach(function (m) {
      var card = el("label", { class: "mcard" + (multi() ? "" : " compact"), title: m.notes || "" });
      var cb = el("input", { type: "checkbox" });
      cb.checked = !!state.selected[m.id];
      if (!multi()) cb.hidden = true;
      cb.addEventListener("change", function () {
        state.selected[m.id] = cb.checked;
        if (!selectedMachines().length) { state.selected[m.id] = true; cb.checked = true; }
        renderResults();
      });
      card.appendChild(cb);
      var body = el("div", { class: "mbody" });
      var head = (multi() ? mdot(m) : "") + '<b>' + esc(mname(m)) + "</b> " + (m.github ? '<a href="https://github.com/' + encodeURIComponent(m.github) + '" title="GitHub profile" aria-label="GitHub profile of ' + esc(m.github) + '">&#8599;</a>' : "") +
        '<span class="muted"> \u00b7 ' + esc([m.cpu, m.gpu, m.os, mres(m)].join(" \u00b7 ")) + "</span>";
      body.innerHTML = '<div class="mtitle">' + head + "</div>" +
        '<div class="muted small">' + m.commit_count + " benchmarked commit" + (m.commit_count === 1 ? "" : "s") + ", latest " + esc(lastCommitDate(m)) + "</div>";
      card.appendChild(body);
      host.appendChild(card);
    });
  }

  // Machine chips next to the run controls: one checkbox per machine (the cards in "Machines" below do the same); at least one stays selected.
  function renderPicker() {
    var host = document.getElementById("mpick");
    if (!host) return;
    host.innerHTML = "";
    D.machines.forEach(function (m) {
      var l = el("label", { class: "chip mchip", title: m.cpu + "; " + m.gpu + "; " + mresTip(m) + "; " + (PLAT_LONG[m.platform] || m.platform) }), cb = el("input", { type: "checkbox" });
      cb.checked = !!state.selected[m.id];
      cb.addEventListener("change", function () {
        state.selected[m.id] = cb.checked;
        if (!selectedMachines().length) { state.selected[m.id] = true; cb.checked = true; }
        renderResults();
      });
      l.appendChild(cb);
      l.insertAdjacentHTML("beforeend", " " + mdot(m) + esc(mname(m)));
      host.appendChild(l);
    });
  }

  function renderControls() {
    var host = document.getElementById("controls");
    host.innerHTML = "";
    var row = el("div", { class: "ctlrow" });
    var seg = el("div", { class: "seg", role: "group", "aria-label": "Runs shown", title: "Warm = run 2 where present, else run 1. Applies to every number on the page." });
    [["warm", "Warm"], ["run1", "Run 1"], ["both", "Both"]].forEach(function (o) {
      var b = el("button", { type: "button", "aria-pressed": state.runMode === o[0] ? "true" : "false" }, o[1]);
      b.addEventListener("click", function () { state.runMode = o[0]; renderControls(); renderResults(); });
      seg.appendChild(b);
    });
    row.appendChild(seg);
    var mb = el("button", { type: "button", class: "seg-btn", "aria-pressed": state.more ? "true" : "false" }, "More metrics");
    mb.addEventListener("click", function () { state.more = !state.more; renderControls(); renderScenarios(); });
    row.appendChild(mb);
    host.appendChild(row);

    if (multi()) {
      row.appendChild(el("span", { class: "mpick", id: "mpick" }));
      var rb = el("button", { type: "button", class: "seg-btn", "aria-pressed": state.relative ? "true" : "false",
        title: "100% is the machine's own value at the baseline commit, so trends can be compared across different hardware. Only benchmark targets can be a baseline." }, "Relative to own baseline");
      rb.addEventListener("click", function () { state.relative = !state.relative; renderControls(); renderResults(); });
      row.appendChild(rb);
      if (state.relative) {
        var sel = el("select", { "aria-label": "Baseline commit" });
        sel.appendChild(el("option", { value: "" }, "each machine's first target"));
        D.targets.forEach(function (t) { sel.appendChild(el("option", { value: t.commit }, short(t.commit) + " " + t.label)); });
        sel.value = state.ref;
        sel.addEventListener("change", function () { state.ref = sel.value; renderResults(); });
        row.appendChild(sel);
      }
      row.appendChild(el("span", { class: "q", id: "cavq", tabindex: "0", hidden: "" }, "(?)"));
    }
    renderPicker();
  }

  function renderCaveat() {
    var q = document.getElementById("cavq"), sel = selectedMachines();
    if (!q) return;
    q.hidden = true;
    if (sel.length < 2 || state.relative) return;
    var res = {}, gpu = {}, plat = {};
    sel.forEach(function (m) { res[m.resolution] = 1; gpu[m.gpu] = 1; plat[m.platform] = 1; });
    var diff = [];
    if (Object.keys(gpu).length > 1) diff.push("GPUs");
    if (Object.keys(res).length > 1) diff.push("resolutions");
    if (Object.keys(plat).length > 1) diff.push("platforms");
    q.title = "Absolute values of machines with different " + (diff.length ? diff.join(" and ") : "CPUs or drivers") +
      " are not comparable (a faster GPU at a lower resolution is not a code improvement). Use \"Relative to own baseline\" to compare trends.";
    q.hidden = false;
  }

  // ---- Compare two commits ----
  var cmp = { a: "", b: "", m: "" };
  function median(xs) {
    var v = xs.slice().sort(function (x, y) { return x - y; }), n = v.length;
    return n % 2 ? v[(n - 1) / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
  }
  function cmpCommits(mid) {
    return D.commits.filter(function (c) {
      return D.rows.some(function (r) { return r.commit === c.commit && r.machine === mid; });
    }).sort(function (x, y) { return crank(y) - crank(x); });   // newest first, by upstream commit order
  }
  // Distance in days is between commit dates, not between the dates the benchmarks were run.
  function cmpTime(c) { return Date.parse(cdate(c) + "T00:00:00Z"); }
  // Age of a commit in whole days from today (UTC dates), for the compare pickers.
  function daysAgo(c) {
    var now = new Date(), today = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
    var n = Math.round((today - cmpTime(c)) / 86400000);
    return n <= 0 ? "today" : n === 1 ? "1 day ago" : n + " days ago";
  }
  // The "before" commit for a given "after" commit: the newest older commit whose commit date is at least a day before it; else the previous commit.
  function cmpOneDay(list, a) {
    var idx = list.indexOf(a), older = list.slice(idx + 1);
    for (var i = 0; i < older.length; i++) if (cmpTime(a) - cmpTime(older[i]) >= 86400000) return older[i];
    return older[0] || a;
  }
  function cmpDefaults(list) {
    if (!list.length) return;
    cmp.b = list[0].commit; cmp.a = cmpOneDay(list, list[0]).commit;   // A = before (older), B = after (newer)
  }
  function cmpRows(mid, commit, scen) {
    return D.rows.filter(function (r) { return r.machine === mid && r.commit === commit && r.scenario === scen; });
  }
  function cmpUrl() {
    var q = [];
    if (cmp.a) q.push("a=" + encodeURIComponent(cmp.a));
    if (cmp.b) q.push("b=" + encodeURIComponent(cmp.b));
    if (multi() && cmp.m) q.push("m=" + encodeURIComponent(cmp.m));
    try { history.replaceState(null, "", q.length ? "?" + q.join("&") + "#compare-sec" : location.pathname); } catch (e) {}
  }
  function cmpReadUrl() {
    var p = new URLSearchParams(location.search);
    var m = p.get("m");
    state.hm = m && machineById(m) ? m : "";
    cmp.m = m && machineById(m) ? m : (D.machines.filter(function (x) { return x.maintainer; })[0] || D.machines[0]).id;
    var list = cmpCommits(cmp.m);
    cmpDefaults(list);
    function find(h) {
      if (!h) return null;
      return list.filter(function (c) { return c.commit.indexOf(h) === 0 || c.commit_full.indexOf(h) === 0; })[0];
    }
    var a = find(p.get("a")), b = find(p.get("b"));
    if (a) cmp.a = a.commit;
    if (b) cmp.b = b.commit;
  }
  function renderCompare() {
    var sec = document.getElementById("compare-sec"), host = document.getElementById("compare");
    var list = cmpCommits(cmp.m);
    if (list.length < 2) { sec.hidden = true; return; }
    sec.hidden = false;
    host.innerHTML = "";
    var bar = el("div", { class: "cmp-bar" });
    function picker(label, key) {
      var l = el("label", { class: "cmp-pick" }, label + " ");
      var s = el("select");
      list.forEach(function (c) { s.appendChild(el("option", { value: c.commit }, clabel(c) + " - " + daysAgo(c))); });
      s.value = cmp[key];
      Array.prototype.forEach.call(s.options, function (o, i) { if (o.value === cmp[key]) s.selectedIndex = i; });
      s.addEventListener("change", function () { cmp[key] = s.value; cmpUrl(); renderCompare(); });
      l.appendChild(s); return l;
    }
    if (multi()) {
      var ml = el("label", { class: "cmp-pick" }, "Machine ");
      var ms = el("select");
      D.machines.forEach(function (m) { ms.appendChild(el("option", { value: m.id }, mname(m))); });
      Array.prototype.forEach.call(ms.options, function (o, i) { if (o.value === cmp.m) ms.selectedIndex = i; });
      ms.addEventListener("change", function () { cmp.m = ms.value; cmpDefaults(cmpCommits(cmp.m)); cmpUrl(); renderCompare(); });
      ml.appendChild(ms); bar.appendChild(ml);
    }
    bar.appendChild(picker("A (before)", "a"));
    bar.appendChild(picker("B (after)", "b"));
    var quick = el("span", { class: "cmp-quick" });
    var prev = el("button", { type: "button" }, "vs previous benchmark");
    prev.addEventListener("click", function () {
      var c = list[Math.max(0, list.map(function (x) { return x.commit; }).indexOf(cmp.b)) + 1];
      if (c) { cmp.a = c.commit; cmpUrl(); renderCompare(); }
    });
    quick.appendChild(prev);
    bar.appendChild(quick);
    host.appendChild(bar);

    var ca = list.filter(function (c) { return c.commit === cmp.a; })[0], cb = list.filter(function (c) { return c.commit === cmp.b; })[0];
    if (!ca || !cb) return;
    var info = el("p", { class: "muted small" });
    info.innerHTML = 'A (before): <a href="' + REPO + "/commit/" + esc(ca.commit_full) + '"><code>' + esc(short(ca.commit)) + "</code></a> " + esc(cdate(ca)) + (ctag(ca) ? " " + esc(ctag(ca)) : "") +
      ' &nbsp; B (after): <a href="' + REPO + "/commit/" + esc(cb.commit_full) + '"><code>' + esc(short(cb.commit)) + "</code></a> " + esc(cdate(cb)) + (ctag(cb) ? " " + esc(ctag(cb)) : "") +
      ' &nbsp; <span title="Basis: ' + (state.runMode === "warm" ? "warm runs" : state.runMode === "run1" ? "run 1" : "median of both runs") + '">(' + (state.runMode === "warm" ? "warm" : state.runMode === "run1" ? "run 1" : "both runs") + ')</span> &nbsp; <a href="' + REPO + "/compare/" + esc(ca.commit_full) + "..." + esc(cb.commit_full) + '">upstream changes between them</a>';
    host.appendChild(info);
    if (ca.commit === cb.commit) host.appendChild(el("p", { class: "muted" }, "A and B are the same commit."));

    var sa = {}, sb = {};
    D.rows.forEach(function (r) {
      if (r.machine !== cmp.m) return;
      if (r.commit === ca.commit) sa[r.series] = 1;
      if (r.commit === cb.commit) sb[r.series] = 1;
    });
    var ka = Object.keys(sa), kb = Object.keys(sb);
    if (ka.length !== 1 || kb.length !== 1 || ka[0] !== kb[0]) {
      host.appendChild(el("p", { class: "caveat" }, "Warning: A is from " + ka.map(seriesName).join(", ") + " and B from " + kb.map(seriesName).join(", ") +
        ". The benchmark procedure differs between series (see \"Runs and series\"), so a difference may come from the procedure rather than from the code."));
    }

    Object.keys(D.scenarios).forEach(function (sid) {
      host.appendChild(el("h3", { class: "cmp-h" }, D.scenarios[sid].title));
      var t = diffTable(machineById(cmp.m), ca, cb, sid);
      if (t) host.appendChild(t);
      else host.appendChild(el("p", { class: "muted" }, "Not measured for one of the commits."));
    });
  }

  function renderResults() {
    renderPicker(); renderMachines(); renderCaveat(); renderCoverage(); renderSameCommit(); renderHeadline(); renderScenarios(); renderTimeline(); if (cmp.m) renderCompare();
  }

  function init(d) {
    D = d;
    D.machines.forEach(function (m) { state.selected[m.id] = true; });
    cmpReadUrl();
    renderStandard(); renderSeries(); renderControls(); renderResults();
    renderCompare();
    document.getElementById("coverage-det").open = multi();
    openHash();
    var t; window.addEventListener("resize", function () { clearTimeout(t); t = setTimeout(renderScenarios, 150); });
  }

  // A link into the folded "How this is measured" part opens it.
  function openHash() {
    var t = location.hash && document.getElementById(decodeURIComponent(location.hash.slice(1)));
    for (var e = t; e; e = e.parentElement) if (e.tagName === "DETAILS") e.open = true;
    if (t) t.scrollIntoView();
  }
  window.addEventListener("hashchange", openHash);

  // bench.json is built with the site (bench/tools/generate.py); ?v= is its content hash, set at build time, so a cached copy never meets a newer page.
  var root = document.getElementById("bench-root");
  function loadData() {
    return fetch("/bench/bench.json?v=" + encodeURIComponent(root.dataset.v)).then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); });
  }
  loadData().then(init).catch(function (e) {
    document.getElementById("scenarios").appendChild(el("p", { class: "box" },
      "Could not load /bench/bench.json (" + e + ")."));
  });
})();
