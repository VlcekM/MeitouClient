# Optional fixes

Original behaviour that is faithful but reads as a bug. Meitou keeps the original's behaviour by default. Each entry is to get an opt-in
setting (off by default) that changes it.

## Robot replies for organic talkers (#1468)

- **Original** (**Verified**, rebirth.mod records and a headless probe of the #1468 save): the Skeleton Doctor's `skeleton doc` (64667-rebirth.mod) opens
  with 64668, whose `target race` is Skeleton. A Talk from an organic character passes it through a Skeleton squad mate, but the talk stays with
  the talker ([player conversation](game/dialogue.md#player-conversation), [retargeting](game/dialogue.md#retargeting)). The reply 64669
  ("Yeah, my parts are wearing out" / "Yeah, I need some fixing") has no conditions, so the organic talker says it.
- **Fix**: hide that reply when the speaker's race is not a robot and none of its limbs is replaced, as DC 79 HAS_ROBOT_LIMBS tests. Alternatively,
  hand the conversation to the squad mate the opening line's search found.

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
- **Fix**: a release by a player-faction character ends the slave's obedience (job 187, as DA 28's split does), so it stays out until its line runs.
