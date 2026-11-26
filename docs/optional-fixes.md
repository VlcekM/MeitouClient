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
