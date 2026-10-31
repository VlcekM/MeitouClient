#!/usr/bin/env python3
"""Markdown tables from play-bench run folders (used by bench.py in the dev kit; also runnable alone). Same maths as the maintainers' bench-compare-table.py.

Usage: bench_table.py <folder>...
       bench_table.py --history <csv> <out dir> <note> <gpu driver> <machine> <cpu> <ram_gb> <os> <kernel>   append one CSV row per successful run
       bench_table.py --settings-diff <kit settings json> <build settings json>   print the keys of the first that differ in the second (the game adds keys of its own on start)
  A folder is either a run folder (it holds bench/frames.csv) or a parent whose sub-folders are run folders.
  Run folders are named <case>-<n>-<build label> (e.g. native-steam-1-base, proton-gog-1-fix, native-gog-2-culprit; the older steam-2-fix, p-gog-1-base also work);
  the order of the builds in a table is the order in <out dir>/builds.txt (label, folder, commit; written by bench-compare.sh), else first seen;
  runs with the same <case> and build label are grouped and the median is taken over them.
  A <case> containing the word proton (or the letter p as a word) is a Proton run, windows / macos a native Windows / macOS run, otherwise native (Linux);
  a <case> containing gog is the unmodded copy, otherwise modded (the Steam copy).

Numbers come from bench/frames.csv, all rows except those of segment "settle":
  frames over 33 ms (%), p50 / p99 / max of frame_ms, frames over 100 ms, p99 / max of sim_slow-world (the slow-world tick, in ms).
Percentile = the sorted value at index round(p/100*(n-1)). The medians are taken per column over the runs.
One-row offset: frames.csv attributes a frame's interval to the next row (the interval holds the previous frame's tail), so the per-system columns
(sim_*, top_system*) are one frame early against frame_ms. The tables here do not pair the two per frame: frame_ms is read as it is and
sim_slow-world is read as its own distribution, as the first analysis (2026-10-27) did, as the maintainers' earlier analyses did.
"""
import csv, os, re, statistics as st, sys

def pct(a, p):
    a = sorted(a)
    return a[min(len(a) - 1, int(round(p / 100 * (len(a) - 1))))]

def run_stats(folder):
    path = os.path.join(folder, 'bench', 'frames.csv')
    if not os.path.isfile(path):
        return None, 'no bench/frames.csv (run failed or not finished)'
    try:
        rows = [r for r in csv.DictReader(open(path, encoding='utf-8', newline='')) if r['segment'] != 'settle']
        f = [float(r['frame_ms']) for r in rows]
        sw = [float(r['sim_slow-world']) for r in rows]
    except (KeyError, ValueError) as e:
        return None, f'frames.csv unreadable ({e})'
    if not f:
        return None, 'frames.csv has no rows after settle'
    return dict(n=len(f), over33=100 * sum(x > 33 for x in f) / len(f), p50=pct(f, 50), p99=pct(f, 99), mx=max(f),
                over100=sum(x > 100 for x in f), sw_p99=pct(sw, 99), sw_max=max(sw)), ''

def note_of(folder):
    p = os.path.join(folder, 'run.txt')
    return open(p, encoding='utf-8', errors='replace').read().strip().replace('\n', '; ') if os.path.isfile(p) else ''

def is_run(path):
    return os.path.isdir(os.path.join(path, 'bench')) or os.path.isfile(os.path.join(path, 'run.txt')) or re.search(r'-(base|fix)$', os.path.basename(path))

def collect(args):
    dirs = []
    for a in args:
        a = a.rstrip('/')
        if is_run(a):
            dirs.append(a)
        elif os.path.isdir(a):
            dirs += [os.path.join(a, d) for d in sorted(os.listdir(a)) if os.path.isdir(os.path.join(a, d)) and is_run(os.path.join(a, d))]
        else:
            print(f'warning: {a} is not a folder', file=sys.stderr)
    return dirs

def split_name(name):
    """(case, run number, build label) of a run folder name: <case>-<n>-<label>, or the older <case>-<base|fix> (n = 1)."""
    m = re.match(r'^(.*?)-(\d+)-(.+)$', name)
    if m: return m.group(1), m.group(2), m.group(3)
    m = re.match(r'^(.*?)-(base|fix)$', name)
    if m: return m.group(1), '1', m.group(2)
    return name, '1', name

def label_order(parent):
    """Build labels in the order given on the command line: <out dir>/builds.txt (label TAB folder TAB commit), if there is one."""
    p = os.path.join(parent, 'builds.txt')
    return [l.split('\t')[0] for l in open(p, encoding='utf-8', errors='replace').read().splitlines() if l.strip()] if os.path.isfile(p) else []

def label(case):
    words = re.split(r'[-_]', case)
    plat = 'Proton' if ('proton' in words or 'p' in words) else 'Windows' if 'windows' in words else 'macOS' if 'macos' in words else 'native'
    data = 'unmodded' if 'gog' in words else 'modded'
    return f'{plat}, {data}'

def fmt(x, nd=1):
    # one decimal, a trailing .0 dropped (197.0 -> 197), as the written-up tables show them; nd is kept for old callers
    t = f'{x:.1f}'
    return t[:-2] if t.endswith('.0') else t

def rel(v, v0):
    if v0 == 0: return ''
    d = 100 * (v - v0) / v0
    return f'{d:+.0f} %'

def main(args):
    dirs = collect(args)
    if not dirs:
        print('no run folders found', file=sys.stderr); return 2
    parents = {os.path.basename(os.path.dirname(os.path.abspath(d))) for d in dirs}
    groups, order, per_run, labels = {}, [], [], {}
    for d in dirs:
        name = os.path.basename(d)
        case, num, build = split_name(name)
        parent = os.path.dirname(os.path.abspath(d))
        par = os.path.basename(parent)
        key = (par, case)
        if key not in groups:
            groups[key] = {}; order.append(key); labels[key] = (parent, [])
        if build not in labels[key][1]: labels[key][1].append(build)
        s, why = run_stats(d)
        per_run.append((par, case, num, build, name, s, why, note_of(d)))
        groups[key].setdefault(build, [])
        if s: groups[key][build].append(s)
    multi = len(parents) > 1
    out = []
    out.append('| case | build | frames over 33 ms | p50 ms | p99 ms | max ms | frames over 100 ms | slow-world tick p99 / max ms | over 33 ms vs first build | p99 vs first build |')
    out.append('|---|---|---|---|---|---|---|---|---|---|')
    failed = 0
    for key in order:
        par, case = key
        parent, seen = labels[key]
        given = [b for b in label_order(parent) if b in seen]
        blist = given + [b for b in seen if b not in given]
        first = None
        for build in blist:
            l = groups[key][build]
            nm = f'{par}/{case}' if multi else case
            lab = f'{label(case)}, ' + (f'median of {len(l)}' if len(l) > 1 else 'single run') + f' ({nm})'
            if not l:
                out.append(f'| {lab} | {build} | no successful run | | | | | | | |'); continue
            med = lambda k: st.median(x[k] for x in l)
            if first is None:
                first = (med('over33'), med('p99')); r1 = r2 = 'first'
            else:
                r1, r2 = rel(med('over33'), first[0]), rel(med('p99'), first[1])
            out.append(f"| {lab} | {build} | {med('over33'):.1f} % | {fmt(med('p50'))} | {fmt(med('p99'))} | {fmt(med('mx'), 0)} | {fmt(med('over100'), 0)} | {fmt(med('sw_p99'))} / {fmt(med('sw_max'), 0)} | {r1} | {r2} |")
    if any(len(b) > 1 for v in groups.values() for b in v.values()) or any(r[5] is None for r in per_run):
        out += ['', 'Per run:', '', '| run | frames (after settle) | frames over 33 ms | p50 ms | p99 ms | max ms | frames over 100 ms | slow-world tick p99 / max ms | notes |', '|---|---|---|---|---|---|---|---|---|']
        for par, case, num, build, name, s, why, note in per_run:
            nm = f'{par}/{name}' if multi else name
            if s is None:
                failed += 1; out.append(f'| {nm} | FAILED: {why} | | | | | | | {note} |')
            else:
                out.append(f"| {nm} | {s['n']} | {s['over33']:.1f} % | {fmt(s['p50'])} | {fmt(s['p99'])} | {fmt(s['mx'], 0)} | {s['over100']} | {fmt(s['sw_p99'])} / {fmt(s['sw_max'], 0)} | {note} |")
    print('\n'.join(out))
    return 1 if failed else 0

HIST_COLS = ['date', 'time', 'build', 'commit', 'platform', 'data', 'save', 'segments', 'run', 'frames_over_33ms_pct', 'p50_ms', 'p99_ms', 'max_ms',
             'frames_over_100ms', 'slow_world_p99_ms', 'slow_world_max_ms', 'gpu', 'nvidia_driver', 'note']
# Columns appended by the dev kit (the first 19 are those of the maintainers' daily-bench.csv, so rows can be merged). nvidia_driver holds the GPU driver version of any vendor.
EXTRA_COLS = ['machine', 'cpu', 'ram_gb', 'os', 'kernel', 'resolution']

# F12 profiler breakdown (same names, order and maths as the maintainers' bench-compare-table.py). Stage names in F12 order; frames.csv has
# cpu_<stage> and gpu_<stage> per frame. CPU = the render thread's time per stage (includes driver waits); GPU = GPU time per stage from timestamp
# queries, plus "other" (gpu_total_ms minus the stamped stages) and "total" (gpu_total_ms). F12 shows no CPU time for the GPU-only stages and no GPU
# time for driver-wait; those are left out. Columns: cpu_<stage>_p50_ms / _p99_ms (stage name with '-' written '_'), cpu_total_*, gpu_<stage>_*,
# gpu_other_*, gpu_total_*; ms, 3 decimals, over the frames after "settle"; blank = frames.csv without these columns. Appended after EXTRA_COLS.
F12_STAGES = ['upd-terrain', 'upd-objects', 'upd-foliage', 'upd-chars', 'sky-prepare', 'uploads', 'cull', 'bake', 'shadows', 'gi', 'reflection', 'sky-draw',
              'terrain', 'objects', 'foliage', 'characters', 'water', 'shafts', 'fog', 'particles', 'post', 'ui', 'submit', 'driver-wait']
F12_GPU_ONLY = {'uploads', 'cull', 'bake'}
F12_CPU_ONLY = {'driver-wait'}
def _cn(stage): return stage.replace('-', '_')
BREAKDOWN_COLS = (
    [f'cpu_{_cn(s)}_{q}_ms' for s in F12_STAGES if s not in F12_GPU_ONLY for q in ('p50', 'p99')] + ['cpu_total_p50_ms', 'cpu_total_p99_ms'] +
    [f'gpu_{_cn(s)}_{q}_ms' for s in F12_STAGES if s not in F12_CPU_ONLY for q in ('p50', 'p99')] +
    ['gpu_other_p50_ms', 'gpu_other_p99_ms', 'gpu_total_p50_ms', 'gpu_total_p99_ms'])

def breakdown(path):
    """dict column -> formatted ms (3 decimals) for BREAKDOWN_COLS from a frames.csv; {} when the file or its columns are missing."""
    if not os.path.isfile(path): return {}
    try:
        with open(path, encoding='utf-8', newline='') as fh:
            rows = [r for r in csv.DictReader(fh) if r['segment'] != 'settle']
        series = {}
        for s in F12_STAGES:
            if s not in F12_GPU_ONLY: series[f'cpu_{_cn(s)}'] = [float(r['cpu_' + s]) for r in rows]
            if s not in F12_CPU_ONLY: series[f'gpu_{_cn(s)}'] = [float(r['gpu_' + s]) for r in rows]
        gtot = [float(r['gpu_total_ms']) for r in rows]
    except (KeyError, ValueError):
        return {}
    if not rows: return {}
    series['cpu_total'] = [sum(v) for v in zip(*[series[f'cpu_{_cn(s)}'] for s in F12_STAGES if s not in F12_GPU_ONLY])]
    gsum = [sum(v) for v in zip(*[series[f'gpu_{_cn(s)}'] for s in F12_STAGES if s not in F12_CPU_ONLY])]
    series['gpu_other'] = [t - g for t, g in zip(gtot, gsum)]
    series['gpu_total'] = gtot
    out = {}
    for k, v in series.items():
        out[f'{k}_p50_ms'] = f'{pct(v, 50):.3f}'
        out[f'{k}_p99_ms'] = f'{pct(v, 99):.3f}'
    return out

def history(csvpath, outdir, note, driver, machine='', cpu='', ram_gb='', os_name='', kernel=''):
    """Append one row per successful run to csvpath (header written if the file is new or empty; existing rows are never touched)."""
    outdir = outdir.rstrip('/')
    cfg = dict(l.split('=', 1) for l in open(os.path.join(outdir, 'settings.txt'), encoding='utf-8', errors='replace').read().splitlines() if '=' in l)
    builds = [l.split('\t') for l in open(os.path.join(outdir, 'builds.txt'), encoding='utf-8', errors='replace').read().splitlines() if l.strip()]
    rows = []
    for d in collect([outdir]):
        case, num, lab = split_name(os.path.basename(d))
        b = next((x for x in builds if x[0] == lab), None)
        s, why = run_stats(d)
        if s is None or b is None: continue
        launch = os.path.join(d, 'meitou', 'launch.txt')
        m = re.match(r'started (\d{4}-\d\d-\d\d)T(\d\d:\d\d:\d\d)', open(launch, encoding='utf-8', errors='replace').readline()) if os.path.isfile(launch) else None
        summ = os.path.join(d, 'bench', 'summary.md')
        gpu = res = ''
        if os.path.isfile(summ):
            line = [l for l in open(summ, encoding='utf-8', errors='replace').read().splitlines() if l.startswith(('Play', '#')) is False and l.strip()]
            line = line[0] if line else ''
            g = re.search(r'\d+x\d+, (.*?), upscaler', line)
            gpu = g.group(1) if g else line
            r = re.search(r'(\d+x\d+), ', line)
            res = r.group(1) if r else ''   # the resolution the benchmark rendered at (the game window), from the game's own summary
        bd = breakdown(os.path.join(d, 'bench', 'frames.csv'))
        rows.append([m.group(1) if m else '', m.group(2) if m else '', b[1], b[2] if len(b) > 2 else '', cfg.get('platform', ''), cfg.get('data', ''),
                     cfg.get('save', ''), cfg.get('segments', ''), num, f"{s['over33']:.1f}", fmt(s['p50']), fmt(s['p99']), fmt(s['mx'], 0), s['over100'],
                     fmt(s['sw_p99']), fmt(s['sw_max'], 0), gpu, driver, note, machine, cpu, ram_gb, os_name, kernel, res]
                    + [bd.get(c, '') for c in BREAKDOWN_COLS])
    new = not os.path.isfile(csvpath) or os.path.getsize(csvpath) == 0
    with open(csvpath, 'a', newline='', encoding='utf-8') as f:
        w = csv.writer(f)
        if new: w.writerow(HIST_COLS + EXTRA_COLS + BREAKDOWN_COLS)
        w.writerows(rows)
    print(f'history: {len(rows)} row(s) appended to {csvpath}')
    return 0

def diff_settings(kit, build):
    """Lines 'key: kit value -> build value' for every setting of the kit file that the build's file lacks or has differently (extra keys in the build are ignored)."""
    import json
    with open(kit, encoding='utf-8') as f: a = json.load(f)
    with open(build, encoding='utf-8') as f: b = json.load(f)
    lines = []
    def walk(x, y, path):
        for k, v in x.items():
            if isinstance(v, dict) and isinstance(y.get(k), dict): walk(v, y[k], path + k + '.')
            elif y.get(k) != v or k not in y:
                lines.append(f'{path}{k}: {v!r} -> {y.get(k, "<missing>")!r}')
    walk(a, b, '')
    return lines

def settings_diff(kit, build):
    lines = diff_settings(kit, build)
    for l in lines: print(l)
    return 1 if lines else 0

if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--settings-diff':
        sys.exit(settings_diff(*sys.argv[2:4]))
    if len(sys.argv) > 1 and sys.argv[1] == '--history':
        # --history <csv> <out dir> <note> <gpu driver> <machine> <cpu> <ram_gb> <os> <kernel>
        sys.exit(history(*sys.argv[2:11]))
    sys.exit(main(sys.argv[1:]))
