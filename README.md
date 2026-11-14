# Ziri's Workshop

<img src="assets/icon-option-handmade.png" width="96" align="right" alt="">

Keeps Ziri Shortfuse's WoW Forever addons on your PC installed and up to date.
Switch on the addons you want, and new versions arrive by themselves.

## Install (once)

1. Download **[ZirisWorkshop.exe](https://github.com/Zyrumi/ziris-workshop/raw/main/app/ZirisWorkshop.exe)**.
2. Run it. Windows may say "Windows protected your PC", because the app isn't signed.
   Click **More info**, then **Run anyway**.
3. The first time, a small box shows the WoW Forever folder it found. Click **Looks good**,
   or **Change...** to pick your World of Warcraft folder.
4. Switch on the addons you want. That's it.

The cog at the top opens these settings again later.

## What it does

- Starts with Windows and sits in the tray, down by the clock. Click the icon to open it.
  The cross at the top hides the window; it keeps running in the tray.
- Checks for updates when it starts, every 3 hours, and shortly after you log out of WoW.
- Downloads only from this repository, checks every download against its checksum,
  and unpacks it only into that addon's own folder.
- Switching an addon off moves its folder to the Recycle Bin. Switch it on again to get it back.
- Updates itself the same way, so you never need a new copy.
- New addons show up in the list by themselves.
- When one addon replaces older ones (Ziri's Toolkit replaces Loot Highlight and Fresh Ranks),
  everyone who had an old one gets the new one. The old folders go to the Recycle Bin and
  their saved settings come along.

After an update, restart WoW to use the new version.

Settings and a log live in `%LOCALAPPDATA%\ZirisWorkshop`.

## Remove it

Open it, click the cog and switch off **Start with Windows**, then right-click the tray icon and pick **Quit**.
Delete the `%LOCALAPPDATA%\ZirisWorkshop` folder and the "Ziri's Workshop" Start menu shortcut.
Your addons stay where they are.

## Publishing (for Ziri)

1. Bump `## Version` in the addon's `.toc`.
2. In this folder: `python tools/publish.py`
3. Check `git status`, then commit and push. Friends get it within 3 hours, or after they next log out of WoW.

- New addon: add it to `addons.json`.
- Addon that replaces others: give it `"replaces": ["OldId"]` in `addons.json` and remove the old ones there.
- App change: bump `AppVersion` in `src/ZirisWorkshop.cs`, then publish.
- Icon: `python tools/make_icon.py --from drawing.png` (PNG with a transparent background), then publish.
- Build only: `build.cmd` (uses the C# compiler that comes with Windows).
