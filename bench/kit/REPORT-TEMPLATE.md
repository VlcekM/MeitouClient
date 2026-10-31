# Meitou daily benchmark report

`bench.py` fills the fields in braces from the machine and the run folders and writes the result as `report.md`. Fill in what it could not know and the two human parts at the end.

- Who: {{WHO}}
- Date: {{DATE}}
- Machine label: {{MACHINE}}

## Hardware and software

| | |
|---|---|
| CPU | {{CPU}} |
| RAM | {{RAM}} |
| GPU | {{GPU}} |
| GPU driver | {{DRIVER}} |
| OS | {{OS}} |
| Kernel | {{KERNEL}} |
| Display resolution | {{DISPLAY}} |

## What was run

| | |
|---|---|
| Meitou commit | {{COMMIT}} |
| Build type | {{BUILD_TYPE}} |
| Settings file | {{SETTINGS}} |
| Kenshi data | {{DATA}} |
| Save | {{SAVE}} |
| Save checksum | {{SAVE_SUM}} |
| Runs of each scenario | {{RUNS}} |
| Note | {{NOTE}} |

Kenshi data, to fill in by hand: store (GOG / Steam), game version, and confirm that no mod is active.

## Results

Play benchmark (segments idle, orbit, zoom, pan), columns as in results-format.md:

{{RESULTS_PLAY}}

Base switch (four switches between the two bases):

{{RESULTS_BASES}}

The raw rows are in `results.csv` next to this file.

## Warnings from the script

{{WARNINGS}}

## Summary (in your own words)

(What stands out? Smooth or hitchy? Which segment was worst? How does it compare with the maintainers' rows in daily-bench.csv, if you have them?)

## Anything odd

(Crashes, a window that opened at a different size, a game that lost focus during the base-switch runs, background load, thermal throttling, a laptop on battery, anything you changed.)
