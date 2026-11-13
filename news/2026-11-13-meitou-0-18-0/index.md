---
title: Meitou 0.18.0
date: 2026-11-13
author: VlcekM
image: banner-wide.jpg
---

Hey hey everyone,

We've been very busy squashing and fixing and eradicating all the bugs and inaccuracies from the base game.
Thanks to everyone sending in saves thru the feedback reports - makes the process of fixing it way quicker!

A lot of navmesh issues remain to be resolved though, so please bear with us for a bit more!

**Please do keep in mind that saves made before this patch likely have issues with buildings not having the original roles/titles. A new save is required for them to be faithful.**

# Towns

- Town residents move into the same homes as in a vanilla new game, so each town has the shops you know, in the same houses
- Signs the original never shows are gone
- A building remembers the last squad made at it, so For sale shows its home and saves keep it

# Camera and controls

- The scroll wheel sets the free camera's speed, and Shift pans faster (200% by default)
- Camera turning lags less above 60 fps, and Controls has a Camera smoothing toggle
- Space resumes the game after the pause button
- Pause after loading and Animals pick up items are now Faithful / Meitou switches

# Fixes

- Pack animals show the backpack they wear
- Swamp House floors are no longer black
- Bloody footprints after walking through blood
- Turret gunners man the turret from up close, not from 15 m off
- Locked gates hold on tilted buildings and slanted doors, such as the Ancient Lab Ramp Gate
- Fighters attack targets standing just off the navmesh edge
- Eaten limbs no longer leave a severed limb item
- Saves with characters from a removed mod load them as Exiles, as the original does
- The Slaves start keeps its escaped slaves, and new player slaves get their Obedient Slave job
- Private homes no longer read "Open to public: Public", so you know when the residents will tell you to leave
- Trading with your own squad member no longer closes the window
- Fitted prosthetic limbs can be taken from loot windows
- The sand at the Watchtower's foot no longer counts as indoors, and windmills no longer sink when placed
- macOS: loading fixed, blood lands where the fight is (outdoors too), and there are separate packages for older Macs (Ventura)
- Metal runs smoother: shaders compile in the background, and the memory budget can size itself to your Mac

Code mods can now add equipment slots too.

The full list is in the [release notes](https://github.com/VlcekM/MeitouClient/releases).
