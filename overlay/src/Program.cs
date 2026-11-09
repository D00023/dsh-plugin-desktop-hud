using System;
using System.Windows;

namespace DshDesktopHud
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string[] safeArgs = args == null ? new string[0] : args;
            Trace.Log("main start argv=[" + string.Join(", ", safeArgs) + "]");

            CliOptions options = CliOptions.Parse(safeArgs);
            Trace.Log("options pipe=[" + (options.Pipe == null ? "" : options.Pipe) + "] stateFile=["
                + (options.StateFile == null ? "" : options.StateFile) + "] ownerPid=" + options.OwnerPid
                + " demo=" + options.Demo + " frozen=" + options.DemoFrozen + " demoState=" + options.DemoState);

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                // Never surface a modal error dialog from a notification HUD.
                Trace.LogException("dispatcher unhandled", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Trace.LogException("appdomain unhandled", e.ExceptionObject as Exception);
            };

            HudWindow window;
            try
            {
                window = new HudWindow(options);
                Trace.Log("window constructed");
                window.Show();
                Trace.Log("window shown");
                window.StartCore();
                Trace.Log("StartCore returned");
            }
            catch (Exception ex)
            {
                Trace.LogException("startup failed", ex);
                return 1;
            }

            int code = app.Run();
            Trace.Log("app.Run returned " + code);
            return code;
        }
    }
}