# Launcher changelog

Released with tags `launcher-v<version>` by `.github/workflows/launcher-release.yml`; each release needs its `## v<version>` section.

## v1.5.0

- **Look** row on the Graphics page: Faithful, Meitou, its example looks or a look you saved in the game. The game applies
  it when it next starts.
- The Graphics tabs follow the game's new pages: Light, Sky, Colour, Water, Skin, then Distances and Detail, and
  Advanced for Interior lamps.
- Shows with game releases from the next one on.

## v1.4.0

- The Graphics page has **every graphics setting** of the game's Tab panel, sliders and Faithful / Meitou switches,
  on **tabs** like the game's pages: Display, World, Lighting, Water, Effects, Skin.
- **Quality preset** (Low, Medium, High, Ultra): the game applies the one you pick when it next starts. Low is the same as
  `--low-end`.
- **Detect** picks the preset for your graphics card, memory and processor. A fresh install does this by itself once;
  settings you already have are kept.
- **Type a value** into a slider's box instead of dragging (Enter to set, Esc to undo).
- Shows with game releases from the next one on.

## v1.3.2

- **Launch as Kenshi on Steam** (Settings, Windows): turn it off to start the game without Steam treating it as Kenshi,
  so no Steam overlay and no Kenshi play time. On by default, as before.

## v1.3.1

- Texture quality's slider now has **Maximum on the right**, like every other slider. Your saved setting stays as it
  was. Shows with game releases from the next one on.

## v1.3.0

- **Videos in news posts**: a post can show a video, as its cover or in the text. It shows the first frame with a play
  button, and only downloads when you press play. Pause and resume with a click or Space, skip 10 s back or forward with
  the buttons or Left/Right, and seek anywhere on the bar. Videos play with sound.
- A video cover can have a **Full video** button that opens the full-quality video in your browser.
- News pictures now fill the post's width (without being blown up past their size), and the date and author sit on
  the right of the title.
- Scroll bars always show at full size.
- The launcher window opens taller.

## v1.2.1

- **Report to the roadmap**: the system picker has a search box. Type to filter the list, arrows move the highlight,
  Enter or a click picks, Esc closes the list.

## v1.2.0

- **Report to the roadmap** under the launcher version: tell the Meitou roadmap that a system works or is broken. The
  report carries the version, platform and whether mods are on, and optionally the end of that version's log.txt.
- **Language** in Settings: switch the launcher to another language, with Brazilian Portuguese as the first. It also sets
  Kenshi's language for the title and settings screens (#80).
- Steam copy on Windows: Play starts Meitou as Kenshi for Steam, so the Steam overlay and play time can work (not yet
  confirmed in a Steam run).
- An update is offered only for a release newer than the newest one installed, and installed versions always stay in the
  Versions list.
- macOS Apple silicon: first start and Settings → Renderer offer **Auto**, native **Metal** and **Vulkan (MoltenVK)**. Auto
  uses Vulkan when its loader is installed and Metal otherwise, so Homebrew is no longer required.
- macOS Vulkan gets only the Vulkan loader from Homebrew, so Homebrew's image libraries can no longer break the game.
  Settings shows the Homebrew command and checks for the loader.
- First start explains that Kenshi is required and links to a guide for getting it on macOS; Find also searches the macOS
  Steam, CrossOver and Whisky folders.
- A game version that only supports Vulkan no longer offers Metal.
- macOS Metal has a **memory budget** in Settings: Safe, Balanced, or High on Macs with 16 GB or more.

## v1.1.2

- When there are two nightly builds on the same day, the Versions list marks the second one "#2" and shows them in order.
  Before, the two looked the same and could appear in the wrong order.

## v1.1.1

- Nightly builds now come twice a day; the afternoon build shows its date in the Versions list like the morning one.

## v1.1.0

- The launcher updates itself: at startup it installs a new launcher version and restarts into it. Turn this off in Settings
  with "Update the launcher automatically"; the update button stays for doing it by hand. Updating to 1.1.0 itself still
  takes one click on that button.

## v1.0.4

- macOS: Play starts the game again instead of it exiting at once because the Vulkan loader wasn't found. The launcher now
  hands the game Homebrew's library folder and the MoltenVK setting it needs (#20).
- macOS: without the Vulkan loader installed, Play says how to install it instead of starting a game that closes at once.
- When the game stops with an error, the status line says so and where its log is.
- While the game is running, Play says so instead of starting a second copy that would overwrite its settings and mod list.

## v1.0.3

- A new launcher version shows an update button in the title bar, on every page; the status line no longer loses the notice.

## v1.0.2

- macOS: the launcher starts again instead of crashing at launch (#16).
- Linux: the AppImage runs on older distributions (glibc 2.35 and newer, e.g. Ubuntu 22.04).
- A Ko-fi button in the News bar for supporting the project.

## v1.0.1

- Reads every page of GitHub releases, not only the newest 100: old game versions stay installable and the launcher's own
  updates are always found.

## v1.0.0

- First release: installs game releases from GitHub (stable or with pre-releases), with a cancellable download and a SHA-256
  check; starts the game with the chosen Kenshi folder.
- Mod list like Kenshi's launcher (enable, drag to reorder, `mods.cfg`), mod presets, RE_Kenshi plugin mods marked.
- Looks like Meitou's own settings: plates, Exo 2 from the Kenshi install, its own title bar.
- Windows Setup, Linux AppImage and macOS packages that update themselves from new launcher releases.
- One Native AOT executable per system: no .NET runtime or DLLs beside it.
