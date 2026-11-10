---
title: Meitou 0.17.2
date: 2026-11-10
author: VlcekM
image: banner.jpg
---

Hey hey everyone,

**0.17.2** is out. It's a bug-fix release, mostly built from the reports you've sent with the in-game Feedback button since 0.17. Thanks to everyone sending in saves, and to **teppuM**, **CrockMr**, **DMLen**, **Demitrix2k**, **LucasSKrewer** and **GosuBoy** for
their fixes and benchmark runs 🙏

# New settings

- **Interface scale** (50-200%) sizes fonts, skins, panels and windows together, and 4K screens get properly sized radio buttons and save lists
- **Main menu style** is now separate from the UI style, so you can have the Faithful title with the Meitou UI, or the other way around
- **Character draw distance** (the original's NPC View Range) is on Graphics > Characters

# Fixes

- Slavers no longer strip a sold slave over and over: a caged slave without shackles gets shackled first, then its owners' medics bandage it
- Rebirth guards re-shackle prisoners who picked their shackles off
- The slave buy list only offers the slaver's own slaves
- Ramps snap to walls, so turrets mount on walls again
- Furniture can't cut into a building's walls, and furniture in a bought town house no longer founds an outpost
- Steal, pick-lock and loot use the Highlight binding instead of a hard-wired Alt, and your `controls.cfg` bindings are read as defaults
- Storages count as in trading range when you're close to them, and general storage takes armour and weapons by hand again
- Saves with a building listed outside its zone load again, and empty inventories stay empty after a load
- Healed sleepers get up, and a sleeper with a broken leg lies in the bed instead of crawling beside it
- Animals have their death items, weight, stats and inventory rows as in the original, and packs sleep together
- Medics only count kits they can reach, carried characters don't treat anyone, and skeleton repair beds heal damage
- Dumb NPCs such as Agnu can be talked to again, and lone travellers stop talking to themselves
- Put in cage and lock picking walk to the cage's floor instead of the storey below
- Craft and research queues show up with older UI layouts such as Dark UI
- Miners reach their rock, workmasters keep up with running slaves, and the camera stops slipping into ruins at close zoom

The full list is in the [release notes](https://github.com/VlcekM/MeitouClient/releases).
