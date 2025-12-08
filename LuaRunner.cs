using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Timers;
using MoonSharp.Interpreter;

namespace Flux
{
    // MoonSharp-backed Lua runner: initializes a Script, provides minimal shims,
    // loads addon files (via .toc when present) and executes them in order.
    public class LuaRunner
    {
        private volatile bool _aceLocaleWatcherInstalled = false;
        private volatile bool _stopping = false;
        private readonly HashSet<string> _executedChunks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Table> _addonNamespaces = new();
        // Registry for preloaded libraries and LibStub.libs
        private readonly Dictionary<string, Table> _libRegistry = new(StringComparer.OrdinalIgnoreCase);
        // Registry for AceAddon-created addon tables
        private readonly Dictionary<string, Table> _aceAddons = new(StringComparer.OrdinalIgnoreCase);
        public string AddonName { get; private set; } = string.Empty;
        public string? AddonFolder { get; private set; }

        private Script? _script;
        private FrameManager? _frameManager;

        public event EventHandler<string>? OnOutput;

        public LuaRunner(string? addonName = null, FrameManager? frameManager = null, string? addonFolder = null)
        {
            AddonName = addonName ?? string.Empty;
            AddonFolder = addonFolder;
            _frameManager = frameManager;
        }

        public void EmitOutput(string s)
        {
            try { OnOutput?.Invoke(this, s); } catch { }
        }

        private void InitScript()
        {
            _script = new Script(CoreModules.Preset_Default);
            _script.Options.DebugPrint = (msg) => EmitOutput("[Lua] " + msg);

            // Minimal LibStub shim: provide a table with NewLibrary and GetLibrary stubs
            var libStubTbl = new Table(_script);
            var libsTbl = new Table(_script);

            // NewLibrary: create/register a library table and return it
            libStubTbl.Set("NewLibrary", DynValue.NewCallback((ctx, args) =>
            {
                var name = args.Count > 0 ? args[0].ToString() ?? string.Empty : string.Empty;
                var minor = args.Count > 1 && args[1].Type == DataType.Number ? (int)args[1].Number : 0;
                EmitOutput($"[LibStub] NewLibrary called name={name} minor={minor}");
                var t = new Table(_script);
                t.Set("__name", DynValue.NewString(name));
                t.Set("__minor", DynValue.NewNumber(minor));
                try { _libRegistry[name] = t; } catch { }
                try { libsTbl.Set(name, DynValue.NewTable(t)); } catch { }
                return DynValue.NewTable(t);
            }));

            // GetLibrary: return previously-registered library table if present
            libStubTbl.Set("GetLibrary", DynValue.NewCallback((ctx, args) =>
            {
                var name = args.Count > 0 ? args[0].ToString() ?? string.Empty : string.Empty;
                EmitOutput($"[LibStub] GetLibrary requested: {name}");
                if (_libRegistry.TryGetValue(name, out var tbl) && tbl != null) return DynValue.NewTable(tbl);
                return DynValue.Nil;
            }));

            // expose LibStub and its libs table
            libStubTbl.Set("libs", DynValue.NewTable(libsTbl));
            _script.Globals.Set("LibStub", DynValue.NewTable(libStubTbl));

            // Pre-register some common libraries so LibStub:GetLibrary won't return nil
            try
            {
                var commonLibs = new[] { "AceAddon-3.0", "AceLocale-3.0", "AceEvent-3.0", "AceTimer-3.0" };
                foreach (var cname in commonLibs)
                {
                    if (!_libRegistry.ContainsKey(cname))
                    {
                        var ct = new Table(_script);
                        _libRegistry[cname] = ct;
                        try { libsTbl.Set(cname, DynValue.NewTable(ct)); } catch { }
                    }
                }
            }
            catch { }

            // Expose a very small 'CreateFrame' stub often used by addons (no-op)
            _script.Globals.Set("CreateFrame", DynValue.NewCallback((ctx, args) => DynValue.NewTable(_script)));

            // Minimal AceAddon-3.0 implementation so addons calling LibStub('AceAddon-3.0'):NewAddon succeed
            try
            {
                var ace = new Table(_script);
                ace.Set("NewAddon", DynValue.NewCallback((ctx, aargs) =>
                {
                    try
                    {
                        // args: (self, addonName, ...)
                        string newName = "";
                        if (aargs.Count >= 2 && aargs[1].Type == DataType.String) newName = aargs[1].String;
                        if (string.IsNullOrEmpty(newName)) return DynValue.Nil;
                        var addonTbl = new Table(_script);
                        // store metadata and simple lifecycle placeholders
                        addonTbl.Set("__name", DynValue.NewString(newName));
                        addonTbl.Set("OnInitialize", DynValue.Nil);
                        addonTbl.Set("OnEnable", DynValue.Nil);
                        try { _aceAddons[newName] = addonTbl; } catch { }
                        EmitOutput($"[AceAddon] NewAddon created: {newName}");
                        return DynValue.NewTable(addonTbl);
                    }
                    catch (Exception ex) { EmitOutput("[AceAddon] NewAddon error: " + ex.Message); }
                    return DynValue.Nil;
                }));

                // also register into lib registry so LibStub:GetLibrary returns it
                try { _libRegistry["AceAddon-3.0"] = ace; } catch { }
                try { var libStub = _script.Globals.Get("LibStub"); if (libStub != null && libStub.Type == DataType.Table) { var libs = libStub.Table.Get("libs"); if (libs != null && libs.Type == DataType.Table) libs.Table.Set("AceAddon-3.0", DynValue.NewTable(ace)); } } catch { }
            }
            catch { }
        }

        // Install a watcher that will detect non-table writes into AceLocale.__locales
        // Useful to detect when a library or addon corrupts the AceLocale registry.
        public void InstallAceLocaleWriteWatcher()
        {
            try
            {
                if (_aceLocaleWatcherInstalled) return;
                if (_script == null) return;
                var timer = new System.Timers.Timer(200);
                timer.AutoReset = true;
                timer.Elapsed += (s, e) =>
                {
                    try
                    {
                        var libStubDv = _script.Globals.Get("LibStub");
                        if (libStubDv == null || libStubDv.IsNil() || libStubDv.Type != DataType.Table) return;
                        var libsDv = libStubDv.Table.Get("libs");
                        if (libsDv == null || libsDv.IsNil() || libsDv.Type != DataType.Table) return;

                        var ace = libsDv.Table.Get("AceLocale-3.0");
                        if (ace == null || ace.IsNil() || ace.Type != DataType.Table) return;

                        // Ensure __locales exists
                        var locales = ace.Table.Get("__locales");
                        Table localesTbl;
                        if (locales == null || locales.IsNil() || locales.Type != DataType.Table)
                        {
                            localesTbl = new Table(_script);
                            ace.Table.Set("__locales", DynValue.NewTable(localesTbl));
                        }
                        else localesTbl = locales.Table;

                        // Attach metatable with __newindex to detect writes
                        var mt = new Table(_script);
                        mt.Set("__newindex", DynValue.NewCallback((ctx, args) =>
                        {
                            try
                            {
                                var key = args.Count >= 2 ? args[1].ToPrintString() ?? "<nil>" : "<nil>";
                                var val = args.Count >= 3 ? args[2] : DynValue.Nil;
                                if (val == null) return DynValue.Nil;
                                if (val.Type != DataType.Table)
                                {
                                    try { EmitOutput($"[LuaWatcher] AceLocale.__locales newindex key={key} assigned non-table type={val.Type} val={val.ToPrintString()}"); } catch { }
                                }
                            }
                            catch { }
                            return DynValue.Nil;
                        }));

                        localesTbl.MetaTable = mt;

                        // mark installed and stop timer
                        _aceLocaleWatcherInstalled = true;
                        try { timer.Stop(); timer.Dispose(); } catch { }
                        EmitOutput("[LuaWatcher] Installed AceLocale.__locales write watcher");
                    }
                    catch { }
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                try { EmitOutput("[LuaRunner] InstallAceLocaleWriteWatcher error: " + ex.Message); } catch { }
            }
        }

        // Preload all library files from a libs folder into the runner's script.
        // This is used to load workspace-wide libraries (e.g. our LIBS folder)
        // before loading an addon so that addons can use those libraries.
        public void PreloadLibs(string libsFolder)
        {
            if (string.IsNullOrEmpty(libsFolder)) return;
            if (!Directory.Exists(libsFolder))
            {
                EmitOutput($"[LuaRunner] PreloadLibs: folder not found: {libsFolder}");
                return;
            }
            if (_script == null) InitScript();
            EmitOutput($"[LuaRunner] Preloading libs from: {libsFolder}");
            LoadLibrariesFromDirectory(libsFolder);
        }

        // Load all .lua files under a directory as potential library files.
        // Skips known problematic files that would overwrite our C# shims or require full WoW UI.
        public void LoadLibrariesFromDirectory(string dirPath)
        {
            try
            {
                if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return;
                var files = Directory.GetFiles(dirPath, "*.lua", SearchOption.AllDirectories).OrderBy(p => p).ToList();
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "LibStub.lua",
                    "Ace3.lua",
                    "ChatThrottleLib.lua",
                    "AceGUIContainer-Window.lua",
                    "AceGUIWidget-DropDown.lua",
                    "AceGUIWidget-DropDown-Items.lua",
                };

                foreach (var f in files)
                {
                    try
                    {
                        var fname = Path.GetFileName(f);
                        if (skip.Contains(fname))
                        {
                            EmitOutput($"[LuaRunner] Skipping preload of {fname}");
                            continue;
                        }

                        var rel = Path.GetRelativePath(dirPath, f);
                        EmitOutput($"[LuaRunner] Preloading lib: {rel}");
                        var code = File.ReadAllText(f);
                        _script!.DoString(code);
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        EmitOutput($"[LuaRunner] Runtime error preloading {f}: {ex.DecoratedMessage}");
                    }
                    catch (Exception ex)
                    {
                        EmitOutput($"[LuaRunner] Error preloading {f}: {ex.Message}");
                    }
                }

                EmitOutput("[LuaRunner] Finished preloading libs");
            }
            catch (Exception ex)
            {
                EmitOutput("[LuaRunner] LoadLibrariesFromDirectory error: " + ex.Message);
            }
        }

        // Load and execute an addon from a folder. If a .toc file matching folder name exists,
        // it will be used to determine load order. Otherwise, all .lua files under the folder
        // are executed in sorted order.
        public void LoadAndRunAddon(string folder)
        {
            if (!Directory.Exists(folder))
            {
                EmitOutput($"[LuaRunner] Folder not found: {folder}");
                return;
            }

            AddonFolder = folder;
            AddonName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? AddonName;

            InitScript();

            var filesToLoad = new List<string>();

            // Try .toc first (common WoW addon convention)
            var tocPath = Path.Combine(folder, AddonName + ".toc");
            if (File.Exists(tocPath))
            {
                EmitOutput($"[LuaRunner] Using TOC: {tocPath}");
                var lines = File.ReadAllLines(tocPath);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("##")) continue;
                    // TOC entries may include metadata like '## Title: ...' — skip those
                    if (line.StartsWith("##")) continue;
                    // Only consider .lua entries (ignore folders or data files)
                    if (line.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = Path.Combine(folder, line.Replace('/', Path.DirectorySeparatorChar));
                        filesToLoad.Add(candidate);
                    }
                }
            }

            if (filesToLoad.Count == 0)
            {
                // fallback: load all .lua files under folder
                var all = Directory.GetFiles(folder, "*.lua", SearchOption.AllDirectories).OrderBy(p => p).ToList();
                filesToLoad.AddRange(all);
            }

            foreach (var f in filesToLoad)
            {
                if (!File.Exists(f))
                {
                    EmitOutput($"[LuaRunner] Skipping missing file: {f}");
                    continue;
                }

                string code;
                try { code = File.ReadAllText(f); }
                catch (Exception ex) { EmitOutput($"[LuaRunner] Failed to read {f}: {ex.Message}"); continue; }

                EmitOutput($"[LuaRunner] Executing: {Path.GetRelativePath(folder, f)}");
                try
                {
                    // Use RunScriptFromString which provides dedupe and diagnostics
                    RunScriptFromString(code, AddonName, null, isLibraryFile: false, libraryMinor: 0, filePath: f);
                }
                catch (ScriptRuntimeException ex)
                {
                    EmitOutput($"[LuaRunner] Runtime error in {f}: {ex.DecoratedMessage}");
                }
                catch (Exception ex)
                {
                    EmitOutput($"[LuaRunner] Error executing {f}: {ex.Message}");
                }
            }

            EmitOutput($"[LuaRunner] Finished loading addon: {AddonName}");
        }

        public void InvokeClosure(Closure? c, params object[] args)
        {
            try
            {
                if (c == null) return;
                try { c.Call(); } catch (ScriptRuntimeException ex) { EmitOutput("[LuaRunner] closure call error: " + ex.DecoratedMessage); } catch (Exception ex) { EmitOutput("[LuaRunner] closure call unknown error: " + ex.Message); }
            }
            catch { }
        }

        // Run a chunk of lua code with enhanced diagnostics and run-once deduplication.
        // Parameters mirror the original loader's expectations:
        // - code: the Lua source
        // - addonName: logical addon name (used to pass namespace)
        // - firstVarArg: optional first arg for libraries
        // - isLibraryFile: whether this is a library file (affects args)
        // - libraryMinor: minor version for library files
        // - filePath: optional physical path used for chunk naming and dedupe
        public void RunScriptFromString(string code, string addonName, string? firstVarArg = null, bool isLibraryFile = false, double libraryMinor = 0, string? filePath = null)
        {
            if (_script == null) InitScript();
            if (_script == null) return;

            try
            {
                // Optional injected diagnostics for known problematic addons (keeps light)
                if (!string.IsNullOrEmpty(filePath) && filePath.IndexOf("Attune", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { EmitOutput("[LuaRunner-Diag] Injecting lightweight in-chunk diagnostics for Attune"); } catch { }
                }

                var chunkName = filePath ?? (isLibraryFile ? $"@{addonName}:{firstVarArg ?? "lib"}" : $"@{addonName}:chunk");
                var func = _script.LoadString(code, null, chunkName);

                // ensure addon namespace table exists
                Table ns;
                if (!_addonNamespaces.TryGetValue(addonName, out ns) || ns == null)
                {
                    ns = new Table(_script);
                    _addonNamespaces[addonName] = ns;
                }

                // Compute canonicalized key for dedupe
                string canonicalChunkKey;
                try
                {
                    if (!string.IsNullOrEmpty(filePath)) canonicalChunkKey = Path.GetFullPath(filePath).ToLowerInvariant();
                    else canonicalChunkKey = (chunkName ?? string.Empty).ToLowerInvariant();
                }
                catch { canonicalChunkKey = (chunkName ?? string.Empty).ToLowerInvariant(); }

                if (_stopping)
                {
                    EmitOutput($"[LuaRunner] Skipping execution of '{chunkName}' because Stop was requested");
                    return;
                }

                if (_executedChunks.Contains(canonicalChunkKey))
                {
                    EmitOutput($"[LuaRunner-Diag] Skipping already-executed chunk='{chunkName}' canonical='{canonicalChunkKey}'");
                    return;
                }

                // Call the function with appropriate args
                if (isLibraryFile)
                {
                    _script.Call(func, DynValue.NewString(firstVarArg ?? addonName), DynValue.NewNumber(libraryMinor));
                }
                else
                {
                    _script.Call(func, DynValue.NewString(addonName), DynValue.NewTable(ns));
                }

                _executedChunks.Add(canonicalChunkKey);
                try { EmitOutput($"[LuaRunner-Diag] Executed chunk canonical='{canonicalChunkKey}' for addon='{addonName}'"); } catch { }
            }
            catch (ScriptRuntimeException ex)
            {
                EmitOutput("[Lua runtime error] " + ex.DecoratedMessage);
            }
            catch (Exception ex)
            {
                EmitOutput("[Lua error] " + ex.Message);
            }
        }

        public void InvokeAceAddonLifecycle(string hookName)
        {
            EmitOutput($"[LuaRunner] Lifecycle hook invoked: {hookName}");
        }

        public void FireEvent(string evName, object?[] args)
        {
            EmitOutput($"[LuaRunner] Event fired: {evName} args={args?.Length ?? 0}");
        }

        public void Stop()
        {
            EmitOutput("[LuaRunner] Stop requested (fresh runner)");
        }
    }
}
