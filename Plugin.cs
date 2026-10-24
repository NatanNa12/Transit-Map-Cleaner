using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

#pragma warning disable CS0618

namespace Natan_Na.Transit.Ram.Cleaner
{
    [BepInPlugin("Natan_Na.Transit.Ram.Cleaner", "Transit RAM Cleaner", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> UnloadMapBundles;
        internal static ConfigEntry<bool> ForceCleanup;
        internal static ConfigEntry<bool> Verbose;

        void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("1. General", "Enabled", true,
                "Master switch for the whole mod.");
            UnloadMapBundles = Config.Bind("1. General", "Unload previous map bundles", true,
                "Release the AssetBundles that held the map you just left. The game itself never releases them, so every visited map stays in RAM.");
            ForceCleanup = Config.Bind("2. Memory", "Force GC and trim working set", true,
                "After releasing the bundles, run a forced full GC and trim the process working set. Leave on unless you see hitches.");
            Verbose = Config.Bind("2. Memory", "Log details", true,
                "Log which bundles were released and how much memory the process holds before/after.");

            new Harmony("Natan_Na.Transit.Ram.Cleaner").PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("Transit RAM Cleaner loaded. Map scene bundles will be released between raids.");
        }

        internal void BeginCleanup(string reason)
        {
            if (!Enabled.Value) return;
            if (EFT.InGameStatus.InRaid) return;
            StartCoroutine(SceneBundleCleaner.CleanupRoutine(reason));
        }

        internal static float WorkingSetGb()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    return p.WorkingSet64 / 1024f / 1024f / 1024f;
            }
            catch { return 0f; }
        }
    }

    internal static class SceneBundleCleaner
    {
        private sealed class Target
        {
            internal AssetBundle Bundle;
            internal string Name;
            internal int SceneCount;
        }

        private static readonly HashSet<AssetBundle> Pending = new HashSet<AssetBundle>();
        private static readonly Dictionary<string, string> SceneToBundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void RecordSceneLoad(string bundleName, string sceneName)
        {
            if (string.IsNullOrEmpty(bundleName) || string.IsNullOrEmpty(sceneName)) return;
            var scene = Path.GetFileNameWithoutExtension(sceneName);
            if (!string.IsNullOrEmpty(scene)) SceneToBundle[scene] = bundleName;
        }

        internal static void CaptureEndingRaid()
        {
            if (!Plugin.UnloadMapBundles.Value) return;

            try
            {
                var manager = EFT.Assets.Manager as EFT.AssetsManager.AssetsManager;
                if (manager == null) return;

                var scenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sceneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectLoadedScenes(scenePaths, sceneNames);
                if (scenePaths.Count == 0 && sceneNames.Count == 0) return;

                var found = 0;

                foreach (var sceneName in sceneNames)
                {
                    if (!SceneToBundle.TryGetValue(sceneName, out var bundleName)) continue;
                    var bundle = ResolveTrackedBundle(manager, bundleName);
                    if (bundle != null && Pending.Add(bundle)) found++;
                }

                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null || Pending.Contains(bundle)) continue;
                    if (!IsTracked(manager, bundle)) continue;

                    string[] bundleScenes;
                    try { bundleScenes = bundle.GetAllScenePaths(); }
                    catch { continue; }
                    if (bundleScenes == null || bundleScenes.Length == 0) continue;

                    if (MatchesLoadedScene(bundleScenes, scenePaths, sceneNames) && Pending.Add(bundle)) found++;
                }

                if (found > 0 && Plugin.Verbose.Value)
                    Plugin.Log.LogInfo($"[capture] queued {found} map bundle(s) for release ({Pending.Count} pending).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[capture] " + e);
            }
        }

        internal static System.Collections.IEnumerator CleanupRoutine(string reason)
        {
            if (Pending.Count == 0) yield break;

            var before = Plugin.WorkingSetGb();
            var unloaded = UnloadPending(out var skipped);

            var releaseOp = Resources.UnloadUnusedAssets();
            while (releaseOp != null && !releaseOp.isDone) yield return null;

            if (Plugin.ForceCleanup.Value)
            {
                try { InGameMemoryManagement.Collect(2, GCCollectionMode.Forced, true, true, true); }
                catch (Exception e) { Plugin.Log.LogWarning("[gc] " + e.Message); }
                try { InGameMemoryManagement.EmptyWorkingSet(); }
                catch (Exception e) { Plugin.Log.LogWarning("[trim] " + e.Message); }
            }

            if (Plugin.Verbose.Value)
            {
                var after = Plugin.WorkingSetGb();
                Plugin.Log.LogInfo(
                    $"[{reason}] released {unloaded} map bundle(s)" +
                    (skipped > 0 ? $", {skipped} kept (scene still loaded)" : "") +
                    $"; working set {before:0.00} GB -> {after:0.00} GB.");
            }
        }

        private static int UnloadPending(out int skipped)
        {
            skipped = 0;
            if (Pending.Count == 0) return 0;
            if (!Plugin.UnloadMapBundles.Value) { Pending.Clear(); return 0; }

            var unloaded = 0;

            try
            {
                var manager = EFT.Assets.Manager as EFT.AssetsManager.AssetsManager;
                if (manager == null)
                {
                    Plugin.Log.LogWarning("[unload] EFT.Assets.Manager is not an AssetsManager; cannot release map bundles.");
                    return 0;
                }

                var inUsePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var inUseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectLoadedScenes(inUsePaths, inUseNames);

                var targets = new List<Target>();
                var stillPending = new List<AssetBundle>();

                foreach (var bundle in Pending)
                {
                    if (bundle == null) continue;

                    string name;
                    string[] bundleScenes;
                    try
                    {
                        name = bundle.name;
                        bundleScenes = bundle.GetAllScenePaths();
                    }
                    catch
                    {
                        stillPending.Add(bundle);
                        continue;
                    }

                    if (MatchesLoadedScene(bundleScenes, inUsePaths, inUseNames))
                    {
                        skipped++;
                        stillPending.Add(bundle);
                        continue;
                    }

                    targets.Add(new Target
                    {
                        Bundle = bundle,
                        Name = name,
                        SceneCount = bundleScenes?.Length ?? 0
                    });
                }

                foreach (var target in targets)
                {
                    if (!ReleaseBundle(manager, target.Bundle))
                    {
                        stillPending.Add(target.Bundle);
                        continue;
                    }

                    unloaded++;
                    if (Plugin.Verbose.Value)
                        Plugin.Log.LogInfo($"[unload] '{target.Name}' released ({target.SceneCount} scene(s)).");
                }

                Pending.Clear();
                foreach (var bundle in stillPending) Pending.Add(bundle);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[unload] " + e);
            }

            return unloaded;
        }

        private static bool ReleaseBundle(EFT.AssetsManager.AssetsManager manager, AssetBundle bundle)
        {
            try
            {
                var tracked = manager.BundlesManager._bundles;
                var keys = new List<string>();
                foreach (var pair in tracked)
                {
                    if (pair.Value != null && ReferenceEquals(pair.Value.AssetBundle, bundle)) keys.Add(pair.Key);
                }

                if (keys.Count == 0)
                {
                    if (Plugin.Verbose.Value)
                        Plugin.Log.LogInfo($"[unload] '{bundle.name}' is not tracked by AssetsManager; skipped.");
                    return false;
                }

                foreach (var key in keys)
                {
                    for (var guard = 0; guard < 64; guard++)
                    {
                        if (!tracked.TryGetValue(key, out var reference) || reference == null) break;

                        var counter = reference.Counter;
                        manager.UnloadBundle(key, false);

                        if (counter <= 1) break;
                        if (tracked.TryGetValue(key, out var after) && ReferenceEquals(after, reference) && after.Counter == counter)
                            break;
                    }

                    DropStaleLoadOperations(manager, key);
                }

                foreach (var pair in tracked)
                {
                    if (pair.Value != null && ReferenceEquals(pair.Value.AssetBundle, bundle)) return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[unload] '{bundle.name}' failed: {e}");
                return false;
            }
        }

        private static void DropStaleLoadOperations(EFT.AssetsManager.AssetsManager manager, string bundleKey)
        {
            var ops = manager._loadAssetOperations;
            if (ops == null || ops.Count == 0) return;

            List<string> stale = null;
            foreach (var pair in ops)
            {
                var op = pair.Value;
                if (op == null) continue;
                if (!string.Equals(op._bundleName, bundleKey, StringComparison.OrdinalIgnoreCase)) continue;
                (stale ?? (stale = new List<string>())).Add(pair.Key);
            }

            if (stale != null)
            {
                foreach (var key in stale) ops.Remove(key);
            }
        }

        private static bool IsTracked(EFT.AssetsManager.AssetsManager manager, AssetBundle bundle)
        {
            foreach (var pair in manager.BundlesManager._bundles)
            {
                if (pair.Value != null && ReferenceEquals(pair.Value.AssetBundle, bundle)) return true;
            }
            return false;
        }

        private static AssetBundle ResolveTrackedBundle(EFT.AssetsManager.AssetsManager manager, string bundleName)
        {
            var tracked = manager.BundlesManager._bundles;
            if (tracked.TryGetValue(bundleName, out var exact) && exact != null && exact.AssetBundle != null)
                return exact.AssetBundle;

            var fileName = Path.GetFileName(bundleName);
            foreach (var pair in tracked)
            {
                if (pair.Value == null || pair.Value.AssetBundle == null) continue;
                if (!string.Equals(pair.Key, bundleName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileName(pair.Key), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                return pair.Value.AssetBundle;
            }

            return null;
        }

        private static void CollectLoadedScenes(HashSet<string> paths, HashSet<string> names)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (!string.IsNullOrEmpty(scene.name)) names.Add(scene.name);
                if (!string.IsNullOrEmpty(scene.path)) paths.Add(Normalize(scene.path));
            }
        }

        private static bool MatchesLoadedScene(string[] bundleScenePaths, HashSet<string> scenePaths, HashSet<string> sceneNames)
        {
            if (bundleScenePaths == null) return false;

            foreach (var raw in bundleScenePaths)
            {
                if (string.IsNullOrEmpty(raw)) continue;

                var path = Normalize(raw);
                if (scenePaths.Contains(path)) return true;

                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(fileName) && sceneNames.Contains(fileName)) return true;
            }

            return false;
        }

        private static string Normalize(string path) => path.Replace('\\', '/');
    }

    internal static class Patches
    {
        [HarmonyPatch(typeof(EFT.AssetsManager.AssetsManager), nameof(EFT.AssetsManager.AssetsManager.LoadScene))]
        internal static class RecordSceneLoads
        {
            [HarmonyPrefix]
            internal static void Prefix(string bundleName, string sceneName)
            {
                SceneBundleCleaner.RecordSceneLoad(bundleName, sceneName);
            }
        }

        [HarmonyPatch(typeof(EFT.TarkovApplication), nameof(EFT.TarkovApplication.TryUnloadGame))]
        internal static class CaptureOnRaidEnd
        {
            [HarmonyPrefix]
            internal static void Prefix()
            {
                if (!EFT.InGameStatus.InRaid) return;
                SceneBundleCleaner.CaptureEndingRaid();
            }
        }

        [HarmonyPatch(typeof(EFT.TarkovApplication), nameof(EFT.TarkovApplication.PrepareGame))]
        internal static class CleanupAtMenu
        {
            [HarmonyPrefix]
            internal static void Prefix()
            {
                if (Plugin.Instance == null) return;
                Plugin.Instance.BeginCleanup("raid-end");
            }
        }
    }
}
