# Getting Kenshi for Meitou on macOS

MeitouClient does **not** include Kenshi's data. It reads the original game's files from a Kenshi installation you already
own. The launcher only needs a folder containing `data/gamedata.base`; it does not start Steam or install Kenshi for you.
Its one use of Steam is the optional [Workshop sync](#workshop-mods) below.

Kenshi is a Windows build. The Steam client on macOS therefore disables its install button; this is a Kenshi/Steam
distribution limit, not something the launcher can bypass. The options below are for users who own Kenshi.

## 1. Copy an existing Windows install (simplest)

On a Windows PC where Kenshi is installed through Steam:

1. Open `Steam/steamapps/common/`.
2. Copy the whole `Kenshi` folder to the Mac (external drive, network share, AirDrop, etc.).
3. Put it somewhere permanent, such as `~/Games/Kenshi`.
4. In the Meitou launcher, choose **Settings → Kenshi folder → Browse** and select that folder.

The folder must contain `data/gamedata.base`. Meitou does not need `kenshi_x64.exe` or Steam to be running.

## 2. Windows Steam through CrossOver or Whisky

This is the most self-contained route if you do not have a Windows PC:

1. Create a Windows bottle in CrossOver or Whisky.
2. Install the Windows Steam client inside the bottle.
3. Install Kenshi in that Steam client.
4. Point Meitou at the bottle's `Kenshi` folder. The launcher searches the usual CrossOver and Whisky bottle folders;
   use **Find**, or browse to `drive_c/Program Files (x86)/Steam/steamapps/common/Kenshi` inside the bottle.

Kenshi does not need to run inside the bottle; only its data folder is used.

## 3. GOG or another legitimate installer

Install Kenshi into a Wine/CrossOver/Whisky prefix with the GOG offline installer, then point the launcher at the
resulting `Kenshi` folder. The launcher also scans common GOG locations inside Wine prefixes.

## 4. Advanced: DepotDownloader/SteamCMD

Only use this if you own Kenshi on Steam and have no Windows PC or Windows-compatible bottle.

This is an **external, unsupported** route:

- [DepotDownloader](https://github.com/SteamRE/DepotDownloader) is a third-party command-line tool. It is not part of
  Meitou and the launcher does not download, bundle or run it.
- It needs a Steam account that owns Kenshi and may ask for Steam Guard/2FA. Never give your password to the launcher;
  run the tool yourself in Terminal.
- Follow the tool's README. Kenshi's Steam app id is **233860**. Download the Windows app/depots into a folder.
- Point the launcher at the resulting `Kenshi` folder (the one containing `data/gamedata.base`).
- SteamCMD is an alternative; it requires the same ownership and login rules.

This route may be affected by Steam's terms and the game's EULA; use it only for a copy you are entitled to. The
launcher deliberately does not automate Steam credentials or depot downloads.

## Workshop mods

The Steam client on macOS cannot install Kenshi, but it does download the Workshop items you are subscribed to (subscribe in the Steam client
or on the Workshop web pages). Start Steam, open the launcher's **Mods** page and click **Sync workshop**: the launcher asks the running client to
download or update them and lists them when they are installed. They are read from
`~/Library/Application Support/Steam/steamapps/workshop/content/233860`, wherever your Kenshi folder is, also when Steam is closed later.
The button needs Steam installed in the usual place and an account that owns Kenshi; the launcher never starts Steam or asks for credentials.
Details and what is verified: [launcher](launcher.md#workshop-sync-macos).

## If the folder is not found

- Make sure you selected the folder that directly contains `data/gamedata.base`, not its parent or `data/`.
- Steam on macOS stores its library under `~/Library/Application Support/Steam`; Finder hides `Library` until you use
  **Go → Go to Folder**.
- CrossOver bottles are under `~/Library/Application Support/CrossOver/Bottles`; Whisky uses its container folder.
- You can always use **Browse** instead of **Find**.

If you do not own Kenshi, buy it from [Steam](https://store.steampowered.com/app/233860/Kenshi/) or
[GOG](https://www.gog.com/game/kenshi). Meitou cannot provide the assets for you.
