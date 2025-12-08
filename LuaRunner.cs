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
        // Timer and event registries to support minimal AceTimer/AceEvent behavior
        private readonly Dictionary<int, System.Timers.Timer> _timers = new();
        private int _nextTimerId = 1;
        private readonly object _timerLock = new();
        private readonly Dictionary<string, List<Closure>> _eventHandlers = new(StringComparer.OrdinalIgnoreCase);
        // Created frame registry for frame-level events/scripts
        private readonly List<Table> _createdFrames = new();
        // hooksecurefunc registries
        private readonly Dictionary<string, List<DynValue>> _globalHooks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DynValue> _globalOriginals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DynValue>> _tableMethodHooks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DynValue> _tableMethodOriginals = new(StringComparer.OrdinalIgnoreCase);
        // Roster for party/raid simulation
        private readonly List<Dictionary<string, object?>> _roster = new();
        public string AddonName { get; private set; } = string.Empty;
        public string? AddonFolder { get; private set; }

        private Script? _script;
        private FrameManager? _frameManager;

        public event EventHandler<string>? OnOutput;
        public event EventHandler<string>? OnSavedVariablesChanged;

        public LuaRunner(string? addonName = null, FrameManager? frameManager = null, string? addonFolder = null)
        {
            AddonName = addonName ?? string.Empty;
            AddonFolder = addonFolder;
            _frameManager = frameManager;
        }

        public void EmitOutput(string s)
        {
            try { OnOutput?.Invoke(this, s); } catch { }
            try { Console.WriteLine(s); } catch { }
        }

        private Table? _savedVarsTable;

        // Initialize or replace the saved-variables backing table and expose a proxy to Lua.
        private void InitializeSavedVariablesTable(Dictionary<string, object?>? initial)
        {
            _savedVarsTable = new Table(_script);
            if (initial != null)
            {
                foreach (var kv in initial)
                {
                    _savedVarsTable.Set(kv.Key, ConvertToDynValue(kv.Value));
                }
            }

            var proxy = new Table(_script);
            var mt = new Table(_script);

            // __index returns from backing store
            mt.Set("__index", DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count >= 2 && args[1].Type == DataType.String)
                {
                    var key = args[1].String;
                    var v = _savedVarsTable.Get(key);
                    return v ?? DynValue.Nil;
                }
                return DynValue.Nil;
            }));

            // __newindex writes into backing store and notifies host
            mt.Set("__newindex", DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count >= 3 && args[1].Type == DataType.String)
                {
                    var key = args[1].String;
                    var val = args[2];
                    _savedVarsTable.Set(key, val);
                    try { OnSavedVariablesChanged?.Invoke(this, AddonName); } catch { }
                }
                return DynValue.Nil;
            }));

            proxy.MetaTable = mt;
            _script.Globals.Set("SavedVariables", DynValue.NewTable(proxy));
        }

        public void LoadSavedVariables(Dictionary<string, object?> dict)
        {
            if (_script == null) InitScript();
            if (_savedVarsTable == null) InitializeSavedVariablesTable(dict);
            else
            {
                foreach (var kv in dict)
                {
                    _savedVarsTable.Set(kv.Key, ConvertToDynValue(kv.Value));
                }
            }
        }

        public Dictionary<string, object?> GetSavedVariablesAsObject()
        {
            var result = new Dictionary<string, object?>();
            if (_savedVarsTable == null) return result;
            foreach (var pair in _savedVarsTable.Pairs)
            {
                var key = pair.Key.String;
                result[key] = DynValueToObject(pair.Value);
            }
            return result;
        }

        private DynValue ConvertToDynValue(object? val)
        {
            if (val == null) return DynValue.Nil;
            if (val is string s) return DynValue.NewString(s);
            if (val is bool b) return DynValue.NewBoolean(b);
            if (val is int i) return DynValue.NewNumber(i);
            if (val is double d) return DynValue.NewNumber(d);
            if (val is float f) return DynValue.NewNumber(f);
            if (val is long l) return DynValue.NewNumber(l);
            if (val is Dictionary<string, object?> dict)
            {
                var t = new Table(_script);
                foreach (var kv in dict)
                {
                    t.Set(kv.Key, ConvertToDynValue(kv.Value));
                }
                return DynValue.NewTable(t);
            }
            return DynValue.NewString(val.ToString() ?? string.Empty);
        }

        private object? DynValueToObject(DynValue v)
        {
            if (v.IsNil()) return null;
            if (v.Type == DataType.String) return v.String;
            if (v.Type == DataType.Boolean) return v.Boolean;
            if (v.Type == DataType.Number) return v.Number;
            if (v.Type == DataType.Table)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var p in v.Table.Pairs)
                {
                    var key = p.Key.String;
                    dict[key] = DynValueToObject(p.Value);
                }
                return dict;
            }
            return v.ToString();
        }

        private void InitScript()
        {
            _script = new Script(CoreModules.Preset_Default);
            _script.Options.DebugPrint = (msg) => EmitOutput("[Lua] " + msg);

            // Helper to make a table automatically provide callable no-op stubs for missing keys.
            void MakeCallableTable(Table tbl, string ownerName)
            {
                try
                {
                    var mtc = new Table(_script);
                    mtc.Set("__index", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            if (cargs.Count >= 2 && cargs[1].Type == DataType.String)
                            {
                                var key = cargs[1].String;
                                // if already present, return it
                                var ex = tbl.Get(key);
                                if (ex != null && !ex.IsNil()) return ex;
                                try { EmitOutput($"[CallableStub] creating no-op for {ownerName}.{key}"); } catch { }
                                var cb = DynValue.NewCallback((ccctx, ccargs) => { try { EmitOutput($"[CallableStub] invoked {ownerName}.{key}"); } catch { } return DynValue.Nil; });
                                tbl.Set(key, cb);
                                return cb;
                            }
                        }
                        catch { }
                        return DynValue.Nil;
                    }));
                    tbl.MetaTable = mtc;
                }
                catch { }
            }

            // Minimal LibStub shim: provide a table with NewLibrary and GetLibrary stubs
            var libStubTbl = new Table(_script);
            var libsTbl = new Table(_script);

            // NewLibrary: create/register a library table and return it.
            // Support both function-call form (LibStub("Name")) and method form (LibStub:NewLibrary(...)).
            libStubTbl.Set("NewLibrary", DynValue.NewCallback((ctx, args) =>
            {
                // args may be (name, minor) or (self, name, minor)
                string name;
                int minor = 0;
                if (args.Count >= 1 && args[0].Type == DataType.Table && args.Count >= 2 && args[1].Type == DataType.String)
                {
                    name = args[1].String;
                    if (args.Count >= 3 && args[2].Type == DataType.Number) minor = (int)args[2].Number;
                }
                else
                {
                    name = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : string.Empty;
                    if (args.Count >= 2 && args[1].Type == DataType.Number) minor = (int)args[1].Number;
                }
                EmitOutput($"[LibStub] NewLibrary called name={name} minor={minor}");
                // If a library is already registered, mimic LibStub semantics:
                // return the existing table when the current registered minor is >= requested minor.
                try
                {
                    if (_libRegistry.TryGetValue(name, out var existing) && existing != null)
                    {
                        var existingMinorDv = existing.Get("__minor");
                        if (existingMinorDv != null && existingMinorDv.Type == DataType.Number)
                        {
                            var existingMinor = (int)existingMinorDv.Number;
                            if (existingMinor >= minor)
                            {
                                // return existing library (no upgrade)
                                return DynValue.NewTable(existing);
                            }
                        }
                    }
                }
                catch { }

                var t = new Table(_script);
                try { MakeCallableTable(t, name); } catch { }
                t.Set("__name", DynValue.NewString(name));
                t.Set("__minor", DynValue.NewNumber(minor));
                try { _libRegistry[name] = t; } catch { }
                try { libsTbl.Set(name, DynValue.NewTable(t)); } catch { }
                return DynValue.NewTable(t);
            }));

            // GetLibrary: return previously-registered library table if present. Support both call forms.
            libStubTbl.Set("GetLibrary", DynValue.NewCallback((ctx, args) =>
            {
                string name;
                if (args.Count >= 1 && args[0].Type == DataType.Table && args.Count >= 2 && args[1].Type == DataType.String)
                {
                    name = args[1].String;
                }
                else
                {
                    name = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : string.Empty;
                }
                EmitOutput($"[LibStub] GetLibrary requested: {name}");
                if (_libRegistry.TryGetValue(name, out var tbl) && tbl != null) return DynValue.NewTable(tbl);
                return DynValue.Nil;
            }));

            // expose LibStub and its libs table; make the table callable via metatable __call so
            // code that does LibStub('Name') works (table with __call behaves like function).
            libStubTbl.Set("libs", DynValue.NewTable(libsTbl));
            var mt = new Table(_script);
            mt.Set("__call", DynValue.NewCallback((ctx, args) =>
            {
                // Calling LibStub(...) should behave like GetLibrary
                if (args.Count >= 1 && args[0].Type == DataType.String)
                {
                    var nm = args[0].String;
                    if (_libRegistry.TryGetValue(nm, out var t) && t != null) return DynValue.NewTable(t);
                }
                else if (args.Count >= 2 && args[0].Type == DataType.Table && args[1].Type == DataType.String)
                {
                    var nm = args[1].String;
                    if (_libRegistry.TryGetValue(nm, out var t) && t != null) return DynValue.NewTable(t);
                }
                return DynValue.Nil;
            }));
            libStubTbl.MetaTable = mt;
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

            // Frame helper: create a table with RegisterEvent/UnregisterEvent/SetScript and basic show/hide
            var frameMt = new Table(_script);

            DynValue CreateFrameFunc(DynValue self, CallbackArguments args)
            {
                try
                {
                    try
                    {
                        string argDesc = "(no args)";
                        if (args != null && args.Count > 0)
                        {
                            var parts = new List<string>();
                            for (int i = 0; i < args.Count; i++)
                            {
                                try { parts.Add(args[i].Type.ToString()); } catch { parts.Add("?" ); }
                            }
                            argDesc = string.Join(',', parts);
                        }
                        EmitOutput($"[CreateFrame] called types={argDesc}");
                    }
                    catch { }
                    var frm = new Table(_script);

                    // internal tables
                    frm.Set("__registeredEvents", DynValue.NewTable(new Table(_script)));
                    frm.Set("__scripts", DynValue.NewTable(new Table(_script)));

                    // RegisterEvent(eventName)
                    frm.Set("RegisterEvent", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            var eventName = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : null;
                            if (string.IsNullOrEmpty(eventName)) return DynValue.Nil;
                            var regs = frm.Get("__registeredEvents").Table;
                            regs.Set(eventName, DynValue.NewBoolean(true));
                            return DynValue.True;
                        }
                        catch { return DynValue.Nil; }
                    }));

                    // UnregisterEvent(eventName)
                    frm.Set("UnregisterEvent", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            var eventName = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : null;
                            if (string.IsNullOrEmpty(eventName)) return DynValue.False;
                            var regs = frm.Get("__registeredEvents").Table;
                            regs.Set(eventName, DynValue.Nil);
                            return DynValue.True;
                        }
                        catch { return DynValue.False; }
                    }));

                    // SetScript(scriptName, func)
                    frm.Set("SetScript", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            var scriptName = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : null;
                            var func = cargs.Count >= 3 ? cargs[2] : DynValue.Nil;
                            if (string.IsNullOrEmpty(scriptName)) return DynValue.Nil;
                            var scripts = frm.Get("__scripts").Table;
                            scripts.Set(scriptName, func);
                            return DynValue.True;
                        }
                        catch { return DynValue.Nil; }
                    }));

                    // Basic show/hide
                    frm.Set("Show", DynValue.NewCallback((cctx, cargs) => { return DynValue.Nil; }));
                    frm.Set("Hide", DynValue.NewCallback((cctx, cargs) => { return DynValue.Nil; }));
                    frm.Set("SetPoint", DynValue.NewCallback((cctx, cargs) => { return DynValue.Nil; }));
                    // Size and anchoring helpers
                    frm.Set("SetSize", DynValue.NewCallback((cctx, cargs) => { return DynValue.Nil; }));
                    frm.Set("GetWidth", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(32)));
                    frm.Set("GetHeight", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(32)));
                    frm.Set("GetCenter", DynValue.NewCallback((cctx, cargs) => DynValue.NewTuple(new[] { DynValue.NewNumber(100), DynValue.NewNumber(100) })));
                    frm.Set("GetEffectiveScale", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(1)));
                    // Additional common frame methods used by UI libs
                    frm.Set("EnableMouse", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("SetFrameStrata", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("SetFrameLevel", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("SetPropagateKeyboardInput", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("IsShown", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(false)));
                    frm.Set("Click", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("ClearAllPoints", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("SetParent", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));

                    // Texture creation helper used heavily in LibDBIcon
                    frm.Set("CreateTexture", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            var tex = new Table(_script);
                            tex.Set("SetSize", DynValue.NewCallback((xctx, xargs) => DynValue.Nil));
                            tex.Set("SetTexture", DynValue.NewCallback((xctx, xargs) => DynValue.Nil));
                            tex.Set("SetPoint", DynValue.NewCallback((xctx, xargs) => DynValue.Nil));
                            tex.Set("SetVertexColor", DynValue.NewCallback((xctx, xargs) => DynValue.Nil));
                            tex.Set("GetVertexColor", DynValue.NewCallback((xctx, xargs) => DynValue.NewTuple(new[] { DynValue.NewNumber(1.0), DynValue.NewNumber(1.0), DynValue.NewNumber(1.0) })));
                            tex.Set("UpdateCoord", DynValue.NewCallback((xctx, xargs) => DynValue.Nil));
                            return DynValue.NewTable(tex);
                        }
                        catch { return DynValue.Nil; }
                    }));
                    // Support basic OnClick script wiring
                    frm.Set("SetScript", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            var scriptName = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : null;
                            var func = cargs.Count >= 3 ? cargs[2] : DynValue.Nil;
                            if (!string.IsNullOrEmpty(scriptName) && func != null && !func.IsNil())
                            {
                                var scripts = frm.Get("__scripts").Table;
                                scripts.Set(scriptName, func);
                            }
                        }
                        catch { }
                        return DynValue.Nil;
                    }));

                    frm.Set("RegisterForClicks", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("RegisterForDrag", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
                    frm.Set("SetHighlightTexture", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));

                    // Simple animation group stub
                    frm.Set("CreateAnimationGroup", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            var grp = new Table(_script);
                            grp.Set("CreateAnimation", DynValue.NewCallback((acctx, aargs) =>
                            {
                                var anim = new Table(_script);
                                anim.Set("SetOrder", DynValue.NewCallback((ic, ia) => DynValue.Nil));
                                anim.Set("SetDuration", DynValue.NewCallback((ic, ia) => DynValue.Nil));
                                anim.Set("SetFromAlpha", DynValue.NewCallback((ic, ia) => DynValue.Nil));
                                anim.Set("SetToAlpha", DynValue.NewCallback((ic, ia) => DynValue.Nil));
                                anim.Set("SetStartDelay", DynValue.NewCallback((ic, ia) => DynValue.Nil));
                                return DynValue.NewTable(anim);
                            }));
                            grp.Set("Play", DynValue.NewCallback((pc, pa) => DynValue.Nil));
                            grp.Set("Stop", DynValue.NewCallback((pc, pa) => DynValue.Nil));
                            grp.Set("SetToFinalAlpha", DynValue.NewCallback((pc, pa) => DynValue.Nil));
                            return DynValue.NewTable(grp);
                        }
                        catch { return DynValue.Nil; }
                    }));

                    // Track created frames for event dispatch
                    try { _createdFrames.Add(frm); } catch { }

                    return DynValue.NewTable(frm);
                }
                catch (Exception ex) { EmitOutput("[CreateFrame] error: " + ex.Message); return DynValue.NewTable(new Table(_script)); }
            }

            _script.Globals.Set("CreateFrame", DynValue.NewCallback((ctx, args) => CreateFrameFunc(DynValue.Nil, args)));

            // Ensure any pre-registered libraries in _libRegistry have a usable `frame` entry
            try
            {
                var createFnDv = _script.Globals.Get("CreateFrame");
                if (createFnDv != null && createFnDv.Type == DataType.Function)
                {
                    foreach (var kv in _libRegistry.ToList())
                    {
                        try
                        {
                            var libTbl = kv.Value;
                            if (libTbl == null) continue;
                            var fv = libTbl.Get("frame");
                            if (fv == null || fv.IsNil())
                            {
                                var dv = _script.Call(createFnDv.Function);
                                if (dv != null && !dv.IsNil() && dv.Type == DataType.Table) libTbl.Set("frame", dv);
                            }
                            // also populate common named frames used by some libs
                            try
                            {
                                var ef = libTbl.Get("eventFrame");
                                if (ef == null || ef.IsNil())
                                {
                                    var dv2 = _script.Call(createFnDv.Function);
                                    if (dv2 != null && !dv2.IsNil() && dv2.Type == DataType.Table) libTbl.Set("eventFrame", dv2);
                                }
                            }
                            catch { }
                            try
                            {
                                var uf = libTbl.Get("updateFrame");
                                if (uf == null || uf.IsNil())
                                {
                                    var dv3 = _script.Call(createFnDv.Function);
                                    if (dv3 != null && !dv3.IsNil() && dv3.Type == DataType.Table) libTbl.Set("updateFrame", dv3);
                                }
                            }
                            catch { }
                            try
                            {
                                var pf = libTbl.Get("popup");
                                if (pf == null || pf.IsNil())
                                {
                                    var dv4 = _script.Call(createFnDv.Function);
                                    if (dv4 != null && !dv4.IsNil() && dv4.Type == DataType.Table) libTbl.Set("popup", dv4);
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // UIParent global
            var uiParent = new Table(_script);
            _script.Globals.Set("UIParent", DynValue.NewTable(uiParent));

            // Basic GameTooltip stub
            var gameTooltip = new Table(_script);
            gameTooltip.Set("SetOwner", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            gameTooltip.Set("SetText", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            gameTooltip.Set("Show", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            gameTooltip.Set("Hide", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            _script.Globals.Set("GameTooltip", DynValue.NewTable(gameTooltip));

            // Chat frame & SendChatMessage
            var chatFrame = new Table(_script);
            chatFrame.Set("AddMessage", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    var msg = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : "";
                    EmitOutput("[Chat] " + msg);
                }
                catch { }
                return DynValue.Nil;
            }));
            _script.Globals.Set("DEFAULT_CHAT_FRAME", DynValue.NewTable(chatFrame));
            _script.Globals.Set("ChatFrame1", DynValue.NewTable(chatFrame));
            _script.Globals.Set("SendChatMessage", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    var msg = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                    EmitOutput("[SendChatMessage] " + msg);
                    // echo to default chat frame
                    var f = _script.Globals.Get("DEFAULT_CHAT_FRAME"); if (f != null && f.Type == DataType.Table) { var add = f.Table.Get("AddMessage"); if (add != null && add.Type == DataType.Function) _script.Call(add, DynValue.NewString(msg)); }
                }
                catch { }
                return DynValue.Nil;
            }));

            // Provide SendAddonMessage global used by many comm libraries
            _script.Globals.Set("SendAddonMessage", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    var prefix = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                    var msg = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : "";
                    var distribution = cargs.Count >= 3 && cargs[2].Type == DataType.String ? cargs[2].String : "";
                    var target = cargs.Count >= 4 && (cargs[3].Type == DataType.String || cargs[3].Type==DataType.Number) ? cargs[3].ToPrintString() : null;
                    EmitOutput($"[SendAddonMessage] prefix={prefix} dist={distribution} target={target} len={msg?.Length}");
                }
                catch { }
                return DynValue.True;
            }));

            // C_ChatInfo shim: expose SendChatMessage/SendAddonMessage methods so ChatThrottleLib will hook them
            var cChat = new Table(_script);
            cChat.Set("SendChatMessage", _script.Globals.Get("SendChatMessage"));
            cChat.Set("SendAddonMessage", _script.Globals.Get("SendAddonMessage"));
            // Provide Register/Unregister for addon message prefixes (used by AceComm)
            cChat.Set("RegisterAddonMessagePrefix", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    var prefix = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : null;
                    EmitOutput($"[C_ChatInfo.RegisterAddonMessagePrefix] prefix={prefix}");
                    return DynValue.True;
                }
                catch { return DynValue.False; }
            }));
            cChat.Set("UnregisterAddonMessagePrefix", DynValue.NewCallback((cctx, cargs) => DynValue.True));
            _script.Globals.Set("C_ChatInfo", DynValue.NewTable(cChat));

            // Provide select and unpack shims used by older Lua libraries
            _script.Globals.Set("select", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count == 0) return DynValue.Nil;
                    if (cargs[0].Type == DataType.String && cargs[0].String == "#") return DynValue.NewNumber(Math.Max(0, cargs.Count - 1));
                    if (cargs[0].Type == DataType.Number)
                    {
                        int start = (int)cargs[0].Number;
                        if (start < 0) start = cargs.Count + start;
                        start = Math.Max(1, start);
                        var outVals = new List<DynValue>();
                        for (int i = start; i < cargs.Count; i++) outVals.Add(cargs[i]);
                        return DynValue.NewTuple(outVals.ToArray());
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            _script.Globals.Set("unpack", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Table)
                    {
                        var t = cargs[0].Table;
                        var vals = t.Pairs.Select(p => p.Value).ToArray();
                        return DynValue.NewTuple(vals);
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            // Lerp helper (low, high, t) -> low + (high-low)*t
            _script.Globals.Set("Lerp", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 3 && cargs[0].Type == DataType.Number && cargs[1].Type == DataType.Number && cargs[2].Type == DataType.Number)
                    {
                        double a = cargs[0].Number;
                        double b = cargs[1].Number;
                        double t = cargs[2].Number;
                        return DynValue.NewNumber(a + (b - a) * t);
                    }
                }
                catch { }
                return DynValue.NewNumber(0);
            }));

            // GetCVar shim for simple console variables used by minimap/map code
            _script.Globals.Set("GetCVar", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.String)
                    {
                        var key = cargs[0].String;
                        if (string.Equals(key, "rotateMinimap", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("0");
                        if (string.Equals(key, "minimapZoom", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("0");
                        return DynValue.NewString("");
                    }
                }
                catch { }
                return DynValue.NewString("");
            }));

            

            // UI helper shims used by HereBeDragons and Pins: CreateFromMixins, CreateFramePool, CreateUnsecuredRegionPoolInstance
            _script.Globals.Set("CreateFromMixins", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    // return a lightweight table to represent a mixin instance
                    var t = new Table(_script);
                    return DynValue.NewTable(t);
                }
                catch { return DynValue.NewTable(new Table(_script)); }
            }));

            _script.Globals.Set("CreateFramePool", DynValue.NewCallback((cctx, cargs) =>
            {
                try { return DynValue.NewTable(new Table(_script)); } catch { return DynValue.NewTable(new Table(_script)); }
            }));

            _script.Globals.Set("CreateUnsecuredRegionPoolInstance", DynValue.NewCallback((cctx, cargs) =>
            {
                try { return DynValue.NewTable(new Table(_script)); } catch { return DynValue.NewTable(new Table(_script)); }
            }));

            // Simple placeholders for map mixins used by HereBeDragons-Pins
            _script.Globals.Set("MapCanvasDataProviderMixin", DynValue.NewTable(new Table(_script)));
            _script.Globals.Set("MapCanvasPinMixin", DynValue.NewTable(new Table(_script)));

            // Player facing stub
            _script.Globals.Set("GetPlayerFacing", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(0)));

            // Common global aliases used in WoW addons
            _script.Globals.Set("tinsert", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 2 && cargs[0].Type == DataType.Table)
                    {
                        var tbl = cargs[0].Table;
                        var val = cargs[1];
                        var nextIdx = tbl.Pairs.Count() + 1;
                        tbl.Set(nextIdx, val);
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            _script.Globals.Set("tremove", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Table)
                    {
                        var tbl = cargs[0].Table;
                        int idx = tbl.Pairs.Count();
                        if (cargs.Count >= 2 && cargs[1].Type == DataType.Number) idx = (int)cargs[1].Number;
                        var v = tbl.Get(idx);
                        tbl.Set(idx, DynValue.Nil);
                        return v ?? DynValue.Nil;
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            _script.Globals.Set("wipe", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Table)
                    {
                        var tbl = cargs[0].Table;
                        var keys = tbl.Pairs.Select(p => p.Key.String).ToList();
                        foreach (var k in keys) tbl.Set(k, DynValue.Nil);
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            // Global RegisterAddonMessagePrefix used by AceComm when C_ChatInfo is not present
            _script.Globals.Set("RegisterAddonMessagePrefix", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    var prefix = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : null;
                    EmitOutput($"[RegisterAddonMessagePrefix] prefix={prefix}");
                    return DynValue.True;
                }
                catch { return DynValue.False; }
            }));

            _script.Globals.Set("UnregisterAddonMessagePrefix", DynValue.NewCallback((cctx, cargs) => DynValue.True));

            // Ambiguate helper used by AceComm to normalize sender names
            _script.Globals.Set("Ambiguate", DynValue.NewCallback((cctx, cargs) =>
            {
                if (cargs.Count >= 1 && cargs[0].Type == DataType.String) return DynValue.NewString(cargs[0].String);
                return DynValue.NewString("");
            }));

            // getfenv shim: Lua 5.1 function to fetch an environment table. Many libs expect this.
            _script.Globals.Set("getfenv", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    // If passed a function, return the globals table (simple shim)
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Function) return DynValue.NewTable(_script.Globals);
                    // otherwise return global environment
                    return DynValue.NewTable(_script.Globals);
                }
                catch { return DynValue.NewTable(_script.Globals); }
            }));

            // Minimal 'bit' library commonly used by older WoW libs (band/bor/bnot/lshift/rshift)
            try
            {
                var bitTbl = new Table(_script);
                bitTbl.Set("band", DynValue.NewCallback((cctx, cargs) =>
                {
                    try
                    {
                        long acc = -1; // all bits set
                        for (int i = 0; i < cargs.Count; i++)
                        {
                            if (cargs[i].Type != DataType.Number) continue;
                            acc &= (long)cargs[i].Number;
                        }
                        return DynValue.NewNumber(acc);
                    }
                    catch { return DynValue.NewNumber(0); }
                }));
                bitTbl.Set("bor", DynValue.NewCallback((cctx, cargs) =>
                {
                    try
                    {
                        long acc = 0;
                        for (int i = 0; i < cargs.Count; i++) if (cargs[i].Type == DataType.Number) acc |= (long)cargs[i].Number;
                        return DynValue.NewNumber(acc);
                    }
                    catch { return DynValue.NewNumber(0); }
                }));
                bitTbl.Set("bnot", DynValue.NewCallback((cctx, cargs) =>
                {
                    try { if (cargs.Count >= 1 && cargs[0].Type == DataType.Number) return DynValue.NewNumber(~(long)cargs[0].Number); }
                    catch { }
                    return DynValue.NewNumber(0);
                }));
                bitTbl.Set("lshift", DynValue.NewCallback((cctx, cargs) =>
                {
                    try { if (cargs.Count >= 2 && cargs[0].Type == DataType.Number && cargs[1].Type == DataType.Number) return DynValue.NewNumber(((long)cargs[0].Number) << (int)cargs[1].Number); }
                    catch { }
                    return DynValue.NewNumber(0);
                }));
                bitTbl.Set("rshift", DynValue.NewCallback((cctx, cargs) =>
                {
                    try { if (cargs.Count >= 2 && cargs[0].Type == DataType.Number && cargs[1].Type == DataType.Number) return DynValue.NewNumber(((long)cargs[0].Number) >> (int)cargs[1].Number); }
                    catch { }
                    return DynValue.NewNumber(0);
                }));
                _script.Globals.Set("bit", DynValue.NewTable(bitTbl));
            }
            catch { }

            // Ensure math.atan2 exists (some libs use math.atan2 explicitly)
            try
            {
                var mathDv = _script.Globals.Get("math");
                if (mathDv != null && mathDv.Type == DataType.Table)
                {
                    var mtable = mathDv.Table;
                    var atan2Dv = mtable.Get("atan2");
                    if (atan2Dv == null || atan2Dv.IsNil())
                    {
                        mtable.Set("atan2", DynValue.NewCallback((mcctx, mcargs) =>
                        {
                            try
                            {
                                if (mcargs.Count >= 2 && mcargs[0].Type == DataType.Number && mcargs[1].Type == DataType.Number)
                                {
                                    // math.atan2(y, x) -> use Math.Atan2(y, x)
                                    double y = mcargs[0].Number; double x = mcargs[1].Number;
                                    return DynValue.NewNumber(Math.Atan2(y, x));
                                }
                            }
                            catch { }
                            return DynValue.NewNumber(0.0);
                        }));
                    }
                }
            }
            catch { }

            // Enum table shim used by some libs (e.g., ChatThrottleLib expects Enum.SendAddonMessageResult)
            try
            {
                var enumTbl = new Table(_script);
                var sendRes = new Table(_script);
                sendRes.Set("Success", DynValue.NewNumber(0));
                sendRes.Set("AddonMessageThrottle", DynValue.NewNumber(3));
                sendRes.Set("NotInGroup", DynValue.NewNumber(5));
                sendRes.Set("ChannelThrottle", DynValue.NewNumber(8));
                sendRes.Set("GeneralError", DynValue.NewNumber(9));
                enumTbl.Set("SendAddonMessageResult", DynValue.NewTable(sendRes));
                _script.Globals.Set("Enum", DynValue.NewTable(enumTbl));
            }
            catch { }

            // Provide a GetFramerate() function used by ChatThrottleLib
            _script.Globals.Set("GetFramerate", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(60)));

            // Ensure table.wipe exists (some libs expect it). Attach to existing 'table' global if present.
            try
            {
                var tableDv = _script.Globals.Get("table");
                if (tableDv != null && tableDv.Type == DataType.Table)
                {
                    var t = tableDv.Table;
                    t.Set("wipe", DynValue.NewCallback((cctx, cargs) =>
                    {
                        if (cargs.Count >= 1 && cargs[0].Type == DataType.Table)
                        {
                            var tbl = cargs[0].Table;
                            var keys = tbl.Pairs.Select(p => p.Key.String).ToList();
                            foreach (var k in keys) tbl.Set(k, DynValue.Nil);
                        }
                        return DynValue.Nil;
                    }));
                }
            }
            catch { }

            // Provide secure call helpers and error handler used by libraries
            _script.Globals.Set("geterrorhandler", DynValue.NewCallback((cctx, cargs) =>
            {
                return DynValue.NewCallback((ecctx, ecargs) =>
                {
                    try { if (ecargs.Count >= 1) EmitOutput("[Lua error] " + ecargs[0].ToPrintString()); } catch { }
                    return DynValue.Nil;
                });
            }));

            _script.Globals.Set("securecallfunction", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0] != null && !cargs[0].IsNil())
                    {
                        var fn = cargs[0];
                        var args = new List<DynValue>();
                        for (int i = 1; i < cargs.Count; i++) args.Add(cargs[i]);
                        if (fn.Type == DataType.Function) { _script.Call(fn.Function, args.ToArray()); return DynValue.True; }
                        if (fn.Type == DataType.Table)
                        {
                            var call = fn.Table.Get("__call"); if (call != null && call.Type == DataType.Function) { _script.Call(call.Function, args.ToArray()); return DynValue.True; }
                        }
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            _script.Globals.Set("securecall", _script.Globals.Get("securecallfunction"));

            // Ensure global _G exists and points to globals table
            try { _script.Globals.Set("_G", DynValue.NewTable(_script.Globals)); } catch { }

            // Install a lazy-global fallback: when code references a missing global name,
            // create a callable table stub that logs its first use and returns safe defaults.
            try
            {
                var globals = _script.Globals;

                DynValue CreateLazyStub(string name)
                {
                    try
                    {
                        var tbl = new Table(_script);
                        var mt2 = new Table(_script);
                        mt2.Set("__call", DynValue.NewCallback((cctx, cargs) =>
                        {
                            try { EmitOutput($"[LazyGlobal] Called missing global '{name}'"); } catch { }
                            // Heuristic returns
                            if (name.StartsWith("Is", StringComparison.OrdinalIgnoreCase) || name.StartsWith("In", StringComparison.OrdinalIgnoreCase)) return DynValue.NewBoolean(false);
                            if (name.StartsWith("GetNum", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Num", StringComparison.OrdinalIgnoreCase)) return DynValue.NewNumber(0);
                            if (name.StartsWith("Get") && name.EndsWith("Text", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("");
                            if (name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("");
                            return DynValue.Nil;
                        }));
                        tbl.MetaTable = mt2;
                        return DynValue.NewTable(tbl);
                    }
                    catch { return DynValue.Nil; }
                }

                var gmt = new Table(_script);
                gmt.Set("__index", DynValue.NewCallback((cctx, cargs) =>
                {
                    try
                    {
                        if (cargs.Count >= 2 && cargs[1].Type == DataType.String)
                        {
                            var nm = cargs[1].String;
                            var existing = globals.Get(nm);
                            if (existing != null && !existing.IsNil()) return existing;
                            var stub = CreateLazyStub(nm);
                            if (stub != null && !stub.IsNil())
                            {
                                globals.Set(nm, stub);
                                return stub;
                            }
                        }
                    }
                    catch { }
                    return DynValue.Nil;
                }));
                globals.MetaTable = gmt;
            }
            catch { }

            // hooksecurefunc shim: allow libs to hook globals or table methods without replacing originals
            try
            {
                _script.Globals.Set("hooksecurefunc", DynValue.NewCallback((cctx, cargs) =>
                {
                    try
                    {
                        // Form 1: hooksecurefunc(table, methodName, func)
                        if (cargs.Count >= 3 && cargs[0].Type == DataType.Table && cargs[1].Type == DataType.String && cargs[2] != null && !cargs[2].IsNil())
                        {
                            var tbl = cargs[0].Table;
                            var method = cargs[1].String;
                            var hook = cargs[2];
                            var key = tbl.GetHashCode() + ":" + method;

                            // register hook
                            if (!_tableMethodHooks.TryGetValue(key, out var list)) { list = new List<DynValue>(); _tableMethodHooks[key] = list; }
                            list.Add(hook);

                            // if original not saved, save and replace with wrapper
                            if (!_tableMethodOriginals.ContainsKey(key))
                            {
                                var existing = tbl.Get(method);
                                if (existing == null || existing.IsNil()) return DynValue.Nil;
                                _tableMethodOriginals[key] = existing;

                                // replace with wrapper
                                tbl.Set(method, DynValue.NewCallback((ictx, iargs) =>
                                {
                                    DynValue ret = DynValue.Nil;
                                    try
                                    {
                                        var orig = _tableMethodOriginals[key];
                                        if (orig != null && !orig.IsNil() && orig.Type == DataType.Function)
                                        {
                                            var callArgs = new List<DynValue>(); for (int _i = 0; _i < iargs.Count; _i++) callArgs.Add(iargs[_i]);
                                            ret = _script.Call(orig.Function, callArgs.ToArray());
                                        }
                                    }
                                    catch { }

                                    // call hooks
                                    try
                                    {
                                        if (_tableMethodHooks.TryGetValue(key, out var hooks))
                                        {
                                            foreach (var h in hooks.ToList())
                                            {
                                                try { if (h.Type == DataType.Function) { var callArgs2 = new List<DynValue>(); for (int _j = 0; _j < iargs.Count; _j++) callArgs2.Add(iargs[_j]); _script.Call(h.Function, callArgs2.ToArray()); } } catch { }
                                            }
                                        }
                                    }
                                    catch { }
                                    return ret;
                                }));
                            }
                            return DynValue.True;
                        }

                        // Form 2: hooksecurefunc(globalName, func)
                        if (cargs.Count >= 2 && cargs[0].Type == DataType.String && cargs[1] != null && !cargs[1].IsNil())
                        {
                            var gname = cargs[0].String;
                            var hook = cargs[1];

                            if (!_globalHooks.TryGetValue(gname, out var glist)) { glist = new List<DynValue>(); _globalHooks[gname] = glist; }
                            glist.Add(hook);

                            // wrap original global function if not already wrapped
                            if (!_globalOriginals.ContainsKey(gname))
                            {
                                var existing = _script.Globals.Get(gname);
                                if (existing == null || existing.IsNil()) return DynValue.Nil;
                                _globalOriginals[gname] = existing;

                                _script.Globals.Set(gname, DynValue.NewCallback((ictx, iargs) =>
                                {
                                    DynValue ret = DynValue.Nil;
                                    try
                                    {
                                        var orig = _globalOriginals[gname];
                                            if (orig != null && !orig.IsNil() && orig.Type == DataType.Function)
                                        {
                                            var callArgs = new List<DynValue>(); for (int _i = 0; _i < iargs.Count; _i++) callArgs.Add(iargs[_i]);
                                            ret = _script.Call(orig.Function, callArgs.ToArray());
                                        }
                                    }
                                    catch { }

                                    try
                                    {
                                        if (_globalHooks.TryGetValue(gname, out var hooks))
                                        {
                                            foreach (var h in hooks.ToList())
                                            {
                                                try { if (h.Type == DataType.Function) { var callArgs2 = new List<DynValue>(); for (int _j = 0; _j < iargs.Count; _j++) callArgs2.Add(iargs[_j]); _script.Call(h.Function, callArgs2.ToArray()); } } catch { }
                                            }
                                        }
                                    }
                                    catch { }
                                    return ret;
                                }));
                            }
                            return DynValue.True;
                        }
                    }
                    catch { }
                    return DynValue.Nil;
                }));
            }
            catch { }

            // Minimap stub
            var minimap = new Table(_script);
            // Provide common methods used by LibDBIcon and other minimap addons
            minimap.Set("SetPoint", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            minimap.Set("GetWidth", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(140)));
            minimap.Set("GetHeight", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(140)));
            minimap.Set("GetCenter", DynValue.NewCallback((cctx, cargs) => DynValue.NewTuple(new[] { DynValue.NewNumber(400), DynValue.NewNumber(300) })));
            minimap.Set("GetEffectiveScale", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(1)));
            // simple HookScript/SetScript storage and zoom state
            minimap.Set("__scripts", DynValue.NewTable(new Table(_script)));
            minimap.Set("__zoom", DynValue.NewNumber(0));
            minimap.Set("SetScript", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    var name = cargs.Count >= 2 && cargs[1].Type == DataType.String ? cargs[1].String : null;
                    var fn = cargs.Count >= 3 ? cargs[2] : DynValue.Nil;
                    if (!string.IsNullOrEmpty(name) && fn != null && !fn.IsNil())
                    {
                        var scriptsDv = minimap.Get("__scripts");
                        if (scriptsDv != null && scriptsDv.Type == DataType.Table) scriptsDv.Table.Set(name, fn);
                    }
                }
                catch { }
                return DynValue.Nil;
            }));
            minimap.Set("HookScript", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            minimap.Set("GetZoom", DynValue.NewCallback((cctx, cargs) =>
            {
                var zv = minimap.Get("__zoom"); if (zv != null && !zv.IsNil() && zv.Type == DataType.Number) return DynValue.NewNumber(zv.Number);
                return DynValue.NewNumber(0);
            }));
            minimap.Set("SetZoom", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Number) minimap.Set("__zoom", DynValue.NewNumber(cargs[0].Number));
                    else if (cargs.Count >= 2 && cargs[1].Type == DataType.Number) minimap.Set("__zoom", DynValue.NewNumber(cargs[1].Number));
                }
                catch { }
                return DynValue.Nil;
            }));
            minimap.Set("GetScale", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(1)));
            minimap.Set("GetMinimapShape", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("ROUND")));
            _script.Globals.Set("Minimap", DynValue.NewTable(minimap));

            // Defensive fallback: if ChatThrottleLib exists but lacks SendAddonMessage/SendChatMessage, provide simple proxies
            try
            {
                var ctlDv = _script.Globals.Get("ChatThrottleLib");
                if (ctlDv == null || ctlDv.IsNil())
                {
                    var ct = new Table(_script);
                    ct.Set("SendAddonMessage", _script.Globals.Get("SendAddonMessage"));
                    ct.Set("SendChatMessage", _script.Globals.Get("SendChatMessage"));
                    _script.Globals.Set("ChatThrottleLib", DynValue.NewTable(ct));
                }
                else if (ctlDv.Type == DataType.Table)
                {
                    var ct = ctlDv.Table;
                    var sam = ct.Get("SendAddonMessage");
                    if (sam == null || sam.IsNil()) ct.Set("SendAddonMessage", _script.Globals.Get("SendAddonMessage"));
                    var scm = ct.Get("SendChatMessage");
                    if (scm == null || scm.IsNil()) ct.Set("SendChatMessage", _script.Globals.Get("SendChatMessage"));
                }
            }
            catch { }

            // Ensure CTL methods accept colon-style calls (self passed) and forward to global send functions
            try
            {
                var ctlDv2 = _script.Globals.Get("ChatThrottleLib");
                // Diagnostic: report CTL presence and SendAddonMessage field
                try { EmitOutput($"[LuaRunner-Diag] ChatThrottleLib present={(ctlDv2!=null && !ctlDv2.IsNil())} type={(ctlDv2!=null?ctlDv2.Type.ToString():"null")} "); } catch { }
                if (ctlDv2 != null && ctlDv2.Type == DataType.Table)
                {
                    var ct = ctlDv2.Table;
                    try { var samDv = ct.Get("SendAddonMessage"); EmitOutput($"[LuaRunner-Diag] CTL.SendAddonMessage present={(samDv!=null && !samDv.IsNil())} type={(samDv!=null?samDv.Type.ToString():"null")} "); } catch { }

                    // Wrap SendAddonMessage: CTL:SendAddonMessage(prio, prefix, text, chattype, target, ...)
                    ct.Set("SendAddonMessage", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            int start = 0;
                            if (cargs.Count >= 1 && cargs[0].Type == DataType.Table) start = 1; // colon form includes self
                            var prefix = (cargs.Count > start + 1 && cargs[start + 1].Type == DataType.String) ? cargs[start + 1].String : "";
                            var text = (cargs.Count > start + 2 && cargs[start + 2].Type == DataType.String) ? cargs[start + 2].String : "";
                            var chattype = (cargs.Count > start + 3 && cargs[start + 3].Type == DataType.String) ? cargs[start + 3].String : "";
                            var target = (cargs.Count > start + 4 && (cargs[start + 4].Type == DataType.String || cargs[start + 4].Type == DataType.Number)) ? cargs[start + 4].ToPrintString() : null;

                            // prefer C_ChatInfo if present
                            var cchat = _script.Globals.Get("C_ChatInfo");
                            if (cchat != null && cchat.Type == DataType.Table)
                            {
                                var sam = cchat.Table.Get("SendAddonMessage");
                                if (sam != null && !sam.IsNil() && sam.Type == DataType.Function)
                                {
                                    _script.Call(sam.Function, DynValue.NewString(prefix), DynValue.NewString(text), DynValue.NewString(chattype), target != null ? DynValue.NewString(target) : DynValue.Nil);
                                    return DynValue.True;
                                }
                            }

                            var gsam = _script.Globals.Get("SendAddonMessage");
                            if (gsam != null && !gsam.IsNil() && gsam.Type == DataType.Function)
                            {
                                if (target != null) _script.Call(gsam.Function, DynValue.NewString(prefix), DynValue.NewString(text), DynValue.NewString(chattype), DynValue.NewString(target));
                                else _script.Call(gsam.Function, DynValue.NewString(prefix), DynValue.NewString(text), DynValue.NewString(chattype));
                                return DynValue.True;
                            }
                        }
                        catch { }
                        return DynValue.NewNumber(0);
                    }));

                    // Wrap SendChatMessage similarly (colon form accepted)
                    ct.Set("SendChatMessage", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            int start = 0;
                            if (cargs.Count >= 1 && cargs[0].Type == DataType.Table) start = 1;
                            var text = (cargs.Count > start && cargs[start].Type == DataType.String) ? cargs[start].String : "";
                            var chattype = (cargs.Count > start + 1 && cargs[start + 1].Type == DataType.String) ? cargs[start + 1].String : "";
                            var target = (cargs.Count > start + 3 && (cargs[start + 3].Type == DataType.String || cargs[start + 3].Type == DataType.Number)) ? cargs[start + 3].ToPrintString() : null;
                            var gscm = _script.Globals.Get("SendChatMessage");
                            if (gscm != null && !gscm.IsNil() && gscm.Type == DataType.Function)
                            {
                                if (target != null) _script.Call(gscm.Function, DynValue.NewString(text), DynValue.NewString(chattype), DynValue.NewString(target));
                                else _script.Call(gscm.Function, DynValue.NewString(text), DynValue.NewString(chattype));
                                return DynValue.True;
                            }
                        }
                        catch { }
                        return DynValue.NewNumber(0);
                    }));

                    // Diagnostic: try invoking the wrapper once to ensure it's callable
                    try
                    {
                        var samTest = ct.Get("SendAddonMessage");
                        if (samTest != null && samTest.Type == DataType.Function)
                        {
                            try
                            {
                                // Call as function (no self) with sample args: prio, prefix, text, chattype, target
                                _script.Call(samTest.Function, DynValue.NewString("NORMAL"), DynValue.NewString("DIAG"), DynValue.NewString("hello"), DynValue.NewString("GUILD"), DynValue.NewString("Player"));
                                try { EmitOutput("[LuaRunner-Diag] CTL.SendAddonMessage test-call succeeded"); } catch { }
                            }
                            catch (Exception ex)
                            {
                                try { EmitOutput("[LuaRunner-Diag] CTL.SendAddonMessage test-call failed: " + ex.Message); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Simple Mixin helper: copy fields from mixin(s) onto target
            _script.Globals.Set("Mixin", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 2 && cargs[0].Type == DataType.Table && cargs[1].Type == DataType.Table)
                    {
                        var target = cargs[0].Table;
                        var mix = cargs[1].Table;
                        foreach (var p in mix.Pairs)
                        {
                            try { target.Set(p.Key, p.Value); } catch { }
                        }
                        return DynValue.NewTable(target);
                    }
                }
                catch { }
                return DynValue.Nil;
            }));

            // WorldMapFrame stub with GetCanvas to satisfy pins
            var worldMapFrame = new Table(_script);
            worldMapFrame.Set("GetCanvas", DynValue.NewCallback((cctx, cargs) =>
            {
                try { return DynValue.NewTable(new Table(_script)); } catch { return DynValue.NewTable(new Table(_script)); }
            }));
            // Allow adding data providers and track them
            worldMapFrame.Set("__providers", DynValue.NewTable(new Table(_script)));
            worldMapFrame.Set("AddDataProvider", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Table)
                    {
                        var prov = cargs[0].Table;
                        var providers = worldMapFrame.Get("__providers").Table;
                        var nextIdx = providers.Pairs.Count() + 1;
                        providers.Set(nextIdx, DynValue.NewTable(prov));
                    }
                }
                catch { }
                return DynValue.Nil;
            }));
            // ensure pinPools table exists (HereBeDragons-Pins expects WorldMapFrame.pinPools[...] assignment)
            try { worldMapFrame.Set("pinPools", DynValue.NewTable(new Table(_script))); } catch { }
            _script.Globals.Set("WorldMapFrame", DynValue.NewTable(worldMapFrame));

            // AddonCompartmentFrame stub used by LibDBIcon
            var acf = new Table(_script);
            acf.Set("registeredAddons", DynValue.NewTable(new Table(_script)));
            acf.Set("RegisterAddon", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 2 && cargs[1].Type == DataType.Table)
                    {
                        var entry = cargs[1].Table;
                        var regs = acf.Get("registeredAddons").Table;
                        // append
                        var nextIdx = regs.Pairs.Count() + 1;
                        regs.Set(nextIdx, DynValue.NewTable(entry));
                    }
                }
                catch { }
                return DynValue.Nil;
            }));
            acf.Set("UpdateDisplay", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            _script.Globals.Set("AddonCompartmentFrame", DynValue.NewTable(acf));

            // Basic environment stubs used by AceDB / AceConfig
            _script.Globals.Set("GetRealmName", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("LocalRealm")));
            _script.Globals.Set("GetLocale", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("enUS")));
            _script.Globals.Set("GetCurrentRegion", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(1)));
            _script.Globals.Set("GetCurrentRegionName", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("US")));
            _script.Globals.Set("UnitClass", DynValue.NewCallback((cctx, cargs) => DynValue.NewTuple(new[] { DynValue.NewString("UNKNOWN"), DynValue.NewString("UNKNOWN") })));
            _script.Globals.Set("UnitRace", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("Human")));
            _script.Globals.Set("UnitFactionGroup", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("Alliance")));

            // Unit API stubs
            _script.Globals.Set("UnitName", DynValue.NewCallback((cctx, cargs) =>
            {
                var u = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                if (string.Equals(u, "player", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("Player");
                return DynValue.Nil;
            }));
            _script.Globals.Set("UnitExists", DynValue.NewCallback((cctx, cargs) =>
            {
                var u = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                return DynValue.NewBoolean(string.Equals(u, "player", StringComparison.OrdinalIgnoreCase));
            }));
            _script.Globals.Set("UnitIsPlayer", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(true)));
            _script.Globals.Set("UnitClass", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("UNKNOWN")));
            _script.Globals.Set("UnitLevel", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(60)));
            _script.Globals.Set("UnitHealth", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(100)));
            _script.Globals.Set("UnitHealthMax", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(100)));
            _script.Globals.Set("UnitPower", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(100)));
            _script.Globals.Set("UnitGUID", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("Player-1")));
            _script.Globals.Set("UnitIsDead", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(false)));
            _script.Globals.Set("UnitAura", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));

            // Group APIs
            _script.Globals.Set("IsInGroup", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(_roster.Count > 0)));
            _script.Globals.Set("GetNumGroupMembers", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(_roster.Count)));
            _script.Globals.Set("IsInRaid", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(false)));

            // Legacy/compatibility group helpers
            _script.Globals.Set("GetNumPartyMembers", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(Math.Max(0, _roster.Count - 1))));
            _script.Globals.Set("GetNumSubgroupMembers", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(Math.Max(0, _roster.Count - 1))));

            // Zone/location helpers
            _script.Globals.Set("GetZoneText", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("Eastern Kingdoms")));
            _script.Globals.Set("GetRealZoneText", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("Elwynn Forest")));
            _script.Globals.Set("GetSubZoneText", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("")));
            _script.Globals.Set("IsInInstance", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(false)));

            // Sound and popup helpers often used by UI code
            _script.Globals.Set("PlaySound", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            _script.Globals.Set("PlaySoundFile", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            _script.Globals.Set("StaticPopup_Show", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("")));

            // Combat/secure environment helpers
            _script.Globals.Set("InCombatLockdown", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(false)));

            // CVar helpers
            _script.Globals.Set("SetCVar", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("")));
            _script.Globals.Set("GetCVarBool", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(false)));

            // Expose a helper so test code or addons can populate a fake roster for unitframe testing.
            _script.Globals.Set("Flux_SetRoster", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    _roster.Clear();
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.Table)
                    {
                        var tbl = cargs[0].Table;
                        var i = 1;
                        foreach (var pair in tbl.Pairs)
                        {
                            var entry = pair.Value;
                            var member = new Dictionary<string, object?>();
                            if (entry.Type == DataType.String)
                            {
                                member["name"] = entry.String;
                            }
                            else if (entry.Type == DataType.Table)
                            {
                                var et = entry.Table;
                                try { var n = et.Get("name"); if (n != null && n.Type == DataType.String) member["name"] = n.String; } catch { }
                                try { var c = et.Get("class"); if (c != null && c.Type == DataType.String) member["class"] = c.String; } catch { }
                                try { var lv = et.Get("level"); if (lv != null && lv.Type == DataType.Number) member["level"] = (int)lv.Number; } catch { }
                            }
                            // assign unit token party1..partyN
                            member["unit"] = "party" + i;
                            member["guid"] = "GUID-" + i;
                            member["online"] = true;
                            member["isDead"] = false;
                            _roster.Add(member);
                            i++;
                        }
                    }
                    return DynValue.True;
                }
                catch { return DynValue.False; }
            }));

            // Helper to map unit token to roster member index
            Func<string, int> mapUnitToIndex = (unit) =>
            {
                if (string.IsNullOrEmpty(unit)) return -1;
                unit = unit.ToLowerInvariant();
                if (unit == "player") return -1;
                if (unit.StartsWith("party"))
                {
                    if (int.TryParse(unit.Substring(5), out var n))
                    {
                        if (n >= 1 && n <= _roster.Count) return n - 1;
                    }
                }
                if (unit.StartsWith("raid"))
                {
                    if (int.TryParse(unit.Substring(4), out var n))
                    {
                        if (n >= 1 && n <= _roster.Count) return n - 1;
                    }
                }
                return -1;
            };

            // Unit mapping helpers
            _script.Globals.Set("UnitGUID", DynValue.NewCallback((cctx, cargs) =>
            {
                var unit = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                if (string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("Player-1");
                var idx = mapUnitToIndex(unit);
                if (idx >= 0 && idx < _roster.Count) return DynValue.NewString((_roster[idx].TryGetValue("guid", out var g) ? (g?.ToString() ?? $"GUID-{idx+1}") : $"GUID-{idx+1}"));
                return DynValue.Nil;
            }));

            _script.Globals.Set("UnitName", DynValue.NewCallback((cctx, cargs) =>
            {
                var unit = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                if (string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("Player");
                var idx = mapUnitToIndex(unit);
                if (idx >= 0 && idx < _roster.Count) return DynValue.NewString(_roster[idx].TryGetValue("name", out var n) ? (n?.ToString() ?? $"Unit{idx+1}") : $"Unit{idx+1}");
                return DynValue.Nil;
            }));

            _script.Globals.Set("UnitExists", DynValue.NewCallback((cctx, cargs) =>
            {
                var unit = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                if (string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase)) return DynValue.NewBoolean(true);
                var idx = mapUnitToIndex(unit);
                return DynValue.NewBoolean(idx >= 0 && idx < _roster.Count);
            }));

            _script.Globals.Set("UnitClass", DynValue.NewCallback((cctx, cargs) =>
            {
                var unit = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                if (string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase)) return DynValue.NewString("UNKNOWN");
                var idx = mapUnitToIndex(unit);
                if (idx >= 0 && idx < _roster.Count) return DynValue.NewString(_roster[idx].TryGetValue("class", out var c) ? (c?.ToString() ?? "UNKNOWN") : "UNKNOWN");
                return DynValue.Nil;
            }));

            _script.Globals.Set("UnitLevel", DynValue.NewCallback((cctx, cargs) =>
            {
                var unit = cargs.Count >= 1 && cargs[0].Type == DataType.String ? cargs[0].String : "";
                if (string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase)) return DynValue.NewNumber(60);
                var idx = mapUnitToIndex(unit);
                if (idx >= 0 && idx < _roster.Count) return DynValue.NewNumber(_roster[idx].TryGetValue("level", out var l) ? Convert.ToDouble(l ?? 1) : 1);
                return DynValue.Nil;
            }));

            // Raid roster info: name, rank, subgroup, level, class, zone, online, isDead, role, isML
            _script.Globals.Set("GetRaidRosterInfo", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count < 1 || cargs[0].Type != DataType.Number) return DynValue.Nil;
                    var idx = (int)cargs[0].Number - 1;
                    if (idx < 0 || idx >= _roster.Count) return DynValue.Nil;
                    var m = _roster[idx];
                    var name = m.TryGetValue("name", out var n) ? (n?.ToString() ?? "") : "";
                    var subgroup = m.TryGetValue("subgroup", out var sg) ? Convert.ToInt32(sg ?? 1) : 1;
                    var level = m.TryGetValue("level", out var lv) ? Convert.ToInt32(lv ?? 1) : 1;
                    var cls = m.TryGetValue("class", out var cl) ? (cl?.ToString() ?? "UNKNOWN") : "UNKNOWN";
                    var online = m.TryGetValue("online", out var on) ? (on is bool ob && ob) : true;
                    var isDead = m.TryGetValue("isDead", out var idd) ? (idd is bool db && db) : false;
                    var tuple = DynValue.NewTuple(new[] { DynValue.NewString(name), DynValue.Nil, DynValue.NewNumber(subgroup), DynValue.NewNumber(level), DynValue.NewString(cls), DynValue.Nil, DynValue.NewBoolean(online), DynValue.NewBoolean(isDead), DynValue.Nil, DynValue.Nil });
                    return tuple;
                }
                catch { return DynValue.Nil; }
            }));

            // Bags/Inventory stubs
            _script.Globals.Set("GetContainerNumSlots", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(0)));
            _script.Globals.Set("GetContainerItemLink", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            _script.Globals.Set("GetInventoryItemLink", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));
            _script.Globals.Set("GetItemInfo", DynValue.NewCallback((cctx, cargs) => DynValue.Nil));

            // Provide GetTime and C_Timer.After shims used by AceTimer and other libs
            _script.Globals.Set("GetTime", DynValue.NewCallback((ctx, args) =>
            {
                try
                {
                    var secs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    return DynValue.NewNumber(secs);
                }
                catch { return DynValue.NewNumber(0.0); }
            }));

            // Minimal cursor helper used by LibDBIcon's OnUpdate
            _script.Globals.Set("GetCursorPosition", DynValue.NewCallback((ctx, args) => DynValue.NewTuple(new[] { DynValue.NewNumber(0), DynValue.NewNumber(0) })));

            // CreateVector2D(x, y) -> object with :GetXY() used by HereBeDragons
            _script.Globals.Set("CreateVector2D", DynValue.NewCallback((ctx, args) =>
            {
                try
                {
                    double x = 0, y = 0;
                    if (args.Count >= 1 && args[0].Type == DataType.Number) x = args[0].Number;
                    if (args.Count >= 2 && args[1].Type == DataType.Number) y = args[1].Number;
                    var v = new Table(_script);
                    v.Set("GetXY", DynValue.NewCallback((cctx, cargs) => DynValue.NewTuple(new[] { DynValue.NewNumber(x), DynValue.NewNumber(y) })));
                    return DynValue.NewTable(v);
                }
                catch { return DynValue.Nil; }
            }));

            // Minimal C_Map stub implementing methods used by HereBeDragons
            var cMap = new Table(_script);
            // GetMapInfo(uiMapID) -> table with name,parentMapID,mapType
            cMap.Set("GetMapInfo", DynValue.NewCallback((ctx, args) =>
            {
                try
                {
                    if (args.Count < 1 || args[0].Type != DataType.Number) return DynValue.Nil;
                    var id = (int)args[0].Number;
                    var info = new Table(_script);
                    info.Set("mapID", DynValue.NewNumber(id));
                    info.Set("name", DynValue.NewString("Map" + id));
                    info.Set("parentMapID", DynValue.Nil);
                    info.Set("mapType", DynValue.NewNumber(0));
                    return DynValue.NewTable(info);
                }
                catch { return DynValue.Nil; }
            }));

            // GetWorldPosFromMapPos(uiMapID, vector2d) -> (instanceID, positionTable)
            cMap.Set("GetWorldPosFromMapPos", DynValue.NewCallback((ctx, args) =>
            {
                try
                {
                    // Return a dummy instance id and a point-like table with GetXY()
                    var pt = new Table(_script);
                    pt.Set("GetXY", DynValue.NewCallback((cctx, cargs) => DynValue.NewTuple(new[] { DynValue.NewNumber(0), DynValue.NewNumber(0) })));
                    return DynValue.NewTuple(new[] { DynValue.NewNumber(0), DynValue.NewTable(pt) });
                }
                catch { return DynValue.Nil; }
            }));

            cMap.Set("GetMapChildrenInfo", DynValue.NewCallback((ctx, args) => DynValue.NewTable(new Table(_script))));
            cMap.Set("GetMapGroupID", DynValue.NewCallback((ctx, args) => DynValue.Nil));
            cMap.Set("GetMapGroupMembersInfo", DynValue.NewCallback((ctx, args) => DynValue.NewTable(new Table(_script))));
            cMap.Set("GetBestMapForUnit", DynValue.NewCallback((ctx, args) => DynValue.NewNumber(0)));
            cMap.Set("GetMapWorldSize", DynValue.NewCallback((ctx, args) => DynValue.NewTuple(new[] { DynValue.NewNumber(1000), DynValue.NewNumber(1000) })));
            cMap.Set("GetMapRectOnMap", DynValue.NewCallback((ctx, args) => DynValue.Nil));
            _script.Globals.Set("C_Map", DynValue.NewTable(cMap));

            // IsLoggedIn(): assume true for library initialization paths that expect the player to be logged in
            _script.Globals.Set("IsLoggedIn", DynValue.NewCallback((cctx, cargs) => DynValue.NewBoolean(true)));

            // AddOn management shims: IsAddOnLoaded, LoadAddOn, GetAddOnMetadata, GetAddOnInfo, GetAddOnEnableState
            _script.Globals.Set("IsAddOnLoaded", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.String)
                    {
                        var nm = cargs[0].String;
                        return DynValue.NewBoolean(_aceAddons.ContainsKey(nm) || _addonNamespaces.ContainsKey(nm));
                    }
                }
                catch { }
                return DynValue.NewBoolean(false);
            }));

            _script.Globals.Set("LoadAddOn", DynValue.NewCallback((cctx, cargs) =>
            {
                try
                {
                    if (cargs.Count >= 1 && cargs[0].Type == DataType.String)
                    {
                        var nm = cargs[0].String;
                        // No-op load: report success if addon has been preloaded into the namespaces map
                        return DynValue.NewBoolean(_addonNamespaces.ContainsKey(nm) || _aceAddons.ContainsKey(nm));
                    }
                }
                catch { }
                return DynValue.NewBoolean(false);
            }));

            _script.Globals.Set("GetAddOnMetadata", DynValue.NewCallback((cctx, cargs) => DynValue.NewString("")));
            _script.Globals.Set("GetAddOnInfo", DynValue.NewCallback((cctx, cargs) => DynValue.NewTuple(new[] { DynValue.NewString(""), DynValue.NewString(""), DynValue.NewNumber(0), DynValue.NewBoolean(false) })));
            _script.Globals.Set("GetAddOnEnableState", DynValue.NewCallback((cctx, cargs) => DynValue.NewNumber(1)));

            // Define project constants used by some libs (HereBeDragons checks these)
            _script.Globals.Set("WOW_PROJECT_ID", DynValue.NewNumber(1));
            _script.Globals.Set("WOW_PROJECT_CLASSIC", DynValue.NewNumber(1));
            _script.Globals.Set("WOW_PROJECT_BURNING_CRUSADE_CLASSIC", DynValue.NewNumber(2));
            _script.Globals.Set("WOW_PROJECT_WRATH_CLASSIC", DynValue.NewNumber(3));
            _script.Globals.Set("WOW_PROJECT_MAINLINE", DynValue.NewNumber(4));

            var cTimerTbl = new Table(_script);
            cTimerTbl.Set("After", DynValue.NewCallback((ctx, args) =>
            {
                try
                {
                    if (args.Count >= 2 && args[0].Type == DataType.Number && args[1] != null && !args[1].IsNil())
                    {
                        double delay = args[0].Number;
                        var fn = args[1];
                        int id;
                        lock (_timerLock) { id = _nextTimerId++; }
                        var t = new System.Timers.Timer(delay * 1000.0);
                        t.AutoReset = false;
                        t.Elapsed += (s, e) =>
                        {
                            try
                            {
                                if (fn.Type == DataType.Function) _script.Call(fn);
                                else if (fn.Type == DataType.Table && fn.Table.Get("__call") != null) _script.Call(fn);
                            }
                            catch (Exception ex) { try { EmitOutput("[C_Timer] callback error: " + ex.Message); } catch { } }
                            try { lock (_timerLock) { if (_timers.ContainsKey(id)) _timers.Remove(id); } } catch { }
                            try { t.Dispose(); } catch { }
                        };
                        lock (_timerLock) { _timers[id] = t; }
                        try { t.Start(); } catch { }
                        return DynValue.NewNumber(id);
                    }
                }
                catch (Exception ex) { try { EmitOutput("[C_Timer] After error: " + ex.Message); } catch { } }
                return DynValue.Nil;
            }));
            _script.Globals.Set("C_Timer", DynValue.NewTable(cTimerTbl));

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
                            try { MakeCallableTable(addonTbl, newName); } catch { }
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
            
            // Provide safe, minimal stubs for some common WoW libraries that many addons expect.
            try
            {
                // ChatThrottleLib stub: very small no-op implementations to satisfy callers
                var ctl = new Table(_script);
                // set a default version so the preloaded ChatThrottleLib.lua doesn't error comparing nil
                ctl.Set("version", DynValue.NewNumber(0));
                ctl.Set("securelyHooked", DynValue.NewBoolean(true));
                ctl.Set("SendAddonMessage", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        // support both method and function call forms
                        int idx = 0;
                        if (args.Count > 0 && args[0].Type == DataType.Table) idx = 1; // skip self
                        var prio = args.Count > idx ? args[idx].ToPrintString() : "NORMAL";
                        var prefix = args.Count > idx + 1 && args[idx + 1].Type == DataType.String ? args[idx + 1].String : "";
                        var text = args.Count > idx + 2 && args[idx + 2].Type == DataType.String ? args[idx + 2].String : "";
                        var distribution = args.Count > idx + 3 && args[idx + 3].Type == DataType.String ? args[idx + 3].String : "";
                        var target = args.Count > idx + 4 && (args[idx + 4].Type == DataType.String || args[idx+4].Type==DataType.Number) ? args[idx + 4].ToPrintString() : null;
                        var queueName = args.Count > idx + 5 && args[idx + 5].Type == DataType.String ? args[idx + 5].String : prefix;
                        var cb = args.Count > idx + 6 ? args[idx + 6] : DynValue.Nil;
                        var textlen = args.Count > idx + 7 && args[idx + 7].Type == DataType.Number ? (int)args[idx + 7].Number : (text?.Length ?? 0);

                        EmitOutput($"[ChatThrottleLib] SendAddonMessage prio={prio} prefix={prefix} dist={distribution} target={target} len={textlen}");

                        // If a callback was supplied, call it to indicate bytes sent and success
                        if (cb != null && !cb.IsNil())
                        {
                            try
                            {
                                if (cb.Type == DataType.Function) cb.Function.Call(DynValue.NewNumber(textlen), DynValue.NewBoolean(true));
                            }
                            catch { }
                        }
                        return DynValue.True;
                    }
                    catch { return DynValue.Nil; }
                }));
                ctl.Set("RegisterAddonMessagePrefix", DynValue.NewCallback((ctx, args) => { return DynValue.True; }));
                try { _libRegistry["ChatThrottleLib"] = ctl; } catch { }
                try { libsTbl.Set("ChatThrottleLib", DynValue.NewTable(ctl)); } catch { }
                // Expose global variable ChatThrottleLib to satisfy code that asserts it
                try { _script.Globals.Set("ChatThrottleLib", DynValue.NewTable(ctl)); } catch { }
                // Also register common versioned name
                try { _libRegistry["ChatThrottleLib-1.0"] = ctl; } catch { }
                try { libsTbl.Set("ChatThrottleLib-1.0", DynValue.NewTable(ctl)); } catch { }

                // AceTimer-3.0 minimal implementation: supports ScheduleTimer/CancelTimer/ScheduleRepeatingTimer
                var aceTimer = new Table(_script);
                aceTimer.Set("ScheduleTimer", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        // args: (self?, funcOrMethod, delay, arg1, arg2, ...)
                        int idx = 0;
                        DynValue selfDv = DynValue.Nil;
                        if (args.Count > 0 && args[0].Type == DataType.Table)
                        {
                            selfDv = args[0];
                            idx = 1;
                        }

                        if (args.Count <= idx)
                        {
                            EmitOutput("[AceTimer] ScheduleTimer missing args");
                            return DynValue.Nil;
                        }

                        DynValue fnDv = args.Count > idx ? args[idx] : DynValue.Nil;
                        double delay = 0.1;
                        if (args.Count > idx + 1 && args[idx + 1].Type == DataType.Number) delay = args[idx + 1].Number;

                        // collect additional args to pass through
                        var passArgs = new List<DynValue>();
                        for (int i = idx + 2; i < args.Count; i++) passArgs.Add(args[i]);

                        int id;
                        lock (_timerLock)
                        {
                            id = _nextTimerId++;
                        }

                        var t = new System.Timers.Timer(delay * 1000.0);
                        t.AutoReset = false;
                        t.Elapsed += (s, e) =>
                        {
                            try
                            {
                                // If fnDv is a function, call it directly
                                if (fnDv != null && !fnDv.IsNil() && fnDv.Type == DataType.Function)
                                {
                                    try { _script.Call(fnDv, passArgs.ToArray()); }
                                    catch (Exception ex) { try { EmitOutput("[AceTimer] scheduled callback error: " + ex.Message); } catch { } }
                                }
                                // If fnDv is a string and self is a table, lookup method at call time
                                else if (fnDv != null && !fnDv.IsNil() && fnDv.Type == DataType.String && selfDv != null && !selfDv.IsNil() && selfDv.Type == DataType.Table)
                                {
                                    try
                                    {
                                        var methodName = fnDv.String;
                                        var methodDv = selfDv.Table.Get(methodName);
                                        if (methodDv != null && !methodDv.IsNil() && methodDv.Type == DataType.Function)
                                        {
                                            // prepend self as first arg
                                            var callArgs = new List<DynValue> { selfDv };
                                            callArgs.AddRange(passArgs);
                                            _script.Call(methodDv, callArgs.ToArray());
                                        }
                                        else { try { EmitOutput($"[AceTimer] method '{methodName}' not found on self"); } catch { } }
                                    }
                                    catch (Exception ex) { try { EmitOutput("[AceTimer] scheduled method error: " + ex.Message); } catch { } }
                                }
                                else { try { EmitOutput("[AceTimer] unsupported callback type for scheduled timer"); } catch { } }
                            }
                            catch (Exception ex) { try { EmitOutput("[AceTimer] scheduled outer error: " + ex.Message); } catch { } }
                            try { lock (_timerLock) { if (_timers.ContainsKey(id)) { _timers.Remove(id); } } } catch { }
                            try { t.Dispose(); } catch { }
                        };

                        lock (_timerLock) { _timers[id] = t; }
                        try { t.Start(); } catch { }
                        return DynValue.NewNumber(id);
                    }
                    catch (Exception ex) { EmitOutput("[AceTimer] ScheduleTimer error: " + ex.Message); }
                    return DynValue.Nil;
                }));

                aceTimer.Set("CancelTimer", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        if (args.Count >= 1 && args[0].Type == DataType.Number)
                        {
                            var id = (int)args[0].Number;
                            lock (_timerLock)
                            {
                                if (_timers.TryGetValue(id, out var t))
                                {
                                    try { t.Stop(); t.Dispose(); } catch { }
                                    _timers.Remove(id);
                                    return DynValue.True;
                                }
                            }
                        }
                    }
                    catch { }
                    return DynValue.False;
                }));

                aceTimer.Set("ScheduleRepeatingTimer", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        // args: (self?, funcOrMethod, delay, arg1, arg2, ...)
                        int idx = 0;
                        DynValue selfDv = DynValue.Nil;
                        if (args.Count > 0 && args[0].Type == DataType.Table)
                        {
                            selfDv = args[0];
                            idx = 1;
                        }

                        if (args.Count <= idx)
                        {
                            EmitOutput("[AceTimer] ScheduleRepeatingTimer missing args");
                            return DynValue.Nil;
                        }

                        DynValue fnDv = args.Count > idx ? args[idx] : DynValue.Nil;
                        double delay = 1.0;
                        if (args.Count > idx + 1 && args[idx + 1].Type == DataType.Number) delay = args[idx + 1].Number;

                        var passArgs = new List<DynValue>();
                        for (int i = idx + 2; i < args.Count; i++) passArgs.Add(args[i]);

                        int id;
                        lock (_timerLock) { id = _nextTimerId++; }
                        var t = new System.Timers.Timer(delay * 1000.0);
                        t.AutoReset = true;
                        t.Elapsed += (s, e) =>
                        {
                            try
                            {
                                if (fnDv != null && !fnDv.IsNil() && fnDv.Type == DataType.Function)
                                {
                                    try { _script.Call(fnDv, passArgs.ToArray()); } catch (Exception ex) { try { EmitOutput("[AceTimer] repeating callback error: " + ex.Message); } catch { } }
                                }
                                else if (fnDv != null && !fnDv.IsNil() && fnDv.Type == DataType.String && selfDv != null && !selfDv.IsNil() && selfDv.Type == DataType.Table)
                                {
                                    try
                                    {
                                        var methodName = fnDv.String;
                                        var methodDv = selfDv.Table.Get(methodName);
                                        if (methodDv != null && !methodDv.IsNil() && methodDv.Type == DataType.Function)
                                        {
                                            var callArgs = new List<DynValue> { selfDv };
                                            callArgs.AddRange(passArgs);
                                            _script.Call(methodDv, callArgs.ToArray());
                                        }
                                        else { try { EmitOutput($"[AceTimer] repeating method '{methodName}' not found on self"); } catch { } }
                                    }
                                    catch (Exception ex) { try { EmitOutput("[AceTimer] repeating method error: " + ex.Message); } catch { } }
                                }
                                else { try { EmitOutput("[AceTimer] unsupported callback type for repeating timer"); } catch { } }
                            }
                            catch (Exception ex) { try { EmitOutput("[AceTimer] repeating outer error: " + ex.Message); } catch { } }
                        };
                        lock (_timerLock) { _timers[id] = t; }
                        try { t.Start(); } catch { }
                        return DynValue.NewNumber(id);
                    }
                    catch (Exception ex) { EmitOutput("[AceTimer] ScheduleRepeatingTimer error: " + ex.Message); }
                    return DynValue.Nil;
                }));

                try { _libRegistry["AceTimer-3.0"] = aceTimer; } catch { }
                try { libsTbl.Set("AceTimer-3.0", DynValue.NewTable(aceTimer)); } catch { }

                // AceEvent-3.0 minimal: RegisterEvent/UnregisterEvent/TriggerEvent
                var aceEvent = new Table(_script);
                aceEvent.Set("RegisterEvent", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        // args: (self, eventName, handler)
                        int idx = 0;
                        if (args.Count > 0 && args[0].Type == DataType.Table) idx = 1;
                        if (args.Count <= idx + 1) return DynValue.Nil;
                        var evName = args[idx].Type == DataType.String ? args[idx].String : null;
                        var handlerDv = args[idx + 1];
                        if (string.IsNullOrEmpty(evName) || handlerDv == null || handlerDv.IsNil() || handlerDv.Type != DataType.Function) return DynValue.Nil;
                        var closure = handlerDv.Function;
                        lock (_eventHandlers)
                        {
                            if (!_eventHandlers.TryGetValue(evName, out var list)) { list = new List<Closure>(); _eventHandlers[evName] = list; }
                            list.Add(closure);
                        }
                        return DynValue.True;
                    }
                    catch { }
                    return DynValue.Nil;
                }));

                aceEvent.Set("UnregisterEvent", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        int idx = 0;
                        if (args.Count > 0 && args[0].Type == DataType.Table) idx = 1;
                        if (args.Count <= idx + 1) return DynValue.False;
                        var evName = args[idx].Type == DataType.String ? args[idx].String : null;
                        var handlerDv = args[idx + 1];
                        if (string.IsNullOrEmpty(evName) || handlerDv == null || handlerDv.IsNil() || handlerDv.Type != DataType.Function) return DynValue.False;
                        var closure = handlerDv.Function;
                        lock (_eventHandlers)
                        {
                            if (_eventHandlers.TryGetValue(evName, out var list))
                            {
                                list.RemoveAll(c => c == closure);
                                return DynValue.True;
                            }
                        }
                    }
                    catch { }
                    return DynValue.False;
                }));

                aceEvent.Set("TriggerEvent", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        if (args.Count < 1) return DynValue.Nil;
                        var evName = args[0].Type == DataType.String ? args[0].String : null;
                        if (string.IsNullOrEmpty(evName)) return DynValue.Nil;
                        List<Closure>? handlers = null;
                        lock (_eventHandlers) { if (_eventHandlers.TryGetValue(evName, out var lst)) handlers = new List<Closure>(lst); }
                        if (handlers == null) return DynValue.Nil;
                        foreach (var h in handlers)
                        {
                            try { h.Call(); } catch (Exception ex) { try { EmitOutput("[AceEvent] handler error: " + ex.Message); } catch { } }
                        }
                    }
                    catch { }
                    return DynValue.Nil;
                }));

                try { _libRegistry["AceEvent-3.0"] = aceEvent; } catch { }
                try { libsTbl.Set("AceEvent-3.0", DynValue.NewTable(aceEvent)); } catch { }
            }
            catch (Exception ex)
            {
                try { EmitOutput("[LuaRunner] Failed to install light shims: " + ex.Message); } catch { }
            }

            // Provide basic GetLocale so libraries that inspect locale don't nil-ref
            _script.Globals.Set("GetLocale", DynValue.NewCallback((ctx, args) => DynValue.NewString("enUS")));

            // CallbackHandler-1.0 implementation: provides callback registration and firing
            try
            {
                var cbh = new Table(_script);
                // New(owner, ...)
                cbh.Set("New", DynValue.NewCallback((ctx, aargs) =>
                {
                    // capture the owner object if provided. Support both
                    // method-call form (CallbackHandler:New(owner,...)) where args[0] is the
                    // CallbackHandler table and args[1] is the owner, and function-call
                    // form (CallbackHandler.New(owner,...)) where args[0] is the owner.
                    Table? owner = null;
                    int ownerIndex = -1;
                    if (aargs.Count >= 2 && aargs[1].Type == DataType.Table) { owner = aargs[1].Table; ownerIndex = 1; }
                    else if (aargs.Count >= 1 && aargs[0].Type == DataType.Table) { owner = aargs[0].Table; ownerIndex = 0; }

                    // optional method names supplied by some libs: (owner, registerName, unregisterName, unregisterAllName)
                    string? registerName = null;
                    string? unregisterName = null;
                    string? unregisterAllName = null;
                    if (ownerIndex >= 0)
                    {
                        if (aargs.Count >= ownerIndex + 2 && aargs[ownerIndex + 1].Type == DataType.String) registerName = aargs[ownerIndex + 1].String;
                        if (aargs.Count >= ownerIndex + 3 && aargs[ownerIndex + 2].Type == DataType.String) unregisterName = aargs[ownerIndex + 2].String;
                        if (aargs.Count >= ownerIndex + 4 && aargs[ownerIndex + 3].Type == DataType.String) unregisterAllName = aargs[ownerIndex + 3].String;
                    }

                    var obj = new Table(_script);
                    // internal registrations table: eventName -> array of { target=tbl, handler=fnOrString }
                    var regs = new Table(_script);
                    obj.Set("__registrations", DynValue.NewTable(regs));

                    // helper to add an entry
                    obj.Set("Register", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            // expected call: callbacks:Register(eventName, handler) where owner was provided to New
                            if (cargs.Count < 2) return DynValue.Nil;
                            var ev = cargs[0].Type == DataType.String ? cargs[0].String : null;
                            var handler = cargs[1];
                            if (string.IsNullOrEmpty(ev) || handler == null || handler.IsNil()) return DynValue.Nil;

                            var listDv = regs.Get(ev);
                            Table list;
                            if (listDv == null || listDv.IsNil()) { list = new Table(_script); regs.Set(ev, DynValue.NewTable(list)); }
                            else list = listDv.Table;

                            var entry = new Table(_script);
                            entry.Set("target", owner != null ? DynValue.NewTable(owner) : DynValue.Nil);
                            entry.Set("handler", handler);
                            list.Set(list.Pairs.Count() + 1, DynValue.NewTable(entry));
                            return DynValue.True;
                        }
                        catch { return DynValue.Nil; }
                    }));

                    // RegisterCallback alias (some libs call this)
                    obj.Set("RegisterCallback", obj.Get("Register"));

                    // If an owner is provided, inject wrapper methods onto owner. Use provided names if available,
                    // otherwise inject common default names so libraries can call owner.RegisterCallback(...)
                    if (owner != null)
                    {
                        try
                        {
                            var regName = !string.IsNullOrEmpty(registerName) ? registerName : "RegisterCallback";
                            var unregName = !string.IsNullOrEmpty(unregisterName) ? unregisterName : "UnregisterCallback";
                            var unregAllName = !string.IsNullOrEmpty(unregisterAllName) ? unregisterAllName : "UnregisterAll";

                            if (!string.IsNullOrEmpty(regName))
                            {
                                owner.Set(regName, DynValue.NewCallback((cctx, cargs) =>
                                {
                                    try
                                    {
                                        // allow both method and function call forms
                                        string? ev = null; DynValue handler = DynValue.Nil;
                                        if (cargs.Count >= 1 && cargs[0].Type == DataType.String) { ev = cargs[0].String; handler = cargs.Count >= 2 ? cargs[1] : DynValue.Nil; }
                                        else if (cargs.Count >= 2 && cargs[1].Type == DataType.String) { ev = cargs[1].String; handler = cargs.Count >= 3 ? cargs[2] : DynValue.Nil; }
                                        if (string.IsNullOrEmpty(ev)) return DynValue.Nil;
                                        var regDv = obj.Get("Register"); if (regDv == null || regDv.IsNil() || regDv.Type != DataType.Function) return DynValue.Nil;
                                        _script.Call(regDv.Function, DynValue.NewString(ev), handler);
                                        return DynValue.True;
                                    }
                                    catch { return DynValue.Nil; }
                                }));
                            }
                            if (!string.IsNullOrEmpty(unregName))
                            {
                                owner.Set(unregName, DynValue.NewCallback((cctx, cargs) =>
                                {
                                    try
                                    {
                                        string? ev = null; DynValue handler = DynValue.Nil;
                                        if (cargs.Count >= 1 && cargs[0].Type == DataType.String) { ev = cargs[0].String; handler = cargs.Count >= 2 ? cargs[1] : DynValue.Nil; }
                                        else if (cargs.Count >= 2 && cargs[1].Type == DataType.String) { ev = cargs[1].String; handler = cargs.Count >= 3 ? cargs[2] : DynValue.Nil; }
                                        if (string.IsNullOrEmpty(ev)) return DynValue.False;
                                        var unregDv = obj.Get("Unregister"); if (unregDv == null || unregDv.IsNil() || unregDv.Type != DataType.Function) return DynValue.False;
                                        _script.Call(unregDv.Function, DynValue.NewString(ev), handler);
                                        return DynValue.True;
                                    }
                                    catch { return DynValue.False; }
                                }));
                            }
                            if (!string.IsNullOrEmpty(unregAllName))
                            {
                                owner.Set(unregAllName, DynValue.NewCallback((cctx, cargs) =>
                                {
                                    try
                                    {
                                        var uall = obj.Get("UnregisterAll"); if (uall == null || uall.IsNil() || uall.Type != DataType.Function) return DynValue.False;
                                        _script.Call(uall.Function);
                                        return DynValue.True;
                                    }
                                    catch { return DynValue.False; }
                                }));
                            }
                        }
                        catch { }
                    }

                    obj.Set("Unregister", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            if (cargs.Count < 2) return DynValue.False;
                            var ev = cargs[0].Type == DataType.String ? cargs[0].String : null;
                            var handler = cargs[1];
                            if (string.IsNullOrEmpty(ev) || handler == null || handler.IsNil()) return DynValue.False;
                            var listDv = regs.Get(ev);
                            if (listDv == null || listDv.IsNil()) return DynValue.False;
                            var list = listDv.Table;
                            var toRemove = new List<int>();
                            int idx = 1;
                            foreach (var p in list.Pairs)
                            {
                                var ent = p.Value;
                                if (ent != null && ent.Type == DataType.Table)
                                {
                                    var hv = ent.Table.Get("handler");
                                    if (hv != null && hv.Type == handler.Type)
                                    {
                                        // simple equality by string or function pointer
                                        if ((hv.Type == DataType.String && handler.Type == DataType.String && hv.String == handler.String) ||
                                            (hv.Type == DataType.Function && handler.Type == DataType.Function && hv.Function == handler.Function))
                                        {
                                            toRemove.Add(idx);
                                        }
                                    }
                                }
                                idx++;
                            }
                            // remove in reverse order
                            for (int i = toRemove.Count - 1; i >= 0; i--) list.Set(toRemove[i], DynValue.Nil);
                            return DynValue.True;
                        }
                        catch { return DynValue.False; }
                    }));

                    obj.Set("UnregisterCallback", obj.Get("Unregister"));

                    obj.Set("UnregisterAll", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            regs = new Table(_script);
                            obj.Set("__registrations", DynValue.NewTable(regs));
                            return DynValue.True;
                        }
                        catch { return DynValue.False; }
                    }));

                    // Fire(event, ...)
                    obj.Set("Fire", DynValue.NewCallback((cctx, cargs) =>
                    {
                        try
                        {
                            if (cargs.Count < 1 || cargs[0].Type != DataType.String) return DynValue.Nil;
                            var ev = cargs[0].String;
                            var listDv = regs.Get(ev);
                            if (listDv == null || listDv.IsNil()) return DynValue.Nil;
                            var list = listDv.Table;

                            // collect args to pass
                            var pass = new List<DynValue>();
                            for (int i = 1; i < cargs.Count; i++) pass.Add(cargs[i]);

                            foreach (var p in list.Pairs)
                            {
                                var ent = p.Value;
                                if (ent == null || ent.IsNil() || ent.Type != DataType.Table) continue;
                                var targetDv = ent.Table.Get("target");
                                var handlerDv = ent.Table.Get("handler");
                                try
                                {
                                    if (handlerDv != null && !handlerDv.IsNil())
                                    {
                                        if (handlerDv.Type == DataType.Function)
                                        {
                                            // call function, pass through args
                                            _script.Call(handlerDv.Function, pass.ToArray());
                                        }
                                        else if (handlerDv.Type == DataType.String && targetDv != null && targetDv.Type == DataType.Table)
                                        {
                                            var methodName = handlerDv.String;
                                            var method = targetDv.Table.Get(methodName);
                                            if (method != null && method.Type == DataType.Function)
                                            {
                                                // prepend target as first arg
                                                var callArgs = new List<DynValue> { targetDv };
                                                callArgs.AddRange(pass);
                                                _script.Call(method.Function, callArgs.ToArray());
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) { try { EmitOutput("[CallbackHandler] handler error: " + ex.Message); } catch { } }
                            }
                        }
                        catch { }
                        return DynValue.Nil;
                    }));

                    return DynValue.NewTable(obj);
                }));
                try { _libRegistry["CallbackHandler-1.0"] = cbh; } catch { }
                try { libsTbl.Set("CallbackHandler-1.0", DynValue.NewTable(cbh)); } catch { }
            }
            catch { }

            // Minimal LibSharedMedia-3.0 shim: provides Register/Fetch/List/IsValid for common media types
            try
            {
                var lsm = new Table(_script);
                // Media containers
                var mediaTable = new Table(_script);
                var mediaList = new Table(_script);
                var defaultMedia = new Table(_script);
                var overrideMedia = new Table(_script);

                lsm.Set("MediaTable", DynValue.NewTable(mediaTable));
                lsm.Set("MediaList", DynValue.NewTable(mediaList));
                lsm.Set("DefaultMedia", DynValue.NewTable(defaultMedia));
                lsm.Set("OverrideMedia", DynValue.NewTable(overrideMedia));

                // MediaType constants
                var mediaTypeTbl = new Table(_script);
                mediaTypeTbl.Set("BACKGROUND", DynValue.NewString("background"));
                mediaTypeTbl.Set("BORDER", DynValue.NewString("border"));
                mediaTypeTbl.Set("FONT", DynValue.NewString("font"));
                mediaTypeTbl.Set("STATUSBAR", DynValue.NewString("statusbar"));
                mediaTypeTbl.Set("SOUND", DynValue.NewString("sound"));
                lsm.Set("MediaType", DynValue.NewTable(mediaTypeTbl));

                // Register(mediatype, key, data, langmask)
                lsm.Set("Register", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        if (args.Count < 3) return DynValue.False;
                        var mediatype = args[0].Type == DataType.String ? args[0].String : null;
                        var key = args[1].Type == DataType.String ? args[1].String : null;
                        var data = args[2];
                        if (string.IsNullOrEmpty(mediatype) || string.IsNullOrEmpty(key)) return DynValue.False;
                        mediatype = mediatype.ToLowerInvariant();
                        var mtable = mediaTable.Get(mediatype);
                        if (mtable == null || mtable.IsNil())
                        {
                            var t = new Table(_script);
                            mediaTable.Set(mediatype, DynValue.NewTable(t));
                            mtable = DynValue.NewTable(t);
                        }
                        // store data as provided (string paths or numeric id)
                        mtable.Table.Set(key, data);
                        // rebuild list: simple sorted keys
                        var list = new Table(_script);
                        int i = 1;
                        foreach (var p in mtable.Table.Pairs)
                        {
                            list.Set(i++, p.Key);
                        }
                        mediaList.Set(mediatype, DynValue.NewTable(list));
                        EmitOutput($"[LibSharedMedia] Registered {mediatype}:{key}");
                        return DynValue.True;
                    }
                    catch { return DynValue.False; }
                }));

                // Fetch(mediatype, key, noDefault)
                lsm.Set("Fetch", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var mediatype = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                        var key = args.Count >= 2 && args[1].Type == DataType.String ? args[1].String : null;
                        if (string.IsNullOrEmpty(mediatype)) return DynValue.Nil;
                        mediatype = mediatype.ToLowerInvariant();
                        var mtableDv = mediaTable.Get(mediatype);
                        if (mtableDv == null || mtableDv.IsNil()) return DynValue.Nil;
                        if (!string.IsNullOrEmpty(key))
                        {
                            var v = mtableDv.Table.Get(key);
                            if (v != null && !v.IsNil()) return v;
                        }
                        var def = defaultMedia.Get(mediatype);
                        if (def != null && !def.IsNil())
                        {
                            var dv = mtableDv.Table.Get(def.String);
                            if (dv != null && !dv.IsNil()) return dv;
                        }
                        return DynValue.Nil;
                    }
                    catch { return DynValue.Nil; }
                }));

                lsm.Set("IsValid", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var mediatype = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                        var key = args.Count >= 2 && args[1].Type == DataType.String ? args[1].String : null;
                        if (string.IsNullOrEmpty(mediatype)) return DynValue.False;
                        mediatype = mediatype.ToLowerInvariant();
                        var mtableDv = mediaTable.Get(mediatype);
                        if (mtableDv == null || mtableDv.IsNil()) return DynValue.False;
                        if (string.IsNullOrEmpty(key)) return DynValue.True;
                        var v = mtableDv.Table.Get(key);
                        return DynValue.NewBoolean(v != null && !v.IsNil());
                    }
                    catch { return DynValue.False; }
                }));

                lsm.Set("List", DynValue.NewCallback((ctx, args) =>
                {
                    var mediatype = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                    if (string.IsNullOrEmpty(mediatype)) return DynValue.Nil;
                    mediatype = mediatype.ToLowerInvariant();
                    var listDv = mediaList.Get(mediatype);
                    if (listDv == null || listDv.IsNil()) return DynValue.Nil;
                    return listDv;
                }));

                lsm.Set("GetGlobal", DynValue.NewCallback((ctx, args) =>
                {
                    var mediatype = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                    if (string.IsNullOrEmpty(mediatype)) return DynValue.Nil;
                    return overrideMedia.Get(mediatype);
                }));

                lsm.Set("SetGlobal", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var mediatype = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                        var key = args.Count >= 2 && args[1].Type == DataType.String ? args[1].String : null;
                        if (string.IsNullOrEmpty(mediatype)) return DynValue.False;
                        mediatype = mediatype.ToLowerInvariant();
                        var mtableDv = mediaTable.Get(mediatype);
                        if (mtableDv == null || mtableDv.IsNil()) return DynValue.False;
                        if (!string.IsNullOrEmpty(key) && mtableDv.Table.Get(key) != null && !mtableDv.Table.Get(key).IsNil())
                        {
                            overrideMedia.Set(mediatype, DynValue.NewString(key));
                            // no callback system; simply log
                            EmitOutput($"[LibSharedMedia] SetGlobal {mediatype} -> {key}");
                            return DynValue.True;
                        }
                        overrideMedia.Set(mediatype, DynValue.Nil);
                        return DynValue.False;
                    }
                    catch { return DynValue.False; }
                }));

                // simple callbacks table to satisfy lib.callbacks:Fire
                var callbacksTbl = new Table(_script);
                callbacksTbl.Set("Fire", DynValue.NewCallback((ctx, args) => { return DynValue.Nil; }));
                lsm.Set("callbacks", DynValue.NewTable(callbacksTbl));

                try { _libRegistry["LibSharedMedia-3.0"] = lsm; } catch { }
                try { libsTbl.Set("LibSharedMedia-3.0", DynValue.NewTable(lsm)); } catch { }
            }
            catch { }

            // Minimal LibDBIcon-1.0 shim to satisfy minimap/broker addons
            try
            {
                var dbicon = new Table(_script);
                var objectsTbl = new Table(_script);
                dbicon.Set("objects", DynValue.NewTable(objectsTbl));
                dbicon.Set("callbacks", DynValue.NewTable(new Table(_script)));
                dbicon.Set("registered", DynValue.NewTable(new Table(_script)));
                dbicon.Set("radius", DynValue.NewNumber(5));

                // helper to get stored button table
                DynValue getButton(string name)
                {
                    var t = objectsTbl.Get(name);
                    if (t == null || t.IsNil()) return DynValue.Nil;
                    return t;
                }

                dbicon.Set("GetMinimapButton", DynValue.NewCallback((ctx, args) =>
                {
                    var name = args.Count >= 2 && args[1].Type == DataType.String ? args[1].String : (args.Count >=1 && args[0].Type==DataType.String?args[0].String:null);
                    if (string.IsNullOrEmpty(name)) return DynValue.Nil;
                    return getButton(name);
                }));

                dbicon.Set("IsRegistered", DynValue.NewCallback((ctx, args) =>
                {
                    var name = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                    if (string.IsNullOrEmpty(name)) return DynValue.False;
                    var b = objectsTbl.Get(name);
                    return DynValue.NewBoolean(b != null && !b.IsNil());
                }));

                dbicon.Set("Register", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var name = args.Count >= 1 && args[0].Type == DataType.String ? args[0].String : null;
                        var objectTable = args.Count >= 2 && args[1].Type == DataType.Table ? args[1].Table : null;
                        var db = args.Count >= 3 && args[2].Type == DataType.Table ? args[2].Table : null;
                        if (string.IsNullOrEmpty(name) || objectTable == null) return DynValue.Nil;
                        // create a button frame using the CreateFrame global
                        try
                        {
                            var cf = _script.Globals.Get("CreateFrame");
                            var res = _script.Call(cf, DynValue.NewString("Button"), DynValue.NewString("LibDBIcon10_" + name));
                            if (res != null && res.Type == DataType.Table)
                            {
                                var btn = res.Table;
                                btn.Set("dataObject", DynValue.NewTable(objectTable));
                                if (db != null) btn.Set("db", DynValue.NewTable(db));
                                btn.Set("icon", DynValue.NewTable(new Table(_script)));
                                objectsTbl.Set(name, DynValue.NewTable(btn));
                                EmitOutput($"[LibDBIcon] Registered icon: {name}");
                            }
                        }
                        catch { }
                        return DynValue.Nil;
                    }
                    catch { return DynValue.Nil; }
                }));

                dbicon.Set("Show", DynValue.NewCallback((ctx, args) => { return DynValue.Nil; }));
                dbicon.Set("Hide", DynValue.NewCallback((ctx, args) => { return DynValue.Nil; }));
                dbicon.Set("Refresh", DynValue.NewCallback((ctx, args) => DynValue.Nil));
                dbicon.Set("GetButtonList", DynValue.NewCallback((ctx, args) =>
                {
                    var t = new Table(_script);
                    int i = 1;
                    foreach (var p in objectsTbl.Pairs) { t.Set(i++, p.Key); }
                    return DynValue.NewTable(t);
                }));

                dbicon.Set("SetButtonRadius", DynValue.NewCallback((ctx, args) => { return DynValue.Nil; }));
                dbicon.Set("SetButtonToPosition", DynValue.NewCallback((ctx, args) => { return DynValue.Nil; }));
                dbicon.Set("AddButtonToCompartment", DynValue.NewCallback((ctx, args) => DynValue.Nil));
                dbicon.Set("RemoveButtonFromCompartment", DynValue.NewCallback((ctx, args) => DynValue.Nil));

                // register into lib registry and LibStub.libs
                try { _libRegistry["LibDBIcon-1.0"] = dbicon; } catch { }
                try { libsTbl.Set("LibDBIcon-1.0", DynValue.NewTable(dbicon)); } catch { }
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
                var files = Directory.GetFiles(dirPath, "*.lua", SearchOption.AllDirectories).ToList();
                // prioritize a small set of critical libs to load first (ChatThrottleLib, CallbackHandler, LibSharedMedia)
                var priority = new[] { "ChatThrottleLib.lua", "CallbackHandler-1.0.lua", "LibSharedMedia-3.0.lua", "HereBeDragons-2.0.lua" };
                files = files.OrderBy(p =>
                {
                    var nm = Path.GetFileName(p);
                    var idx = Array.IndexOf(priority, nm);
                    return idx == -1 ? priority.Length : idx;
                }).ThenBy(p => p).ToList();
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "LibStub.lua",
                    "Ace3.lua",
                    // ChatThrottleLib.lua removed from skip so we can preload the real library
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
                // We'll execute the chunk using DoString with a small wrapper that calls the
                // chunk function with temporary globals to emulate the '...' vararg parameters
                // that addons expect. This avoids MoonSharp ScriptPrivateResource ownership
                // checks that occur when calling closures across resource boundaries.
                // Create temporary global slots for passing args into the wrapper call.
                var tempArg1Name = "__flux_runner_arg1";
                var tempArg2Name = "__flux_runner_arg2";
                var tempArg3Name = "__flux_runner_arg3";
                // (Temporary globals will be populated after the addon namespace table is prepared)

                // ensure addon namespace table exists
                Table ns;
                if (!_addonNamespaces.TryGetValue(addonName, out ns) || ns == null)
                {
                    ns = new Table(_script);
                    _addonNamespaces[addonName] = ns;
                }

                // Populate temporary globals according to whether this is a library file
                try
                {
                    if (isLibraryFile)
                    {
                        _script.Globals.Set(tempArg1Name, DynValue.NewString(firstVarArg ?? addonName));
                        _script.Globals.Set(tempArg2Name, DynValue.NewNumber(libraryMinor));
                        _script.Globals.Set(tempArg3Name, DynValue.Nil);
                    }
                    else
                    {
                        _script.Globals.Set(tempArg1Name, DynValue.NewString(addonName));
                        _script.Globals.Set(tempArg2Name, DynValue.NewTable(ns));
                        _script.Globals.Set(tempArg3Name, DynValue.Nil);
                    }
                }
                catch { }
                // Expose the addon namespace as a global table matching the addon name (many addons expect this)
                try { _script.Globals.Set(addonName, DynValue.NewTable(ns)); } catch { }

                // Heuristic: ensure common saved-variable and DB globals exist for the addon so data files don't index nil
                try
                {
                    var sv = _script.Globals.Get("SavedVariables");
                    if (sv != null && sv.Type == DataType.Table)
                    {
                        var svTbl = sv.Table;
                        var existing = svTbl.Get(addonName);
                        if (existing == null || existing.IsNil())
                        {
                            var newTb = new Table(_script);
                            svTbl.Set(addonName, DynValue.NewTable(newTb));
                            existing = DynValue.NewTable(newTb);
                        }
                        // Attach to namespace as .db for convenience
                        try { ns.Set("db", existing); } catch { }
                    }

                    // Also create common global names pointing to the addon namespace or its DB
                    try { _script.Globals.Set(addonName + "DB", ns.Get("db") ?? DynValue.NewTable(ns)); } catch { }
                    try { _script.Globals.Set(addonName + "_DB", ns.Get("db") ?? DynValue.NewTable(ns)); } catch { }
                    try { _script.Globals.Set(addonName + "Settings", ns.Get("db") ?? DynValue.NewTable(ns)); } catch { }
                }
                catch { }

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

                // Execute the chunk by wrapping it in an anonymous function and invoking it
                // with the temporary globals as arguments. This preserves execution inside
                // the same Script resource and allows us to pass the namespace table.
                try
                {
                    // Preferred path: load the chunk as a function and call its Closure directly.
                    // This keeps execution in the same Script resource and often avoids ownership issues.
                    DynValue func = null;
                    try
                    {
                        func = _script.LoadString(code, null, chunkName);
                    }
                    catch (Exception ex)
                    {
                        try { EmitOutput("[LuaRunner-Diag] LoadString failed: " + ex.Message); } catch { }
                    }

                    bool called = false;
                    if (func != null && func.Type == DataType.Function && func.Function != null)
                    {
                        try
                        {
                            if (isLibraryFile)
                            {
                                func.Function.Call(DynValue.NewString(firstVarArg ?? addonName), DynValue.NewNumber(libraryMinor));
                            }
                            else
                            {
                                func.Function.Call(DynValue.NewString(addonName), DynValue.NewTable(ns));
                            }
                            called = true;
                        }
                        catch (ScriptRuntimeException ex)
                        {
                            try { EmitOutput("[LuaRunner-Diag] Closure.Call ScriptRuntimeException: " + ex.DecoratedMessage); } catch { }
                            // Don't rethrow here; let fallback path (DoString) attempt execution in the script context.
                        }
                        catch (Exception ex)
                        {
                            try { EmitOutput("[LuaRunner-Diag] Closure.Call exception: " + ex.Message); } catch { }
                            // fall through to fallback
                        }
                    }

                    if (!called)
                    {
                        // Fallback: emulate varargs by assigning temporary globals then run the chunk directly
                        string toRun;
                        if (isLibraryFile)
                        {
                            toRun = "local __arg1, __arg2 = __flux_runner_arg1, __flux_runner_arg2\n" + code;
                        }
                        else
                        {
                            toRun = "local __addon, __ns = __flux_runner_arg1, __flux_runner_arg2\n" + code;
                        }
                        _script.DoString(toRun, null, chunkName);
                    }
                }
                finally
                {
                    // cleanup temporary globals
                    try { _script.Globals.Set(tempArg1Name, DynValue.Nil); } catch { }
                    try { _script.Globals.Set(tempArg2Name, DynValue.Nil); } catch { }
                    try { _script.Globals.Set(tempArg3Name, DynValue.Nil); } catch { }
                }

                _executedChunks.Add(canonicalChunkKey);
                try { EmitOutput($"[LuaRunner-Diag] Executed chunk canonical='{canonicalChunkKey}' for addon='{addonName}'"); } catch { }
            }
            catch (ScriptRuntimeException ex)
            {
                try { EmitOutput("[Lua runtime error] " + ex.DecoratedMessage + " | Stack: " + ex.StackTrace); } catch { try { EmitOutput("[Lua runtime error] " + ex.DecoratedMessage); } catch { } }
            }
            catch (Exception ex)
            {
                try { EmitOutput("[Lua error] " + ex.ToString()); } catch { try { EmitOutput("[Lua error] " + ex.Message); } catch { } }
            }
        }

        public void InvokeAceAddonLifecycle(string hookName)
        {
            EmitOutput($"[LuaRunner] Lifecycle hook invoked: {hookName}");
        }

        public void FireEvent(string evName, object?[] args)
        {
            EmitOutput($"[LuaRunner] Event fired: {evName} args={args?.Length ?? 0}");

            // Dispatch to frame-level registered OnEvent scripts
            try
            {
                foreach (var frm in _createdFrames)
                {
                    try
                    {
                        var regsDv = frm.Get("__registeredEvents");
                        if (regsDv == null || regsDv.IsNil() || regsDv.Type != DataType.Table) continue;
                        var has = regsDv.Table.Get(evName);
                        if (has == null || has.IsNil()) continue;

                        // find OnEvent script
                        var scriptsDv = frm.Get("__scripts");
                        if (scriptsDv == null || scriptsDv.IsNil() || scriptsDv.Type != DataType.Table) continue;
                        var onEvent = scriptsDv.Table.Get("OnEvent");
                        if (onEvent == null || onEvent.IsNil() || onEvent.Type != DataType.Function) continue;

                        // Build args: (frame, eventName, ...)
                        var callArgs = new List<DynValue> { DynValue.NewTable(frm) };
                        callArgs.Add(DynValue.NewString(evName));
                        if (args != null)
                        {
                            foreach (var a in args)
                            {
                                if (a == null) callArgs.Add(DynValue.Nil);
                                else if (a is string s) callArgs.Add(DynValue.NewString(s));
                                else if (a is int ii) callArgs.Add(DynValue.NewNumber(ii));
                                else if (a is double dd) callArgs.Add(DynValue.NewNumber(dd));
                                else callArgs.Add(DynValue.NewString(a.ToString() ?? string.Empty));
                            }
                        }

                        try { onEvent.Function.Call(callArgs.ToArray()); } catch (Exception ex) { EmitOutput("[LuaRunner] frame OnEvent error: " + ex.Message); }
                    }
                    catch { }
                }
            }
            catch { }

            // Dispatch to AceEvent-style handlers (lib callbacks)
            try
            {
                if (_eventHandlers.TryGetValue(evName, out var handlers) && handlers != null)
                {
                    foreach (var h in handlers)
                    {
                        try
                        {
                            // call handler with event name as first arg
                            h.Call(DynValue.NewString(evName));
                        }
                        catch (Exception ex) { EmitOutput("[LuaRunner] AceEvent handler error: " + ex.Message); }
                    }
                }
            }
            catch { }
        }

        public void Stop()
        {
            EmitOutput("[LuaRunner] Stop requested (fresh runner)");
        }
    }
}
