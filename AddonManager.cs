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

                // Read TOC to detect interface/version and collect file list
                string? tocPath = null;
                string[]? tocLines = null;
                int detectedInterface = 0;
                bool detectedClassic = false;
                try
                {
                    var tocFiles = Directory.GetFiles(folderPath, "*.toc", SearchOption.TopDirectoryOnly);
                    tocPath = tocFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(addonName, StringComparison.OrdinalIgnoreCase)) ?? tocFiles.FirstOrDefault();
                    if (tocPath != null) tocLines = File.ReadAllLines(tocPath);
                    if (tocLines != null)
                    {
                        foreach (var raw in tocLines)
                        {
                            var line = raw.Trim();
                            if (line.StartsWith("#"))
                            {
                                var meta = line.TrimStart('#').Trim();
                                if (meta.StartsWith("Interface:", StringComparison.OrdinalIgnoreCase) || meta.StartsWith("Interface ", StringComparison.OrdinalIgnoreCase))
                                {
                                    var parts = meta.Split(':');
                                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var iv)) detectedInterface = iv;
                                }
                                if (meta.IndexOf("classic", StringComparison.OrdinalIgnoreCase) >= 0 || meta.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0) detectedClassic = true;
                            }
                        }
                    }
                }
                catch { }

                // Create runner
                var runner = new LuaRunner(addonName, _frameManager, folderPath, detectedInterface, detectedClassic);
                if (outputCallback != null) runner.OnOutput += (_, msg) => outputCallback?.Invoke(this, msg);
                runner.OnSavedVariablesChanged += (_, __) => ScheduleSave(addonName);

                // Early environment prep: ensure saved-variable tables and minimal ChatThrottleLib
                var prep = @"
    -- Early environment prep: ensure saved-variable tables and a robust ChatThrottleLib stub
    ChatThrottleLib = ChatThrottleLib or {}
    ChatThrottleLib._callbacks = ChatThrottleLib._callbacks or {}
    ChatThrottleLib.version = ChatThrottleLib.version or 999
    ChatThrottleLib.MAX_CPS = ChatThrottleLib.MAX_CPS or 800
    ChatThrottleLib.MSG_OVERHEAD = ChatThrottleLib.MSG_OVERHEAD or 40
    ChatThrottleLib.BURST = ChatThrottleLib.BURST or 4000
    ChatThrottleLib.MIN_FPS = ChatThrottleLib.MIN_FPS or 20
    ChatThrottleLib.Frame = ChatThrottleLib.Frame or { SetScript = function() end }
    function ChatThrottleLib:RegisterCallback(name, fn)
        if not name or type(fn) ~= 'function' then return end
        self._callbacks[name] = fn
    end
    function ChatThrottleLib:CanSendAddonMessage()
        return true
    end
    function ChatThrottleLib:SendAddonMessage(prefix, message, commType, target)
        local cb = self._callbacks and (self._callbacks['CommSend'] or self._callbacks['OnSend'])
        if type(cb) == 'function' then pcall(cb, prefix, message, commType, target) end
        return true
    end
    function ChatThrottleLib:Init() end
    function ChatThrottleLib:OnUpdate() end

    -- ensure saved-variable tables exist under common names
    _G['{addonName}_DB'] = _G['{addonName}_DB'] or {}
    _G['{addonName}_Data'] = _G['{addonName}_Data'] or _G['{addonName}_DB'] or {}
    _G['{addonName}_DATA'] = _G['{addonName}_DATA'] or _G['{addonName}_DB'] or {}
    _G['{addonName}Data']  = _G['{addonName}Data'] or _G['{addonName}_DB'] or {}

    -- Ensure AceLocale is safe: wrap existing GetLocale to return empty table instead of throwing
    if type(LibStub) == 'table' then
        local ok, ace = pcall(function() return LibStub('AceLocale-3.0') end)
        if ok and type(ace) == 'table' then
            local origGet = ace.GetLocale
            ace.__locales = ace.__locales or {}
            ace.GetLocale = function(self, app, silent)
                if origGet then
                    local ok2, res = pcall(origGet, self, app, silent)
                    if ok2 then return res end
                end
                self.__locales[app] = self.__locales[app] or {}
                return self.__locales[app]
            end
            if type(ace.NewLocale) ~= 'function' then
                function ace:NewLocale(app, locale, isDefault, silent)
                    self.__locales[app] = self.__locales[app] or {}
                    self.__locales[app][locale] = self.__locales[app][locale] or {}
                    return self.__locales[app][locale]
                end
            end
            if type(LibStub.libs) == 'table' then LibStub.libs['AceLocale-3.0'] = ace end
        end
    end
    ";
                try { runner.RunScriptFromString(prep, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__prepare_env_early.lua"); } catch { }

                // Defensive fallback: ensure CallbackHandler/LibStub entries exist so libraries expecting CallbackHandler:New won't nil-crash
                var defensive = @"
    -- Defensive: ensure LibStub and CallbackHandler minimal implementation exist early
    if type(LibStub) ~= 'table' then
        LibStub = LibStub or { libs = {}, minors = {}, NewLibrary = function() return nil end, GetLibrary = function() return nil end }
    end
    local ok, cbh = pcall(function() return LibStub and LibStub('CallbackHandler-1.0') end)
    if not ok or type(cbh) ~= 'table' then
        cbh = LibStub and LibStub.libs and LibStub.libs['CallbackHandler-1.0'] or {}
    end

    -- Provide a tolerant CallbackHandler:New if missing. This implements a small, safe
    -- callback registry with Register/Unregister and Fire so libraries can rely on it.
    if type(cbh.New) ~= 'function' then
        function cbh:New(target, RegisterName, UnregisterName, UnregisterAllName)
            RegisterName = RegisterName or 'RegisterCallback'
            UnregisterName = UnregisterName or 'UnregisterCallback'
            if UnregisterAllName == nil then UnregisterAllName = 'UnregisterAllCallbacks' end

            local events = {}
            local registry = { events = events }

            function registry:Fire(eventname, ...)
                local handlers = events[eventname]
                if not handlers then return end
                for who, func in pairs(handlers) do
                    if type(func) == 'function' then
                        pcall(func, ...)
                    end
                end
            end

            target[RegisterName] = function(self, eventname, method, ...)
                if type(eventname) ~= 'string' then return end
                events[eventname] = events[eventname] or {}
                if type(method) == 'string' then
                    if type(self) == 'table' and type(self[method]) == 'function' then
                        events[eventname][self] = function(...) return self[method](self, ...) end
                    end
                elseif type(method) == 'function' then
                    events[eventname][self] = method
                end
            end

            target[UnregisterName] = function(self, eventname)
                if type(eventname) ~= 'string' then return end
                if events[eventname] then events[eventname][self] = nil end
            end

            if UnregisterAllName then
                target[UnregisterAllName] = function(...)
                    for i=1, select('#', ...) do
                        local who = select(i, ...)
                        for en, handlers in pairs(events) do
                            handlers[who] = nil
                        end
                    end
                end
            end

            return registry
        end
    end

    LibStub = LibStub or {}
    LibStub.libs = LibStub.libs or {}
    LibStub.libs['CallbackHandler-1.0'] = cbh
";
                try { runner.RunScriptFromString(defensive, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__defensive_callbackhandler.lua"); } catch { }

                // Preload workspace Ace3 libs (preferred) and local libs
                var whitelist = new[] { "AceAddon-3.0","AceEvent-3.0","AceComm-3.0","AceConsole-3.0","AceDB-3.0","AceGUI-3.0","AceLocale-3.0","CallbackHandler-1.0","LibStub","LibDataBroker-1.1","LibDBIcon-1.0","AceTimer-3.0","AceConfig-3.0" };
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
                                    var code = File.ReadAllText(file);
                                    var libName = Path.GetFileNameWithoutExtension(file);
                                    runner.RunScriptFromString(code, addonName, libName, isLibraryFile: true, libraryMinor: 0, filePath: file);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Local libs
                try
                {
                    var libDirs = new[] { "Libs", "libs", "lib", "Lib" };
                    foreach (var libDirName in libDirs)
                    {
                        var localLibFull = Path.GetFullPath(Path.Combine(folderPath, libDirName));
                        if (!Directory.Exists(localLibFull)) continue;
                        foreach (var file in Directory.GetFiles(localLibFull, "*.lua", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var rel = Path.GetRelativePath(localLibFull, file).Replace('\\', '/');
                                if (whitelist.Any(w => rel.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    var code = File.ReadAllText(file);
                                    var libName = Path.GetFileNameWithoutExtension(file);
                                    runner.RunScriptFromString(code, addonName, libName, isLibraryFile: true, libraryMinor: 0, filePath: file);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Post-workspace shim: safe Embed, ChatThrottle defaults, AceLocale fallback
                var postShim = @"
    if LibStub and type(LibStub.libs) == 'table' then
        for name, lib in pairs(LibStub.libs) do
            if lib and type(lib) == 'table' and type(lib.Embed) ~= 'function' then
                lib.Embed = function(self, target)
                    if type(self.RegisterComm) == 'function' and type(target.RegisterComm) ~= 'function' then target.RegisterComm = self.RegisterComm end
                    if type(self.SendCommMessage) == 'function' and type(target.SendCommMessage) ~= 'function' then target.SendCommMessage = self.SendCommMessage end
                    return target
                end
            end
        end
    end

    if ChatThrottleLib then
        ChatThrottleLib.version = ChatThrottleLib.version or 999
        ChatThrottleLib.MAX_CPS = ChatThrottleLib.MAX_CPS or 800
        ChatThrottleLib.MSG_OVERHEAD = ChatThrottleLib.MSG_OVERHEAD or 40
        ChatThrottleLib.BURST = ChatThrottleLib.BURST or 4000
        ChatThrottleLib.MIN_FPS = ChatThrottleLib.MIN_FPS or 20
        ChatThrottleLib.Frame = ChatThrottleLib.Frame or { SetScript = function() end }
        ChatThrottleLib.CanSendAddonMessage = ChatThrottleLib.CanSendAddonMessage or function() return true end
        ChatThrottleLib.SendAddonMessage = ChatThrottleLib.SendAddonMessage or function(...) return true end
    end

    if type(LibStub) == 'table' then
        local ok, ace = pcall(function() return LibStub('AceLocale-3.0') end)
        if not ace or type(ace) ~= 'table' then
            ace = { __locales = {} }
            function ace:NewLocale(app, locale, isDefault, silent)
                self.__locales[app] = self.__locales[app] or {}
                self.__locales[app][locale] = self.__locales[app][locale] or {}
                return self.__locales[app][locale]
            end
            function ace:GetLocale(app, silent)
                self.__locales[app] = self.__locales[app] or {}
                local cur = (GetLocale and GetLocale()) or 'enUS'
                return self.__locales[app][cur] or {}
            end
            if type(LibStub.libs) == 'table' then LibStub.libs['AceLocale-3.0'] = ace end
        else
            ace.__locales = ace.__locales or {}
            if type(ace.NewLocale) ~= 'function' then function ace:NewLocale(app, locale, isDefault, silent) self.__locales[app] = self.__locales[app] or {}; self.__locales[app][locale] = self.__locales[app][locale] or {}; return self.__locales[app][locale] end end
            if type(ace.GetLocale) ~= 'function' then function ace:GetLocale(app, silent) self.__locales[app] = self.__locales[app] or {}; local cur = (GetLocale and GetLocale()) or 'enUS'; return self.__locales[app][cur] or {} end end
        end
    end
    ";
                try { runner.RunScriptFromString(postShim, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__post_workspace_shim.lua"); } catch { }

                                // Ensure an AceLocale entry exists for this addon so GetLocale won't error when libraries query it
                                try
                                {
                                        var ensureLocale = $@"
local ok, ace = pcall(function() return LibStub and LibStub('AceLocale-3.0') end)
if ok and type(ace) == 'table' and type(ace.NewLocale) == 'function' then
    ace:NewLocale('{addonName}', GetLocale(), true)
end
";
                                        try { runner.RunScriptFromString(ensureLocale, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__ensure_locale.lua"); } catch { }
                                }
                                catch { }

                                // Ensure common LibStub libraries exist as minimal placeholders so calls to LibStub("Name")
                                // don't produce hard errors when a library is missing. This creates empty tables for
                                // commonly-queried names which addons expect to exist at load-time.
                                try
                                {
                                        var ensureLibs = @"
if type(LibStub) == 'table' then
    LibStub.libs = LibStub.libs or {}
    local names = { 'AceConfigRegistry-3.0', 'AceConfigCmd-3.0', 'AceConfigDialog-3.0', 'AceLocale-3.0', 'LibDBIcon-1.0', 'LibDataBroker-1.1' }
    for i, n in ipairs(names) do
        if LibStub.libs[n] == nil then LibStub.libs[n] = {} end
    end
end
";
                                        try { runner.RunScriptFromString(ensureLibs, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: "__ensure_libstub_fallbacks.lua"); } catch { }
                                }
                                catch { }

                // Execute files listed in TOC (or all lua files in folder as fallback)
                var executedAny = false;
                try
                {
                    if (tocLines != null)
                    {
                        foreach (var lraw in tocLines)
                        {
                            var l = lraw.Trim();
                            if (string.IsNullOrEmpty(l) || l.StartsWith("#")) continue;
                            var candidate = l.Split('\t')[0].Trim();
                            if (candidate.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                            {
                                var file = Path.Combine(folderPath, candidate.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(file))
                                {
                                    try { var code = File.ReadAllText(file); runner.RunScriptFromString(code, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: file); executedAny = true; } catch (Exception ex) { outputCallback?.Invoke(this, $"[AddonManager] Lua execution error in {candidate}: {ex.Message}"); }
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
                                                    var bg = backdrop.Attribute("bgFile")?.Value ?? backdrop.Attribute("backgroundFile")?.Value;
                                                    if (!string.IsNullOrEmpty(bg))
                                                    {
                                                        var bmpPath = Path.Combine(folderPath, bg.Replace('/', Path.DirectorySeparatorChar));
                                                        if (File.Exists(bmpPath))
                                                        {
                                                            try { var bmp = new Avalonia.Media.Imaging.Bitmap(bmpPath); vf.BackdropBrush = new Avalonia.Media.ImageBrush(bmp) { Stretch = Avalonia.Media.Stretch.Fill }; } catch { }
                                                        }
                                                    }
                                                }
                                                var fs = fe.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "FontString", StringComparison.OrdinalIgnoreCase));
                                                if (fs != null) { var text = fs.Attribute("text")?.Value ?? fs.Attribute("Text")?.Value; if (!string.IsNullOrEmpty(text)) vf.Text = text; }
                                                _frameManager?.UpdateVisual(vf);
                                            }
                                            catch (Exception ex) { outputCallback?.Invoke(this, $"[AddonManager] Failed to instantiate frame from XML: {ex.Message}"); }
                                        }
                                    }
                                    catch (Exception ex) { outputCallback?.Invoke(this, $"[AddonManager] XML parse error: {ex.Message}"); }
                                }
                            }
                        }
                    }
                }
                catch { }

                if (!executedAny)
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(folderPath, "*.lua", SearchOption.AllDirectories))
                        {
                            try { var code = File.ReadAllText(file); runner.RunScriptFromString(code, addonName, null, isLibraryFile: false, libraryMinor: 0, filePath: file); } catch { }
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
