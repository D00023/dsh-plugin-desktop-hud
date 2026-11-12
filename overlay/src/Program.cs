using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace DshDesktopHud
{
    internal static class Program
    {
        /// <summary>
        /// Exit code used when a HUD for the same state file is already running.
        /// hud-bridge.js treats it as "do not respawn", so the user never sees two cards.
        /// </summary>
        private const int DuplicateInstanceExitCode = 42;

        /// <summary>Kept alive for the whole process lifetime; the OS releases it on exit.</summary>
        private static Mutex singleInstance;

        /// <summary>One mutex per state file, so separate DSH profiles can still have their own card.</summary>
        private static string InstanceMutexName(string stateFile)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(stateFile.Trim().ToLowerInvariant()));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < 8; i++) builder.Append(hash[i].ToString("x2"));
                return "Local\\dsh-desktop-hud-" + builder.ToString();
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string[] safeArgs = args == null ? new string[0] : args;
            Trace.Log("main start argv=[" + string.Join(", ", safeArgs) + "]");

            CliOptions options = CliOptions.Parse(safeArgs);
            Trace.Log("options pipe=[" + (options.Pipe == null ? "" : options.Pipe) + "] stateFile=["
                + (options.StateFile == null ? "" : options.StateFile) + "] ownerPid=" + options.OwnerPid
                + " demo=" + options.Demo + " frozen=" + options.DemoFrozen + " demoState=" + options.DemoState);

            // Demo runs are dev tooling and may legitimately coexist with the live card.
            if (!string.IsNullOrEmpty(options.StateFile) && !options.Demo && !options.DemoMulti)
            {
                string mutexName = InstanceMutexName(options.StateFile);
                bool createdNew;
                singleInstance = new Mutex(true, mutexName, out createdNew);
                if (!createdNew)
                {
                    Trace.Log("another HUD instance already owns this state file; exit(" + DuplicateInstanceExitCode + ")");
                    return DuplicateInstanceExitCode;
                }
                Trace.Log("single-instance mutex acquired [" + mutexName + "]");
            }

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