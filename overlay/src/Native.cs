using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DshDesktopHud
{
    /// <summary>
    /// Win32 interop: locate / focus the DSH (Electron) main window, read its title,
    /// cursor position and the system DPI scale.
    /// </summary>
    internal static class Native
    {
        public const int SW_RESTORE = 9;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        private const string DshProcessName = "DeepSeek Harness";
        private const string ElectronClassPrefix = "Chrome_WidgetWin_";

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitTimeout = 0x00000102;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorNotFound = 1168;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static readonly Dictionary<uint, string> ProcessNameCache = new Dictionary<uint, string>();
        private static readonly object CacheLock = new object();
        private static EnumWindowsProc _enumProc; // keep a rooted delegate instance

        private static string ProcessNameOf(uint pid)
        {
            if (pid == 0) return "";
            lock (CacheLock)
            {
                string cached;
                if (ProcessNameCache.TryGetValue(pid, out cached)) return cached;
            }

            string name = "";
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    name = p.ProcessName;
                }
            }
            catch (Exception)
            {
                name = "";
            }

            lock (CacheLock)
            {
                ProcessNameCache[pid] = name;
            }
            return name;
        }

        private static string WindowText(IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return "";
                StringBuilder sb = new StringBuilder(len + 2);
                int got = GetWindowText(hWnd, sb, sb.Capacity);
                if (got <= 0) return "";
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string ClassName(IntPtr hWnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                int got = GetClassName(hWnd, sb, sb.Capacity);
                if (got <= 0) return "";
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Finds the DSH Electron main window: a visible Chrome_WidgetWin_* top-level
        /// window with a non-empty title, preferring the process named "DeepSeek Harness".
        /// </summary>
        public static IntPtr FindDshMainWindow()
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;

            if (_enumProc == null)
            {
                _enumProc = delegate(IntPtr hWnd, IntPtr lParam)
                {
                    return true;
                };
            }

            EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    string cls = ClassName(hWnd);
                    if (cls.Length == 0) return true;
                    if (!cls.StartsWith(ElectronClassPrefix, StringComparison.Ordinal)) return true;

                    string title = WindowText(hWnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    string proc = ProcessNameOf(pid);

                    int score = 0;
                    if (string.Equals(proc, DshProcessName, StringComparison.OrdinalIgnoreCase)) score += 100;
                    else if (proc.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0) score += 70;
                    if (title.IndexOf(DshProcessName, StringComparison.OrdinalIgnoreCase) >= 0) score += 20;

                    // First (top-most) eligible window wins ties: EnumWindows enumerates z-order.
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = hWnd;
                    }
                }
                catch (Exception)
                {
                }
                return true;
            };

            try
            {
                EnumWindows(callback, IntPtr.Zero);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
            return best;
        }

        /// <summary>
        /// Reads the DSH main window title and strips the trailing " — DeepSeek Harness"
        /// suffix. Returns null when the window is missing or the title has no title part.
        /// </summary>
        public static string GetDshWindowTitleDisplay()
        {
            try
            {
                IntPtr hWnd = FindDshMainWindow();
                if (hWnd == IntPtr.Zero) return null;
                string raw = WindowText(hWnd);
                return TitleUtil.CleanWindowTitle(raw);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Brings the DSH main window to the foreground; false when not found.</summary>
        public static bool FocusDshMainWindow()
        {
            try
            {
                IntPtr hWnd = FindDshMainWindow();
                if (hWnd == IntPtr.Zero) return false;

                if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

                // Clicking our own window may give us foreground rights; attach to the current
                // foreground thread anyway so SetForegroundWindow is not refused by the shell.
                IntPtr foreground = GetForegroundWindow();
                uint foregroundThread = 0;
                if (foreground != IntPtr.Zero)
                {
                    uint dummy;
                    foregroundThread = GetWindowThreadProcessId(foreground, out dummy);
                }
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                if (foregroundThread != 0 && foregroundThread != myThread)
                {
                    try
                    {
                        attached = AttachThreadInput(foregroundThread, myThread, true);
                    }
                    catch (Exception)
                    {
                        attached = false;
                    }
                }

                bool ok = false;
                try
                {
                    ok = SetForegroundWindow(hWnd);
                    BringWindowToTop(hWnd);
                }
                finally
                {
                    if (attached)
                    {
                        try
                        {
                            AttachThreadInput(foregroundThread, myThread, false);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the process is still alive (or when we cannot prove it is gone).
        /// Uses OpenProcess + WaitForSingleObject so an access-denied sandbox (which makes
        /// Process.GetProcessById throw "not running" for a live process) does not kill the HUD.
        /// </summary>
        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
                if (handle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ErrorInvalidParameter || err == ErrorNotFound)
                    {
                        Trace.Log("IsProcessAlive pid=" + pid + " OpenProcess err=" + err + " -> gone");
                        return false;
                    }
                    Trace.Log("IsProcessAlive pid=" + pid + " OpenProcess err=" + err + " -> cannot tell, treated alive");
                    return true;
                }

                uint wait = WaitForSingleObject(handle, 0);
                if (wait == WaitObject0)
                {
                    Trace.Log("IsProcessAlive pid=" + pid + " signaled (exited) -> gone");
                    return false;
                }
                if (wait == WaitTimeout)
                {
                    return true;
                }
                Trace.Log("IsProcessAlive pid=" + pid + " wait=" + wait + " -> cannot tell, treated alive");
                return true;
            }
            catch (Exception ex)
            {
                Trace.LogException("IsProcessAlive failed pid=" + pid, ex);
                return true; // never kill the HUD on an unreadable owner
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        CloseHandle(handle);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>Hides a window from the taskbar / alt-tab (used for our own HUD window).</summary>
        public static void MakeToolWindow(IntPtr hWnd)
        {
            try
            {
                int exStyle = GetWindowLong32(hWnd, GWL_EXSTYLE);
                int updated = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                if (updated != exStyle) SetWindowLong32(hWnd, GWL_EXSTYLE, updated);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>System DPI scale (144 dpi => 1.5). Falls back to 1.0.</summary>
        public static double GetDpiScale()
        {
            try
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    double scale = g.DpiX / 96.0;
                    if (scale <= 0.1) return 1.0;
                    return scale;
                }
            }
            catch (Exception)
            {
                return 1.0;
            }
        }
    }
}