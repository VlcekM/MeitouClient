---
title: Meitou 0.23.0
date: 2026-11-26
author: VlcekM
image: banner-wide.png
---

Hey hey everybody, @sashkaa here,

Been a week or two since we made a release news post like this 😎

Feels like forever, with the quick pace of progress Meitou's had since its unveiling.

This patch has been a long time coming, with a ton of fixes and parity work.

I'll go out, and boldly declare that we **now have feature parity** with the base game. *But*, we're definitely not at 1.0 yet.
There will be bugs. Our vision for 1.0 is to have Kenshi run, feel, look better than the base game did, while still recognizably Kenshi. To make
picking MeitouClient the no-brainer default when wanting to play Kenshi. And we are very close.

Anyway, here are some highlights:

# Unified AI chooser & real order queues

We now control the characters the same way Kenshi does - the same decision tree, same AI goals.
In practice, this means NPCs should rescue downed allies, guard towns, shoppers buy from counters,
wandering traders visit, and raids should pillage you faithfully.

# Building fixes
Huge thanks to @Mazal Tofuson (TheIvoryDisaster) for a number of bugfixes (not only) and improvements in the building system, including placing/snapping turrets,
walls, auto-hauling, and base management in general.

# Bugfixing & bugfixing & bugfixing
We have fixed over 160 bugs since 0.22.1 and the last couple of days, a large amount of issue reports we receive are vanilla behavior.
I believe that this is a huge indicator that we are going the right way. We have also added many optional fixes for vanilla bugs in Meitou.
Huge thanks to @Jame for the incredible bug triage and fixing work.

# Performance improvements

I took a peek at some stuff we were doing inefficiently, and I managed to remove a very large amount of the stutters while travelling and when playing, including around halving the CPU frame time.
I also added a few small options that should reduce the render quality just a bit, but improve performance by a lot on integrated graphics and laptops. My office laptop now reaches almost 60FPS in the hub.
Also decreased ram usage by about 15-25%.

# Quality of life, modding and RE_Kenshi
We have integrated some quality of life mods (such as squad size, XP modifier, hiding hats) directly into Meitou,
and have made making performance benchmarks easier than ever.
Our amazing community modders have ported over a metric ton of mods from RE_Kenshi, and our launcher now suggests
either Meitou replacements or compatibility patches for RE_Kenshi mods, with the option to automatically subscribe to them.

And a lot more.

The full list is in the [release notes](https://github.com/VlcekM/MeitouClient/releases/tag/v0.23.0).
