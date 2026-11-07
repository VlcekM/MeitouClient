# Characters Emotions (native port)

The Workshop plugin mod "Characters Emotions - Realtime expression" (item 3791017374, `CharactersEmotions.mod`, RE_Kenshi plugin `Plugins/CharactersEmotions.dll`,
MIT, Copyright (c) 2026 호옹이호옹) animates faces with shape keys. Meitou does not load the DLL ([rekenshi](formats/rekenshi.md)); it ships a C# re-implementation,
`src/Meitou.Compat.CharactersEmotions`, as a bundled code mod ([modding](modding.md#ports-of-re_kenshi-plugin-mods)) on top of the face API
([face-shape-keys](face-shape-keys.md)). It loads when `CharactersEmotions.mod` is enabled; its settings are on Settings > Mod settings, not the plugin's F6 menu.

**Sources.** The plugin's source is not published (searched 2026-11-07; the Workshop item says the source archive is "backed up" in the mod); the port is written from
the readmes (`readme_eng.txt`, `readme_animemotions_eng.txt`) and from the default INI text and keys the DLL embeds in its string table (read with a strings listing, not
decompiled; the logic is ours). The INI files are not shipped with the mod: the plugin writes them on first run. All behaviour below is therefore **Observed**
(documented or visible) unless marked.

## Companion data mod

"Vanilla Race for Character's Emotions" (3791607178, `VanillaRace4CharactersEmotions.mod`) points the Greenlander, Scorchlander, Shek and Hive (and Hive Queen)
bodies at `character/meshes/CEBodies/*.mesh`: the vanilla body meshes with 11 extra poses, `CE_Blinking`, `CE_Normal1-3`, `CE_InCombat`, `CE_Starving`, `CE_Hurt`,
`CE_Resting`, `CE_Sleeping`, `CE_Damaged`, `CE_KnockOut`. **Verified** (mesh reader on `human_male`, `bone_male`, `Hive_Queen`: one submesh, target 1, 660 to 2800 vertices
per CE pose, next to the races' own `wide_cheekbones`... poses). No `CE_HardWorks*`, `CE_Stealth*` or `CE_Speech*` pose is in them, so animation expressions and speech
morphing are inactive on these bodies (they are for VRoid-style meshes from other mods). The race-sharing rule of the Workshop text applies: another mod replacing the
race meshes removes the expressions.

## Behaviour implemented

A character is supported when its body mesh has every key of the INI's expressions (the support check; the plugin's too). Handled: the player's characters
(only selected ones with "Only selected characters"); "Other characters" (off by default, an extension) adds nearby NPCs; at most "Number of characters" (30), the
nearest to the camera (player characters always). Per character, on the simulation clock (so paused = frozen, `step` = repeatable):

- **Blinking**: every `Blinking_Frequency` + random(`Blinking_Randomizer`) ms (2000 + 0 to 1500), `Blinking_Time` (200 ms) closing and opening (triangle); not asleep or
  knocked out, not during an animation expression with `IgnoreBlink`.
- **Situation**, priority KnockOut > Attacked > Sleep > Angry > Pain > Sad > Relax > Normal: knocked out or dead (`BodyStatus.Unconscious`, `Dead`); attacked (a resolved
  combat hit on it, via the combat hit feed, kept `EmotionAttacked_KeepTime` 500 ms then faded over `OffTime` 1000 ms, blended over the lower situation); asleep (in a bed,
  or clips `sleepinbed`, `sleeponfloor`); angry (in combat or combat mode); pain (the worst body part below `HurtBoundaryValue` 0.7 of its health); sad (hunger under
  2.5, the "Hungry" level of [character-stats](game/character-stats.md)); relax (clips `sitting chair`, `sitting idle`). Status is re-read every `Check_Frequency` (100 ms) +
  random 0 to `Check_Frequency_Randomize` (20 ms).
- **Normal**: while nothing else shows, every `EmotionNormal_Frequency` (3500) + random(3000) ms one of the `[EmotionNormalN]` sections is picked and kept
  `KeepTime` (5000 ms); the face eases in and out over `MorphTime` (800 ms).
- **Animation expressions** (`CharactersEmotions_animemotions.ini`): the playing clip whose `anim name` (our layer name) has a section drives its keys by
  frame = clip time / clip length x `TotalAnimFrame`, linear between control frames, none outside the first and last frame and at frame 0; shown only while the
  situation's priority index is under `EmotionPriority`; `OnlyAnimEmotion` drops the covered default expression meanwhile. Needs the `RequirementShapeKey` keys.
- `InvalidShapeKey` keys are set to 0 on a supported character (VRoid-style sliders that would fight a blink).
- Weights of a changing situation ease at 0.25 s (**Unknown** in the plugin: it documents a time for the normal expression only; 50 ms rise for Attacked).

INI files are read from the data mod's `Plugins` folder when present (a modder's edits), else the defaults above are used; `MEITOU_EMOTIONS_INI=<folder>` reads them
from another folder; `MEITOU_EMOTIONS_TRACE=1` logs each player character's situation, clip layers and weights when they change.

## Not supported

- **Speech morphing** (`CE_Speech*` while a character talks; the plugin hooks `Dialogue::say`): needs a per-speaker speech event the simulation does not publish, and
  no installed body has the keys.
- The plugin's hotkeys (F6 menu, `P` per-character ban) and its `CharactersEmotions_settings.ini`: replaced by the Mod settings page.
- The plugin's "unsupported characters flicker once" quirk and its log file: not applicable.
- Portraits show the face of their last redraw ([face-shape-keys](face-shape-keys.md)).
- Details the plugin does not document and the port guesses: the pain, hunger and fade rules above, and whether dead characters show the knock-out face.

## Verification

**Observed** (2026-11-07, `siwa-test6` with the user's 664-mod list plus both CE mods, Vulkan, paused and `step`ped, area cleared, 4K close-ups of a spawned Greenlander
and of Siwa, run with and without `CharactersEmotions.mod` enabled and otherwise identical): the support check admits Siwa, Seto, Trak and other CE-body characters
and skips bodies of other mods (logged by name); a blink closes the Greenlander's eyes for about two frames of the stepped series (no change elsewhere in the face that
frame), a 70-point injury gives the pain face (open mouth, lowered brows), the combat state switches Siwa to `CE_InCombat` with a 0.25 s cross-fade
(trace). The animation path ran with a substitute INI (clip `stealth idle` driving `CE_Normal2` over frames 0-40): the weights ramp with the clip time in the trace, since
the real keys (`CE_Stealth*`, `CE_HardWorks*`) are on no installed body. **Not captured**: the knock-out and attacked faces (the ragdoll lands face down; their
weights are set, the situation logic is trivial but unobserved on screen), sitting and bed (no chair or bed was set up), `stonemine` (no mining job set up), portraits.
Decapitated bodies and LOD levels share the same morph base by construction; not looked at.

## Cost

**Observed** (2026-11-07, Release, the same save, about 100 characters expressive with "Other characters" on, a temporary stopwatch round the rebuild, removed after): rebuilding
one character's morph (summing 1 to 3 poses over about 3,200 slots and writing the 50 KB region) costs 0.030 ms, and only on a frame where its weights moved: a held face
(the common case: weights move for under a second per change, then stay) costs one array compare in the renderer and the mod's per-frame `Update` (a few dictionary
and array operations). Worst case, every supported character morphing in the same frame, is 30 x 0.03 ms = 0.9 ms. Memory: three regions per expressive character
(3 x 3,200 x 16 B = 150 KB), released with the character's render state. The mod's own `Update` was not timed separately from the frame.
