# Ziri's Workshop

<img src="assets/icon-option-ziri.png" width="96" align="right" alt="">

Keeps Ziri Shortfuse's WoW Forever addons on your PC installed and up to date.
Tick the addons you want, and new versions arrive by themselves.

## Install (once)

1. Download **[ZirisWorkshop.exe](https://github.com/Zyrumi/ziris-workshop/raw/main/app/ZirisWorkshop.exe)**.
2. Run it. Windows may say "Windows protected your PC", because the app isn't signed.
   Click **More info**, then **Run anyway**.
3. Tick the addons you want. That's it.

It finds WoW Forever by itself. If it can't, click **Change...** and pick your
World of Warcraft folder.

## What it does

- Starts with Windows and sits in the tray, down by the clock. Click the icon to open it.
- Checks for updates when it starts, every 3 hours, and shortly after you log out of WoW.
- Downloads only from this repository, checks every download against its checksum,
  and unpacks it only into that addon's own folder.
- Unticking an addon moves its folder to the Recycle Bin. Tick it again to get it back.
- Updates itself the same way, so you never need a new copy.
- New addons show up in the list by themselves.

After an update, restart WoW to use the new version.

Settings and a log live in `%LOCALAPPDATA%\ZirisWorkshop`.

## Remove it

Open it, untick **Start with Windows**, then right-click the tray icon and pick **Quit**.
Delete the `%LOCALAPPDATA%\ZirisWorkshop` folder and the "Ziri's Workshop" Start menu shortcut.
Your addons stay where they are.

## Publishing (for Ziri)

1. Bump `## Version` in the addon's `.toc`.
2. In this folder: `python tools/publish.py`
3. Check `git status`, then commit and push. Friends get it within 3 hours, or after they next log out of WoW.

- New addon: add it to `addons.json`.
- App change: bump `AppVersion` in `src/ZirisWorkshop.cs`, then publish.
- Icon: `python tools/make_icon.py ziri` (or `bomb`), then publish.
- Build only: `build.cmd` (uses the C# compiler that comes with Windows).
