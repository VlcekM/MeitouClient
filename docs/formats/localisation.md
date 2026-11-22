# Localisation

How the original chooses its language and what a language brings, and how Meitou does the same
(`src/Meitou.Core/Localisation/`, `GameDatabase.Load`). Install layout: `locale/<code>/` holds
`LC_MESSAGES/main.mo` (compiled gettext, plus `main.po`/`main.pot` text sources), `<code>.translation`
(FCS record layer), fonts and other resource files, `tips.txt`, `names*.txt`. Codes seen: de_DE, es_ES,
fr_FR, it_IT, ja_JP, ko_KR, pt_BR, ru_RU, zh_CN; `en_GB` has only the `.pot`. Message format: [gettext](gettext.md).

## Language selection

Addresses are `kenshi_x64.exe` (1.0.68).

- **Verified** (`FUN_140175a90`): a fixed table maps the ten Steam names (english, german, french, russian, italian, portuguese,
  spanish, chinese, japanese, korean) to codes; each `locale/` folder not `_`-prefixed and not in the table is added with an empty Steam name.
  At every start, from `settings.cfg`'s `language` (L) and `steam_language` (S):
  1. Only when `SteamAPI_Init` succeeded (`FUN_140745490`): the Steam client's language (`ISteamApps` version 008,
     `GetCurrentGameLanguage`) replaces S when it differs, and that counts as a change.
  2. When L is empty and S is not, or on a change: the table entry whose Steam name is S.
  3. Else, or when none matched: L, if it is a table code or a folder.
  4. Else `en_GB`.
  5. Written back: `language` = the chosen code; `steam_language` = S, or when S is empty the chosen entry's Steam name. Setting a key
     replaces its line or appends one (`FUN_14017e1d0`), then the file is saved (`FUN_14013c0b0`).
  So a language picked in `settings.cfg` stays until the Steam client's language changes. Related: `FUN_1401753e0`, `FUN_1401751d0`,
  `FUN_1401750a0`, `FUN_140174c00`, `FUN_140174d50`; launcher language message `FUN_140124650`.
- **Verified** (`Ogre_RegisterResourceLocations` `140816d90`): for a non-English language its `locale/<code>` is registered as a resource
  folder after the base game's and before the mods' (the last registered location wins a name), so fonts, layouts, images and sounds
  placed there override the base. `en_GB` adds nothing.
- **Verified** (`Mods_LocateModTranslation` `14086f240`): per loaded mod, the first sorted sub-folder X of `<mod>/locale` that holds
  `LC_MESSAGES/main.mo` or `X.translation`. The first mod in load order that has one replaces the active language with X
  (messages from that mod's folder; the user's core translation is not applied).
- **Verified** (`Fcs_GameLoadSequence` `140870ae0`, `Fcs_LoadTranslationFile` `1406c29a0`): `.translation` files load with the mod loader.
  Order: core files, the user's `locale/<code>/<code>.translation`, mods, then the translation of the **first** mod that has one (not all
  mods' translations). **Observed** (en_GB vs de/ja/ru dumps): a translation record changes the name (and text fields) of the record with
  the same key; records of unloaded mods it names match nothing.
- **Verified** (tips `FUN_14091a280`, names `FUN_140641d80`): `tips.txt` and the `names*.txt` pools are resource lookups, so the language
  folder's copy replaces `data/`'s.
- **Observed**: `factionNames.txt`, `tips_DE.txt` and `translated.txt` are not read by the game.

## Meitou implementation

- `GameLanguage.Detect` applies the rules above; Meitou also accepts a code it has built-in texts for (`MeitouTranslations.HasEmbedded`)
  without a `locale/` folder. `SteamSession` reads the Steam client's language once Steamworks has started (`GameLanguage.SteamClientLanguage`;
  none in headless, screenshot and listing runs, which start no Steam). `Loc.Use` runs at start after `UserConfig.Load()`:
  `GameLanguage.StoreChoice` writes the two lines back (only when they change, other lines and bytes kept), then the language is
  detected. The launcher's Settings page
  picks the language for the game and itself: it writes only the `language` line of `settings.cfg` ([launcher](../launcher.md)).
- **Catalog** (`GettextCatalog`): `main.mo` (LE/BE, plural splits, `\x04` context), else `main.po` (fuzzy and obsolete skipped). Plural rule
  (`PluralRule`) is parsed from the header `Plural-Forms`; Germanic when absent. Verified for all eight locales: `.mo` and `.po` give the same messages.
- **Placeholders**: `{N}`, `{N,num}`, `{N,num=fix,p=K}` (1-based, boost-format). `num` uses the language's culture. Translations may reorder them.
- **API**: `Loc.Tr(msgid[, args])`, `Loc.Plural(singular, plural, n, args)`. A msgid with no translation, and any text that is not a msgid
  (Meitou-only), returns unchanged, so lookups are exact and English stays English.
- **Record names**: a translation changes `GameRecord.Name`; logic that matched English names uses `GameRecord.BaseName` (the name before
  translation): GLOBAL CONSTANTS, the money item, "Nameless", the blank squad, default empire, faction/start/appearance finds.
  Translation records that match nothing are dropped after loading.
- **UI strings**: strings that exist as msgids in `main.pot` go through the catalog at central sinks: UI bindings (`UiBindings.Coded`, captions and
  combo items), HUD `AddLines`, context-menu verbs, `Notice`, `Confirm`/`UpperTr`, `PlaceTooltip` (keys and values; bracketed headings by their
  inside), stat help (`StatHelpCatalog`), the loading tip, the day text, and the interpolated simulation messages (relations, bounties, research,
  campaigns, construction, fixtures, discoveries, crime names ([economy 11.2](../game/economy.md#112-how-a-bounty-is-gained)), crimes, arrests, bail,
  locks, shackles, trade; the host's save/load notices). The simulation keeps its English markers (the dialogue's engine `[Leave]` choice,
  matched by that text) and translates only the text it publishes or posts; the world hash and saves never contain translated text. Layout
  captions the original leaves English stay English.
- **Meitou's own texts** (`MeitouTranslations`): the launcher, the title menu (`UpperTr`) and the settings menu's drawn captions, hints and choices
  are not in Kenshi's catalog, so after it `Loc.Tr` looks them up in a second gettext catalog: `src/Meitou.Core/Localisation/locale/<code>.po`
  (embedded; one per Kenshi language: `de_DE`, `es_ES`, `fr_FR`, `it_IT`, `ja_JP`, `ko_KR`, `pt_BR`, `ru_RU`, `zh_CN`). Over it go `locale/<code>/meitou.po` of the Kenshi folder, then of each loaded mod in load order, a
  later file winning a message as a later resource location wins a file (`GameLanguage.MeitouFiles`; `<code>` is the active code, a translation
  mod's too), so a user or mod adds a language or overrides a string without a rebuild. Meitou's rule: the original has no such file. Same format
  and rules as the game's catalog, exact lookups, English msgids; the launcher reads the built-in and Kenshi folder files for its own labels.
  Settings rows measure the active font's wrapped label and hint and grow to fit. A text the code builds with a value in it
  keeps the English sentence as its msgid with the game's `{1}`, `{2}` placeholders (`LauncherText.Translate("Version {1} for {2}.", …)`), so
  the catalog matches it and a translation may reorder the holes. Game and simulation messages do the same (`Loc.Tr("{1} is arresting {2}!", …)`). A
  character's line in a message ("name: line", `LockSystem.Say`) keeps the line as its own game msgid ("Pick failed") inside a `{1}: {2}` frame,
  so every language with a game catalogue translates the line. A Meitou entry cannot override a game msgid (the game's catalog is asked first).
  Where the game's sense is wrong, the code asks under a `msgctxt` (`Loc.TrIn`; without a context entry it falls back to `Tr`): the load notice
  "Loaded" is `msgctxt "save notice"`, since the game's "Loaded" is the ranged-weapon label (`getGUIData.cpp`, zh "已装载"; **Verified** in
  en_GB `main.pot`, zh_CN `main.po`).
- **Keeping Meitou's catalogs complete**: `.github/scripts/loc-strings.mjs` finds Meitou's texts in the code (literal arguments of `Loc.Tr`,
  `Loc.TrIn` (with its context), `Loc.Plural` (as a pair), `Loc.Mark`, `Loc.TrCaption`, `LauncherText.Translate`, `UpperTr` and the launcher's
  `Tr` anywhere, inside ternaries and interpolation holes too, but not a literal used as a key, switch pattern or comparison; prose literals in
  the player-facing files its `SCOPE` lists; captions of the launcher's `.axaml` files) and writes them to `locale/meitou.pot` (`--write`).
  Without arguments it fails when the template is stale, when a built-in `.po` lacks a usable translation (no entry, fuzzy, empty or wrong
  number of plural forms, placeholders or leading and trailing whitespace that differ from the English), on duplicate entries, and on interpolated prose (`$"..."`, which no catalog can match)
  not in `locale/meitou.built`, the baseline of built texts still to convert (`--write` only removes lines from it). The build fails on the
  same findings, at the text: `analyzers/Meitou.Analyzers` ports the rules, MEI0001 for built texts and MEI0002 for a message a catalog
  cannot translate (the stale template and duplicate entries stay the script's). `locale/meitou.ignore`
  lists prose that is not Meitou's to translate in two sections: texts Kenshi's own catalog translates (a Meitou entry cannot override a game
  msgid), which still need an entry when the launcher shows them, since it reads no game catalog; and widget, skin, icon, font, brand and
  record names. `loc-fill.mjs` translates the gaps with Claude (a failed batch is split and retried, an unusable entry replaced) and marks each
  entry `#. machine-translated`; run it locally (`npm ci` in `.github/scripts`, `ANTHROPIC_API_KEY` set) when MEI0002 fails the build.
- **Fonts, tips, names**: through the overlay folder (`OgreScriptResources.ForInstall`) and `Loc.FindTextFile`.
- **New-game option help**: `NewGameHelpCatalog` reads the installed English `main.pot` source references and passes the exact msgid through `Loc.Tr`; no paraphrases or translated paragraphs are embedded. Missing translations fall back to the original English; a missing template leaves help empty. Font resources use the active locale overlay.
- All text files are read and written as UTF-8, except `settings.cfg`: without a byte order mark it is edited as Latin-1, so its bytes are kept.
