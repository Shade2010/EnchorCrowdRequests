# Enchor - Crowd Requests — uGUI build

A BepInEx 6 (IL2CPP) plugin for **Clone Hero v1.x**: search and download
[enchor.us](https://www.enchor.us/) songs from inside the game, with a **uGUI** interface that matches
Clone Hero's look and shows **album-art thumbnails** in the results list.

Press **F9** in-game to open it (configurable in `BepInEx\config\enchor.crowdrequests.cfg`).

## Features
- Search + instrument/difficulty filters + paging.
- Results list with **album art**, name/artist, charter/length/difficulties, and an issues marker.
- **Download** (immediate) or **+ Queue**; a queue panel with **Download All** / **Clear** / remove.
- **Change Folder…** — native Windows folder picker for your download location (persists).
- Auto-rescan after downloads so songs appear without leaving the menu.
- Dims the game + suppresses keyboard while open so typing doesn't drive the menu behind it.

## Install (no build needed)
1. Install **BepInEx 6 (IL2CPP, win-x64)** into your Clone Hero folder and run the game once.
2. Unzip `dist\EnchorCrowdRequests-Installer.zip` and run `install.ps1` (it auto-detects Clone Hero and copies the DLL),
   or just copy `EnchorCrowdRequests.dll` into `<Clone Hero>\BepInEx\plugins\`.
3. Launch Clone Hero and press **F9**.

## Build from source
1. Clone Hero v1.x with **BepInEx 6 (IL2CPP, win-x64)** installed and run once (generates `BepInEx\interop`).
2. A .NET SDK 8 (system-wide, or the local `..\dotnet` install).
3. From this folder: `.\build.ps1` (auto-detects the install, builds, copies the DLL to `BepInEx\plugins`).
4. To produce the shareable installer zip: `.\make-installer.ps1`.

## Notes
- Reuses the proven backend from the IMGUI build (`EncoreApi`, `SngExtractor`, `SongPath`, `SimpleJSON`,
  `Rescan`, `InputBlock`, `FolderPicker`); only the UI layer (`UIController.cs`) is new (uGUI + TextMeshPro).
- Album art: downloaded jpg → `Texture2D` → `RawImage` scaled by its RectTransform.
- See `PROGRESS.md` for milestone status and IL2CPP/uGUI implementation notes.
