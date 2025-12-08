using System;
using System.Collections.Generic;

namespace Flux
{
    // Minimal, fresh AddonManager scaffold.
    // Keeps the UI working while providing a simple, clean backend to build on.
    public class AddonManager
    {
        private readonly FrameManager? _frameManager;
        private readonly Dictionary<string, LuaRunner> _runners = new();

        public AddonManager(FrameManager? frameManager = null)
        {
            _frameManager = frameManager;
            // default to preferring repo libs (UI can toggle)
            UseRepoLibs = true;
        }

        // Simple persisted-ish toggle (no file I/O in this fresh restart)
        public bool UseRepoLibs { get; set; }

        // Create a simple toggle visual in the FrameManager (UI convenience)
        public void CreateToggleVisual(FrameManager frameManager, double x = 8, double y = 8)
        {
            if (frameManager == null) return;
            var tf = frameManager.CreateFrame(null);
            tf.X = x; tf.Y = y; tf.Width = 200; tf.Height = 28;
            tf.Text = GetToggleText();
            tf.OnClickAction = () =>
            {
                UseRepoLibs = !UseRepoLibs;
                tf.Text = GetToggleText();
            };
            frameManager.UpdateVisual(tf);
        }

        private string GetToggleText() => UseRepoLibs ? "Libs: Repo" : "Libs: Local";

        // Load an addon folder and return a minimal LuaRunner instance.
        public LuaRunner? LoadAddonFromFolder(string folderPath, EventHandler<string>? outputCallback = null)
        {
            if (string.IsNullOrEmpty(folderPath)) return null;
            var name = System.IO.Path.GetFileName(folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            var runner = new LuaRunner(name, _frameManager, folderPath);
            // Load saved variables from data/savedvars/{addon}.json if present
            try
            {
                var svDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "data", "savedvars");
                var svPath = System.IO.Path.Combine(svDir, name + ".json");
                if (System.IO.File.Exists(svPath))
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(svPath);
                        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                        if (dict != null) runner.LoadSavedVariables(dict);
                        runner.EmitOutput($"[AddonManager] Loaded saved variables from: {svPath}");
                    }
                    catch (Exception ex) { runner.EmitOutput("[AddonManager] Failed to load saved variables: " + ex.Message); }
                }
            }
            catch { }
            // If configured to prefer repo libs, preload them before running the addon.
            try
            {
                if (UseRepoLibs)
                {
                    // Prefer uppercase 'LIBS' then lowercase 'libs'
                    var cwd = System.IO.Directory.GetCurrentDirectory();
                    var repoLibs = System.IO.Path.Combine(cwd, "LIBS");
                    if (!System.IO.Directory.Exists(repoLibs)) repoLibs = System.IO.Path.Combine(cwd, "libs");
                    if (System.IO.Directory.Exists(repoLibs))
                    {
                        runner.EmitOutput($"[AddonManager] Preloading repo libs from: {repoLibs}");
                        runner.PreloadLibs(repoLibs);
                    }
                    else
                    {
                        runner.EmitOutput("[AddonManager] No repo LIBS folder found to preload.");
                    }
                }
            }
            catch (Exception ex) { runner.EmitOutput("[AddonManager] Error preloading libs: " + ex.Message); }
            if (outputCallback != null) runner.OnOutput += outputCallback;
            _runners[name] = runner;
            runner.EmitOutput($"[AddonManager] Loaded (fresh) addon: {name}");
            // Install watchers before running so we can catch corruption during load
            try { runner.InstallAceLocaleWriteWatcher(); } catch { }
            // Inject a small test roster to help unitframe/party/raid code when running locally
            try
            {
                var rosterScript = @"Flux_SetRoster({ 'Alice', { name = 'Bob', class = 'MAGE', level = 60 }, 'Charlie' })";
                runner.RunScriptFromString(rosterScript, name, null, false, 0, null);
                runner.EmitOutput("[AddonManager] Injected test roster via Flux_SetRoster");
            }
            catch (Exception ex) { runner.EmitOutput("[AddonManager] Failed to inject roster: " + ex.Message); }

            // Immediately load and run the addon files (TOC or all .lua)
            try { runner.LoadAndRunAddon(folderPath); } catch (Exception ex) { runner.EmitOutput($"[AddonManager] error running addon: {ex.Message}"); }
            return runner;
        }

        // Trigger a simple event on all loaded runners.
        public void TriggerEvent(string evName, object?[]? args = null)
        {
            foreach (var kv in _runners)
            {
                try { kv.Value.FireEvent(evName, args ?? Array.Empty<object?>()); } catch { }
            }
        }

        // Save saved variables (no-op placeholder for fresh restart)
        public void SaveSavedVariables(string addonName)
        {
            if (_runners.TryGetValue(addonName, out var r))
            {
                try
                {
                    var dict = r.GetSavedVariablesAsObject();
                    var svDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "data", "savedvars");
                    try { System.IO.Directory.CreateDirectory(svDir); } catch { }
                    var svPath = System.IO.Path.Combine(svDir, addonName + ".json");
                    var json = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(svPath, json);
                    r.EmitOutput($"[AddonManager] Saved saved variables to: {svPath}");
                }
                catch (Exception ex)
                {
                    r.EmitOutput("[AddonManager] SaveSavedVariables error: " + ex.Message);
                }
            }
        }

        // Stop all runners
        public void StopAll()
        {
            foreach (var kv in _runners)
            {
                try { kv.Value.Stop(); } catch { }
            }
            _runners.Clear();
        }
    }
}
 
