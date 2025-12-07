using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

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

            // Try to read TOC early to detect Interface version and Classic vs Retail hints
            string? tocPath = null;
            string[]? tocLines = null;
            int detectedInterface = 0;
            bool detectedClassic = false;
            try
            {
                var tocFiles = Directory.GetFiles(folderPath, "*.toc", SearchOption.TopDirectoryOnly);
                tocPath = tocFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(addonName, StringComparison.OrdinalIgnoreCase))
                          ?? tocFiles.FirstOrDefault();
                if (tocPath != null)
                {
                    tocLines = File.ReadAllLines(tocPath);
                    foreach (var raw in tocLines)
                    {
                        var line = raw.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (!line.StartsWith("#")) continue;
                        var meta = line.TrimStart('#').Trim();
                        if (meta.StartsWith("Interface:", StringComparison.OrdinalIgnoreCase) || meta.StartsWith("Interface ", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = meta.Split(':');
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var iv)) detectedInterface = iv;
                        }
                        if (meta.IndexOf("classic", StringComparison.OrdinalIgnoreCase) >= 0 || meta.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            detectedClassic = true;
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (!detectedClassic && folderPath.IndexOf("_classic_", StringComparison.OrdinalIgnoreCase) >= 0) detectedClassic = true;
                if (!detectedClassic && folderPath.IndexOf("classic", StringComparison.OrdinalIgnoreCase) >= 0) detectedClassic = true;
                if (!detectedClassic && addonName.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0) detectedClassic = true;
            }
            catch { }

            var runner = new LuaRunner(addonName, _frameManager, folderPath, detectedInterface, detectedClassic);
            if (outputCallback != null)
                runner.OnOutput += outputCallback;

                        // Inject a small prep snippet early to create saved-variable globals and minimal ChatThrottleLib
                        try
                        {
                                var prep = $@"
    if _G['{addonName}_DB'] == nil then _G['{addonName}_DB'] = {{}} end
    if _G['{addonName}_Data'] == nil then _G['{addonName}_Data'] = _G['{addonName}_DB'] end
    if _G['ChatThrottleLib'] == nil then
        ChatThrottleLib = {{}}
        ChatThrottleLib.version = 999
        ChatThrottleLib.MAX_CPS = 800
        ChatThrottleLib.MSG_OVERHEAD = 40
        ChatThrottleLib.BURST = 4000
        ChatThrottleLib.MIN_FPS = 20
        function ChatThrottleLib:CanSendAddonMessage() return true end
        function ChatThrottleLib:SendAddonMessage(...) return true end
        function ChatThrottleLib:RegisterCallback(...) end
        function ChatThrottleLib:Init() end
        function ChatThrottleLib:OnUpdate() end
";
                                try { runner.RunScriptFromString(prep, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__prepare_env_early.lua"); } catch { }
                        }
                        catch { }

                        // Preload embedded libs (prefer addon's libs), then fallback to workspace Ace3
                        try
                        {
                var whitelist = new[] {
                    "AceAddon-3.0","AceEvent-3.0","AceComm-3.0","AceConsole-3.0","AceDB-3.0",
                    "AceGUI-3.0","AceLocale-3.0","CallbackHandler-1.0","LibStub","LibDataBroker-1.1","LibDBIcon-1.0","AceTimer-3.0","AceConfig-3.0"
                };

                var libDirs = new[] { "Libs", "libs", "lib", "Lib" };
                foreach (var libDirName in libDirs)
                {
                    try
                    {
                        var localLibFull = Path.GetFullPath(Path.Combine(folderPath, libDirName));
                        if (Directory.Exists(localLibFull))
                        {
                            var tocReferencesLibs = false;
                            if (tocLines != null)
                            {
                                foreach (var tl in tocLines)
                                {
                                    if (tl.IndexOf(libDirName + "/", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        tocReferencesLibs = true;
                                        break;
                                    }
                                }
                            }

                            // Only consider local libs if the TOC references them, or if they appear to be present
                            if (tocReferencesLibs || Directory.EnumerateFiles(localLibFull, "*.lua", SearchOption.AllDirectories).Any())
                            {
                                foreach (var file in Directory.GetFiles(localLibFull, "*.lua", SearchOption.AllDirectories))
                                {
                                    try
                                    {
                                        var rel = Path.GetRelativePath(localLibFull, file).Replace('\\', '/');
                                        if (whitelist.Any(w => rel.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                                        {
                                            try
                                            {
                                                var code = File.ReadAllText(file);
                                                var libName = Path.GetFileNameWithoutExtension(file);
                                                runner.RunScriptFromString(code, addonName, libName, isLibraryFile: true, libraryMinor: 0, filePath: file);
                                            }
                                            catch { }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Fallback: workspace Ace3 (if present alongside the project)
                try
                {
                    var workspaceAce = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Ace3"));
                    if (Directory.Exists(workspaceAce))
                    {
                        foreach (var file in Directory.GetFiles(workspaceAce, "*.lua", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var rel = Path.GetRelativePath(workspaceAce, file).Replace('\\', '/');
                                if (whitelist.Any(w => rel.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    try
                                    {
                                        var code = File.ReadAllText(file);
                                        var libName = Path.GetFileNameWithoutExtension(file);
                                        runner.RunScriptFromString(code, addonName, libName, isLibraryFile: true, libraryMinor: 0, filePath: file);
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }

            // Inject a small prep snippet to create saved-variable globals and minimal ChatThrottleLib
            try
            {
                                var prep = $@"
    function ChatThrottleLib:RegisterCallback(...) end
    function ChatThrottleLib:Init() end
    function ChatThrottleLib:OnUpdate() end
end

_G[addon..'_DB'] = _G[addon..'_DB'] or {{}}
_G[addon..'_Data'] = _G[addon..'_Data'] or _G[addon..'_DB'] or {{}}
_G[addon..'_DATA'] = _G[addon..'_DATA'] or _G[addon..'_DB'] or {{}}
_G[addon..'Data'] = _G[addon..'Data'] or _G[addon..'_DB'] or {{}}
-- Provide a silent AceLocale:GetLocale if present
if AceLocale and AceLocale.GetLocale then
    pcall(function() AceLocale.GetLocale(addon, true) end)
end
";

                runner.RunScriptFromString(prep, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__prepare_env.lua");
            }
            catch { }

            // Execute files listed in TOC (or all lua files in folder as fallback)
            var executedAny = false;
            try
            {
                if (tocLines != null)
                {
                    foreach (var line in tocLines)
                    {
                        var l = line.Trim();
                        if (string.IsNullOrEmpty(l)) continue;
                        if (l.StartsWith("#")) continue;
                        var candidate = l.Split('\t')[0].Trim();
                        if (candidate.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                        {
                            var file = Path.Combine(folderPath, candidate.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(file))
                            {
                                try
                                {
                                    var code = File.ReadAllText(file);
                                    runner.RunScriptFromString(code, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: file);
                                    executedAny = true;
                                }
                                catch (Exception ex)
                                {
                                    outputCallback?.Invoke(this, $"[AddonManager] Lua execution error in {candidate}: {ex.Message}");
                                }
                            }
                        }
                        else if (candidate.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            var xmlFile = Path.Combine(folderPath, candidate.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(xmlFile))
                            {
                                try
                                {
                                    var xml = File.ReadAllText(xmlFile);
                                    var doc = XDocument.Parse(xml);
                                    var frames = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "Frame", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name.LocalName, "Button", StringComparison.OrdinalIgnoreCase));
                                    foreach (var fe in frames)
                                    {
                                        try
                                        {
                                            if (_frameManager == null) continue;
                                            var vf = _frameManager.CreateFrame(runner);

                                            var backdrop = fe.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Backdrop", StringComparison.OrdinalIgnoreCase));
                                            if (backdrop != null)
                                            {
                                                try
                                                {
                                                    var bg = backdrop.Attribute("bgFile")?.Value ?? backdrop.Attribute("backgroundFile")?.Value;
                                                    if (!string.IsNullOrEmpty(bg))
                                                    {
                                                        var bmpPath = Path.Combine(folderPath, bg.Replace('/', Path.DirectorySeparatorChar));
                                                        if (File.Exists(bmpPath))
                                                        {
                                                            try
                                                            {
                                                                var bmp = new Avalonia.Media.Imaging.Bitmap(bmpPath);
                                                                vf.BackdropBrush = new Avalonia.Media.ImageBrush(bmp) { Stretch = Avalonia.Media.Stretch.Fill };
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                }
                                                catch { }
                                            }

                                            var fs = fe.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "FontString", StringComparison.OrdinalIgnoreCase));
                                            if (fs != null)
                                            {
                                                var text = fs.Attribute("text")?.Value ?? fs.Attribute("Text")?.Value;
                                                if (!string.IsNullOrEmpty(text)) vf.Text = text;
                                            }

                                            _frameManager?.UpdateVisual(vf);
                                        }
                                        catch (Exception ex)
                                        {
                                            outputCallback?.Invoke(this, $"[AddonManager] Failed to instantiate frame from XML: {ex.Message}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    outputCallback?.Invoke(this, $"[AddonManager] XML parse error: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (!executedAny)
            {
                // Fallback: execute all lua files in folder
                try
                {
                    foreach (var file in Directory.GetFiles(folderPath, "*.lua", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var code = File.ReadAllText(file);
                            runner.RunScriptFromString(code, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: file);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            try
            {
                outputCallback?.Invoke(this, $"[AddonManager] Invoking AceAddon OnInitialize/OnEnable for {addonName}");
                runner.InvokeAceAddonLifecycle("OnInitialize");
                runner.InvokeAceAddonLifecycle("OnEnable");
            }
            catch (Exception ex)
            {
                outputCallback?.Invoke(this, $"[AddonManager] Error invoking lifecycle hooks: {ex.Message}");
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
                try { runner.TriggerEvent(eventName, args); } catch { }
            }
        }
    }
}
