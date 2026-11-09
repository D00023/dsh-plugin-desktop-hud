using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace DshDesktopHud
{
    /// <summary>
    /// File-based state transport (primary). The Host plugin atomically rewrites one JSON
    /// state file; the HUD watches it (FileSystemWatcher) and also polls every 400 ms.
    /// Liveness is judged by the file mtime: a heartbeat older than the stale limit, or a
    /// file that disappears after it was seen, exits the HUD with code 0.
    /// </summary>
    internal sealed class StateFileClient : IDisposable
    {
        private const int PollMilliseconds = 400;

        private readonly string _path;
        private readonly double _staleSeconds;
        private readonly double _missingSeconds;
        private readonly double _disappearSeconds;
        private readonly ManualResetEvent _wake = new ManualResetEvent(false);

        private Thread _thread;
        private volatile bool _stop;
        private FileSystemWatcher _watcher;
        private DateTime _startUtc;
        private DateTime _lastMtimeUtc = DateTime.MinValue;
        private DateTime _disappearedUtc = DateTime.MinValue;
        private bool _sawFile;
        private bool _fatalRaised;

        public event Action<Dictionary<string, object>> StateChanged;
        public event Action Fatal;

        public StateFileClient(string path, double staleSeconds, double missingSeconds, double disappearSeconds)
        {
            _path = path == null ? "" : path;
            _staleSeconds = staleSeconds;
            _missingSeconds = missingSeconds;
            _disappearSeconds = disappearSeconds;
        }

        public void Start()
        {
            if (_path.Length == 0)
            {
                Trace.Log("state file path is empty -> fatal");
                RaiseFatal();
                return;
            }
            _startUtc = DateTime.UtcNow;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "hud-state-file";
            _thread.Start();
        }

        private void Run()
        {
            Trace.Log("state file thread start path=[" + _path + "] staleSeconds=" + _staleSeconds
                + " missingSeconds=" + _missingSeconds);
            SetupWatcher();
            while (!_stop)
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    Trace.LogException("state file poll failed", ex);
                }
                if (_stop || _fatalRaised) break;
                try
                {
                    _wake.WaitOne(PollMilliseconds);
                }
                catch (Exception)
                {
                }
            }
            TeardownWatcher();
            Trace.Log("state file thread stop");
        }

        private void Poll()
        {
            DateTime now = DateTime.UtcNow;

            bool exists = false;
            DateTime mtime = DateTime.MinValue;
            try
            {
                if (File.Exists(_path))
                {
                    exists = true;
                    mtime = File.GetLastWriteTimeUtc(_path);
                }
            }
            catch (Exception ex)
            {
                Trace.LogException("state file stat failed", ex);
                return;
            }

            if (!exists)
            {
                if (_sawFile)
                {
                    // A correct Host replace is atomic (rename over the old file), but a brief
                    // delete/create window is tolerated so a non-atomic writer cannot kill us.
                    if (_disappearedUtc == DateTime.MinValue)
                    {
                        _disappearedUtc = now;
                        Trace.Log("state file disappeared (grace " + _disappearSeconds.ToString("0.0") + "s)");
                        return;
                    }
                    double goneAge = (now - _disappearedUtc).TotalSeconds;
                    if (goneAge >= _disappearSeconds)
                    {
                        // The Host deletes the file when it shuts down: that is a clean exit.
                        Trace.Log("state file deleted after first read (gone " + goneAge.ToString("0.0") + "s) -> exit");
                        RaiseFatal();
                    }
                    return;
                }
                double missingAge = (now - _startUtc).TotalSeconds;
                if (missingAge >= _missingSeconds)
                {
                    Trace.Log("state file never appeared after " + missingAge.ToString("0.0") + "s -> exit");
                    RaiseFatal();
                    return;
                }
                return;
            }

            _sawFile = true;
            _disappearedUtc = DateTime.MinValue;

            // A Host "rename over" replace can momentarily expose a zero/epoch timestamp to a
            // racing stat (File.GetLastWriteTimeUtc returns the FILETIME epoch). Never treat
            // that as real staleness: skip this poll and retry on the next one.
            if (mtime.Year < 2000)
            {
                Trace.Log("state file mtime not usable yet (year=" + mtime.Year + ") -> skip poll");
                return;
            }

            double staleAge = (now - mtime).TotalSeconds;
            if (staleAge >= _staleSeconds)
            {
                Trace.Log("state file stale age=" + staleAge.ToString("0.0") + "s -> exit");
                RaiseFatal();
                return;
            }

            if (mtime <= _lastMtimeUtc) return; // no new heartbeat yet

            string text;
            try
            {
                // Share the file with the Host's atomic replace: ask for full sharing so a
                // concurrent rename/delete can never make our read fail with a sharing violation.
                using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    using (StreamReader reader = new StreamReader(fs, new UTF8Encoding(false), true))
                    {
                        text = reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                // A half-written file is expected now and then: keep the previous state.
                Trace.LogException("state file read failed (partial write, ignored)", ex);
                return;
            }

            Dictionary<string, object> obj = ParseMessage(text);
            if (obj == null)
            {
                Trace.Log("state file parse failed (ignored) length=" + text.Length);
                _lastMtimeUtc = mtime;
                return;
            }

            bool first = _lastMtimeUtc == DateTime.MinValue;
            _lastMtimeUtc = mtime;
            Trace.Log((first ? "state file: first read" : "state file: read")
                + " mtime=" + mtime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                + " bytes=" + text.Length
                + " mode=[" + SafeGet(obj, "mode") + "] title=[" + SafeGet(obj, "title") + "]");
            RaiseChanged(obj);
        }

        private static string SafeGet(Dictionary<string, object> obj, string key)
        {
            try
            {
                string v = Json.GetString(obj, key);
                return v == null ? "" : v;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Parse the state file text as one JSON object. Tolerates a trailing newline (the Host
        /// writes one) and, defensively, any extra bytes after the last closing brace.
        /// </summary>
        private static Dictionary<string, object> ParseMessage(string text)
        {
            if (text == null) return null;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return null;

            int end = trimmed.LastIndexOf('}');
            if (end < 0) return Json.ParseObjectOrNull(trimmed);
            if (end + 1 < trimmed.Length)
            {
                Dictionary<string, object> tolerant = Json.ParseObjectOrNull(trimmed.Substring(0, end + 1));
                if (tolerant != null) return tolerant;
            }
            return Json.ParseObjectOrNull(trimmed);
        }

        private void SetupWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                string file = Path.GetFileName(_path);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;

                FileSystemWatcher watcher = new FileSystemWatcher(dir, file);
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                    | NotifyFilters.Size | NotifyFilters.CreationTime;
                watcher.Changed += delegate { Wake(); };
                watcher.Created += delegate { Wake(); };
                watcher.Renamed += delegate { Wake(); };
                watcher.Deleted += delegate { Wake(); };
                watcher.Error += delegate { Wake(); };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
                Trace.Log("state file watcher on dir=[" + dir + "]");
            }
            catch (Exception ex)
            {
                Trace.LogException("state file watcher setup failed (polling only)", ex);
            }
        }

        private void TeardownWatcher()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch (Exception)
            {
            }
        }

        private void Wake()
        {
            try
            {
                _wake.Set();
            }
            catch (Exception)
            {
            }
        }

        private void RaiseChanged(Dictionary<string, object> obj)
        {
            Action<Dictionary<string, object>> handler = StateChanged;
            if (handler == null) return;
            try
            {
                handler(obj);
            }
            catch (Exception ex)
            {
                Trace.LogException("state file change handler failed", ex);
            }
        }

        private void RaiseFatal()
        {
            if (_fatalRaised) return;
            _fatalRaised = true;
            Action handler = Fatal;
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Trace.LogException("state file fatal handler failed", ex);
            }
        }

        public void Dispose()
        {
            _stop = true;
            Wake();
            TeardownWatcher();
            Trace.Log("state file client disposed");
        }
    }
}