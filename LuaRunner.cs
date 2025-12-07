using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using System.Text.Json;
using System.Timers;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia;

namespace Flux
{
    public class LuaRunner
    {
        // preserved closure for preregistered AceAddon.NewAddon so library-created tables
        // that overwrite our registry still get a callable NewAddon injected.
        private DynValue _preregisteredAceAddonNewAddon = null;
        private readonly Dictionary<string, Table> _addonNamespaces = new();
        private Script _script;
        private FrameManager? _frameManager;
        private readonly Dictionary<string, Table> _libRegistry = new();
        private readonly Dictionary<string, Dictionary<string, Closure>> _aceRegisteredHandlers = new();
        private readonly Dictionary<string, Table> _aceAddons = new();
        private readonly Dictionary<string, Dictionary<int, System.Timers.Timer>> _aceTimers = new();
        private int _nextTimerId = 1;
        private Dictionary<string, List<Closure>> _eventHandlers = new();
        private Table _savedVarsTable;
        public string AddonName { get; }

        public event EventHandler<string>? OnSavedVariablesChanged;

        public event EventHandler<string>? OnOutput;

        private void EmitOutput(string s)
        {
            try
            {
                OnOutput?.Invoke(this, s);
            }
            catch { }
            try { Console.WriteLine(s); } catch { }
        }
        private readonly string? _addonFolder;
        // Public accessor for the addon folder the runner was created with
        public string? AddonFolder => _addonFolder;
        public bool IsClassic { get; private set; }
        public int InterfaceVersion { get; private set; }
        public LuaRunner(string addonName, FrameManager? frameManager = null, string? addonFolder = null, int interfaceVersion = 0, bool isClassic = false)
        {
            _frameManager = frameManager;
            AddonName = addonName;
            _addonFolder = addonFolder;
            IsClassic = isClassic;
            InterfaceVersion = interfaceVersion;

            // Create script with common core modules so libraries have standard Lua functions
            // Enable Basic, Table, String, Math and Coroutine modules (safe subset)
            _script = new Script(CoreModules.Basic | CoreModules.Table | CoreModules.String | CoreModules.Math | CoreModules.Coroutine);

            // Ensure essential base functions exist (setmetatable/getmetatable/rawset/rawget/loadstring/tonumber/next)
            try
            {
                _script.Globals["setmetatable"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 2 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        if (a[1].Type == DataType.Table) tbl.MetaTable = a[1].Table;
                        return DynValue.NewTable(tbl);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["getmetatable"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.Table)
                    {
                        var mt = a[0].Table.MetaTable;
                        return mt != null ? DynValue.NewTable(mt) : DynValue.Nil;
                    }
                    return DynValue.Nil;
                });

                _script.Globals["rawset"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 3 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        tbl.Set(a[1], a[2]);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["rawget"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 2 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        return tbl.Get(a[1]);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["next"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        var first = true;
                        DynValue lastKey = DynValue.Nil;
                        if (a.Count >= 2) lastKey = a[1];
                        foreach (var p in tbl.Pairs)
                        {
                            if (lastKey.Type == DataType.Nil)
                            {
                                return DynValue.NewTuple(p.Key, p.Value);
                            }
                            if (p.Key.Equals(lastKey))
                            {
                                // return next after this key
                                // but continue to next iteration to find it
                                lastKey = DynValue.Nil; // mark to return next
                                continue;
                            }
                        }
                    }
                    return DynValue.Nil;
                });

                // Ensure pairs and ipairs exist (MoonSharp should provide these via CoreModules.Basic, but be explicit)
                if (_script.Globals.Get("pairs") == null || _script.Globals.Get("pairs").IsNil())
                {
                    _script.Globals["pairs"] = DynValue.NewCallback((c, a) =>
                    {
                        if (a.Count >= 1 && a[0].Type == DataType.Table)
                        {
                            var tbl = a[0].Table;
                            var nextFn = _script.Globals.Get("next");
                            return DynValue.NewTuple(nextFn, DynValue.NewTable(tbl), DynValue.Nil);
                        }
                        return DynValue.Nil;
                    });
                }

                if (_script.Globals.Get("ipairs") == null || _script.Globals.Get("ipairs").IsNil())
                {
                    _script.Globals["ipairs"] = DynValue.NewCallback((c, a) =>
                    {
                        if (a.Count >= 1 && a[0].Type == DataType.Table)
                        {
                            var tbl = a[0].Table;
                            // Create an iterator function that returns index, value
                            var iterFn = DynValue.NewCallback((c2, a2) =>
                            {
                                if (a2.Count >= 2 && a2[1].Type == DataType.Number)
                                {
                                    var i = (int)a2[1].Number + 1;
                                    var val = tbl.Get(i);
                                    if (val.IsNotNil()) return DynValue.NewTuple(DynValue.NewNumber(i), val);
                                }
                                return DynValue.Nil;
                            });
                            return DynValue.NewTuple(iterFn, DynValue.NewTable(tbl), DynValue.NewNumber(0));
                        }
                        return DynValue.Nil;
                    });
                }

                _script.Globals["tonumber"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1)
                    {
                        var v = a[0];
                        if (v.Type == DataType.Number) return v;
                        if (v.Type == DataType.String && double.TryParse(v.String, out var d)) return DynValue.NewNumber(d);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["loadstring"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        try
                        {
                            var chunk = _script.LoadString(a[0].String);
                            return DynValue.NewCallback((c2, a2) => { return _script.Call(chunk); });
                        }
                        catch { }
                    }
                    return DynValue.Nil;
                });
            }
            catch { }

            
            // Expose common string function aliases used by many addons (strmatch, strfind, strsub, format)
            try
            {
                var strMod = _script.Globals.Get("string");
                if (strMod != null && strMod.Type == DataType.Table)
                {
                    var st = strMod.Table;
                    var maybe = st.Get("match"); if (maybe != null) _script.Globals["strmatch"] = maybe;
                    maybe = st.Get("find"); if (maybe != null) _script.Globals["strfind"] = maybe;
                    maybe = st.Get("sub"); if (maybe != null) _script.Globals["strsub"] = maybe;
                    maybe = st.Get("upper"); if (maybe != null) _script.Globals["strupper"] = maybe;
                    maybe = st.Get("lower"); if (maybe != null) _script.Globals["strlower"] = maybe;
                    maybe = st.Get("format"); if (maybe != null) _script.Globals["format"] = maybe;
                }
            }
            catch { }

            // Provide a basic geterrorhandler so libraries using xpcall/errorhandler don't nil-call
            try
            {
                _script.Globals["geterrorhandler"] = DynValue.NewCallback((c, a) =>
                {
                    // return a function that simply prints the error
                    return DynValue.NewCallback((c2, a2) =>
                    {
                        try { if (a2 != null && a2.Count >= 1) EmitOutput("[Lua error handler] " + a2[0].ToPrintString()); } catch { }
                        return DynValue.Nil;
                    });
                });
            }
            catch { }

            // Provide a global safecall(func, ...) helper used widely by Ace3 libraries
            try
            {
                _script.Globals["safecall"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args == null || args.Count == 0) return DynValue.NewBoolean(true);
                    var func = args[0];
                    if (func == null || (func.Type != DataType.Function && func.Type != DataType.ClrFunction))
                    {
                        // nothing to call
                        return DynValue.NewBoolean(true);
                    }

                    // collect args after the first
                    var callArgs = new List<DynValue>();
                    for (int i = 1; i < args.Count; i++) callArgs.Add(args[i]);

                    try
                    {
                        var res = _script.Call(func, callArgs.ToArray());
                        // return true, and the function's return value(s) if any
                        if (res == null) return DynValue.NewBoolean(true);
                        return DynValue.NewTuple(DynValue.NewBoolean(true), res);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            var handler = _script.Globals.Get("geterrorhandler");
                            if (handler != null && handler.Type == DataType.Function)
                            {
                                _script.Call(handler, DynValue.NewString(ex.Message));
                            }
                        }
                        catch { }
                        return DynValue.NewTuple(DynValue.NewBoolean(false), DynValue.NewString(ex.Message));
                    }
                });
            }
            catch { }

            // Provide IsLoggedIn shim (return true so AceAddon enable queue runs)
            try { _script.Globals["IsLoggedIn"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(true)); } catch { }

            // Provide hooksecurefunc stub (used by AceGUI widgets) — just no-op the hook
            try { _script.Globals["hooksecurefunc"] = DynValue.NewCallback((c, a) => DynValue.Nil); } catch { }

            // Provide ChatEdit_InsertLink stub (used by AceGUI EditBox)
            try { _script.Globals["ChatEdit_InsertLink"] = DynValue.NewCallback((c, a) => DynValue.Nil); } catch { }

                // Provide GetTime and GetFramerate shims used by many libraries (ChatThrottleLib expects these)
            try
            {
                _script.Globals["GetTime"] = DynValue.NewCallback((c, a) =>
                {
                    // return seconds with fractional part
                    var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    return DynValue.NewNumber(ms / 1000.0);
                });

                _script.Globals["GetFramerate"] = DynValue.NewCallback((c, a) =>
                {
                    // Provide a reasonable default framerate so ChatThrottleLib comparisons don't see nil
                    return DynValue.NewNumber(60);
                });

                // Provide SendChatMessage / SendAddonMessage stubs so libraries can call them
                _script.Globals["SendChatMessage"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(true));
                _script.Globals["SendAddonMessage"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(true));

                // Provide a C_ChatInfo table with SendChatMessage/SendAddonMessage wrappers
                try
                {
                    var cchat = new Table(_script);
                    // Use the global functions if present, otherwise no-op
                    var sendChat = _script.Globals.Get("SendChatMessage");
                    var sendAddon = _script.Globals.Get("SendAddonMessage");
                    if (sendChat != null) cchat.Set("SendChatMessage", sendChat);
                    if (sendAddon != null) cchat.Set("SendAddonMessage", sendAddon);
                    // Logged variant falls back to SendAddonMessage
                    if (sendAddon != null) cchat.Set("SendAddonMessageLogged", sendAddon);
                    _script.Globals["C_ChatInfo"] = DynValue.NewTable(cchat);
                }
                catch { }

                // Provide a minimal C_BattleNet shim with SendGameData
                try
                {
                    var cbn = new Table(_script);
                    cbn.Set("SendGameData", DynValue.NewCallback((c2, a2) => DynValue.NewBoolean(true)));
                    _script.Globals["C_BattleNet"] = DynValue.NewTable(cbn);
                }
                catch { }

                // Provide InCombatLockdown shim so UI dialogs don't nil-call
                try { _script.Globals["InCombatLockdown"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false)); } catch { }
            }
            catch { }

            // Robust strmatch: coerce first arg to string if needed, handle common pattern "%d+" via Regex,
            // otherwise fall back to string.match if available.
            try
            {
                _script.Globals["strmatch"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 2)
                    {
                        var subjectDv = args[0];
                        var patDv = args[1];
                        string subject = subjectDv.Type == DataType.String ? subjectDv.String : subjectDv.ToPrintString();
                        string pat = patDv.Type == DataType.String ? patDv.String ?? string.Empty : patDv.ToPrintString();
                        try
                        {
                            if (pat == "%d+")
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(subject ?? string.Empty, "\\d+");
                                if (m.Success) return DynValue.NewString(m.Value);
                                return DynValue.Nil;
                            }
                            // fallback to string.match if present
                            var strTbl = _script.Globals.Get("string");
                            if (strTbl != null && strTbl.Type == DataType.Table)
                            {
                                var matchFn = strTbl.Table.Get("match");
                                if (matchFn != null && matchFn.Type == DataType.Function)
                                {
                                    return _script.Call(matchFn.Function, DynValue.NewString(subject), DynValue.NewString(pat));
                                }
                            }
                        }
                        catch { }
                    }
                    return DynValue.Nil;
                });
            }
            catch { }

            // securecallfunction shim - run functions safely similar to WoW's secure call
            try
            {
                _script.Globals["securecallfunction"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1)
                    {
                        var fn = args[0];
                        try
                        {
                            if (fn.Type == DataType.Function) // Lua function
                            {
                                var callArgs = new List<DynValue>();
                                for (int i = 1; i < args.Count; i++) callArgs.Add(args[i]);
                                _script.Call(fn.Function, callArgs.ToArray());
                            }
                            else if (fn.Type == DataType.ClrFunction)
                            {
                                var callArgs = new List<DynValue>();
                                for (int i = 1; i < args.Count; i++) callArgs.Add(args[i]);
                                _script.Call(fn);
                            }
                        }
                        catch { }
                    }
                    return DynValue.Nil;
                });

                // alias
                _script.Globals["securecall"] = _script.Globals["securecallfunction"];
            }
            catch { }

            // Provide pcall/xpcall shims if missing to support libraries using protected calls
            try
            {
                _script.Globals["pcall"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && (args[0].Type == DataType.Function || args[0].Type == DataType.ClrFunction))
                    {
                        try
                        {
                            var func = args[0].Type == DataType.Function ? (object)args[0].Function : args[0];
                            // Call and return true + results
                            if (args[0].Type == DataType.Function)
                            {
                                var res = _script.Call(args[0].Function);
                                return DynValue.NewTuple(DynValue.NewBoolean(true), res ?? DynValue.Nil);
                            }
                        }
                        catch (ScriptRuntimeException ex)
                        {
                            return DynValue.NewTuple(DynValue.NewBoolean(false), DynValue.NewString(ex.Message));
                        }
                        catch (Exception ex)
                        {
                            return DynValue.NewTuple(DynValue.NewBoolean(false), DynValue.NewString(ex.Message));
                        }
                    }
                    return DynValue.NewTuple(DynValue.NewBoolean(false));
                });

                _script.Globals["xpcall"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 2 && (args[0].Type == DataType.Function || args[0].Type == DataType.ClrFunction))
                    {
                        var func = args[0];
                        var handler = args[1];
                        try
                        {
                            var res = _script.Call(func.Function);
                            return DynValue.NewTuple(DynValue.NewBoolean(true), res ?? DynValue.Nil);
                        }
                        catch (ScriptRuntimeException ex)
                        {
                            try
                            {
                                if (handler != null && handler.Type == DataType.Function) _script.Call(handler.Function, DynValue.NewString(ex.Message));
                            }
                            catch { }
                            return DynValue.NewTuple(DynValue.NewBoolean(false), DynValue.NewString(ex.Message));
                        }
                        catch (Exception ex)
                        {
                            try { if (handler != null && handler.Type == DataType.Function) _script.Call(handler.Function, DynValue.NewString(ex.Message)); } catch { }
                            return DynValue.NewTuple(DynValue.NewBoolean(false), DynValue.NewString(ex.Message));
                        }
                    }
                    return DynValue.NewTuple(DynValue.NewBoolean(false));
                });
            }
            catch { }

            // Convenience globals/shims for compatibility with many WoW libraries
            try { _script.Globals["_G"] = _script.Globals; } catch { }

            try
            {
                _script.Globals["select"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count == 0) return DynValue.Nil;
                    if (args[0].Type == DataType.Number)
                    {
                        int start = (int)args[0].Number;
                        var list = new List<DynValue>();
                        for (int i = start; i < args.Count; i++) list.Add(args[i]);
                        return DynValue.NewTuple(list.ToArray());
                    }
                    if (args[0].Type == DataType.String && args[0].String == "#")
                    {
                        return DynValue.NewNumber(args.Count - 1);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["unpack"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && args[0].Type == DataType.Table)
                    {
                        var tbl = args[0].Table;
                        var list = new List<DynValue>();
                        foreach (var p in tbl.Values) list.Add(p);
                        return DynValue.NewTuple(list.ToArray());
                    }
                    return DynValue.Nil;
                });
            }
            catch { }

            try { _script.Globals["GetLocale"] = DynValue.NewCallback((c, a) => DynValue.NewString("enUS")); } catch { }
            try { _script.Globals["GetAddOnMetadata"] = DynValue.NewCallback((c, a) => DynValue.NewString(string.Empty)); } catch { }
            try { _script.Globals["GetAddOnInfo"] = DynValue.NewCallback((c, a) => DynValue.NewTuple(DynValue.NewString(""), DynValue.NewBoolean(false))); } catch { }
            try { _script.Globals["UnitName"] = DynValue.NewCallback((c, a) => DynValue.NewString("Player")); } catch { }
            // Additional defensive shims used by many Ace libraries and DB code
            try { _script.Globals["Ambiguate"] = DynValue.NewCallback((c, a) => { if (a.Count>=1 && a[0].Type==DataType.String) return DynValue.NewString(a[0].String); return DynValue.NewString(string.Empty); }); } catch { }
            try { 
                var strTbl = _script.Globals.Get("string");
                if (strTbl != null && strTbl.Type == DataType.Table)
                {
                    var matchFn = strTbl.Table.Get("match");
                    if (matchFn != null) _script.Globals["match"] = matchFn;
                }
            } catch { }

            try { _script.Globals["GetRealmName"] = DynValue.NewCallback((c,a) => DynValue.NewString("LocalRealm")); } catch { }
            try { _script.Globals["GetCurrentRegion"] = DynValue.NewCallback((c,a) => DynValue.NewNumber(1)); } catch { }
            try { _script.Globals["GetCurrentRegionName"] = DynValue.NewCallback((c,a) => DynValue.NewString("US")); } catch { }
            try { _script.Globals["UnitClass"] = DynValue.NewCallback((c,a) => DynValue.NewTuple(DynValue.NewString("Warrior"), DynValue.NewString("WARRIOR"))); } catch { }
            try { _script.Globals["UnitRace"] = DynValue.NewCallback((c,a) => DynValue.NewTuple(DynValue.NewString("Human"), DynValue.NewString("Human"))); } catch { }
            try { _script.Globals["UnitFactionGroup"] = DynValue.NewCallback((c,a) => DynValue.NewString("Alliance")); } catch { }
            // table helpers commonly expected by WoW addons
            try
            {
                _script.Globals["tinsert"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 2 && args[0].Type == DataType.Table)
                    {
                        var tbl = args[0].Table;
                        DynValue val = args[1];
                        // find max numeric index
                        int max = 0;
                        foreach (var p in tbl.Pairs)
                        {
                            if (p.Key.Type == DataType.Number)
                            {
                                int k = (int)p.Key.Number;
                                if (k > max) max = k;
                            }
                        }
                        tbl.Set(DynValue.NewNumber(max + 1), val);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["tremove"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && args[0].Type == DataType.Table)
                    {
                        var tbl = args[0].Table;
                        int max = 0;
                        foreach (var p in tbl.Pairs)
                        {
                            if (p.Key.Type == DataType.Number)
                            {
                                int k = (int)p.Key.Number;
                                if (k > max) max = k;
                            }
                        }
                        int idx = max;
                        if (args.Count >= 2 && args[1].Type == DataType.Number) idx = (int)args[1].Number;
                        var key = DynValue.NewNumber(idx);
                        var val = tbl.Get(idx);
                        tbl.Set(key, DynValue.Nil);
                        return val ?? DynValue.Nil;
                    }
                    return DynValue.Nil;
                });

                _script.Globals["wipe"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && args[0].Type == DataType.Table)
                    {
                        var tbl = args[0].Table;
                        var keys = new List<DynValue>();
                        foreach (var p in tbl.Pairs) keys.Add(p.Key);
                        foreach (var k in keys) tbl.Set(k, DynValue.Nil);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["tContains"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 2 && args[0].Type == DataType.Table)
                    {
                        var tbl = args[0].Table;
                        var search = args[1];
                        foreach (var p in tbl.Pairs)
                        {
                            if (p.Value.Equals(search)) return DynValue.NewBoolean(true);
                        }
                    }
                    return DynValue.NewBoolean(false);
                });
            }
            catch { }

            // Improve GetAddOnMetadata to return sensible defaults for Title/Version
            try
            {
                _script.Globals["GetAddOnMetadata"] = DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 2 && args[0].Type == DataType.String && args[1].Type == DataType.String)
                    {
                        var requestedAddon = args[0].String;
                        var key = args[1].String;
                        // If caller requests metadata for our current addon or passes empty name, return values
                        if (string.IsNullOrEmpty(requestedAddon) || string.Equals(requestedAddon, AddonName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(key, "Title", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
                                return DynValue.NewString(AddonName);
                            if (string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "X-MinVersion", StringComparison.OrdinalIgnoreCase))
                                return DynValue.NewString("1.0");
                        }
                    }
                    return DynValue.NewString(string.Empty);
                });
            }
            catch { }

            // Create saved variables table with metatable to detect writes
            InitializeSavedVariablesTable(null);

            // Initialize a minimal LibStub implementation so embedded libs (Ace3) can register
            InitializeLibStub();

            // Pre-create ChatThrottleLib table so libraries that capture it at load
            // time (like AceComm) get a stable table that will be populated when
            // the real ChatThrottleLib chunk runs.
            try
            {
                if (_script.Globals.Get("ChatThrottleLib") == null || _script.Globals.Get("ChatThrottleLib").IsNil())
                {
                    var ctlTbl = new Table(_script);
                    // provide minimal stubs expected by other libs
                    ctlTbl.Set("SendChatMessage", DynValue.NewCallback((c, a) => DynValue.NewBoolean(true)));
                    ctlTbl.Set("SendAddonMessage", DynValue.NewCallback((c, a) => DynValue.NewBoolean(true)));
                    _script.Globals["ChatThrottleLib"] = DynValue.NewTable(ctlTbl);
                }
            }
            catch { }

            // Create a visible Minimap global so libraries like LibDBIcon can hook scripts
            try
            {
                VisualFrame? minimapVf = null;
                if (_frameManager != null) minimapVf = _frameManager.CreateFrame(this);
                if (minimapVf != null)
                {
                    minimapVf.Text = "Minimap";
                    minimapVf.Width = 100; minimapVf.Height = 100;
                    _frameManager?.UpdateVisual(minimapVf);
                }

                var mmTbl = new Table(_script);
                // HookScript/SetScript store closures to run on enter/leave
                mmTbl.Set("HookScript", DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 2 && (a[1].Type == DataType.Function || a[1].Type == DataType.ClrFunction) && a[0].Type == DataType.String)
                    {
                        var evt = a[0].String;
                        if (evt == "OnEnter") { if (minimapVf != null) minimapVf.OnEnter = a[1].Function; }
                        else if (evt == "OnLeave") { if (minimapVf != null) minimapVf.OnLeave = a[1].Function; }
                    }
                    return DynValue.Nil;
                }));

                mmTbl.Set("SetScript", DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 2 && (a[1].Type == DataType.Function || a[1].Type == DataType.ClrFunction) && a[0].Type == DataType.String)
                    {
                        var evt = a[0].String;
                        if (evt == "OnEnter") { if (minimapVf != null) minimapVf.OnEnter = a[1].Function; }
                        else if (evt == "OnLeave") { if (minimapVf != null) minimapVf.OnLeave = a[1].Function; }
                    }
                    return DynValue.Nil;
                }));

                mmTbl.Set("Ping", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                mmTbl.Set("GetMinimapShape", DynValue.NewCallback((c, a) => DynValue.NewString("ROUND")));
                mmTbl.Set("GetZoom", DynValue.NewCallback((c, a) => DynValue.NewNumber(1)));
                mmTbl.Set("SetZoom", DynValue.NewCallback((c, a) => DynValue.Nil));

                _script.Globals["Minimap"] = DynValue.NewTable(mmTbl);
            }
            catch { }

            // Provide a safe 'print' function
            _script.Globals["print"] = DynValue.NewCallback((ctx, args) =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (i > 0) sb.Append("\t");
                        sb.Append(args[i].ToPrintString());
                    }

                    EmitOutput(sb.ToString());
                }
                catch (Exception ex)
                {
                    EmitOutput("[print-error] " + ex.Message);
                }

                return DynValue.Nil;
            });

            // Provide a small WoW API table
            var wowTable = new Table(_script);
            wowTable.Set("RegisterEvent", DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count >= 2)
                {
                    var ev = args[0].ToString();
                    var cb = args[1];
                    if (cb.Type == DataType.Function || cb.Type == DataType.ClrFunction)
                    {
                        if (!_eventHandlers.ContainsKey(ev)) _eventHandlers[ev] = new List<Closure>();
                        _eventHandlers[ev].Add(cb.Function);
                        EmitOutput($"[WoW] Registered event handler for {ev}");
                    }
                }
                return DynValue.Nil;
            }));

            // Provide time and utility functions
            wowTable.Set("GetTime", DynValue.NewCallback((ctx, args) =>
            {
                // Return seconds since Unix epoch as a number
                var seconds = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
                return DynValue.NewNumber(seconds);
            }));

            wowTable.Set("CreateFrame", DynValue.NewCallback((ctx, args) =>
            {
                // Create a visual frame via FrameManager if available
                VisualFrame? vf = null;
                if (_frameManager != null)
                {
                    vf = _frameManager.CreateFrame(this);
                }

                // Return a Lua table with frame methods that operate on the visual frame
                var t = new Table(_script);

                t.Set("SetSize", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 2 && a[0].Type == DataType.Number && a[1].Type == DataType.Number)
                    {
                        vf.Width = a[0].Number;
                        vf.Height = a[1].Number;
                        _frameManager?.UpdateVisual(vf);
                    }
                    return DynValue.Nil;
                }));

                t.Set("SetPoint", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    // If numeric x,y provided: simple set
                    if (a.Count >= 2 && a[0].Type == DataType.Number && a[1].Type == DataType.Number)
                    {
                        vf.X = a[0].Number;
                        vf.Y = a[1].Number;
                        _frameManager?.UpdateVisual(vf);
                        return DynValue.Nil;
                    }

                    // Anchor syntax: SetPoint(anchor, parentTableOrString, relAnchor, x, y)
                    if (a.Count >= 5 && a[0].Type == DataType.String && (a[1].Type == DataType.Table || a[1].Type == DataType.String) && a[2].Type == DataType.String && a[3].Type == DataType.Number && a[4].Type == DataType.Number)
                    {
                        var anchor = a[0].String.ToUpperInvariant();
                        // parent can be table (frame) or string 'UIParent'
                        VisualFrame? parentVf = null;
                        if (a[1].Type == DataType.Table)
                        {
                            var idV = a[1].Table.Get("__id");
                            if (idV != null && idV.Type == DataType.String)
                            {
                                parentVf = _frameManager?.FindById(idV.String);
                            }
                        }

                        var relAnchor = a[2].String.ToUpperInvariant();
                        var offX = a[3].Number;
                        var offY = a[4].Number;

                        double parentX = 0, parentY = 0, parentW = 0, parentH = 0;
                        if (parentVf != null)
                        {
                            parentX = parentVf.X;
                            parentY = parentVf.Y;
                            parentW = parentVf.Width;
                            parentH = parentVf.Height;
                        }
                        else
                        {
                            var size = _frameManager?.GetCanvasSize() ?? new Size(0, 0);
                            parentX = 0; parentY = 0; parentW = size.Width; parentH = size.Height;
                        }

                        (double ax, double ay) = AnchorToOffset(anchor, vf.Width, vf.Height);
                        (double pax, double pay) = AnchorToOffset(relAnchor, parentW, parentH);

                        var targetX = parentX + pax - ax + offX;
                        var targetY = parentY + pay - ay + offY;
                        vf.X = targetX;
                        vf.Y = targetY;
                        _frameManager?.UpdateVisual(vf);
                    }

                    return DynValue.Nil;
                }));

                t.Set("Show", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    vf.Visible = true;
                        _frameManager?.UpdateVisual(vf);
                    return DynValue.Nil;
                }));

                t.Set("IsVisible", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.NewBoolean(false);
                    return DynValue.NewBoolean(vf.Visible);
                }));

                t.Set("Hide", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    vf.Visible = false;
                    _frameManager?.UpdateVisual(vf);
                    return DynValue.Nil;
                }));

                // Set opacity/alpha
                t.Set("SetAlpha", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 1 && (a[0].Type == DataType.Number || a[0].Type == DataType.Boolean))
                    {
                        vf.Opacity = a[0].Type == DataType.Number ? a[0].Number : (a[0].Boolean ? 1.0 : 0.0);
                        _frameManager?.UpdateVisual(vf);
                    }
                    return DynValue.Nil;
                }));

                // Text helpers
                t.Set("SetText", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        vf.Text = a[0].String;
                        _frameManager?.UpdateVisual(vf);
                    }
                    return DynValue.Nil;
                }));

                t.Set("GetText", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.NewString(string.Empty);
                    return DynValue.NewString(vf.Text ?? string.Empty);
                }));

                t.Set("SetBackdrop", DynValue.NewCallback((c, a) =>
                {
                    // Accept a color name string (e.g., "Red", "LightGray"), hex color (#RRGGBB),
                    // or a table: { texture = "path", ninepatch = {left=..., right=..., top=..., bottom=...}, tile = true }
                    if (vf == null) return DynValue.Nil;
                    try
                    {
                        if (a.Count >= 1)
                        {
                            // Support both call styles: f:SetBackdrop(tbl) (self, tbl) and f.SetBackdrop(tbl) (tbl)
                            DynValue payload = a[0];
                            if (a.Count >= 2) payload = a[1];

                            if (payload.Type == DataType.String)
                            {
                                var colorName = payload.String;
                                // Hex color #RRGGBB or #AARRGGBB
                                if (!string.IsNullOrEmpty(colorName) && colorName.StartsWith("#"))
                                {
                                    try
                                    {
                                        var col = Avalonia.Media.Color.Parse(colorName);
                                        vf.BackdropBrush = new SolidColorBrush(col);
                                        vf.UseNinePatch = false;
                                        _frameManager?.UpdateVisual(vf);
                                    }
                                    catch { }
                                }
                                else
                                {
                                    var brushesType = typeof(Avalonia.Media.Brushes);
                                    var prop = brushesType.GetProperty(colorName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
                                    if (prop != null)
                                    {
                                        var brush = prop.GetValue(null) as Avalonia.Media.IBrush;
                                        if (brush != null)
                                        {
                                            vf.BackdropBrush = brush;
                                            vf.UseNinePatch = false;
                                            _frameManager?.UpdateVisual(vf);
                                        }
                                    }
                                }
                            }
                            else if (payload.Type == DataType.Table)
                            {
                                // parse options
                                var table = payload.Table;
                                string? texturePath = null;
                                (int left, int right, int top, int bottom)? insets = null;
                                bool tile = false;

                                var tex = table.Get("texture");
                                if (tex != null && tex.Type == DataType.String) texturePath = tex.String;
                                var np = table.Get("ninepatch");
                                if (np != null && np.Type == DataType.Table)
                                {
                                    var nt = np.Table;
                                    int left = (int)(nt.Get("left")?.Number ?? 0);
                                    int right = (int)(nt.Get("right")?.Number ?? 0);
                                    int top = (int)(nt.Get("top")?.Number ?? 0);
                                    int bottom = (int)(nt.Get("bottom")?.Number ?? 0);
                                    insets = (left, right, top, bottom);
                                }
                                var ti = table.Get("tile");
                                if (ti != null && ti.Type == DataType.Boolean) tile = ti.Boolean;

                                if (!string.IsNullOrEmpty(texturePath))
                                {
                                    try
                                    {
                                        string resolved = texturePath;
                                        if (!System.IO.Path.IsPathRooted(texturePath) && !string.IsNullOrEmpty(_addonFolder))
                                        {
                                            resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(_addonFolder, texturePath));
                                        }
                                        EmitOutput($"[LuaRunner] SetBackdrop requested texture='{texturePath}' resolved='{resolved}'");
                                        if (System.IO.File.Exists(resolved))
                                        {
                                            EmitOutput($"[LuaRunner] Texture file found: {resolved}");
                                            var bmp = new Avalonia.Media.Imaging.Bitmap(resolved);
                                            vf.BackdropBitmap = bmp;
                                            vf.TileBackdrop = tile;
                                            if (insets.HasValue)
                                            {
                                                vf.NinePatchInsets = insets;
                                                vf.UseNinePatch = true;
                                                EmitOutput($"[LuaRunner] Enabled nine-patch with insets={insets.Value}");
                                            }
                                            else
                                            {
                                                vf.UseNinePatch = false;
                                                // set as ImageBrush
                                                vf.BackdropBrush = new ImageBrush(bmp) { Stretch = Avalonia.Media.Stretch.Fill };
                                                EmitOutput($"[LuaRunner] Applied image brush from: {resolved}");
                                            }
                                            _frameManager?.UpdateVisual(vf);
                                        }
                                        else
                                        {
                                            EmitOutput($"[LuaRunner] Texture file not found: {resolved}");
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                    return DynValue.Nil;
                }));

                // Backdrop texture (image path relative to addon folder or absolute)
                t.Set("SetBackdropTexture", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var path = a[0].String;
                        try
                        {
                            string resolved = path;
                            if (!System.IO.Path.IsPathRooted(path) && !string.IsNullOrEmpty(_addonFolder))
                            {
                                resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(_addonFolder, path));
                            }
                            if (System.IO.File.Exists(resolved))
                            {
                                // Create an ImageBrush from the bitmap
                                try
                                {
                                    var bmp = new Bitmap(resolved);
                                    var ib = new ImageBrush(bmp);
                                    vf.BackdropBrush = ib;
                                    _frameManager?.UpdateVisual(vf);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                    return DynValue.Nil;
                }));

                // Font size helper
                t.Set("SetFontSize", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 1 && a[0].Type == DataType.Number)
                    {
                        vf.FontSize = a[0].Number;
                        _frameManager?.UpdateVisual(vf);
                    }
                    return DynValue.Nil;
                }));

                t.Set("SetScript", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    // Support both f:SetScript("OnClick", fn) and f.SetScript(f, "OnClick", fn)
                    int argIndex = 0;
                    DynValue scriptNameDv = null;
                    DynValue funcDv = null;
                    if (a.Count >= 2 && a[0].Type == DataType.String)
                    {
                        scriptNameDv = a[0];
                        funcDv = a[1];
                    }
                    else if (a.Count >= 3 && a[0].Type == DataType.Table && a[1].Type == DataType.String)
                    {
                        scriptNameDv = a[1];
                        funcDv = a[2];
                    }

                    if (scriptNameDv != null && funcDv != null && (funcDv.Type == DataType.Function || funcDv.Type == DataType.ClrFunction))
                    {
                        var scriptName = scriptNameDv.String;
                        if (scriptName == "OnClick") vf.OnClick = funcDv.Function;
                        else if (scriptName == "OnUpdate") vf.OnUpdate = funcDv.Function;
                        else if (scriptName == "OnEnter") vf.OnEnter = funcDv.Function;
                        else if (scriptName == "OnLeave") vf.OnLeave = funcDv.Function;
                        else if (scriptName == "OnEvent") vf.OnEvent = funcDv.Function;
                    }
                    return DynValue.Nil;
                }));

                    // Common UI frame helpers often used by AceConfig and widgets
                    t.Set("EnableMouse", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    t.Set("SetFrameStrata", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    t.Set("SetFrameLevel", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    t.Set("SetFixedFrameStrata", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    t.Set("SetFixedFrameLevel", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    t.Set("SetPropagateKeyboardInput", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));

                    // CreateFontString stub: returns a small object supporting SetSize/SetPoint/SetText/Show/Hide
                    DynValue createFontStringFn = DynValue.NewCallback((c, a) =>
                    {
                        EmitOutput("[LuaRunner-Diag] CreateFontString called");
                        var ft = new Table(_script);
                        ft.Set("SetSize", DynValue.NewCallback((c2, a2) => { return DynValue.Nil; }));
                        ft.Set("SetPoint", DynValue.NewCallback((c2, a2) => { return DynValue.Nil; }));
                        ft.Set("SetText", DynValue.NewCallback((c2, a2) =>
                        {
                            try
                            {
                                if (a2 != null && a2.Count >= 1 && a2[0].Type == DataType.String)
                                {
                                    if (vf != null) vf.Text = a2[0].String;
                                    _frameManager?.UpdateVisual(vf);
                                }
                            }
                            catch { }
                            return DynValue.Nil;
                        }));
                        ft.Set("Show", DynValue.NewCallback((c2, a2) => DynValue.Nil));
                        ft.Set("Hide", DynValue.NewCallback((c2, a2) => DynValue.Nil));
                        return DynValue.NewTable(ft);
                    });
                    t.Set("CreateFontString", createFontStringFn);

                    // Font object setters used by UI widgets
                    t.Set("SetNormalFontObject", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    t.Set("SetHighlightFontObject", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));

                    // Helper to create a lightweight texture object with SetTexCoord/SetTexture
                    Func<DynValue> makeTexture = () =>
                    {
                        var tex = new Table(_script);
                        var setTexCoordFn = DynValue.NewCallback((c2, a2) => DynValue.Nil);
                        tex.Set("SetTexCoord", setTexCoordFn);
                        tex.Set("SetTexture", DynValue.NewCallback((c2, a2) => DynValue.Nil));

                        // Provide a __call metamethod so defensive callers that try to call
                        // the texture object (unexpected but some code paths may) get
                        // the texture table returned and chaining works.
                        try
                        {
                            var mt = new Table(_script);
                            mt.Set("__call", DynValue.NewCallback((c2, a2) => DynValue.NewTable(tex)));
                            tex.MetaTable = mt;
                        }
                        catch { }

                        return DynValue.NewTable(tex);
                    };

                    // Simple texture helpers so widgets can call SetNormalTexture/GetNormalTexture and use :SetTexCoord()
                    t.Set("SetNormalTexture", DynValue.NewCallback((c, a) =>
                    {
                        try
                        {
                            // Accept numeric texture IDs or strings; always return a texture table
                            EmitOutput($"[LuaRunner-Diag] SetNormalTexture called for frame={vf?.Id} args={a.Count}");
                            var texDv = makeTexture();
                            t.Set("__normalTexture", texDv);
                            EmitOutput($"[LuaRunner-Diag] SetNormalTexture stored __normalTexture for frame={vf?.Id}");
                            return texDv;
                        }
                        catch { return DynValue.Nil; }
                    }));

                    t.Set("GetNormalTexture", DynValue.NewCallback((c, a) =>
                    {
                        try {
                            var v = t.Get("__normalTexture");
                            EmitOutput($"[LuaRunner-Diag] GetNormalTexture for frame={vf?.Id} returning: { (v==null?"(null)":v.Type.ToString()) }");
                            // If missing or not a table, create a defensive texture object and store it
                            if (v == null || v.Type == DataType.Nil || v.Type != DataType.Table)
                            {
                                var texDv = makeTexture();
                                t.Set("__normalTexture", texDv);
                                EmitOutput($"[LuaRunner-Diag] GetNormalTexture created fallback texture for frame={vf?.Id}");
                                // ensure SetTexCoord present on the newly created texture
                                try { texDv.Table.Set("SetTexCoord", DynValue.NewCallback((c2, a2) => DynValue.Nil)); } catch { }
                                return texDv;
                            }
                            // Ensure SetTexCoord exists on the returned texture table and is callable
                            try
                            {
                                var inner = v.Table;
                                var s = inner.Get("SetTexCoord");
                                if (s == null || s.Type == DataType.Nil || !(s.Type == DataType.Function || s.Type == DataType.ClrFunction))
                                {
                                    var fn = DynValue.NewCallback((c2, a2) => DynValue.Nil);
                                    inner.Set("SetTexCoord", fn);
                                    // ensure __call metamethod returns the texture table so chaining works
                                    try
                                    {
                                        var mt = inner.MetaTable ?? new Table(_script);
                                        mt.Set("__call", DynValue.NewCallback((c2, a2) => DynValue.NewTable(inner)));
                                        inner.MetaTable = mt;
                                    }
                                    catch { }
                                    EmitOutput($"[LuaRunner-Diag] Injected fallback SetTexCoord into texture for frame={vf?.Id}");
                                }
                                else
                                {
                                    EmitOutput($"[LuaRunner-Diag] Existing SetTexCoord type={s.Type} for frame={vf?.Id}");
                                }
                            }
                            catch (Exception ex) { EmitOutput($"[LuaRunner-Diag] GetNormalTexture inner-check exception: {ex.Message}"); }
                            return v ?? DynValue.Nil;
                        } catch (Exception ex) { EmitOutput($"[LuaRunner-Diag] GetNormalTexture exception: {ex.Message}"); return DynValue.Nil; }
                    }));

                    t.Set("SetPushedTexture", DynValue.NewCallback((c, a) =>
                    {
                        try
                        {
                            EmitOutput($"[LuaRunner-Diag] SetPushedTexture called for frame={vf?.Id} args={a.Count}");
                            var texDv = makeTexture();
                            t.Set("__pushedTexture", texDv);
                            EmitOutput($"[LuaRunner-Diag] SetPushedTexture stored __pushedTexture for frame={vf?.Id}");
                            return texDv;
                        }
                        catch { return DynValue.Nil; }
                    }));

                    t.Set("GetPushedTexture", DynValue.NewCallback((c, a) =>
                    {
                        try {
                            var v = t.Get("__pushedTexture");
                            EmitOutput($"[LuaRunner-Diag] GetPushedTexture for frame={vf?.Id} returning: { (v==null?"(null)":v.Type.ToString()) }");
                            if (v == null || v.Type == DataType.Nil || v.Type != DataType.Table)
                            {
                                var texDv = makeTexture();
                                t.Set("__pushedtexture", texDv); // small tolerance if callers use different casing
                                t.Set("__pushedTexture", texDv);
                                EmitOutput($"[LuaRunner-Diag] GetPushedTexture created fallback texture for frame={vf?.Id}");
                                try { texDv.Table.Set("SetTexCoord", DynValue.NewCallback((c2, a2) => DynValue.Nil)); } catch { }
                                return texDv;
                            }
                            try
                            {
                                var inner = v.Table;
                                var s = inner.Get("SetTexCoord");
                                if (s == null || s.Type == DataType.Nil || !(s.Type == DataType.Function || s.Type == DataType.ClrFunction))
                                {
                                    var fn = DynValue.NewCallback((c2, a2) => DynValue.Nil);
                                    inner.Set("SetTexCoord", fn);
                                    try
                                    {
                                        var mt = inner.MetaTable ?? new Table(_script);
                                        mt.Set("__call", DynValue.NewCallback((c2, a2) => DynValue.NewTable(inner)));
                                        inner.MetaTable = mt;
                                    }
                                    catch { }
                                    EmitOutput($"[LuaRunner-Diag] Injected fallback SetTexCoord into pushed texture for frame={vf?.Id}");
                                }
                                else
                                {
                                    EmitOutput($"[LuaRunner-Diag] Existing pushed SetTexCoord type={s.Type} for frame={vf?.Id}");
                                }
                            }
                            catch (Exception ex) { EmitOutput($"[LuaRunner-Diag] GetPushedTexture inner-check exception: {ex.Message}"); }
                            return v ?? DynValue.Nil;
                        } catch (Exception ex) { EmitOutput($"[LuaRunner-Diag] GetPushedTexture exception: {ex.Message}"); return DynValue.Nil; }
                    }));

                    t.Set("SetHighlightTexture", DynValue.NewCallback((c, a) =>
                    {
                        try
                        {
                            var texDv = makeTexture();
                            // normalize key name to __highlightTexture to match GetHighlightTexture
                            t.Set("__highlightTexture", texDv);
                            return texDv;
                        }
                        catch { return DynValue.Nil; }
                    }));

                    t.Set("GetHighlightTexture", DynValue.NewCallback((c, a) =>
                    {
                        try {
                            var v = t.Get("__highlightTexture");
                            EmitOutput($"[LuaRunner-Diag] GetHighlightTexture returning: { (v==null?"(null)":v.Type.ToString()) }");
                            if (v == null || v.Type == DataType.Nil || v.Type != DataType.Table)
                            {
                                var texDv = makeTexture();
                                t.Set("__highlighttexture", texDv);
                                t.Set("__highlightTexture", texDv);
                                EmitOutput($"[LuaRunner-Diag] GetHighlightTexture created fallback texture for frame={vf?.Id}");
                                try { texDv.Table.Set("SetTexCoord", DynValue.NewCallback((c2, a2) => DynValue.Nil)); } catch { }
                                return texDv;
                            }
                            try
                            {
                                var inner = v.Table;
                                var s = inner.Get("SetTexCoord");
                                if (s == null || s.Type == DataType.Nil || !(s.Type == DataType.Function || s.Type == DataType.ClrFunction))
                                {
                                    var fn = DynValue.NewCallback((c2, a2) => DynValue.Nil);
                                    inner.Set("SetTexCoord", fn);
                                    try
                                    {
                                        var mt = inner.MetaTable ?? new Table(_script);
                                        mt.Set("__call", DynValue.NewCallback((c2, a2) => DynValue.NewTable(inner)));
                                        inner.MetaTable = mt;
                                    }
                                    catch { }
                                    EmitOutput($"[LuaRunner-Diag] Injected fallback SetTexCoord into highlight texture for frame={vf?.Id}");
                                }
                                else
                                {
                                    EmitOutput($"[LuaRunner-Diag] Existing highlight SetTexCoord type={s.Type} for frame={vf?.Id}");
                                }
                            }
                            catch (Exception ex) { EmitOutput($"[LuaRunner-Diag] GetHighlightTexture inner-check exception: {ex.Message}"); }
                            return v ?? DynValue.Nil;
                        } catch (Exception ex) { EmitOutput($"[LuaRunner-Diag] GetHighlightTexture exception: {ex.Message}"); return DynValue.Nil; }
                    }));

                    // Common helper: mirror SetAllPoints to size/position the visual frame to its parent
                    t.Set("SetAllPoints", DynValue.NewCallback((c, a) =>
                    {
                        if (vf == null) return DynValue.Nil;
                        try
                        {
                            VisualFrame? parentVf = null;
                            if (a.Count >= 1 && a[0].Type == DataType.Table)
                            {
                                var idV = a[0].Table.Get("__id");
                                if (idV != null && idV.Type == DataType.String) parentVf = _frameManager?.FindById(idV.String);
                            }
                            if (parentVf != null)
                            {
                                vf.X = parentVf.X;
                                vf.Y = parentVf.Y;
                                vf.Width = parentVf.Width;
                                vf.Height = parentVf.Height;
                                _frameManager?.UpdateVisual(vf);
                            }
                        }
                        catch { }
                        return DynValue.Nil;
                    }));

                    // No-op ClearAllPoints to satisfy AceGUI release usage
                    t.Set("ClearAllPoints", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));

                    // No-op SetParent (some libraries call :SetParent)
                    t.Set("SetParent", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));

                    // Provide a simple userdata table getters used by AceGUI
                    try
                    {
                        var ud = new Table(_script);
                        t.Set("__userdata", DynValue.NewTable(ud));
                        t.Set("GetUserDataTable", DynValue.NewCallback((c, a) => { var v = t.Get("__userdata"); return v ?? DynValue.NewTable(new Table(_script)); }));
                        t.Set("GetUserData", DynValue.NewCallback((c, a) => { if (a.Count>=1 && a[0].Type==DataType.String) { var udv = t.Get("__userdata"); if (udv!=null && udv.Type==DataType.Table) { var val = udv.Table.Get(a[0]); return val ?? DynValue.Nil; } } return DynValue.Nil; }));
                        t.Set("ReleaseChildren", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                    }
                    catch { }

                    // Provide a prototype/metatable fallback so frames created via different paths
                    // still get the common UI helpers if a lookup misses on the instance table.
                    try
                    {
                        var proto = new Table(_script);
                        proto.Set("CreateFontString", createFontStringFn);
                        proto.Set("SetNormalTexture", t.Get("SetNormalTexture") ?? DynValue.Nil);
                        proto.Set("GetNormalTexture", t.Get("GetNormalTexture") ?? DynValue.Nil);
                        proto.Set("SetPushedTexture", t.Get("SetPushedTexture") ?? DynValue.Nil);
                        proto.Set("GetPushedTexture", t.Get("GetPushedTexture") ?? DynValue.Nil);
                        proto.Set("SetHighlightTexture", t.Get("SetHighlightTexture") ?? DynValue.Nil);
                        proto.Set("GetHighlightTexture", t.Get("GetHighlightTexture") ?? DynValue.Nil);
                        proto.Set("UnregisterAllEvents", t.Get("UnregisterAllEvents") ?? DynValue.Nil);
                        proto.Set("SetPropagateKeyboardInput", t.Get("SetPropagateKeyboardInput") ?? DynValue.Nil);

                        var meta = new Table(_script);
                        meta.Set("__index", DynValue.NewTable(proto));
                        t.MetaTable = meta;
                    }
                    catch { }

                    // Provide UnregisterAllEvents no-op to satisfy libraries that call it
                    t.Set("UnregisterAllEvents", DynValue.NewCallback((c, a) =>
                    {
                        try
                        {
                            if (vf != null && vf.RegisteredEventWrappers != null)
                            {
                                foreach (var kv in vf.RegisteredEventWrappers)
                                {
                                    var ev = kv.Key;
                                    var closure = kv.Value;
                                    if (_eventHandlers.TryGetValue(ev, out var list)) list.RemoveAll(cw => cw == closure);
                                }
                                vf.RegisteredEventWrappers.Clear();
                            }
                        }
                        catch { }
                        return DynValue.Nil;
                    }));

                // Allow frames to register/unregister for events
                t.Set("RegisterEvent", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    // Support both f:RegisterEvent("EVENT") and f.RegisterEvent(f, "EVENT")
                    string? ev = null;
                    if (a.Count >= 1 && a[0].Type == DataType.String) ev = a[0].String;
                    else if (a.Count >= 2 && a[0].Type == DataType.Table && a[1].Type == DataType.String) ev = a[1].String;

                    if (ev != null)
                    {
                        if (vf.OnEvent != null)
                        {
                            var wrapper = DynValue.NewCallback((ctx2, cbArgs) =>
                            {
                                try
                                {
                                    var argsList = new List<DynValue> { DynValue.NewTable(vf.ToTable(_script)) };
                                    if (cbArgs != null)
                                    {
                                        for (int i = 0; i < cbArgs.Count; i++) argsList.Add(cbArgs[i]);
                                    }
                                    _script.Call(vf.OnEvent, argsList.ToArray());
                                }
                                catch { }
                                return DynValue.Nil;
                            });

                            if (!_eventHandlers.ContainsKey(ev)) _eventHandlers[ev] = new List<Closure>();
                            _eventHandlers[ev].Add(wrapper.Function);
                            vf.RegisteredEventWrappers ??= new Dictionary<string, Closure>();
                            vf.RegisteredEventWrappers[ev] = wrapper.Function;
                        }
                        else
                        {
                            if (!_eventHandlers.ContainsKey(ev)) _eventHandlers[ev] = new List<Closure>();
                        }
                    }
                    return DynValue.Nil;
                }));

                t.Set("UnregisterEvent", DynValue.NewCallback((c, a) =>
                {
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var ev = a[0].String;
                        if (vf.RegisteredEventWrappers != null && vf.RegisteredEventWrappers.TryGetValue(ev, out var closure))
                        {
                            if (_eventHandlers.TryGetValue(ev, out var list)) list.RemoveAll(cw => cw == closure);
                            vf.RegisteredEventWrappers.Remove(ev);
                        }
                    }
                    return DynValue.Nil;
                }));

                // Provide reference to the C# frame id (for debugging)
                t.Set("__id", DynValue.NewString(vf?.Id ?? string.Empty));

                // If the caller provided a name (CreateFrame(type, name, parent, template)),
                // use it for a sensible default visual and WoW-like placement so addons
                // that create canonical frames appear in expected locations.
                try
                {
                    string? frameName = null;
                    if (args != null && args.Count >= 2 && args[1].Type == DataType.String)
                    {
                        frameName = args[1].String;
                    }

                    if (!string.IsNullOrEmpty(frameName))
                    {
                        // expose name to Lua table and show as label in the visual
                        t.Set("__name", DynValue.NewString(frameName));
                        vf.Text = frameName;

                        // Ensure a reasonable default size for common frames
                        if (vf.Width <= 0) vf.Width = 120;
                        if (vf.Height <= 0) vf.Height = 40;

                        var canvasSize = _frameManager?.GetCanvasSize() ?? new Size(800, 600);

                        // Basic WoW-like placements (top-left origin):
                        // PlayerFrame: bottom-left, Minimap: top-right, PartyMemberFrameN: stacked left area, ChatFrame: bottom-left large
                        if (frameName.Equals("PlayerFrame", StringComparison.OrdinalIgnoreCase))
                        {
                            vf.X = 20;
                            vf.Y = canvasSize.Height - vf.Height - 20;
                        }
                        else if (frameName.IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // round minimap visually handled in FrameManager by checking vf.Text == "Minimap"
                            vf.Width = Math.Max(vf.Width, 100);
                            vf.Height = Math.Max(vf.Height, 100);
                            vf.X = canvasSize.Width - vf.Width - 20;
                            vf.Y = 20;
                        }
                        else if (frameName.StartsWith("PartyMemberFrame", StringComparison.OrdinalIgnoreCase))
                        {
                            // Party members stack vertically to the right of player frame area
                            var m = System.Text.RegularExpressions.Regex.Match(frameName, "\\d+");
                            int idx = 1;
                            if (m.Success) int.TryParse(m.Value, out idx);
                            double baseX = 20 + 140; // to the right of a typical player frame
                            double baseY = canvasSize.Height - vf.Height - 20 - (idx - 1) * (vf.Height + 8);
                            vf.X = baseX;
                            vf.Y = baseY;
                        }
                        else if (frameName.StartsWith("ChatFrame", StringComparison.OrdinalIgnoreCase))
                        {
                            vf.Width = Math.Max(vf.Width, 340);
                            vf.Height = Math.Max(vf.Height, 160);
                            vf.X = 20;
                            vf.Y = canvasSize.Height - vf.Height - 20 - 60; // leave room for player frame
                        }

                        _frameManager?.UpdateVisual(vf);
                    }
                }
                catch { }

                // Diagnostic: list methods on the returned table for easier debugging
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[LuaRunner-Diag] CreateFrame returning table methods: ");
                    foreach (var p in t.Pairs)
                    {
                        sb.AppendFormat("{0}({1}) ", p.Key.Type == DataType.String ? p.Key.String : p.Key.ToPrintString(), p.Value.Type);
                    }
                    EmitOutput(sb.ToString());
                }
                catch { }

                return DynValue.NewTable(t);
            }));

            _script.Globals["WoW"] = DynValue.NewTable(wowTable);
            // Expose common WoW globals for compatibility: CreateFrame, GetTime, RegisterEvent and UIParent
            try
            {
                var create = wowTable.Get("CreateFrame");
                if (create != null) _script.Globals["CreateFrame"] = create;
            }
            catch { }
            try
            {
                var gt = wowTable.Get("GetTime");
                if (gt != null) _script.Globals["GetTime"] = gt;
            }
            catch { }
            try
            {
                var reg = wowTable.Get("RegisterEvent");
                if (reg != null) _script.Globals["RegisterEvent"] = reg;
            }
            catch { }

            try
            {
                // Provide a simple UIParent table so code referencing it doesn't nil-index
                var uiParent = new Table(_script);
                uiParent.Set("__name", DynValue.NewString("UIParent"));
                _script.Globals["UIParent"] = DynValue.NewTable(uiParent);
            }
            catch { }

            // Expose basic build info and project constants to emulate Classic/Retail differences
            try
            {
                var build = IsClassic ? "Classic" : "Retail";
                var buildString = IsClassic ? "1.0" : "1.0";
                _script.Globals["GetBuildInfo"] = DynValue.NewCallback((c, a) => DynValue.NewTuple(DynValue.NewString(buildString), DynValue.NewString("build"), DynValue.NewNumber(InterfaceVersion)));
            }
            catch { }

            try
            {
                // WOW_PROJECT constants
                _script.Globals["WOW_PROJECT_MAINLINE"] = DynValue.NewNumber(1);
                _script.Globals["WOW_PROJECT_CLASSIC"] = DynValue.NewNumber(2);
                _script.Globals["WOW_PROJECT_ID"] = DynValue.NewNumber(IsClassic ? 2 : 1);
            }
            catch { }

            // C_Timer minimal shim: After(callback, seconds)
            try
            {
                var ctimer = new Table(_script);
                ctimer.Set("After", DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 2 && (args[0].Type == DataType.Function || args[0].Type == DataType.ClrFunction) && args[1].Type == DataType.Number)
                    {
                        var func = args[0].Function;
                        var delay = args[1].Number;
                        var timer = new System.Timers.Timer(delay * 1000.0) { AutoReset = false };
                        timer.Elapsed += (s, e) =>
                        {
                            try
                            {
                                timer.Stop();
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { _script.Call(func); } catch { }
                                });
                            }
                            catch { }
                            finally { try { timer.Dispose(); } catch { } }
                        };
                        timer.Start();
                    }
                    return DynValue.Nil;
                }));
                _script.Globals["C_Timer"] = DynValue.NewTable(ctimer);
            }
            catch { }

            // Provide table.* aliases (table.insert/remove/concat)
            try
            {
                var tmod = new Table(_script);
                tmod.Set("insert", DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 2 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        DynValue val = a[1];
                        int max = 0;
                        foreach (var p in tbl.Pairs) if (p.Key.Type == DataType.Number) max = Math.Max(max, (int)p.Key.Number);
                        tbl.Set(DynValue.NewNumber(max + 1), val);
                    }
                    return DynValue.Nil;
                }));
                tmod.Set("remove", DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        int idx = 0;
                        if (a.Count >= 2 && a[1].Type == DataType.Number) idx = (int)a[1].Number;
                        if (idx == 0)
                        {
                            int max = 0; foreach (var p in tbl.Pairs) if (p.Key.Type == DataType.Number) max = Math.Max(max, (int)p.Key.Number);
                            idx = max;
                        }
                        var val = tbl.Get(idx);
                        tbl.Set(DynValue.NewNumber(idx), DynValue.Nil);
                        return val ?? DynValue.Nil;
                    }
                    return DynValue.Nil;
                }));
                tmod.Set("concat", DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.Table)
                    {
                        var tbl = a[0].Table;
                        var sb = new System.Text.StringBuilder();
                        foreach (var p in tbl.Values) sb.Append(p.ToPrintString());
                        return DynValue.NewString(sb.ToString());
                    }
                    return DynValue.NewString(string.Empty);
                }));
                _script.Globals["table"] = DynValue.NewTable(tmod);
            }
            catch { }

            // Basic global stubs to emulate Vanilla/Classic environment
            try
            {
                // DEFAULT_CHAT_FRAME:AddMessage
                var chatFrame = new Table(_script);
                chatFrame.Set("AddMessage", DynValue.NewCallback((c, a) =>
                {
                    try
                    {
                        if (a.Count >= 1) EmitOutput("[CHAT] " + a[0].ToPrintString());
                    }
                    catch { }
                    return DynValue.Nil;
                }));
                _script.Globals["DEFAULT_CHAT_FRAME"] = DynValue.NewTable(chatFrame);

                // Simple addon loaded registry
                var loadedSet = new Table(_script);
                _script.Globals["_LoadedAddOns"] = DynValue.NewTable(loadedSet);

                _script.Globals["IsAddOnLoaded"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var name = a[0].String;
                        // consider the current addon as loaded
                        if (string.Equals(name, AddonName, StringComparison.OrdinalIgnoreCase)) return DynValue.NewBoolean(true);
                        var v = loadedSet.Get(name);
                        return DynValue.NewBoolean(v.Type != DataType.Nil);
                    }
                    return DynValue.NewBoolean(false);
                });

                _script.Globals["LoadAddOn"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var name = a[0].String;
                        loadedSet.Set(name, DynValue.NewBoolean(true));
                        EmitOutput($"[AddOn] LoadAddOn requested: {name}");
                        return DynValue.NewBoolean(true);
                    }
                    return DynValue.NewBoolean(false);
                });

                // PlaySound / PlaySoundFile no-op (log)
                _script.Globals["PlaySound"] = DynValue.NewCallback((c, a) => { try { if (a.Count>=1) EmitOutput("[Sound] " + a[0].ToPrintString()); } catch { } return DynValue.Nil; });
                _script.Globals["PlaySoundFile"] = DynValue.NewCallback((c, a) => { try { if (a.Count>=1) EmitOutput("[SoundFile] " + a[0].ToPrintString()); } catch { } return DynValue.Nil; });

                // Simple CVar storage
                var cvarTable = new Table(_script);
                _script.Globals["_CVars"] = DynValue.NewTable(cvarTable);
                _script.Globals["GetCVar"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var key = a[0].String;
                        var v = cvarTable.Get(key);
                        if (v.Type == DataType.String) return v;
                        if (v.Type == DataType.Number) return v;
                        return DynValue.NewString(string.Empty);
                    }
                    return DynValue.NewString(string.Empty);
                });
                _script.Globals["SetCVar"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 2 && a[0].Type == DataType.String)
                    {
                        var key = a[0].String;
                        var val = a[1];
                        cvarTable.Set(key, val);
                    }
                    return DynValue.Nil;
                });
            }
            catch { }

        }

        private void InitializeLibStub()
        {
            try
            {
                var libStub = new Table(_script);
                // Provide libs and minors tables as in real LibStub
                var libsTbl = new Table(_script);
                var minorsTbl = new Table(_script);
                libStub.Set("libs", DynValue.NewTable(libsTbl));
                libStub.Set("minors", DynValue.NewTable(minorsTbl));
                // Present a compatible minor version so embedded LibStub files do not override our implementation
                libStub.Set("minor", DynValue.NewNumber(2));

                // __call metamethod: LibStub("Name") -> returns library table or nil
                var mt = new Table(_script);
                mt.Set("__call", DynValue.NewCallback((ctx, args) =>
                {
                    // Diagnostic: log LibStub __call invocations
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("[LuaRunner-Diag] LibStub.__call args=");
                        for (int i = 0; i < args.Count; i++) sb.AppendFormat("[{0}:{1}]", i, args[i].Type);
                        EmitOutput(sb.ToString());
                    }
                    catch { }

                    // Support both LibStub("Name") and LibStub:__call("Name") where self may appear as first arg
                    string? name = null;
                    if (args.Count >= 1 && args[0].Type == DataType.String) name = args[0].String;
                    else if (args.Count >= 2 && args[1].Type == DataType.String) name = args[1].String;

                    if (!string.IsNullOrEmpty(name))
                    {
                        var dv = libsTbl.Get(name);
                        if (dv != null && dv.Type == DataType.Table)
                        {
                            try
                            {
                                var maybeNew = dv.Table.Get("NewAddon");
                                EmitOutput($"[LuaRunner-Diag] LibStub.__call returning existing libsTbl['{name}'] NewAddonType={(maybeNew==null?"(null)":maybeNew.Type.ToString())}");
                                // Ensure AceAddon exposes NewAddon even if overwritten by library code
                                if ((maybeNew == null || maybeNew.Type == DataType.Nil) && string.Equals(name, "AceAddon-3.0", StringComparison.OrdinalIgnoreCase) && _preregisteredAceAddonNewAddon != null && _preregisteredAceAddonNewAddon.Type != DataType.Nil)
                                {
                                    dv.Table.Set("NewAddon", _preregisteredAceAddonNewAddon);
                                    EmitOutput($"[LuaRunner-Diag] Injected preserved NewAddon into libsTbl['{name}']");
                                }
                                // Wrap the NewAddon entry with a stable CLR callback so method-call syntax always works.
                                try
                                {
                                    var finalNew = dv.Table.Get("NewAddon");
                                    if (finalNew != null && finalNew.Type != DataType.Nil)
                                    {
                                        var captured = finalNew; // capture the original implementation
                                        dv.Table.Set("NewAddon", DynValue.NewCallback((ctx2, args2) =>
                                        {
                                            try
                                            {
                                                var callArgs = new List<DynValue>();
                                                for (int i = 0; i < args2.Count; i++) callArgs.Add(args2[i]);
                                                if (captured.Type == DataType.Function)
                                                {
                                                    return _script.Call(captured.Function, callArgs.ToArray());
                                                }
                                                // For ClrFunction and other callables, just call via script.Call
                                                return _script.Call(captured, callArgs.ToArray());
                                            }
                                            catch (Exception ex)
                                            {
                                                try { EmitOutput("[LuaRunner-Diag] NewAddon wrapper exception: " + ex.Message); } catch { }
                                            }
                                            return DynValue.Nil;
                                        }));
                                        EmitOutput($"[LuaRunner-Diag] Wrapped NewAddon on libsTbl['{name}'] to ensure callable method semantics");
                                    }
                                }
                                catch { }
                            }
                            catch { }
                            return DynValue.NewTable(dv.Table);
                        }
                        // fallback: if not in libsTbl, check our internal registry populated earlier
                        if (_libRegistry.TryGetValue(name, out var regTbl) && regTbl != null)
                        {
                            try
                            {
                                var maybeNew = regTbl.Get("NewAddon");
                                EmitOutput($"[LuaRunner-Diag] LibStub.__call returning _libRegistry['{name}'] NewAddonType={(maybeNew==null?"(null)":maybeNew.Type.ToString())}");
                                if ((maybeNew == null || maybeNew.Type == DataType.Nil) && string.Equals(name, "AceAddon-3.0", StringComparison.OrdinalIgnoreCase) && _preregisteredAceAddonNewAddon != null && _preregisteredAceAddonNewAddon.Type != DataType.Nil)
                                {
                                    regTbl.Set("NewAddon", _preregisteredAceAddonNewAddon);
                                    EmitOutput($"[LuaRunner-Diag] Injected preserved NewAddon into _libRegistry['{name}']");
                                }
                                // Wrap the NewAddon entry in the registry table as well so callers using :NewAddon will succeed.
                                try
                                {
                                    var finalNew = regTbl.Get("NewAddon");
                                    if (finalNew != null && finalNew.Type != DataType.Nil)
                                    {
                                        var captured = finalNew;
                                        regTbl.Set("NewAddon", DynValue.NewCallback((ctx2, args2) =>
                                        {
                                            try
                                            {
                                                var callArgs = new List<DynValue>();
                                                for (int i = 0; i < args2.Count; i++) callArgs.Add(args2[i]);
                                                if (captured.Type == DataType.Function)
                                                {
                                                    return _script.Call(captured.Function, callArgs.ToArray());
                                                }
                                                return _script.Call(captured, callArgs.ToArray());
                                            }
                                            catch (Exception ex)
                                            {
                                                try { EmitOutput("[LuaRunner-Diag] NewAddon(wrapper) exception: " + ex.Message); } catch { }
                                            }
                                            return DynValue.Nil;
                                        }));
                                        EmitOutput($"[LuaRunner-Diag] Wrapped NewAddon on _libRegistry['{name}'] to ensure callable method semantics");
                                    }
                                }
                                catch { }
                            }
                            catch { }
                            return DynValue.NewTable(regTbl);
                        }
                    }
                    return DynValue.Nil;
                }));

                // LibStub:NewLibrary(name, minor)
                libStub.Set("NewLibrary", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("[LuaRunner-Diag] LibStub.NewLibrary called args=");
                        for (int i = 0; i < args.Count; i++) sb.AppendFormat("[{0}:{1}]", i, args[i].Type);
                        EmitOutput(sb.ToString());
                    }
                    catch { }

                    // support both NewLibrary(name, minor) and :NewLibrary(name, minor) where self is first arg
                    string? name = null;
                    int minor = 0;
                    if (args.Count >= 1 && args[0].Type == DataType.String) name = args[0].String;
                    else if (args.Count >= 2 && args[1].Type == DataType.String) name = args[1].String;

                    if (args.Count >= 2)
                    {
                        var maybe = args[1].Type == DataType.Number ? args[1] : (args.Count >= 3 ? args[2] : DynValue.Nil);
                        if (maybe != null && maybe.Type == DataType.Number) minor = (int)maybe.Number;
                        else if (maybe != null && maybe.Type == DataType.String)
                        {
                            var mstr = maybe.String ?? string.Empty;
                            var mm = System.Text.RegularExpressions.Regex.Match(mstr, "\\d+");
                            if (mm.Success) minor = int.Parse(mm.Value);
                        }
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        var existingDv = libsTbl.Get(name);
                        var existingMinorDv = minorsTbl.Get(name);
                        int existingMinor = 0;
                        if (existingMinorDv != null && existingMinorDv.Type == DataType.Number) existingMinor = (int)existingMinorDv.Number;
                        if (existingMinor >= minor) return DynValue.Nil;

                        var t = existingDv != null && existingDv.Type == DataType.Table ? existingDv.Table : new Table(_script);
                        t.Set("__name", DynValue.NewString(name));
                        t.Set("__minor", DynValue.NewNumber(minor));
                        libsTbl.Set(name, DynValue.NewTable(t));
                        minorsTbl.Set(name, DynValue.NewNumber(minor));
                        // Mirror into C# registry so LibStub calls from other contexts can find it
                        try
                        {
                            _libRegistry[name] = t;
                        }
                        catch { }
                        try { EmitOutput($"[LuaRunner-Diag] LibStub.NewLibrary created lib '{name}' minor={minor}"); } catch { }
                        return DynValue.NewTable(t);
                    }
                    return DynValue.Nil;
                }));

                // LibStub:GetLibrary(name, silent)
                libStub.Set("GetLibrary", DynValue.NewCallback((ctx, args) =>
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("[LuaRunner-Diag] LibStub.GetLibrary called args=");
                        for (int i = 0; i < args.Count; i++) sb.AppendFormat("[{0}:{1}]", i, args[i].Type);
                        EmitOutput(sb.ToString());
                    }
                    catch { }

                    // support both GetLibrary(name, silent) and :GetLibrary(name, silent)
                    string? name = null;
                    bool silent = false;
                    if (args.Count >= 1 && args[0].Type == DataType.String) name = args[0].String;
                    else if (args.Count >= 2 && args[1].Type == DataType.String) name = args[1].String;

                    if (args.Count >= 2)
                    {
                        if (args[1].Type == DataType.Boolean) silent = args[1].Boolean;
                        else if (args.Count >= 3 && args[2].Type == DataType.Boolean) silent = args[2].Boolean;
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        var dv = libsTbl.Get(name);
                        var mdv = minorsTbl.Get(name);
                        if (dv != null && dv.Type == DataType.Table)
                        {
                            if (mdv != null && mdv.Type == DataType.Number) return DynValue.NewTuple(DynValue.NewTable(dv.Table), mdv);
                            return DynValue.NewTable(dv.Table);
                        }
                        // Instead of throwing a hard error when a library isn't found,
                        // create a minimal placeholder table so dependent libraries can
                        // continue initialization without failing outright.
                        if (!silent)
                        {
                            var placeholder = new Table(_script);
                            placeholder.Set("__name", DynValue.NewString(name));
                            placeholder.Set("__minor", DynValue.NewNumber(0));
                            libsTbl.Set(name, DynValue.NewTable(placeholder));
                            minorsTbl.Set(name, DynValue.NewNumber(0));
                            try { _libRegistry[name] = placeholder; } catch { }
                            EmitOutput($"[LuaRunner-Diag] LibStub.GetLibrary created placeholder for missing lib '{name}'");
                            return DynValue.NewTable(placeholder);
                        }
                    }
                    return DynValue.Nil;
                }));

                libStub.MetaTable = mt;
                _script.Globals["LibStub"] = DynValue.NewTable(libStub);
                // also mirror into our _libRegistry for compatibility
                _libRegistry["LibStub"] = libStub;

                // Diagnostic: report whether NewLibrary/GetLibrary are present and callable
                try
                {
                    var newLibDv = libStub.Get("NewLibrary");
                    if (newLibDv == null || newLibDv.Type == DataType.Nil)
                    {
                        EmitOutput("[LuaRunner-Diag] LibStub.NewLibrary is nil");
                    }
                    else
                    {
                        EmitOutput($"[LuaRunner-Diag] LibStub.NewLibrary type={newLibDv.Type}");
                        try
                        {
                            // Try a harmless call to create a temporary lib and remove it
                            var tmp = _script.Call(newLibDv.Function, DynValue.NewString("FluxTestLib"), DynValue.NewNumber(1));
                            EmitOutput("[LuaRunner-Diag] LibStub.NewLibrary callable (test) returned: " + (tmp?.ToPrintString() ?? "(nil)"));
                        }
                        catch (Exception ex)
                        {
                            EmitOutput("[LuaRunner-Diag] LibStub.NewLibrary call failed: " + ex.Message);
                        }
                    }
                }
                catch { }

                // Pre-register a minimal AceAddon-3.0 implementation
                    try
                    {
                        var ace = new Table(_script);
                        // Ensure LibStub.libs contains our AceAddon table so LibStub('AceAddon-3.0') returns it
                        try { libsTbl.Set("AceAddon-3.0", DynValue.NewTable(ace)); } catch { }
                    ace.Set("NewAddon", DynValue.NewCallback((ctx, args) =>
                    {
                        // Args: name [, ...mixins]
                        string? name = null;
                        // support both NewAddon("Name", ...) and :NewAddon("Name", ...)
                        if (args.Count >= 1 && args[0].Type == DataType.String)
                        {
                            name = args[0].String;
                        }
                        else if (args.Count >= 2 && args[0].Type == DataType.Table && args[1].Type == DataType.String)
                        {
                            name = args[1].String;
                        }

                        if (!string.IsNullOrEmpty(name))
                        {
                            var addonTbl = new Table(_script);
                            addonTbl.Set("__name", DynValue.NewString(name));

                            // RegisterEvent method: self:RegisterEvent(event, handler)
                            addonTbl.Set("RegisterEvent", DynValue.NewCallback((c2, a2) =>
                            {
                                if (a2.Count >= 2 && a2[0].Type == DataType.Table && a2[1].Type == DataType.String)
                                {
                                    var selfTable = a2[0].Table;
                                    var ev = a2[1].String;

                                    // Handler can be function or string method name
                                    DynValue handler = null;
                                    if (a2.Count >= 3) handler = a2[2];

                                    Closure targetClosure = null;

                                    if (handler != null && handler.Type == DataType.Function)
                                    {
                                        targetClosure = handler.Function;
                                    }
                                    else if (handler != null && handler.Type == DataType.String)
                                    {
                                        var m = selfTable.Get(handler.String);
                                        if (m != null && (m.Type == DataType.Function || m.Type == DataType.ClrFunction)) targetClosure = m.Function;
                                    }

                                    // If no explicit handler, look for a method named the event (OnEvent) - skip for now

                                    if (targetClosure != null)
                                    {
                                        // Create a wrapper that supplies the addon table as first arg
                                        var wrapper = DynValue.NewCallback((ctx3, cbArgs) =>
                                        {
                                            try
                                            {
                                                var argsList = new List<DynValue>();
                                                argsList.Add(DynValue.NewTable(selfTable));
                                                if (cbArgs != null)
                                                {
                                                    for (int ai = 0; ai < cbArgs.Count; ai++)
                                                    {
                                                        argsList.Add(cbArgs[ai]);
                                                    }
                                                }
                                                _script.Call(targetClosure, argsList.ToArray());
                                            }
                                            catch { }
                                            return DynValue.Nil;
                                        });

                                        // store mapping for unregister
                                        var addonName = selfTable.Get("__name")?.String ?? name;
                                        lock (_aceRegisteredHandlers)
                                        {
                                            if (!_aceRegisteredHandlers.ContainsKey(addonName)) _aceRegisteredHandlers[addonName] = new Dictionary<string, Closure>();
                                            _aceRegisteredHandlers[addonName][ev] = wrapper.Function;
                                        }

                                        if (!_eventHandlers.ContainsKey(ev)) _eventHandlers[ev] = new List<Closure>();
                                        _eventHandlers[ev].Add(wrapper.Function);
                                    }
                                }
                                return DynValue.Nil;
                            }));

                            // UnregisterEvent
                            addonTbl.Set("UnregisterEvent", DynValue.NewCallback((c2, a2) =>
                            {
                                if (a2.Count >= 2 && a2[0].Type == DataType.Table && a2[1].Type == DataType.String)
                                {
                                    var selfTable = a2[0].Table;
                                    var ev = a2[1].String;
                                    var addonName = selfTable.Get("__name")?.String ?? name;
                                    lock (_aceRegisteredHandlers)
                                    {
                                        if (_aceRegisteredHandlers.TryGetValue(addonName, out var map) && map.TryGetValue(ev, out var closure))
                                        {
                                            // remove from global handlers list
                                            if (_eventHandlers.TryGetValue(ev, out var list))
                                            {
                                                list.RemoveAll(c => c == closure);
                                            }
                                            map.Remove(ev);
                                        }
                                    }
                                }
                                return DynValue.Nil;
                            }));

                            _aceAddons[name] = addonTbl;
                            return DynValue.NewTable(addonTbl);
                        }
                        return DynValue.Nil;
                    }));

                        // preserve a reference to this NewAddon closure so we can inject it later
                        try
                        {
                            var maybeNew = ace.Get("NewAddon");
                            if (maybeNew != null && maybeNew.Type != DataType.Nil) _preregisteredAceAddonNewAddon = maybeNew;
                        }
                        catch { }

                        // Provide a minimal CallbackHandler implementation globally so libraries
                        // calling `CallbackHandler:New(...)` don't nil-call during initialization.
                        try
                        {
                            var cbh = new Table(_script);
                            cbh.Set("New", DynValue.NewCallback((ctx, args) =>
                            {
                                // Return a simple handler object with Register/Unregister/Fire
                                var handler = new Table(_script);
                                handler.Set("_callbacks", DynValue.NewTable(new Table(_script)));
                                handler.Set("Register", DynValue.NewCallback((c2, a2) => { return DynValue.Nil; }));
                                handler.Set("Unregister", DynValue.NewCallback((c2, a2) => { return DynValue.Nil; }));
                                handler.Set("UnregisterAll", DynValue.NewCallback((c2, a2) => { return DynValue.Nil; }));
                                handler.Set("Fire", DynValue.NewCallback((c2, a2) => { return DynValue.Nil; }));
                                return DynValue.NewTable(handler);
                            }));
                            _script.Globals["CallbackHandler"] = DynValue.NewTable(cbh);
                            // also register into LibStub.libs for callers that query it
                            try { if (_libRegistry != null) _libRegistry["CallbackHandler-1.0"] = cbh; } catch { }
                            try { libsTbl.Set("CallbackHandler-1.0", DynValue.NewTable(cbh)); } catch { }
                        }
                        catch { }

                    // Also register AceEvent-3.0 as a no-op library so mixins resolve
                    var aceEvent = new Table(_script);
                    // AceEvent: RegisterEvent(object, eventName, handler) and UnregisterEvent(object, eventName)
                    aceEvent.Set("RegisterEvent", DynValue.NewCallback((ctx2, a2) =>
                    {
                        if (a2.Count >= 3 && a2[0].Type == DataType.Table && a2[1].Type == DataType.String)
                        {
                            var objTable = a2[0].Table;
                            var ev = a2[1].String;
                            DynValue handler = a2.Count >= 3 ? a2[2] : DynValue.Nil;

                            Closure target = null;
                            if (handler != null && handler.Type == DataType.Function) target = handler.Function;
                            else if (handler != null && handler.Type == DataType.String)
                            {
                                var m = objTable.Get(handler.String);
                                if (m != null && (m.Type == DataType.Function || m.Type == DataType.ClrFunction)) target = m.Function;
                            }

                            if (target != null)
                            {
                                var addonName = objTable.Get("__name")?.String ?? "";
                                var wrapper = DynValue.NewCallback((ctx3, cbArgs) =>
                                {
                                    try
                                    {
                                        var argsList = new List<DynValue> { DynValue.NewTable(objTable) };
                                        if (cbArgs != null)
                                        {
                                            for (int i = 0; i < cbArgs.Count; i++) argsList.Add(cbArgs[i]);
                                        }
                                        _script.Call(target, argsList.ToArray());
                                    }
                                    catch { }
                                    return DynValue.Nil;
                                });

                                lock (_aceRegisteredHandlers)
                                {
                                    if (!_aceRegisteredHandlers.ContainsKey(addonName)) _aceRegisteredHandlers[addonName] = new Dictionary<string, Closure>();
                                    _aceRegisteredHandlers[addonName][ev] = wrapper.Function;
                                }

                                if (!_eventHandlers.ContainsKey(ev)) _eventHandlers[ev] = new List<Closure>();
                                _eventHandlers[ev].Add(wrapper.Function);
                            }
                        }
                        return DynValue.Nil;
                    }));

                    aceEvent.Set("UnregisterEvent", DynValue.NewCallback((ctx2, a2) =>
                    {
                        if (a2.Count >= 2 && a2[0].Type == DataType.Table && a2[1].Type == DataType.String)
                        {
                            var objTable = a2[0].Table;
                            var ev = a2[1].String;
                            var addonName = objTable.Get("__name")?.String ?? "";
                            lock (_aceRegisteredHandlers)
                            {
                                if (_aceRegisteredHandlers.TryGetValue(addonName, out var map) && map.TryGetValue(ev, out var closure))
                                {
                                    if (_eventHandlers.TryGetValue(ev, out var list)) list.RemoveAll(c => c == closure);
                                    map.Remove(ev);
                                }
                            }
                        }
                        return DynValue.Nil;
                    }));

                    _libRegistry["AceEvent-3.0"] = aceEvent;
                    try { libsTbl.Set("AceEvent-3.0", DynValue.NewTable(aceEvent)); } catch { }

                    // AceTimer: ScheduleTimer(object, funcOrName, delaySeconds) -> timerId; CancelTimer(object, timerId)
                    var aceTimer = new Table(_script);
                    aceTimer.Set("ScheduleTimer", DynValue.NewCallback((ctx2, a2) =>
                    {
                        // support both obj:ScheduleTimer(func, delay) (self, func, delay) and ScheduleTimer(obj, func, delay)
                        Table objTable = null;
                        DynValue funcVal = null;
                        double delay = 0;
                        if (a2.Count >= 3 && a2[0].Type == DataType.Table)
                        {
                            objTable = a2[0].Table;
                            funcVal = a2[1];
                            delay = a2[2].Number;
                        }
                        else if (a2.Count >= 2 && a2[0].Type == DataType.Function)
                        {
                            // called as :ScheduleTimer(func, delay) where self is implicit
                            funcVal = a2[0];
                            delay = a2[1].Number;
                        }

                        // no fallback for implicit self; require object table or function provided

                        if (funcVal == null || delay <= 0) return DynValue.Nil;

                        var addonName = objTable?.Get("__name")?.String ?? "";
                        int id;
                        lock (_aceTimers)
                        {
                            id = _nextTimerId++;
                            if (!_aceTimers.ContainsKey(addonName)) _aceTimers[addonName] = new Dictionary<int, System.Timers.Timer>();
                        }

                        var timer = new System.Timers.Timer(delay * 1000.0) { AutoReset = false };
                        timer.Elapsed += (s, e) =>
                        {
                            try
                            {
                                timer.Stop();
                                lock (_aceTimers)
                                {
                                    if (_aceTimers.TryGetValue(addonName, out var map)) map.Remove(id);
                                }

                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        if (funcVal.Type == DataType.Function)
                                        {
                                            _script.Call(funcVal.Function, objTable != null ? DynValue.NewTable(objTable) : DynValue.Nil);
                                        }
                                        else if (funcVal.Type == DataType.String && objTable != null)
                                        {
                                            var m = objTable.Get(funcVal.String);
                                            if (m != null && m.Type == DataType.Function) _script.Call(m.Function, DynValue.NewTable(objTable));
                                        }
                                    }
                                    catch { }
                                });
                            }
                            catch { }
                            finally
                            {
                                try { timer.Dispose(); } catch { }
                            }
                        };
                        // Mirror into C# registry so LibStub calls from other contexts can find it
                        lock (_aceTimers)
                        {
                            _aceTimers[addonName][id] = timer;
                        }
                        timer.Start();
                        return DynValue.NewNumber(id);
                    }));

                    aceTimer.Set("CancelTimer", DynValue.NewCallback((ctx2, a2) =>
                    {
                        if (a2.Count >= 2 && a2[0].Type == DataType.Table && a2[1].Type == DataType.Number)
                        {
                            var objTable = a2[0].Table;
                            var id = (int)a2[1].Number;
                            var addonName = objTable.Get("__name")?.String ?? "";
                            lock (_aceTimers)
                            {
                                if (_aceTimers.TryGetValue(addonName, out var map) && map.TryGetValue(id, out var t))
                                {
                                    try { t.Stop(); t.Dispose(); } catch { }
                                    map.Remove(id);
                                }
                            }
                        }
                        return DynValue.Nil;
                    }));

                    _libRegistry["AceTimer-3.0"] = aceTimer;
                    try { libsTbl.Set("AceTimer-3.0", DynValue.NewTable(aceTimer)); } catch { }
                    // expose the AceAddon library object
                    _libRegistry["AceAddon-3.0"] = ace;
                    // Pre-register common library names so LibStub:GetLibrary won't return nil
                    try
                    {
                        var commonLibs = new[] {
                            "CallbackHandler-1.0", "LibDataBroker-1.1", "LibDBIcon-1.0",
                            "HereBeDragons-2.0", "HereBeDragons-Pins-2.0", "AceGUI-3.0",
                            "AceConsole-3.0", "AceDB-3.0", "LibStub",
                            // Ensure core Ace libraries are present in libsTbl early so LibStub("Name") returns them
                            "AceAddon-3.0", "AceLocale-3.0", "AceEvent-3.0", "AceTimer-3.0"
                        };
                        foreach (var cname in commonLibs)
                        {
                            if (!_libRegistry.ContainsKey(cname)) _libRegistry[cname] = new Table(_script);
                        }
                        // Also populate the LibStub libs table so LibStub("Name") returns the table directly
                        try
                        {
                            foreach (var cname in commonLibs)
                            {
                                if (_libRegistry.TryGetValue(cname, out var tbl) && tbl != null)
                                {
                                    try { libsTbl.Set(cname, DynValue.NewTable(tbl)); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                    catch { }

                        // Provide a minimal AceLocale implementation: GetLocale(addonName) -> table
                    try
                    {
                        var aceLocale = new Table(_script);
                        aceLocale.Set("GetLocale", DynValue.NewCallback((ctx, args) =>
                        {
                            // return a table that returns the key string for any missing localization
                            var locTbl = new Table(_script);
                            var mtLoc = new Table(_script);
                            mtLoc.Set("__index", DynValue.NewCallback((c2, a2) =>
                            {
                                // __index called with (table, key)
                                if (a2.Count >= 2 && a2[1].Type == DataType.String)
                                {
                                    return DynValue.NewString(a2[1].String);
                                }
                                return DynValue.NewString(string.Empty);
                            }));
                            locTbl.MetaTable = mtLoc;
                            return DynValue.NewTable(locTbl);
                        }));
                        _libRegistry["AceLocale-3.0"] = aceLocale;
                        // Ensure LibStub.libs returns this concrete table (replace any placeholder)
                        try { libsTbl.Set("AceLocale-3.0", DynValue.NewTable(aceLocale)); EmitOutput("[LuaRunner-Diag] Registered AceLocale-3.0 into LibStub.libs"); } catch { }
                        // Also expose as a global for older code paths
                        try { _script.Globals["AceLocale-3.0"] = DynValue.NewTable(aceLocale); } catch { }
                    }
                    catch { }

                    // Minimal LibDataBroker implementation
                    try
                    {
                        var ldb = new Table(_script);
                        var objects = new Table(_script);
                        ldb.Set("_objects", DynValue.NewTable(objects));
                        ldb.Set("NewDataObject", DynValue.NewCallback((ctx, args) =>
                        {
                            if (args.Count >= 2 && args[0].Type == DataType.String && args[1].Type == DataType.Table)
                            {
                                var name = args[0].String;
                                var obj = args[1].Table;
                                // attach a simple callbacks table to the object
                                var cbTbl = new Table(_script);
                                var cbStore = new Dictionary<string, List<Closure>>();
                                cbTbl.Set("Register", DynValue.NewCallback((c2, a2) =>
                                {
                                    if (a2.Count >= 2 && a2[0].Type == DataType.String && (a2[1].Type == DataType.Function || a2[1].Type == DataType.ClrFunction))
                                    {
                                        var ev = a2[0].String;
                                        if (!cbStore.ContainsKey(ev)) cbStore[ev] = new List<Closure>();
                                        cbStore[ev].Add(a2[1].Function);
                                    }
                                    return DynValue.Nil;
                                }));
                                cbTbl.Set("Unregister", DynValue.NewCallback((c2, a2) =>
                                {
                                    if (a2.Count >= 2 && a2[0].Type == DataType.String)
                                    {
                                        var ev = a2[0].String;
                                        if (cbStore.ContainsKey(ev) && a2.Count >= 2)
                                        {
                                            // remove matching function if provided
                                            // (simple remove all for now)
                                            cbStore.Remove(ev);
                                        }
                                    }
                                    return DynValue.Nil;
                                }));
                                cbTbl.Set("Fire", DynValue.NewCallback((c2, a2) =>
                                {
                                    if (a2.Count >= 1 && a2[0].Type == DataType.String)
                                    {
                                        var ev = a2[0].String;
                                        if (cbStore.TryGetValue(ev, out var list))
                                        {
                                            foreach (var fn in list)
                                            {
                                                try { _script.Call(fn); } catch { }
                                            }
                                        }
                                    }
                                    return DynValue.Nil;
                                }));

                                obj.Set("callbacks", DynValue.NewTable(cbTbl));
                                objects.Set(name, DynValue.NewTable(obj));
                                return DynValue.NewTable(obj);
                            }
                            return DynValue.Nil;
                        }));
                        _libRegistry["LibDataBroker-1.1"] = ldb;
                        try { libsTbl.Set("LibDataBroker-1.1", DynValue.NewTable(ldb)); } catch { }
                    }
                    catch { }

                    // Minimal LibDBIcon implementation (no-op show/hide/register)
                    try
                    {
                        var dbicon = new Table(_script);
                        dbicon.Set("Register", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                        dbicon.Set("Show", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                        dbicon.Set("Hide", DynValue.NewCallback((c, a) => { return DynValue.Nil; }));
                        _libRegistry["LibDBIcon-1.0"] = dbicon;
                        try { libsTbl.Set("LibDBIcon-1.0", DynValue.NewTable(dbicon)); } catch { }
                    }
                    catch { }

                        // Minimal CallbackHandler-1.0 shim: provides New(name) -> handler with Register/Unregister/Fire
                        try
                        {
                            var cbh = new Table(_script);
                            cbh.Set("New", DynValue.NewCallback((ctx, args) =>
                            {
                                var handlerTbl = new Table(_script);
                                // internal storage for callbacks: table of lists
                                var cbStorage = new Table(_script);
                                handlerTbl.Set("__callbacks", DynValue.NewTable(cbStorage));

                                handlerTbl.Set("RegisterCallback", DynValue.NewCallback((c2, a2) =>
                                {
                                    if (a2.Count >= 2 && a2[0].Type == DataType.String && (a2[1].Type == DataType.Function || a2[1].Type == DataType.ClrFunction))
                                    {
                                        var evName = a2[0].String;
                                        var listDv = cbStorage.Get(evName);
                                        Table listTbl;
                                        if (listDv.Type == DataType.Table) listTbl = listDv.Table;
                                        else { listTbl = new Table(_script); cbStorage.Set(evName, DynValue.NewTable(listTbl)); }
                                        // append
                                        int max = 0; foreach (var p in listTbl.Pairs) if (p.Key.Type == DataType.Number) max = Math.Max(max, (int)p.Key.Number);
                                        listTbl.Set(DynValue.NewNumber(max + 1), a2[1]);
                                    }
                                    return DynValue.Nil;
                                }));

                                // Aliases commonly used by libraries
                                handlerTbl.Set("Register", handlerTbl.Get("RegisterCallback"));

                                handlerTbl.Set("UnregisterCallback", DynValue.NewCallback((c2, a2) =>
                                {
                                    if (a2.Count >= 2 && a2[0].Type == DataType.String)
                                    {
                                        var evName = a2[0].String;
                                        var cb = a2.Count >= 2 ? a2[1] : null;
                                        var listDv = cbStorage.Get(evName);
                                        if (listDv.Type == DataType.Table && cb != null)
                                        {
                                            var listTbl = listDv.Table;
                                            var keys = new List<DynValue>();
                                            foreach (var p in listTbl.Pairs) if (!p.Value.Equals(cb)) keys.Add(p.Key);
                                            foreach (var k in keys) listTbl.Set(k, DynValue.Nil);
                                        }
                                    }
                                    return DynValue.Nil;
                                }));

                                handlerTbl.Set("Unregister", handlerTbl.Get("UnregisterCallback"));

                                handlerTbl.Set("Fire", DynValue.NewCallback((c2, a2) =>
                                {
                                    if (a2.Count >= 1 && a2[0].Type == DataType.String)
                                    {
                                        var evName = a2[0].String;
                                        var listDv = cbStorage.Get(evName);
                                        if (listDv.Type == DataType.Table)
                                        {
                                            var listTbl = listDv.Table;
                                            foreach (var p in listTbl.Pairs)
                                            {
                                                var fn = p.Value;
                                                try
                                                {
                                                    if (fn.Type == DataType.Function) _script.Call(fn.Function);
                                                    else if (fn.Type == DataType.ClrFunction) _script.Call(fn);
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                    return DynValue.Nil;
                                }));

                                return DynValue.NewTable(handlerTbl);
                            }));

                            _libRegistry["CallbackHandler-1.0"] = cbh;
                            // also place into globals so older code can call CallbackHandler:New
                            _script.Globals["CallbackHandler"] = DynValue.NewTable(cbh);
                        }
                        catch { }
                }
                catch { }
            }
            catch { }
        }

        public void RunScriptFromString(string code, string addonName, string? firstVarArg = null, bool isLibraryFile = false, double libraryMinor = 0, string? filePath = null)
        {
            try
            {
            // Load the chunk and call it with (addonName, namespaceTable) like WoW does (local name, ns = ...)
            // Provide a chunk name (filePath) so runtime errors include the source filename/line numbers
            var chunkName = filePath ?? (isLibraryFile ? $"@{addonName}:{firstVarArg ?? "lib"}" : $"@{addonName}:chunk");
            var func = _script.LoadString(code, null, chunkName);
                Table ns;
                if (!_addonNamespaces.TryGetValue(addonName, out ns))
                {
                    ns = new Table(_script);
                    _addonNamespaces[addonName] = ns;
                }
                // Determine first vararg to pass: for library files, pass (MAJOR, MINOR).
                var firstArg = firstVarArg ?? addonName;

                // Diagnostic: report presence of key globals before executing chunk
                try
                {
                    var cf = _script.Globals.Get("CreateFrame");
                    var ls = _script.Globals.Get("LibStub");
                    EmitOutput($"[LuaRunner-Diag] About to execute chunk='{chunkName}' CreateFrameType={cf?.Type} LibStubType={ls?.Type}");
                }
                catch { }

                // Extra diagnostics for Attune startup: inspect LibStub('AceAddon-3.0') and its NewAddon field
                try
                {
                    if ((filePath != null && filePath.IndexOf("Attune\\Attune.lua", StringComparison.OrdinalIgnoreCase) >= 0) || string.Equals(addonName, "Attune", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var dbgChunk = @"local a = LibStub('AceAddon-3.0'); if a==nil then print('[LuaRunner-Diag] Pre-Attune: LibStub returned nil for AceAddon') else print('[LuaRunner-Diag] Pre-Attune: LibStub AceAddon object type='..type(a)..' NewAddon type='..(a.NewAddon and type(a.NewAddon) or 'nil')) end
local loc = LibStub('AceLocale-3.0'); if loc==nil then print('[LuaRunner-Diag] Pre-Attune: AceLocale returned nil') else print('[LuaRunner-Diag] Pre-Attune: AceLocale type='..type(loc)..' GetLocale type='..(loc.GetLocale and type(loc.GetLocale) or 'nil')); if type(loc.GetLocale)=='function' then local L = loc:GetLocale('Attune'); print('[LuaRunner-Diag] Pre-Attune: AceLocale:GetLocale returned type='..type(L)) else print('[LuaRunner-Diag] Pre-Attune: AceLocale:GetLocale missing') end end";
                            _script.DoString(dbgChunk);
                        }
                        catch { }
                    }
                }
                catch { }

                if (isLibraryFile)
                {
                    // Libraries expect (MAJOR, MINOR)
                    _script.Call(func, DynValue.NewString(firstArg), DynValue.NewNumber(libraryMinor));
                }
                else
                {
                    // Addon files expect (addonName, namespaceTable)
                    _script.Call(func, DynValue.NewString(addonName), DynValue.NewTable(ns));
                }
                EmitOutput($"[Lua] Script {addonName} executed.");
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

        // Load all .lua files under a directory as library files (useful for preloading Ace3 libs)
        public void LoadLibrariesFromDirectory(string dirPath)
        {
            try
            {
                if (string.IsNullOrEmpty(dirPath) || !System.IO.Directory.Exists(dirPath)) return;
                var files = System.IO.Directory.GetFiles(dirPath, "*.lua", System.IO.SearchOption.AllDirectories);
                foreach (var f in files.OrderBy(p => p))
                {
                    try
                    {
                        // Skip certain files which overwrite our C# shims or depend heavily on WoW UI
                        var fname = System.IO.Path.GetFileName(f);
                        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "LibStub.lua",
                            "Ace3.lua",
                            "ChatThrottleLib.lua",
                            "AceGUIContainer-Window.lua",
                            "AceGUIWidget-DropDown.lua",
                            "AceGUIWidget-DropDown-Items.lua",
                        };
                        if (skip.Contains(fname))
                        {
                            EmitOutput($"[LuaRunner] Skipping preload of {fname}");
                            continue;
                        }
                        var code = System.IO.File.ReadAllText(f);
                        var libName = System.IO.Path.GetFileNameWithoutExtension(f);
                        // Use the file's relative path as chunk name for better diagnostics
                        var chunkRel = f;
                        RunScriptFromString(code, libName, libName, isLibraryFile: true, libraryMinor: 0, filePath: chunkRel);
                        EmitOutput($"[LuaRunner] Preloaded library: {libName} from {f}");
                    }
                    catch (Exception ex)
                    {
                        EmitOutput($"[LuaRunner] Failed to preload lib {f}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                EmitOutput($"[LuaRunner] LoadLibrariesFromDirectory error: {ex.Message}");
            }
        }

        // Load only libraries whose folder name matches the whitelist (top-level dir names under dirPath)
        public void LoadLibrariesFromDirectory(string dirPath, IEnumerable<string> whitelist)
        {
            try
            {
                if (string.IsNullOrEmpty(dirPath) || !System.IO.Directory.Exists(dirPath)) return;
                var allowed = new HashSet<string>(whitelist.Select(s => s.ToLowerInvariant()));
                var topDirs = System.IO.Directory.GetDirectories(dirPath);
                foreach (var td in topDirs)
                {
                    var name = System.IO.Path.GetFileName(td)?.ToLowerInvariant() ?? string.Empty;
                    if (!allowed.Contains(name)) continue;
                    var files = System.IO.Directory.GetFiles(td, "*.lua", System.IO.SearchOption.AllDirectories);
                    foreach (var f in files.OrderBy(p => p))
                    {
                        try
                        {
                            // Skip problematic files that would overwrite our shims or require UI
                            var fname = System.IO.Path.GetFileName(f);
                            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                "LibStub.lua",
                                "Ace3.lua",
                                "ChatThrottleLib.lua",
                                "AceGUIContainer-Window.lua",
                                "AceGUIWidget-DropDown.lua",
                                "AceGUIWidget-DropDown-Items.lua",
                            };
                            if (skip.Contains(fname))
                            {
                                EmitOutput($"[LuaRunner] Skipping preload of {fname}");
                                continue;
                            }

                            var code = System.IO.File.ReadAllText(f);
                            var libName = System.IO.Path.GetFileNameWithoutExtension(f);
                            var chunkRel = f;
                            RunScriptFromString(code, libName, libName, isLibraryFile: true, libraryMinor: 0, filePath: chunkRel);
                            EmitOutput($"[LuaRunner] Preloaded library: {libName} from {f}");
                        }
                        catch (Exception ex)
                        {
                            EmitOutput($"[LuaRunner] Failed to preload lib {f}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EmitOutput($"[LuaRunner] LoadLibrariesFromDirectory error: {ex.Message}");
            }
        }

        public void InvokeClosure(Closure c, params object?[] args)
        {
            try
            {
                var dynArgs = new List<DynValue>();
                foreach (var a in args)
                {
                    if (a == null) dynArgs.Add(DynValue.Nil);
                    else if (a is string s) dynArgs.Add(DynValue.NewString(s));
                    else if (a is int i) dynArgs.Add(DynValue.NewNumber(i));
                    else if (a is double d) dynArgs.Add(DynValue.NewNumber(d));
                    else if (a is bool b) dynArgs.Add(DynValue.NewBoolean(b));
                    else dynArgs.Add(DynValue.NewString(a.ToString()));
                }

                _script.Call(c, dynArgs.ToArray());
            }
            catch (Exception ex)
            {
                EmitOutput("[Closure error] " + ex.Message);
            }
        }

        // Trigger a registered event with C# values
        public void TriggerEvent(string eventName, params object?[] args)
        {
            if (_eventHandlers.TryGetValue(eventName, out var handlers))
            {
                var dynArgs = new List<DynValue>();
                foreach (var a in args)
                {
                    if (a == null) dynArgs.Add(DynValue.Nil);
                    else if (a is string s) dynArgs.Add(DynValue.NewString(s));
                    else if (a is int i) dynArgs.Add(DynValue.NewNumber(i));
                    else if (a is double d) dynArgs.Add(DynValue.NewNumber(d));
                    else if (a is bool b) dynArgs.Add(DynValue.NewBoolean(b));
                    else dynArgs.Add(DynValue.NewString(a.ToString()));
                }

                foreach (var h in handlers)
                {
                    try
                    {
                        if (h == null)
                        {
                            EmitOutput($"[LuaRunner] TriggerEvent {eventName} skipping null handler");
                            continue;
                        }
                        try { EmitOutput($"[LuaRunner] TriggerEvent invoking handler for {eventName} (closure={h.GetHashCode()})"); } catch { }
                        _script.Call(h, dynArgs.ToArray());
                    }
                    catch (Exception ex)
                    {
                        EmitOutput("[Event handler error] " + ex.Message);
                    }
                }
            }
        }

        // Invoke lifecycle hooks (OnInitialize, OnEnable) for registered Ace addons
        public void InvokeAceAddonLifecycle(string hookName)
        {
            foreach (var kv in _aceAddons)
            {
                var addonTbl = kv.Value;
                try
                {
                    var member = addonTbl.Get(hookName);
                    if (member != null && member.Type == DataType.Function)
                    {
                        // call with addon table as first arg
                        _script.Call(member.Function, DynValue.NewTable(addonTbl));
                        EmitOutput($"[LuaRunner] Called {hookName} on addon {kv.Key}");
                    }
                }
                catch (Exception ex)
                {
                    EmitOutput($"[LuaRunner] Error calling {hookName} on {kv.Key}: {ex.Message}");
                }
            }
        }

        public void LoadSavedVariables(Dictionary<string, object?> dict)
        {
            // Populate backing saved-variables table
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
            if (val is JsonElement je)
            {
                // handle simple json values and objects
                switch (je.ValueKind)
                {
                    case JsonValueKind.String: return DynValue.NewString(je.GetString() ?? string.Empty);
                    case JsonValueKind.Number: return DynValue.NewNumber(je.GetDouble());
                    case JsonValueKind.True: return DynValue.NewBoolean(true);
                    case JsonValueKind.False: return DynValue.NewBoolean(false);
                    case JsonValueKind.Object:
                        var t = new Table(_script);
                        foreach (var prop in je.EnumerateObject())
                        {
                            t.Set(prop.Name, ConvertToDynValue(prop.Value));
                        }
                        return DynValue.NewTable(t);
                }
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

        private void InitializeSavedVariablesTable(Dictionary<string, object?>? initial)
        {
            // backing storage
            _savedVarsTable = new Table(_script);
            if (initial != null)
            {
                foreach (var kv in initial)
                {
                    _savedVarsTable.Set(kv.Key, ConvertToDynValue(kv.Value));
                }
            }

            // proxy table exposed to Lua with metatable to trap writes
            var proxy = new Table(_script);
            var mt = new Table(_script);

            // __index: return value from backing table
            mt.Set("__index", DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count >= 2)
                {
                    var key = args[1];
                    if (key.Type == DataType.String)
                    {
                        var v = _savedVarsTable.Get(key.String);
                        return v ?? DynValue.Nil;
                    }
                }
                return DynValue.Nil;
            }));

            // __newindex: set in backing table and notify host
            mt.Set("__newindex", DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count >= 3)
                {
                    var key = args[1];
                    var val = args[2];
                    if (key.Type == DataType.String)
                    {
                        _savedVarsTable.Set(key.String, val);
                        try { OnSavedVariablesChanged?.Invoke(this, AddonName); } catch { }
                    }
                }
                return DynValue.Nil;
            }));

            proxy.MetaTable = mt;

            _script.Globals["SavedVariables"] = DynValue.NewTable(proxy);
        }

        private void InitializeVanillaApiStubs()
        {
            try
            {
                // Unit-related stubs
                _script.Globals["UnitExists"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var u = a[0].String;
                        if (string.IsNullOrEmpty(u)) return DynValue.NewBoolean(false);
                        // consider common units present
                        if (u == "player" || u == "target" || u.StartsWith("party") || u.StartsWith("raid")) return DynValue.NewBoolean(true);
                    }
                    return DynValue.NewBoolean(false);
                });

                _script.Globals["UnitIsDeadOrGhost"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
                _script.Globals["UnitLevel"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(60));
                _script.Globals["UnitHealth"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(100));
                _script.Globals["UnitHealthMax"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(100));

                _script.Globals["UnitClass"] = DynValue.NewCallback((c, a) =>
                {
                    // return className, classFile, classID
                    return DynValue.NewTuple(DynValue.NewString("Warrior"), DynValue.NewString("WARRIOR"), DynValue.NewNumber(1));
                });

                // Group/raid stubs
                _script.Globals["IsInGroup"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
                _script.Globals["IsInRaid"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
                _script.Globals["GetNumGroupMembers"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));
                _script.Globals["GetNumSubgroupMembers"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));

                // Map/zone stubs
                _script.Globals["GetZoneText"] = DynValue.NewCallback((c, a) => DynValue.NewString("Unknown"));
                _script.Globals["GetRealZoneText"] = _script.Globals["GetZoneText"];
                _script.Globals["GetCurrentMapAreaID"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));
                _script.Globals["GetPlayerMapPosition"] = DynValue.NewCallback((c, a) => DynValue.NewTuple(DynValue.NewNumber(0.0), DynValue.NewNumber(0.0)));

                // Spell/item info stubs (best-effort)
                _script.Globals["GetSpellInfo"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1)
                    {
                        if (a[0].Type == DataType.String) return DynValue.NewString(a[0].String);
                        if (a[0].Type == DataType.Number) return DynValue.NewString("Spell" + a[0].Number);
                    }
                    return DynValue.Nil;
                });

                _script.Globals["GetItemInfo"] = DynValue.NewCallback((c, a) =>
                {
                    if (a.Count >= 1)
                    {
                        if (a[0].Type == DataType.String) return DynValue.NewString(a[0].String);
                        if (a[0].Type == DataType.Number) return DynValue.NewString("Item" + a[0].Number);
                    }
                    return DynValue.Nil;
                });

                // Combat/instance stubs
                _script.Globals["InCombatLockdown"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
                _script.Globals["IsInInstance"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));

                // Messaging / addon comms
                _script.Globals["SendAddonMessage"] = DynValue.NewCallback((c, a) =>
                {
                    try
                    {
                        var parts = new List<string>();
                        for (int i = 0; i < a.Count; i++) parts.Add(a[i].ToPrintString());
                        EmitOutput("[AddonMessage] " + string.Join(" | ", parts));
                    }
                    catch { }
                    return DynValue.NewBoolean(true);
                });

                // Cursor/tooltip minimal stubs
                _script.Globals["GetCursorPosition"] = DynValue.NewCallback((c, a) => DynValue.NewTuple(DynValue.NewNumber(0), DynValue.NewNumber(0)));
                _script.Globals["SetCursor"] = DynValue.NewCallback((c, a) => DynValue.Nil);

                // Misc
                _script.Globals["UnitName"] = DynValue.NewCallback((c, a) => DynValue.NewString("Player"));
                _script.Globals["GetBuildInfo"] = DynValue.NewCallback((c, a) => DynValue.NewTuple(DynValue.NewString("1.0"), DynValue.NewString("build"), DynValue.NewNumber(InterfaceVersion)));

                // Provide GameFontNormal/GetFont shim used by some addons

            try
            {
                // Provide a minimal C_QuestLog with IsQuestFlaggedCompleted to avoid nil-index when used in fallbacks
                var cql = new Table(_script);
                cql.Set("IsQuestFlaggedCompleted", DynValue.NewCallback((c, a) => DynValue.NewBoolean(false)));
                _script.Globals["C_QuestLog"] = DynValue.NewTable(cql);

                // Provide GetRealmName
                _script.Globals["GetRealmName"] = DynValue.NewCallback((c, a) => DynValue.NewString("Realm"));

                // Provide getfenv(0) -> return global environment
                _script.Globals["getfenv"] = DynValue.NewCallback((c, a) =>
                {
                    // If called with 0 or nil return _G
                    return DynValue.NewTable(_script.Globals);
                });

                // Provide GameFontHighlight similar to GameFontNormal
                var gh = new Table(_script);
                gh.Set("GetFont", DynValue.NewCallback((c, a) => DynValue.NewTuple(DynValue.NewString("Arial"), DynValue.NewNumber(12), DynValue.NewString(""))));
                _script.Globals["GameFontHighlight"] = DynValue.NewTable(gh);
            }
            catch { }
                try
                {
                    var gf = new Table(_script);
                    gf.Set("GetFont", DynValue.NewCallback((c, a) =>
                    {
                        // return fontName, height, flags
                        return DynValue.NewTuple(DynValue.NewString("Arial"), DynValue.NewNumber(12), DynValue.NewString(""));
                    }));
                    _script.Globals["GameFontNormal"] = DynValue.NewTable(gf);
                }
                catch { }
            }
            catch { }
        }

        private (double, double) AnchorToOffset(string anchor, double w, double h)
        {
            // returns offset from top-left for the given anchor name
            switch (anchor)
            {
                case "TOPLEFT": return (0, 0);
                case "TOP": return (w / 2.0, 0);
                case "TOPRIGHT": return (w, 0);
                case "LEFT": return (0, h / 2.0);
                case "CENTER": return (w / 2.0, h / 2.0);
                case "RIGHT": return (w, h / 2.0);
                case "BOTTOMLEFT": return (0, h);
                case "BOTTOM": return (w / 2.0, h);
                case "BOTTOMRIGHT": return (w, h);
                default: return (0, 0);
            }
        }
    }
}
