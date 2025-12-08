using System;
using System.Linq;
using System.Reflection;

namespace Flux
{
    // Lightweight prototype wrapper that attempts to find a KopiLua assembly at runtime
    // and execute a small chunk. This uses reflection so the project doesn't require
    // a compile-time dependency on KopiLua.
    public static class KopiLuaRunner
    {
        // Attempt to run a Lua string using any detected KopiLua-like API.
        // Returns (success, diagnosticText).
        public static (bool, string) TryRunString(string code)
        {
            try
            {
                // Try load assembly by common names
                var names = new[] { "KopiLua", "KopiLua.Lua", "KopiLuaNet", "Kopi" };
                Assembly asm = null;
                foreach (var n in names)
                {
                    try { asm = Assembly.Load(n); if (asm != null) break; } catch { }
                }

                // If Load by name failed, attempt to find any loaded assembly that looks like KopiLua
                if (asm == null)
                {
                    asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name.IndexOf("KopiLua", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (asm == null)
                {
                    return (false, "KopiLua assembly not found in AppDomain.");
                }

                // Try to find a type that looks like a Lua runtime (but if not, we'll search the whole assembly)
                var types = asm.GetTypes();

                // Search for any method that looks like it will execute a chunk. We'll accept:
                // - static or instance methods with "dostring"/"ldostring"/"do" in the name and a string parameter
                // - functions named luaL_dostring that take (IntPtr, string) after creating a state
                MethodInfo foundMethod = null;
                object foundTargetInstance = null;

                // Helper to test whether a MethodInfo is invokable with a single string argument
                bool AcceptsString(MethodInfo mi)
                {
                    var ps = mi.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType == typeof(string);
                }

                // 1) Search all types for a static or instance method that accepts a single string
                foreach (var t in types)
                {
                    try
                    {
                        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                        foreach (var m in methods)
                        {
                            var name = m.Name.ToLowerInvariant();
                            if (name.Contains("dostring") || name.Contains("ldostring") || name == "do" || name.Contains("dostring"))
                            {
                                if (AcceptsString(m))
                                {
                                    foundMethod = m;
                                    if (!m.IsStatic)
                                    {
                                        // attempt to create an instance using parameterless ctor
                                        try { foundTargetInstance = Activator.CreateInstance(t); } catch { foundTargetInstance = null; }
                                    }
                                    break;
                                }
                            }
                        }
                        if (foundMethod != null) break;
                    }
                    catch { }
                }

                // 2) If not found, try luaL_newstate + luaL_dostring style pattern
                if (foundMethod == null)
                {
                    MethodInfo newstate = null;
                    MethodInfo ldostring = null;
                    Type stateHolderType = null;

                    foreach (var t in types)
                    {
                        try
                        {
                            var mNew = t.GetMethod("luaL_newstate", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                            var mDo = t.GetMethod("luaL_dostring", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                            if (mNew != null && mDo != null)
                            {
                                newstate = mNew;
                                ldostring = mDo;
                                stateHolderType = t;
                                break;
                            }
                            // also support methods named NewState / CreateState
                            mNew = t.GetMethod("NewState", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("CreateState", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                            mDo = t.GetMethod("luaL_dostring", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("LdoString", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                            if (mNew != null && mDo != null)
                            {
                                newstate = mNew;
                                ldostring = mDo;
                                stateHolderType = t;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (newstate != null && ldostring != null)
                    {
                        try
                        {
                            // create state
                            var state = newstate.Invoke(null, new object[] { });
                            // try invoke do-string with (state, code)
                            var possibleParams = ldostring.GetParameters();
                            if (possibleParams.Length == 2 && possibleParams[1].ParameterType == typeof(string))
                            {
                                var res = ldostring.Invoke(null, new object[] { state, code });
                                return (true, "KopiLua executed (luaL_dostring path): " + (res?.ToString() ?? "(ok)"));
                            }
                        }
                        catch (TargetInvocationException tie)
                        {
                            return (false, "Invocation failed (luaL path): " + (tie.InnerException?.ToString() ?? tie.ToString()));
                        }
                        catch (Exception ex)
                        {
                            return (false, "Invocation error (luaL path): " + ex.ToString());
                        }
                    }

                    // If luaL_dostring wasn't present, try the two-step: luaL_loadstring / lua_load + lua_pcall
                    // Search for load + pcall methods and invoke them if available.
                    try
                    {
                        MethodInfo loadMethod = null;
                        MethodInfo pcallMethod = null;
                        // ensure we have a newstate creator available for the load+pcall path
                        if (newstate == null)
                        {
                            foreach (var t in types)
                            {
                                try
                                {
                                    var mNew2 = t.GetMethod("luaL_newstate", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("NewState", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("CreateState", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                                    if (mNew2 != null)
                                    {
                                        newstate = mNew2;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                        foreach (var t in types)
                        {
                            try
                            {
                                var mLoad = t.GetMethod("luaL_loadstring", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("luaL_loadbuffer", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("lua_load", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("LoadString", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                                var mPcall = t.GetMethod("lua_pcall", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("PCall", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase) ?? t.GetMethod("pcall", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                                if (mLoad != null && mPcall != null)
                                {
                                    loadMethod = mLoad;
                                    pcallMethod = mPcall;
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (loadMethod != null && pcallMethod != null)
                        {
                            try
                            {
                                var state = newstate.Invoke(null, new object[] { });
                                // invoke load
                                var loadRes = loadMethod.Invoke(null, new object[] { state, code });
                                // call pcall(state, nargs=0, nresults=-1, errfunc=0)
                                var pcallRes = pcallMethod.Invoke(null, new object[] { state, 0, -1, 0 });
                                return (true, "KopiLua executed (load+pcall path): loadResult=" + (loadRes?.ToString() ?? "(null)") + ", pcallResult=" + (pcallRes?.ToString() ?? "(null)"));
                            }
                            catch (TargetInvocationException tie)
                            {
                                return (false, "Invocation failed (load+pcall path): " + (tie.InnerException?.ToString() ?? tie.ToString()));
                            }
                            catch (Exception ex)
                            {
                                // If argument type conversions fail (e.g. CharPtr), provide extra diagnostics
                                try
                                {
                                    var sb = new System.Text.StringBuilder();
                                    sb.AppendLine("Invocation error (load+pcall path): " + ex.Message);
                                    sb.AppendLine("--- Candidate nested types related to 'Char' or 'Ptr' ---");
                                    foreach (var t in types.Where(tt => tt.Name.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0 || tt.Name.IndexOf("ptr", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                                    {
                                        try
                                        {
                                            sb.AppendLine("Type: " + t.FullName);
                                            foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                                            {
                                                sb.AppendLine("  ctor(" + string.Join(",", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                                            }
                                            sb.AppendLine("  Methods:");
                                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic).Take(8))
                                            {
                                                sb.AppendLine("    " + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                                            }
                                        }
                                        catch { }
                                    }
                                    return (false, sb.ToString());
                                }
                                catch
                                {
                                    return (false, "Invocation error (load+pcall path): " + ex.ToString());
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (foundMethod == null)
                {
                    // Provide a concise diagnostic listing of candidate types/methods to aid debugging
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("No suitable DoString-like method found in KopiLua assembly. Candidate types/methods:");
                        int typeCount = 0;
                        foreach (var t in types)
                        {
                            if (typeCount++ >= 12) break;
                            try
                            {
                                sb.AppendLine($"- Type: {t.FullName}");
                                var mlist = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                                int mcount = 0;
                                foreach (var m in mlist)
                                {
                                    if (mcount++ >= 8) break;
                                    sb.AppendLine($"    - {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                                }
                            }
                            catch { }
                        }
                        return (false, sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        return (false, "No suitable DoString-like method found in KopiLua assembly. (diagnostic failure: " + ex.Message + ")");
                    }
                }

                // Attempt invocation of the found method
                try
                {
                    var args = new object[] { code };
                    var result = foundMethod.Invoke(foundMethod.IsStatic ? null : foundTargetInstance, args);
                    return (true, "KopiLua executed: " + (result?.ToString() ?? "(ok)"));
                }
                catch (TargetInvocationException tie)
                {
                    return (false, "Invocation failed: " + (tie.InnerException?.ToString() ?? tie.ToString()));
                }
                catch (Exception ex)
                {
                    return (false, "Invocation error: " + ex.ToString());
                }
            }
            catch (Exception ex)
            {
                return (false, "Unexpected error probing KopiLua: " + ex.ToString());
            }
        }
    }
}
