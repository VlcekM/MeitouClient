# Optional fixes

Original behaviour that is faithful but reads as a bug. Each entry gets a setting on the Fixes page that changes it (off keeps the
original's behaviour), or is to get one.

## Robot replies for organic talkers (#1468)

- **Original** (**Verified**, rebirth.mod records and a headless probe of the #1468 save): the Skeleton Doctor's `skeleton doc` (64667-rebirth.mod) opens
  with 64668, whose `target race` is Skeleton. A Talk from an organic character passes it through a Skeleton squad mate, but the talk stays with
  the talker ([player conversation](game/dialogue.md#player-conversation), [retargeting](game/dialogue.md#retargeting)). The reply 64669
  ("Yeah, my parts are wearing out" / "Yeah, I need some fixing") has no conditions, so the organic talker says it.
- **Fix**: hide that reply when the speaker's race is not a robot and none of its limbs is replaced, as DC 79 HAS_ROBOT_LIMBS tests. Alternatively,
  hand the conversation to the squad mate the opening line's search found.

## Building window over the extended information panel (#1544)

- **Original** (**Verified**, decompile and the installed layouts): a selected building's own window puts its bottom-left corner on the point the
  HUD stored at its build, just above the collapsed `ExtendingPanel` ([placement](game/ui-screens.md)), whether the ^ button has extended the
  panel or not. The windows (`Kenshi_InventoryGeneric*Window.layout`) are on MyGUI's `Window` layer, above the HUD's `Middle`
  (`Kenshi_MainPanel.layout`, `core_layer.xml`), so the window covers the extended panel's state lines: [UPGRADE], Dismantle.
- **Fix** (Fixes setting "Building window above the information panel", `UserConfig.FixBuildingWindowClear`, on by default): while the panel is
  extended the point is the extended panel's top less 1 % of the screen height; each ^ press moves an open building window still on the old point
  to the new one (a dragged or linked window stays); a window taller than the room keeps its top on the screen.

## Two storage windows at once (#1590)

- **Original** (**Verified**, decompile of 1.0.68 `kenshi_x64.exe`): a storage or container order (`FUN_1407918a0`) closes every inventory
  window, then opens the looter's window and the target's (`FUN_1406e6820` / `FUN_1406e6af0`), neither in the manager's own-window slot
  (`+0xe0`). Selecting a building (`FUN_1405504a0`, `FUN_140729c80`) calls `FUN_1406e6640`, which brings forward a window the building already
  has and otherwise closes only the previous own window and opens the building's beside the target ([inventory windows](game/ui-screens.md)).
  So a storage opened by order and a second storage then selected show two STORAGE windows.
- **Fix** (Fixes setting "One storage window at a time", `UserConfig.FixReplaceTargetWindow`, on by default): selecting another building
  with a window while an order's storage or container is shown closes it and the looter's inventory (a container closes in the world), and the
  selected building's window opens alone, as a left click on it does with nothing open.

## Freed slaves walk back into a cage (#1575)

- **Original** (**Verified**, decompile of 1.0.68 `kenshi_x64.exe` and a headless probe of the #1575 save): Set Free (`Task_ReleasePrisoner`, start
  `FUN_1403570a0`) leaves the slave state and the owner as they are ([locks](game/locks.md#cages-and-beds)), and the order 133 it gives ends at its start
  (`Task_GetOutOfCage` slot 2 `FUN_140355630` marks the task done; slot 3 does nothing then). Nothing on the way tests the freed flag: the obedient-slave
  pass (`FUN_14050e5b0`) loads the master squad's Slave list, and 172's finder (`FUN_1408caaf0`, filter `FUN_1408db510`) takes the nearest free cage of
  the owner's faction, usually the one just left ([Obedient Slave](game/ai-tasks.md#slave-work-shout-and-capture)). The Holy Nation farm slaves
  (`holy slaves`, 43393-rebirth.mod) are owned by `Holy Farm Cage Guards`, whose `Cage Warden` package (5221-gamedata.base) lists `Slave get in Cage`
  0-24, so a slave the player sets free walks back in at once and the cage locks behind it. The event-13 chain's second line, whose three variants
  (3470, 3472, 3474-dialogue-01.mod) carry `change AI` `Escaping slave- run away solo`, comes 2 + 0.12 x length seconds after "I'm free..."
  ([timing](game/dialogue.md)), when the slave is already locked in.
- **Fix** (Fixes setting "Freed slaves stay free", `World.FreedSlaveFix`, on by default): a release by a player-faction character ends the
  slave's obedience (job 187, as DA 28's split does), so it stays out until its line runs.
