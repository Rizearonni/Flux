using System;
using System.Linq;
using System.Reflection;

namespace Flux
{
    // Minimal starter for a direct KopiLua-backed runner.
    // Uses dynamic/reflection to call any DoString-like API on KopiLua.Lua.
    public static class KopiLuaDirectRunner
    {
        public static (bool, string) TryRunString(string code)
        {
            try
            {
                // Try to find KopiLua.Lua type in loaded assemblies
                var luaType = Type.GetType("KopiLua.Lua, KopiLua")
                              ?? AppDomain.CurrentDomain.GetAssemblies()
                                  .SelectMany(a => {
                                      try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                                  })
                                  .FirstOrDefault(t => string.Equals(t.FullName, "KopiLua.Lua", StringComparison.OrdinalIgnoreCase));

                if (luaType == null)
                    return (false, "KopiLua.Lua type not found in AppDomain.");

                // Try instance creation
                object luaInstance = null;
                try { luaInstance = Activator.CreateInstance(luaType); } catch (Exception ex) { luaInstance = null; }

                // Candidate method names (instance/static)
                var names = new[] { "DoString", "LdoString", "dostring", "luaL_dostring", "L_DoString", "Do" };

                foreach (var n in names)
                {
                    // instance method
                    var mi = luaType.GetMethod(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (mi != null && luaInstance != null)
                    {
                        try
                        {
                            var res = mi.Invoke(luaInstance, new object[] { code });
                            return (true, $"Executed {n} (instance) => " + (res?.ToString() ?? "(ok)"));
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

                    // static method
                    mi = luaType.GetMethod(n, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (mi != null)
                    {
                        try
                        {
                            var res = mi.Invoke(null, new object[] { code });
                            return (true, $"Executed {n} (static) => " + (res?.ToString() ?? "(ok)"));
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
                }

                // Fallback: look for luaL_newstate + luaL_dostring / luaL_loadstring+lua_pcall
                var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
                var newstate = types.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)).FirstOrDefault(m => string.Equals(m.Name, "luaL_newstate", StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, "NewState", StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, "CreateState", StringComparison.OrdinalIgnoreCase));
                var ldostring = types.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)).FirstOrDefault(m => string.Equals(m.Name, "luaL_dostring", StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, "LdoString", StringComparison.OrdinalIgnoreCase));

                if (newstate != null && ldostring != null)
                {
                    try
                    {
                        var state = newstate.Invoke(null, new object[] { });
                        var res = ldostring.Invoke(null, new object[] { state, code });
                        return (true, "Executed via luaL_dostring path => " + (res?.ToString() ?? "(ok)"));
                    }
                    catch (Exception ex)
                    {
                        return (false, "luaL path invocation error: " + ex.ToString());
                    }
                }

                return (false, "No runnable KopiLua entrypoint found.");
            }
            catch (Exception ex)
            {
                return (false, "Unexpected error: " + ex.ToString());
            }
        }
    }
}
