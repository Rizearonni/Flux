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
