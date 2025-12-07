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
        private readonly Dictionary<string, Table> _addonNamespaces = new();
        private Script _script;
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

        private FrameManager? _frameManager;

        private readonly string? _addonFolder;

        public LuaRunner(string addonName, FrameManager? frameManager = null, string? addonFolder = null)
        {
            _frameManager = frameManager;
            AddonName = addonName;
            _addonFolder = addonFolder;

            // Create script with common core modules so libraries have standard Lua functions
            // Enable Basic, Table, String, Math and Coroutine modules (safe subset)
            _script = new Script(CoreModules.Basic | CoreModules.Table | CoreModules.String | CoreModules.Math | CoreModules.Coroutine);

            // Create saved variables table with metatable to detect writes
            InitializeSavedVariablesTable(null);

            // Initialize a minimal LibStub implementation so embedded libs (Ace3) can register
            InitializeLibStub();

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
                    if (a.Count >= 2 && a[0].Type == DataType.String && (a[1].Type == DataType.Function || a[1].Type == DataType.ClrFunction))
                    {
                        var scriptName = a[0].String;
                        if (scriptName == "OnClick")
                        {
                            vf.OnClick = a[1].Function;
                        }
                        else if (scriptName == "OnUpdate")
                        {
                            vf.OnUpdate = a[1].Function;
                        }
                        else if (scriptName == "OnEnter")
                        {
                            vf.OnEnter = a[1].Function;
                        }
                        else if (scriptName == "OnLeave")
                        {
                            vf.OnLeave = a[1].Function;
                        }
                    }
                    return DynValue.Nil;
                }));

                // Provide reference to the C# frame id (for debugging)
                t.Set("__id", DynValue.NewString(vf?.Id ?? string.Empty));

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
        }

        private void InitializeLibStub()
        {
            try
            {
                var libStub = new Table(_script);

                // __call metamethod: LibStub("Name") -> returns library table or nil
                var mt = new Table(_script);
                mt.Set("__call", DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && args[0].Type == DataType.String)
                    {
                        var name = args[0].String;
                        if (_libRegistry.TryGetValue(name, out var t)) return DynValue.NewTable(t);
                    }
                    return DynValue.Nil;
                }));

                // LibStub:NewLibrary(name, minor)
                libStub.Set("NewLibrary", DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && args[0].Type == DataType.String)
                    {
                        var name = args[0].String;
                        int minor = 0;
                        if (args.Count >= 2 && args[1].Type == DataType.Number) minor = (int)args[1].Number;

                        if (_libRegistry.TryGetValue(name, out var existing))
                        {
                            // If existing minor is >= requested, do nothing
                            var exMinorDyn = existing.Get("__minor");
                            if (exMinorDyn != null && exMinorDyn.Type == DataType.Number && (int)exMinorDyn.Number >= minor)
                            {
                                return DynValue.Nil;
                            }
                        }

                        var t = new Table(_script);
                        t.Set("__name", DynValue.NewString(name));
                        t.Set("__minor", DynValue.NewNumber(minor));
                        _libRegistry[name] = t;
                        return DynValue.NewTable(t);
                    }
                    return DynValue.Nil;
                }));

                // LibStub:GetLibrary(name, silent)
                libStub.Set("GetLibrary", DynValue.NewCallback((ctx, args) =>
                {
                    if (args.Count >= 1 && args[0].Type == DataType.String)
                    {
                        var name = args[0].String;
                        if (_libRegistry.TryGetValue(name, out var t)) return DynValue.NewTable(t);
                    }
                    return DynValue.Nil;
                }));

                libStub.MetaTable = mt;
                _script.Globals["LibStub"] = DynValue.NewTable(libStub);

                // Pre-register a minimal AceAddon-3.0 implementation
                try
                {
                    var ace = new Table(_script);
                    ace.Set("NewAddon", DynValue.NewCallback((ctx, args) =>
                    {
                        // Args: name [, ...mixins]
                        if (args.Count >= 1 && args[0].Type == DataType.String)
                        {
                            var name = args[0].String;
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
                    // expose the AceAddon library object
                    _libRegistry["AceAddon-3.0"] = ace;
                }
                catch { }
            }
            catch { }
        }

        public void RunScriptFromString(string code, string addonName, string? firstVarArg = null)
        {
            try
            {
                // Load the chunk and call it with (addonName, namespaceTable) like WoW does (local name, ns = ...)
                var func = _script.LoadString(code);
                Table ns;
                if (!_addonNamespaces.TryGetValue(addonName, out ns))
                {
                    ns = new Table(_script);
                    _addonNamespaces[addonName] = ns;
                }

                // Determine first vararg to pass: library files may need their own MAJOR name
                var firstArg = firstVarArg ?? addonName;

                // Call the loaded chunk with (firstArg, namespaceTable)
                _script.Call(func, DynValue.NewString(firstArg), DynValue.NewTable(ns));
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
