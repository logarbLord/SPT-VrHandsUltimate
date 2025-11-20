using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[BepInPlugin("com.darzhar.bundlereplacer", "SPT Bundle Replacer mod", "1.3.5")]
public class BundleReplacer : BaseUnityPlugin
{
    private ConfigEntry<string> mappingFilePathCfg;
    private ConfigEntry<string> bundlesFolderCfg;
    private ConfigEntry<bool> verboseLoggingCfg;
    private static ManualLogSource staticLogger;

    // in-memory map: original filename -> replacement full path
    private static Dictionary<string, string> replacementMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private Harmony harmony;

    // convenience property used by static Harmony prefixes
    public static bool VerboseLogging => _verboseLogging;
    private static bool _verboseLogging = false;

    void Awake()
    {
        staticLogger = Logger;

        mappingFilePathCfg = Config.Bind("General", "MappingsFile", "BepInEx/plugins/BundleReplacer/mappings.json",
            "Mapping File Location (relative to SPT Folder)");

        bundlesFolderCfg = Config.Bind("General", "BundlesFolder", "BepInEx/plugins/BundleReplacer/bundles",
            "Bundles Folder location (relative to SPT Folder) |  Files are matched by EXACT filename.");

        verboseLoggingCfg = Config.Bind("Debug", "VerboseLogging", false,
            "If true the plugin logs every AssetBundle.LoadFromFile(Async) path it sees. Use for debugging to detect how the game requests the bundles.");

        _verboseLogging = verboseLoggingCfg.Value;

        LoadMappings();

        harmony = new Harmony("com.darzhar.bundlereplacer");
        harmony.PatchAll();

        Logger.LogInfo($"BundleReplacer loaded. Mapping entries: {replacementMap.Count}. VerboseLogging={_verboseLogging}");
    }

    private void LoadMappings()
    {
        replacementMap.Clear();

        string gameRoot = GetGameRoot();

        // Define mapping file path
        string mappingsFile = mappingFilePathCfg.Value;
        if (!Path.IsPathRooted(mappingsFile))
            mappingsFile = Path.Combine(gameRoot, mappingsFile.Replace('/', Path.DirectorySeparatorChar));

        bool mappingsLoaded = false;
        if (File.Exists(mappingsFile))
        {
            try
            {
                var json = File.ReadAllText(mappingsFile);
                var dict = MiniJSON.Deserialize(json) as Dictionary<string, object>;
                if (dict == null)
                {
                    Logger.LogWarning($"Mappings file {mappingsFile} could not be parsed as JSON object.");
                }
                else
                {
                    foreach (var kv in dict)
                    {
                        string key = kv.Key.Trim();
                        string val = kv.Value.ToString().Trim();

                        string resolved = val;
                        if (!Path.IsPathRooted(resolved))
                            resolved = Path.Combine(gameRoot, resolved.Replace('/', Path.DirectorySeparatorChar));

                        replacementMap[key] = resolved;
                        Logger.LogInfo($"Mapping loaded (from JSON): {key} -> {resolved}");
                    }
                    mappingsLoaded = true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load mappings: {ex}");
            }
        }
        else
        {
            Logger.LogInfo($"Mappings file not found at: {mappingsFile}. Will scan bundles folder instead (or combine if both exist).");
        }

        // Define bundles folder
        string bundlesFolder = bundlesFolderCfg.Value;
        if (!Path.IsPathRooted(bundlesFolder))
            bundlesFolder = Path.Combine(gameRoot, bundlesFolder.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(bundlesFolder))
        {
            var files = Directory.GetFiles(bundlesFolder, "*.bundle", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                Logger.LogInfo($"Bundles folder exists but contains no .bundle files: {bundlesFolder}");
            }
            else
            {
                foreach (var file in files)
                {
                    string fname = Path.GetFileName(file);
                    if (replacementMap.ContainsKey(fname))
                    {
                        // explicit mapping takes precedence; just log that we skipped auto-mapping
                        Logger.LogInfo($"Auto-scan found {fname} but JSON mapping exists; skipping auto-map for this filename.");
                        continue;
                    }

                    replacementMap[fname] = file;
                    Logger.LogInfo($"Auto-mapped: {fname} -> {file}");
                }
            }
        }
        else
        {
            Logger.LogInfo($"Bundles folder not found: {bundlesFolder}. Create it and drop replacement .bundle files there to enable auto-mapping.");
        }

        if (!mappingsLoaded && replacementMap.Count == 0)
        {
            Logger.LogWarning("No bundle replacements found. Create mappings.json or drop .bundle files into the bundles folder.");
        }
    }

    private static string GetGameRoot()
    {
        try
        {
            var pathsType = Type.GetType("BepInEx.Paths, BepInEx");
            if (pathsType != null)
            {
                var p = pathsType.GetProperty("GameRootPath");
                if (p != null)
                {
                    var v = p.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
        }
        catch { /* ignore */ }

        return Directory.GetCurrentDirectory();
    }

    // Central handler used by Harmony patches below for synchronous replacements
    public static bool TryReplaceBundleByFilename(string requestedPath, out AssetBundle result)
    {
        result = null;
        try
        {
            if (string.IsNullOrEmpty(requestedPath)) return true;
            string filename = Path.GetFileName(requestedPath);
            if (string.IsNullOrEmpty(filename)) return true;

            // debug: log when we see the filename the user cares about or verbose logging enabled
            if (_verboseLogging || filename.IndexOf("bear_hands_skin.bundle", StringComparison.OrdinalIgnoreCase) >= 0)
                staticLogger.LogInfo($"[DEBUG] TryReplaceBundleByFilename requestedPath='{requestedPath}' filename='{filename}'");

            if (!replacementMap.TryGetValue(filename, out string replacementPath))
                return true;

            if (!File.Exists(replacementPath))
            {
                staticLogger.LogWarning($"Replacement file for '{filename}' listed but not found at '{replacementPath}'.");
                return true;
            }

            staticLogger.LogInfo($"Replacing bundle '{filename}' with '{replacementPath}' (sync)");

            // Load bytes and create AssetBundle from memory (avoids recursion into LoadFromFile)
            byte[] bytes = File.ReadAllBytes(replacementPath);
            result = AssetBundle.LoadFromMemory(bytes);
            if (result == null)
            {
                staticLogger.LogError($"AssetBundle.LoadFromMemory returned null for replacement file '{replacementPath}'");
                return true; // fallback to original
            }

            return false; // we handled it; skip original
        }
        catch (Exception ex)
        {
            staticLogger.LogError($"Exception in TryReplaceBundleByFilename: {ex}");
            result = null;
            return true;
        }
    }

    // New: central handler for async replacements
    public static bool TryReplaceBundleByFilenameAsync(string requestedPath, out AssetBundleCreateRequest result)
    {
        result = null;
        try
        {
            if (string.IsNullOrEmpty(requestedPath)) return true;
            string filename = Path.GetFileName(requestedPath);
            if (string.IsNullOrEmpty(filename)) return true;

            if (_verboseLogging || filename.IndexOf("bear_hands_skin.bundle", StringComparison.OrdinalIgnoreCase) >= 0)
                staticLogger.LogInfo($"[DEBUG] TryReplaceBundleByFilenameAsync requestedPath='{requestedPath}' filename='{filename}'");

            if (!replacementMap.TryGetValue(filename, out string replacementPath))
                return true;

            if (!File.Exists(replacementPath))
            {
                staticLogger.LogWarning($"Replacement file for '{filename}' listed but not found at '{replacementPath}'.");
                return true;
            }

            staticLogger.LogInfo($"Replacing bundle '{filename}' with '{replacementPath}' (async)");

            // Load bytes and create AssetBundle via the async API
            byte[] bytes = File.ReadAllBytes(replacementPath);
            result = AssetBundle.LoadFromMemoryAsync(bytes);
            if (result == null)
            {
                staticLogger.LogError($"AssetBundle.LoadFromMemoryAsync returned null for replacement file '{replacementPath}'");
                return true; // fallback to original
            }

            return false; // handled; skip original
        }
        catch (Exception ex)
        {
            staticLogger.LogError($"Exception in TryReplaceBundleByFilenameAsync: {ex}");
            result = null;
            return true;
        }
    }

    void OnDestroy()
    {
        try
        {
            if (harmony == null) return;

            var unpatchSelf = typeof(Harmony).GetMethod("UnpatchSelf");
            if (unpatchSelf != null)
            {
                unpatchSelf.Invoke(harmony, null);
                return;
            }

            var unpatchAllInstance = typeof(Harmony).GetMethod("UnpatchAll", new Type[] { typeof(string) });
            if (unpatchAllInstance != null)
            {
                unpatchAllInstance.Invoke(harmony, new object[] { harmony.Id });
                return;
            }

            var unpatchIDStatic = typeof(Harmony).GetMethod("UnpatchID", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (unpatchIDStatic != null)
            {
                unpatchIDStatic.Invoke(null, new object[] { harmony.Id });
                return;
            }
        }
        catch { }
    }
}

// Harmony patches for AssetBundle.LoadFromFile overloads and LoadFromFileAsync replacements/logging.
[HarmonyPatch]
static class AssetBundlePatches
{
    // Helper for debug logging in prefixes
    private static void MaybeLogPath(string method, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            string filename = Path.GetFileName(path);
            // Change 'SpecificFucking.bundle' to one thats causing issues
            if (BundleReplacer.VerboseLogging || (filename?.IndexOf("SpecificFucking.bundle", StringComparison.OrdinalIgnoreCase) >= 0))
                BundleReplacerStaticLog($"[{method}] path='{path}' filename='{filename}'");
        }
        catch { }
    }

    // wrapper to ensure static access to the plugin logger (safe if plugin loaded)
    private static void BundleReplacerStaticLog(string msg)
    {
        try
        {
            var loggerField = typeof(BundleReplacer).GetField("staticLogger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var logger = loggerField?.GetValue(null) as ManualLogSource;
            logger?.LogInfo(msg);
        }
        catch { }
    }

    // SYNC LoadFromFile prefixes (existing behavior)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AssetBundle), "LoadFromFile", new Type[] { typeof(string) })]
    static bool Prefix_LoadFromFile_1(string path, ref AssetBundle __result)
    {
        MaybeLogPath("AssetBundle.LoadFromFile", path);
        return BundleReplacer.TryReplaceBundleByFilename(path, out __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AssetBundle), "LoadFromFile", new Type[] { typeof(string), typeof(uint) })]
    static bool Prefix_LoadFromFile_2(string path, uint crc, ref AssetBundle __result)
    {
        MaybeLogPath("AssetBundle.LoadFromFile(crc)", path);
        return BundleReplacer.TryReplaceBundleByFilename(path, out __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AssetBundle), "LoadFromFile", new Type[] { typeof(string), typeof(uint), typeof(ulong) })]
    static bool Prefix_LoadFromFile_3(string path, uint crc, ulong offset, ref AssetBundle __result)
    {
        MaybeLogPath("AssetBundle.LoadFromFile(crc,offset)", path);
        return BundleReplacer.TryReplaceBundleByFilename(path, out __result);
    }

    // ASYNC LoadFromFileAsync prefixes — now replacing with LoadFromMemoryAsync if mapping exists
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AssetBundle), "LoadFromFileAsync", new Type[] { typeof(string) })]
    static bool Prefix_LoadFromFileAsync_1(string path, ref AssetBundleCreateRequest __result)
    {
        MaybeLogPath("AssetBundle.LoadFromFileAsync", path);
        return BundleReplacer.TryReplaceBundleByFilenameAsync(path, out __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AssetBundle), "LoadFromFileAsync", new Type[] { typeof(string), typeof(uint) })]
    static bool Prefix_LoadFromFileAsync_2(string path, uint crc, ref AssetBundleCreateRequest __result)
    {
        MaybeLogPath("AssetBundle.LoadFromFileAsync(crc)", path);
        return BundleReplacer.TryReplaceBundleByFilenameAsync(path, out __result);
    }
}

// Minimal JSON parser for simple mapping (so we don't introduce Newtonsoft dependency). ((thanks githubCopilot))
static class MiniJSON
{
    public static class Json
    {
        public static object Deserialize(string json) => MiniJSON.Deserialize(json);
    }

    public static object JsonDeserialize(string json) => Deserialize(json);

    public static object Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return MiniJsonParser.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    // Very small JSON parser only for { "key": "value", ... } where values are strings.
    private static class MiniJsonParser
    {
        public static object Parse(string json)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{') return null;
            i++; // skip {
            while (true)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '}') { i++; break; }
                string key = ParseString(json, ref i);
                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] != ':') return null;
                i++; // :
                SkipWhitespace(json, ref i);
                string val = ParseValueOrString(json, ref i);
                dict[key] = val;
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
            }
            return dict;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++; // skip opening "
            int start = i;
            while (i < s.Length)
            {
                if (s[i] == '"') break;
                if (s[i] == '\\') i++; // skip escape
                i++;
            }
            string raw = s.Substring(start, i - start);
            if (i < s.Length && s[i] == '"') i++; // skip closing "
            return Unescape(raw);
        }

        private static string ParseValueOrString(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '"') return ParseString(s, ref i);
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
            string raw = s.Substring(start, i - start).Trim();
            return Unescape(raw);
        }

        private static string Unescape(string s)
        {
            if (s == null) return null;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }
    }
}