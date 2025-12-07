using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia;

namespace Flux
{
    public class LuaRunner
    {
        private Script _script;
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

            // Create script with no core modules for sandboxing
            _script = new Script(CoreModules.None);

            // Create saved variables table with metatable to detect writes
            InitializeSavedVariablesTable(null);

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
                    // Accept a color name string (e.g., "Red", "LightGray") or hex color (#RRGGBB) and apply a Brush
                    if (vf == null) return DynValue.Nil;
                    if (a.Count >= 1 && a[0].Type == DataType.String)
                    {
                        var colorName = a[0].String;
                        try
                        {
                            // Hex color #RRGGBB or #AARRGGBB
                            if (!string.IsNullOrEmpty(colorName) && colorName.StartsWith("#"))
                            {
                                try
                                {
                                    var col = Avalonia.Media.Color.Parse(colorName);
                                    vf.BackdropBrush = new SolidColorBrush(col);
                                    _frameManager?.UpdateVisual(vf);
                                }
                                catch { }
                            }
                            else
                            {
                                // Try to find a Brushes.<Name> property
                                var brushesType = typeof(Avalonia.Media.Brushes);
                                var prop = brushesType.GetProperty(colorName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
                                if (prop != null)
                                {
                                    var brush = prop.GetValue(null) as Avalonia.Media.IBrush;
                                    if (brush != null)
                                    {
                                        vf.BackdropBrush = brush;
                                        _frameManager?.UpdateVisual(vf);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
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
        }

        public void RunScriptFromString(string code, string addonName)
        {
            try
            {
                // Execute the code directly in the script's environment
                _script.DoString(code);
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
