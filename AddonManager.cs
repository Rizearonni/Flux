using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Flux
{
    public class AddonManager
    {
        private readonly string _dataDir;
        private readonly Dictionary<string, LuaRunner> _runners = new();
        private readonly FrameManager? _frameManager;

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
            var runner = new LuaRunner(addonName, _frameManager);
            if (outputCallback != null)
                runner.OnOutput += outputCallback;

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

        public void TriggerEvent(string eventName, params object?[] args)
        {
            foreach (var runner in _runners.Values)
            {
                runner.TriggerEvent(eventName, args);
            }
        }
    }
}
