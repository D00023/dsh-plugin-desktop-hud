using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace DshDesktopHud
{
    /// <summary>
    /// Named-pipe client. The Host plugin is the pipe server; the HUD connects, sends
    /// one hello line, then consumes JSON lines. Reconnect policy (PROTOCOL.md):
    /// 250 ms backoff; if the pipe has never connected within 90 s exit 0; if an
    /// established connection is lost and reconnection keeps failing for 60 s exit 0.
    /// </summary>
    internal sealed class PipeClient
    {
        private const string PipePathPrefix = @"\\.\pipe\";
        private const int ConnectTimeoutMs = 1000;
        private const int RetryDelayMs = 250;

        private readonly string _pipeName;
        private readonly double _dpi;
        private readonly TimeSpan _initialTimeout;
        private readonly TimeSpan _reconnectTimeout;
        private readonly object _writeLock = new object();

        private Thread _thread;
        private volatile bool _stop;
        private StreamWriter _writer;
        private bool _everConnected;
        private DateTime _lostUtc = DateTime.MinValue;
        private DateTime _startedUtc;
        private bool _fatalRaised;

        public event Action Connected;
        public event Action<Dictionary<string, object>> Message;
        public event Action Disconnected;
        public event Action Fatal;

        public PipeClient(string pipeArgument, double dpi)
        {
            _pipeName = NormalizePipeName(pipeArgument);
            _dpi = dpi;
            _initialTimeout = TimeSpan.FromSeconds(Env.Seconds("DSH_HUD_CONNECT_SECONDS", 90));
            _reconnectTimeout = TimeSpan.FromSeconds(Env.Seconds("DSH_HUD_RECONNECT_SECONDS", 60));
        }

        public bool HasTarget
        {
            get { return !string.IsNullOrEmpty(_pipeName); }
        }

        private static string NormalizePipeName(string raw)
        {
            if (raw == null) return null;
            string s = raw.Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith(PipePathPrefix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(PipePathPrefix.Length);
            if (s.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            s = s.Trim('\\');
            if (s.Length == 0) return null;
            return s;
        }

        public void Start()
        {
            if (!HasTarget) return;
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Name = "HUD-pipe";
            _thread = t;
            t.Start();
        }

        public void Stop()
        {
            Trace.Log("pipe stop requested");
            _stop = true;
            try
            {
                StreamWriter w;
                lock (_writeLock)
                {
                    w = _writer;
                    _writer = null;
                }
                if (w != null) w.Dispose();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Writes one JSON line if connected; silently drops it otherwise.</summary>
        public void SendLine(string json)
        {
            if (json == null) return;
            try
            {
                lock (_writeLock)
                {
                    StreamWriter w = _writer;
                    if (w == null) return;
                    w.Write(json);
                    w.Write('\n');
                    w.Flush();
                }
            }
            catch (Exception)
            {
            }
        }

        private void Run()
        {
            _startedUtc = DateTime.UtcNow;
            Trace.Log("pipe thread start name=[" + _pipeName + "] initialTimeout=" + _initialTimeout.TotalSeconds
                + "s reconnectTimeout=" + _reconnectTimeout.TotalSeconds + "s");
            while (!_stop)
            {
                RunCycle();
                if (_stop) return;

                DateTime now = DateTime.UtcNow;
                if (_everConnected && _lostUtc != DateTime.MinValue)
                {
                    if (now - _lostUtc >= _reconnectTimeout)
                    {
                        Trace.Log("reconnect window exhausted -> fatal");
                        RaiseFatal();
                        return;
                    }
                }
                else if (!_everConnected && now - _startedUtc >= _initialTimeout)
                {
                    Trace.Log("initial connect window exhausted -> fatal");
                    RaiseFatal();
                    return;
                }

                Thread.Sleep(RetryDelayMs);
            }
        }

        private void RunCycle()
        {
            bool cycleConnected = false;
            try
            {
                Trace.Log("connect attempt name=[" + _pipeName + "]");
                using (NamedPipeClientStream stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    stream.Connect(ConnectTimeoutMs);
                    cycleConnected = true;
                    _everConnected = true;
                    _lostUtc = DateTime.MinValue;
                    Trace.Log("connected to host pipe");

                    UTF8Encoding encoding = new UTF8Encoding(false);
                    StreamReader reader = new StreamReader(stream, encoding, true, 8192);
                    StreamWriter writer = new StreamWriter(stream, encoding, 8192);
                    writer.AutoFlush = true;

                    lock (_writeLock)
                    {
                        _writer = writer;
                    }

                    RaiseConnected();

                    SendLine("{\"t\":\"hello\",\"v\":1,\"dpi\":" + FormatDpi(_dpi) + "}");
                    Trace.Log("hello sent");

                    string line;
                    while (!_stop && (line = reader.ReadLine()) != null)
                    {
                        Trace.Log("rx line=[" + line + "]");
                        if (line.Length == 0) continue;
                        Dictionary<string, object> message = Json.ParseObjectOrNull(line);
                        if (message == null) continue; // malformed lines are ignored
                        RaiseMessage(message);
                    }
                    Trace.Log("host closed the pipe");
                }
            }
            catch (Exception ex)
            {
                // Pipe absent / closed / IO failure: retry silently, never show a dialog.
                Trace.LogException("pipe cycle failed", ex);
            }
            finally
            {
                lock (_writeLock)
                {
                    _writer = null;
                }
                if (cycleConnected && !_stop)
                {
                    _lostUtc = DateTime.UtcNow;
                    Trace.Log("disconnected -> will retry");
                    RaiseDisconnected();
                }
            }
        }

        private static string FormatDpi(double dpi)
        {
            if (dpi <= 0) return "1";
            return dpi.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void RaiseConnected()
        {
            try
            {
                Action handler = Connected;
                if (handler != null) handler();
            }
            catch (Exception)
            {
            }
        }

        private void RaiseDisconnected()
        {
            try
            {
                Action handler = Disconnected;
                if (handler != null) handler();
            }
            catch (Exception)
            {
            }
        }

        private void RaiseMessage(Dictionary<string, object> message)
        {
            try
            {
                Action<Dictionary<string, object>> handler = Message;
                if (handler != null) handler(message);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseFatal()
        {
            if (_fatalRaised) return;
            _fatalRaised = true;
            try
            {
                Action handler = Fatal;
                if (handler != null) handler();
            }
            catch (Exception)
            {
            }
        }
    }
}