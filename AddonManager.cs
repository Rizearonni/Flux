using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
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

            // Load embedded libraries (common folders: libs, Libs, Lib, Ace3) before running addon files
            try
            {
                var libDirs = new[] { "libs", "Libs", "lib", "Lib", "Libraries", "Ace3" };
                foreach (var d in Directory.GetDirectories(folderPath))
                {
                    var name = Path.GetFileName(d);
                    if (libDirs.Any(ld => string.Equals(ld, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        outputCallback?.Invoke(this, $"[AddonManager] Loading embedded library folder: {name}");
                        var libFiles = Directory.GetFiles(d, "*.lua", SearchOption.AllDirectories).OrderBy(f => f);
                        foreach (var lf in libFiles)
                        {
                            try
                            {
                                var rel = Path.GetRelativePath(folderPath, lf);
                                outputCallback?.Invoke(this, $"[AddonManager] Loading lib file: {rel}");
                                var code = File.ReadAllText(lf);
                                runner.RunScriptFromString(code, addonName);
                            }
                            catch (Exception ex)
                            {
                                outputCallback?.Invoke(this, $"[AddonManager] Failed to load lib file {lf}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch { }

            // Determine files to execute. Prefer a .toc file (preserves addon-defined order).
            var filesToRun = new List<string>();
            try
            {
                // Try to find a TOC file: prefer <addonName>.toc, otherwise any .toc in the root
                var tocFiles = Directory.GetFiles(folderPath, "*.toc", SearchOption.TopDirectoryOnly);
                string? toc = tocFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(addonName, StringComparison.OrdinalIgnoreCase))
                              ?? tocFiles.FirstOrDefault();

                if (toc != null)
                {
                    outputCallback?.Invoke(this, $"[AddonManager] Found TOC: {Path.GetFileName(toc)}");
                    var lines = File.ReadAllLines(toc);
                    foreach (var raw in lines)
                    {
                        var line = raw.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#")) continue; // comments/metadata

                        // Toc entries may include additional parameters separated by whitespace; take first token
                        var token = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0];
                        // Only consider .lua files for now
                        if (!token.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) continue;

                        var resolved = Path.GetFullPath(Path.Combine(folderPath, token.Replace('/', Path.DirectorySeparatorChar)));
                        if (File.Exists(resolved))
                        {
                            filesToRun.Add(resolved);
                            outputCallback?.Invoke(this, $"[AddonManager] TOC -> enqueue: {token}");
                        }
                        else
                        {
                            outputCallback?.Invoke(this, $"[AddonManager] TOC referenced file not found: {token}");
                        }
                    }
                }
                else
                {
                    // No TOC: fall back to loading all .lua files recursively in deterministic order
                    outputCallback?.Invoke(this, "[AddonManager] No TOC found — loading all .lua files recursively");
                    filesToRun.AddRange(Directory.GetFiles(folderPath, "*.lua", SearchOption.AllDirectories).OrderBy(f => f));
                }
            }
            catch (Exception ex)
            {
                outputCallback?.Invoke(this, $"[AddonManager] Error while locating files: {ex.Message}");
                // fallback
                filesToRun.AddRange(Directory.GetFiles(folderPath, "*.lua", SearchOption.AllDirectories).OrderBy(f => f));
            }

            // Execute collected files in order
            foreach (var f in filesToRun)
            {
                try
                {
                    var rel = Path.GetRelativePath(folderPath, f);
                    outputCallback?.Invoke(this, $"[AddonManager] Executing: {rel}");
                    var code = File.ReadAllText(f);
                    runner.RunScriptFromString(code, addonName);
                }
                catch (Exception ex)
                {
                    outputCallback?.Invoke(this, $"[AddonManager] Failed to execute {f}: {ex.Message}");
                }
            }

            // Parse XML UI files (simple support) and instantiate frames before returning
            try
            {
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).OrderBy(f => f);
                foreach (var xf in xmlFiles)
                {
                    outputCallback?.Invoke(this, $"[AddonManager] Parsing XML UI: {Path.GetRelativePath(folderPath, xf)}");
                    try
                    {
                        var doc = XDocument.Load(xf);
                        // Find Frame elements
                        var frames = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "Frame", StringComparison.OrdinalIgnoreCase));
                        foreach (var fe in frames)
                        {
                            try
                            {
                                // Create a visual frame via FrameManager
                                if (_frameManager == null) break;
                                var vf = _frameManager.CreateFrame(runner);

                                // Width/Height attributes
                                var wAttr = fe.Attribute("width") ?? fe.Attribute("Width");
                                var hAttr = fe.Attribute("height") ?? fe.Attribute("Height");
                                if (wAttr != null && double.TryParse(wAttr.Value, out var w)) vf.Width = w;
                                if (hAttr != null && double.TryParse(hAttr.Value, out var h)) vf.Height = h;

                                // Anchor: look for first Anchor element under Anchors
                                var anchor = fe.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Anchor", StringComparison.OrdinalIgnoreCase));
                                if (anchor != null)
                                {
                                    var xAttr = anchor.Attribute("x") ?? anchor.Attribute("X");
                                    var yAttr = anchor.Attribute("y") ?? anchor.Attribute("Y");
                                    if (xAttr != null && double.TryParse(xAttr.Value, out var ax)) vf.X = ax;
                                    if (yAttr != null && double.TryParse(yAttr.Value, out var ay)) vf.Y = ay;
                                }

                                // Backdrop: find Backdrop element and bgFile attribute
                                var backdrop = fe.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Backdrop", StringComparison.OrdinalIgnoreCase));
                                if (backdrop != null)
                                {
                                    var bg = backdrop.Attribute("bgFile") ?? backdrop.Attribute("bg") ?? backdrop.Attribute("file");
                                    if (bg != null && !string.IsNullOrEmpty(bg.Value))
                                    {
                                        var tex = bg.Value.Replace('/', Path.DirectorySeparatorChar);
                                        var resolved = Path.GetFullPath(Path.Combine(folderPath, tex));
                                        if (File.Exists(resolved))
                                        {
                                            try
                                            {
                                                var bmp = new Avalonia.Media.Imaging.Bitmap(resolved);
                                                vf.BackdropBitmap = bmp;
                                                // edgeSize/insets
                                                int edge = 0;
                                                var edgeAttr = backdrop.Attribute("edgeSize") ?? backdrop.Attribute("edge") ?? backdrop.Attribute("edgeSizePixels");
                                                if (edgeAttr != null) int.TryParse(edgeAttr.Value, out edge);
                                                if (edge > 0)
                                                {
                                                    vf.NinePatchInsets = (edge, edge, edge, edge);
                                                    vf.UseNinePatch = true;
                                                }
                                                else
                                                {
                                                    vf.BackdropBrush = new Avalonia.Media.ImageBrush(bmp) { Stretch = Avalonia.Media.Stretch.Fill };
                                                    vf.UseNinePatch = false;
                                                }

                                                var tileAttr = backdrop.Attribute("tile");
                                                if (tileAttr != null && bool.TryParse(tileAttr.Value, out var tile)) vf.TileBackdrop = tile;
                                            }
                                            catch { }
                                        }
                                    }
                                }

                                // Regions: simple FontString -> Text
                                var fs = fe.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "FontString", StringComparison.OrdinalIgnoreCase));
                                if (fs != null)
                                {
                                    var text = fs.Attribute("text")?.Value ?? fs.Attribute("Text")?.Value;
                                    if (!string.IsNullOrEmpty(text)) vf.Text = text;
                                }

                                // Final visual update
                                _frameManager.UpdateVisual(vf);
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
            catch { }

            // Invoke lifecycle hooks for AceAddon-3.0 if any addons were created by the runner
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
