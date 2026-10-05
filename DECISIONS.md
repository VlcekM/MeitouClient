# Decisions (Part B)

- Agents run on Sonnet (user's instruction in chat; the prompt said Opus).
- Probes reference the prebuilt Meitou.Data.dll / Meitou.Core.dll (HintPath) rather than ProjectReference, so
  concurrent agent builds never share the worktree's obj folders.
- Concurrency: 4 subsystem chains at a time (machine also runs the named re-dump and other sessions).
- New docs go under docs/game/ (game logic) except the save format (docs/formats/save.md).
