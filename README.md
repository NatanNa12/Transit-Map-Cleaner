# Transit RAM Cleaner

Release the previous map's AssetBundles after a raid so they do not stay in memory.

**Author:** Natan_Na
**Version:** 1.0.0
**SPT Version:** 4.1.6
**License:** MIT

---

## What This Mod Does

EFT keeps the AssetBundles of every map you load for the lifetime of the game process. After a few transits the process is still holding geometry, textures and audio from maps you have already left.

This plugin tracks which AssetBundle loaded each map scene. When a raid ends, it records the bundles of the map you are leaving. Back at the menu, before the next location is prepared, it:

- unloads those bundles through EFT's own `AssetsManager`;
- removes stale load operations left for the same bundles;
- calls `Resources.UnloadUnusedAssets`;
- optionally runs a forced GC and trims the process working set.

A bundle whose scene is still loaded is never released, and the cleanup only runs between raids, never while you are in one.

---

## Configuration

File: `BepInEx/config/com.natan.transitramcleaner.cfg`

| Section | Key | Default | Meaning |
|---|---|---|---|
| 1. General | Enabled | `true` | Master switch for the whole mod |
| 1. General | Unload previous map bundles | `true` | Release the map bundles left by the previous raid |
| 2. Memory | Force GC and trim working set | `true` | Forced full GC and working-set trim after the bundles are released |
| 2. Memory | Log details | `true` | Log released bundles and the working set before/after |

---

## Installation

Extract `Transit.Ram.Cleaner.zip` into the SPT game folder:

```
BepInEx/plugins/Transit.Ram.Cleaner.dll
```

Launch the server, then the game. There is no server component and nothing else to install.

---

## Notes

- The cleanup happens on the way back to the menu. A short hitch is possible right after a raid; turn off *Force GC and trim working set* if you prefer to leave the working set alone.
- Only map scene bundles are released. Loot, UI, audio and other bundles loaded by the game are left untouched.
- The mod does nothing while a raid is running, and nothing when *Enabled* is off.
- Nothing outside the process is changed: no profile, quest, trader, item or server file is touched.
- Details are written to the BepInEx console. Turn off *Log details* to keep the log quiet.

---

## Building

Requires the .NET SDK and an SPT 4.1.6 installation. The project reads the game assemblies from `D:\SPT` by default; pass `SptDir` if yours is installed elsewhere:

```sh
dotnet build Transit.Ram.Cleaner.sln -c Release
dotnet build Transit.Ram.Cleaner.sln -c Release -p:SptDir="C:\Path\To\SPT\"
```

The build produces `bin/Release/Transit.Ram.Cleaner.dll` and `Transit.Ram.Cleaner.zip` in the project folder.

To copy the compiled DLL straight into a local SPT install, add `-p:DeployToGame=true` to the build command. This is off by default.

---

## Support

If this mod is useful to you and you want to support development, you can buy me a coffee on Ko-fi: [ko-fi.com/natanna](https://ko-fi.com/natanna)

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/natanna)

---

## License

MIT. See [LICENSE](LICENSE).
