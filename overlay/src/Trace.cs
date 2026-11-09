using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DshDesktopHud
{
    /// <summary>
    /// Opt-in diagnostic trace. Enabled only when the DSH_HUD_TRACE environment variable
    /// names a log file; all call sites are cheap no-ops otherwise. Never throws, never
    /// shows UI.
    ///
    /// The parent directory is created when possible; if the requested file cannot be
    /// written (e.g. a sandbox forbids that path) the trace falls back to the executable
    /// directory and then the current directory, so a debug environment always has a
    /// writable target.
    /// </summary>
    internal static class Trace
    {
        private static readonly object Sync = new object();
        private static bool _resolved;
        private static string _path;
        private static bool _fallbackResolved;
        private static string[] _fallbackPaths;

        private static string Resolve()
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    string p = Environment.GetEnvironmentVariable("DSH_HUD_TRACE");
                    if (!string.IsNullOrEmpty(p)) _path = p.Trim();
                }
                catch (Exception)
                {
                }
            }
            return _path;
        }

        /// <summary>Fallback targets tried when the requested trace file is not writable.</summary>
        private static string[] FallbackPaths()
        {
            if (_fallbackResolved) return _fallbackPaths;
            _fallbackResolved = true;
            string[] candidates = new string[3];
            int count = 0;
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir)) candidates[count++] = Path.Combine(dir, "hud-trace.log");
            }
            catch (Exception)
            {
            }
            try
            {
                candidates[count++] = Path.Combine(Environment.CurrentDirectory, "hud-trace.log");
            }
            catch (Exception)
            {
            }
            candidates[count++] = Path.Combine(Path.GetTempPath(), "dsh-desktop-hud-trace.log");
            _fallbackPaths = candidates;
            return _fallbackPaths;
        }

        public static void Log(string message)
        {
            try
            {
                if (Resolve() == null) return;
                string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " [T" + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + "] "
                    + message + Environment.NewLine;
                lock (Sync)
                {
                    if (TryAppend(_path, line)) return;
                    string[] fallbacks = FallbackPaths();
                    for (int i = 0; i < fallbacks.Length; i++)
                    {
                        string candidate = fallbacks[i];
                        if (candidate == null) continue;
                        if (string.Equals(candidate, _path, StringComparison.OrdinalIgnoreCase)) continue;
                        if (TryAppend(candidate, line))
                        {
                            _path = candidate;
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool TryAppend(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line, new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void LogException(string context, Exception ex)
        {
            if (ex == null)
            {
                Log(context + ": <null exception>");
                return;
            }
            Log(context + ": " + ex.GetType().FullName + ": " + ex.Message);
        }
    }
}