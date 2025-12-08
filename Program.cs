using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace Flux
{
    internal static class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                // Quick prototype check for KopiLua availability before launching UI
                try
                {
                    var (ok, msg) = KopiLuaRunner.TryRunString("return 1+1");
                    Console.WriteLine("[KopiLuaProbe] " + (ok ? "available" : "missing") + " - " + msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[KopiLuaProbe] probe failed: " + ex.Message);
                }

                BuildAvaloniaApp()
                    .StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("[Unhandled Exception] " + ex.ToString()); } catch { }
                try { System.IO.File.WriteAllText("run_unhandled_exception.txt", ex.ToString()); } catch { }
                try { Environment.ExitCode = 1; } catch { }
                // rethrow so calling runners still observe the crash after we've logged details
                throw;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();
    }
}
