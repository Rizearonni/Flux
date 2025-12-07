using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Timers;

namespace Flux
{
    public class AddonManager
    {
        private readonly string _dataDir;
        private readonly Dictionary<string, LuaRunner> _runners = new();
        private readonly FrameManager? _frameManager;
        private readonly Dictionary<string, System.Timers.Timer> _saveTimers = new();

        public AddonManager(FrameManager? frameManager = null)
        {
            _frameManager = frameManager;
            _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "data", "savedvars");
            _dataDir = Path.GetFullPath(_dataDir);
            Directory.CreateDirectory(_dataDir);
        }

        public IEnumerable<string> LoadedAddons => _runners.Keys.ToList();

        public LuaRunner? LoadAddonFromFolder(string folderPath, EventHandler<string>? outputCallback = null)
        {
            if (!Directory.Exists(folderPath)) return null;

            var addonName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var runner = new LuaRunner(addonName, _frameManager, folderPath);
            if (outputCallback != null)
                runner.OnOutput += outputCallback;

            // Auto-save when SavedVariables change (debounced)
            runner.OnSavedVariablesChanged += (s, name) =>
            {
                try { ScheduleSave(name); } catch { }
            };

            // Load saved variables if present
            var saveFile = Path.Combine(_dataDir, addonName + ".json");
            if (File.Exists(saveFile))
            {
                try
                {
                    var json = File.ReadAllText(saveFile);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                    if (dict != null)
                        runner.LoadSavedVariables(dict);
                }
                catch { /* ignore errors for prototype */ }
            }

            // Execute .lua files in alphabetical order
            var luaFiles = Directory.GetFiles(folderPath, "*.lua").OrderBy(f => f);
            foreach (var f in luaFiles)
            {
                var code = File.ReadAllText(f);
                runner.RunScriptFromString(code, addonName);
            }

            _runners[addonName] = runner;
            return runner;
        }

        public void SaveSavedVariables(string addonName)
        {
            if (!_runners.TryGetValue(addonName, out var runner)) return;
            var obj = runner.GetSavedVariablesAsObject();
            var saveFile = Path.Combine(_dataDir, addonName + ".json");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(obj, opts);
            File.WriteAllText(saveFile, json);
        }

        private void ScheduleSave(string addonName, double debounceMs = 2000)
        {
            lock (_saveTimers)
            {
                if (_saveTimers.TryGetValue(addonName, out var existing))
                {
                    // reset timer
                    existing.Stop();
                    existing.Interval = debounceMs;
                    existing.Start();
                    return;
                }

                var timer = new System.Timers.Timer(debounceMs) { AutoReset = false };
                timer.Elapsed += (s, e) =>
                {
                    try
                    {
                        SaveSavedVariables(addonName);
                    }
                    catch { }
                    finally
                    {
                        lock (_saveTimers)
                        {
                            if (_saveTimers.TryGetValue(addonName, out var t) && t == timer)
                            {
                                _saveTimers.Remove(addonName);
                                try { t.Dispose(); } catch { }
                            }
                        }
                    }
                };

                _saveTimers[addonName] = timer;
                timer.Start();
            }
        }

        public void TriggerEvent(string eventName, params object?[] args)
        {
            foreach (var runner in _runners.Values)
            {
                runner.TriggerEvent(eventName, args);
            }
        }
    }
}
