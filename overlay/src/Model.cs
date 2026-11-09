using System;
using System.Collections.Generic;
using System.Globalization;

namespace DshDesktopHud
{
    internal enum HudMode
    {
        Idle = 0,
        Running = 1,
        Attention = 2,
        Question = 3,
        Done = 4,
        Error = 5
    }

    /// <summary>One row in the expanded task list (v3 transport schema).</summary>
    internal sealed class HudTask
    {
        public string Session = "";
        public string Title = "";
        public string Detail = "";
        public HudMode Status = HudMode.Running;
        public long Since;
    }

    /// <summary>One logical HUD state as sent by the Host plugin.</summary>
    internal sealed class HudState
    {
        public HudMode Mode;
        public string Title = "";
        public string Detail = "";
        public string Session = "";
        public long Since;

        /// <summary>
        /// v3 task list. Null means "old v2 message" and the HUD keeps the single-card
        /// rendering; an empty list is the Host saying "no tasks right now" (idle pill).
        /// </summary>
        public List<HudTask> Tasks;

        public HudState()
        {
        }

        public HudState(HudMode mode, string title, string detail)
        {
            Mode = mode;
            Title = title == null ? "" : title;
            Detail = detail == null ? "" : detail;
        }

        public static bool TryParseMode(string s, out HudMode mode)
        {
            mode = HudMode.Idle;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "idle": mode = HudMode.Idle; return true;
                case "running": mode = HudMode.Running; return true;
                case "attention": mode = HudMode.Attention; return true;
                case "question": mode = HudMode.Question; return true;
                case "done": mode = HudMode.Done; return true;
                case "error": mode = HudMode.Error; return true;
                default: return false;
            }
        }

        /// <summary>Default second-line label for each mode.</summary>
        public static string LabelFor(HudMode mode)
        {
            switch (mode)
            {
                case HudMode.Running: return "执行中";
                case HudMode.Attention: return "需要你授权";
                case HudMode.Question: return "需要你回答";
                case HudMode.Done: return "任务完成";
                case HudMode.Error: return "出错了";
                default: return "空闲";
            }
        }

        /// <summary>
        /// Parses one Host "state" message. Returns null only for a null message; v3 says an
        /// unknown mode falls back to idle, and a malformed "tasks" list is ignored row by row.
        /// </summary>
        public static HudState FromMessage(Dictionary<string, object> msg)
        {
            if (msg == null) return null;

            HudMode mode;
            if (!TryParseMode(Json.GetString(msg, "mode"), out mode)) mode = HudMode.Idle;

            HudState s = new HudState();
            s.Mode = mode;

            string title = Json.GetString(msg, "title");
            s.Title = title == null ? "" : title;

            string detail = Json.GetString(msg, "detail");
            s.Detail = detail == null ? "" : detail;

            string session = Json.GetString(msg, "session");
            s.Session = session == null ? "" : session;

            double since;
            if (Json.TryGetDouble(msg, "since", out since))
            {
                if (since > 0 && since < 1e15) s.Since = (long)since;
            }

            // v3 "tasks": only honoured when the message declares v>=3. Missing/2 = single-task legacy
// (PROTOCOL.md: the HUD renders the collapsed card only and ignores any tasks array).
            double version;
            bool multiTask = false;
            if (Json.TryGetDouble(msg, "v", out version) && version >= 3.0) multiTask = true;
            List<object> rawTasks = multiTask ? Json.GetArray(msg, "tasks") : null;
            if (rawTasks != null)
            {
                List<HudTask> tasks = new List<HudTask>();
                for (int i = 0; i < rawTasks.Count; i++)
                {
                    Dictionary<string, object> item = rawTasks[i] as Dictionary<string, object>;
                    if (item == null) continue; // malformed row: skip, keep the rest
                    HudTask task = new HudTask();

                    string taskSession = Json.GetString(item, "session");
                    task.Session = taskSession == null ? "" : taskSession;

                    string taskTitle = Json.GetString(item, "title");
                    task.Title = taskTitle == null ? "" : taskTitle;

                    string taskDetail = Json.GetString(item, "detail");
                    task.Detail = taskDetail == null ? "" : taskDetail;

                    // Statuses outside the enumeration are tolerated as "running" (PROTOCOL.md).
                    HudMode status;
                    if (!TryParseMode(Json.GetString(item, "status"), out status)) status = HudMode.Running;
                    task.Status = status;

                    double taskSince;
                    if (Json.TryGetDouble(item, "since", out taskSince))
                    {
                        if (taskSince > 0 && taskSince < 1e15) task.Since = (long)taskSince;
                    }
                    tasks.Add(task);
                }
                s.Tasks = tasks;
            }
            return s;
        }

        /// <summary>Demo fixture for a mode (titles mirror the approved mock-up).</summary>
        public static HudState Sample(HudMode mode)
        {
            switch (mode)
            {
                case HudMode.Running:
                    return new HudState(HudMode.Running, "搭建 GitHub 连接器服务", "执行中");
                case HudMode.Attention:
                    return new HudState(HudMode.Attention, "搭建 GitHub 连接器服务", "需要你授权");
                case HudMode.Question:
                    return new HudState(HudMode.Question, "检查 llama.cpp 安装情况", "需要你回答");
                case HudMode.Done:
                    return new HudState(HudMode.Done, "任务完成", "任务完成");
                case HudMode.Error:
                    return new HudState(HudMode.Error, "搭建 GitHub 连接器服务", "出错了");
                default:
                    return new HudState(HudMode.Idle, "", "");
            }
        }
    }

    /// <summary>One frame of the --demo-multi tour: a state plus the requested expand mode.</summary>
    internal sealed class MultiDemoScene
    {
        public HudState State;
        public bool Expanded;

        public MultiDemoScene(HudState state, bool expanded)
        {
            State = state;
            Expanded = expanded;
        }
    }

    /// <summary>Sample task lists for --demo-multi (titles mirror the approved mock-up).</summary>
    internal static class MultiDemo
    {
        private static HudTask Task(string session, string title, HudMode status, string detail)
        {
            HudTask t = new HudTask();
            t.Session = session;
            t.Title = title;
            t.Status = status;
            t.Detail = detail;
            return t;
        }

        private static HudState State(HudMode mode, string title, string detail, HudTask[] tasks)
        {
            HudState s = new HudState(mode, title, detail);
            s.Tasks = new List<HudTask>(tasks);
            return s;
        }

        public static List<MultiDemoScene> Scenes()
        {
            HudTask[] three = new HudTask[]
            {
                Task("s-hud", "桌面常驻任务提示窗口", HudMode.Running, "执行中"),
                Task("s-github", "GitHub 连接器服务搭建", HudMode.Running, "执行中"),
                Task("s-browser", "浏览器自动化排查", HudMode.Attention, "需要你授权")
            };

            HudTask[] six = new HudTask[]
            {
                Task("s-hud", "桌面常驻任务提示窗口", HudMode.Running, "执行中"),
                Task("s-github", "GitHub 连接器服务搭建", HudMode.Running, "执行中"),
                Task("s-browser", "浏览器自动化排查", HudMode.Question, "需要你回答"),
                Task("s-weekly", "周报整理", HudMode.Running, "执行中"),
                Task("s-llama", "检查 llama.cpp 安装情况", HudMode.Done, "任务完成"),
                Task("s-deploy", "搭建 GitHub 连接器服务", HudMode.Error, "出错了")
            };

            HudTask[] attentionFirst = new HudTask[]
            {
                Task("s-browser", "浏览器自动化排查", HudMode.Attention, "需要你授权"),
                Task("s-github", "GitHub 连接器服务搭建", HudMode.Running, "执行中"),
                Task("s-hud", "桌面常驻任务提示窗口", HudMode.Running, "执行中")
            };

            HudTask[] doneAndError = new HudTask[]
            {
                Task("s-llama", "检查 llama.cpp 安装情况", HudMode.Done, "任务完成"),
                Task("s-deploy", "搭建 GitHub 连接器服务", HudMode.Error, "出错了"),
                Task("s-weekly", "周报整理", HudMode.Running, "执行中")
            };

            List<MultiDemoScene> scenes = new List<MultiDemoScene>();
            scenes.Add(new MultiDemoScene(State(HudMode.Running, "桌面常驻任务提示窗口", "执行中", three), false));
            scenes.Add(new MultiDemoScene(State(HudMode.Running, "桌面常驻任务提示窗口", "执行中", three), true));
            scenes.Add(new MultiDemoScene(State(HudMode.Running, "桌面常驻任务提示窗口", "执行中", six), true));
            scenes.Add(new MultiDemoScene(State(HudMode.Attention, "浏览器自动化排查", "需要你授权", attentionFirst), true));
            scenes.Add(new MultiDemoScene(State(HudMode.Done, "任务完成", "任务完成", doneAndError), true));
            return scenes;
        }
    }

    /// <summary>Command line options. Unknown arguments are ignored.</summary>
    internal sealed class CliOptions
    {
        public string Pipe;
        public string StateFile;
        public int OwnerPid;
        public bool Demo;
        public bool DemoFrozen;
        public bool DemoMulti;
        public bool StartExpanded;
        public HudMode DemoState = HudMode.Idle;

        public static CliOptions Parse(string[] args)
        {
            CliOptions o = new CliOptions();
            if (args == null) return o;

            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i];
                if (raw == null) continue;

                string key = raw;
                string val = null;
                int eq = raw.IndexOf('=');
                if (eq > 0)
                {
                    key = raw.Substring(0, eq);
                    val = raw.Substring(eq + 1);
                }

                switch (key.Trim().ToLowerInvariant())
                {
                    case "--pipe":
                        if (val == null) val = TakeValue(args, ref i);
                        if (!string.IsNullOrEmpty(val)) o.Pipe = val;
                        break;

                    case "--state-file":
                        if (val == null) val = TakeValue(args, ref i);
                        if (!string.IsNullOrEmpty(val)) o.StateFile = val;
                        break;

                    case "--owner-pid":
                        if (val == null) val = TakeValue(args, ref i);
                        {
                            int pid;
                            if (val != null && int.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0)
                                o.OwnerPid = pid;
                        }
                        break;

                    case "--demo":
                        o.Demo = true;
                        break;

                    case "--demo-multi":
                        o.DemoMulti = true;
                        break;

                    case "--expanded":
                        o.StartExpanded = true;
                        break;

                    case "--state":
                        if (val == null) val = TakeValue(args, ref i);
                        {
                            HudMode m;
                            if (HudState.TryParseMode(val, out m))
                            {
                                o.Demo = true;
                                o.DemoFrozen = true;
                                o.DemoState = m;
                            }
                        }
                        break;
                }
            }
            return o;
        }

        private static string TakeValue(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return null;
            string next = args[i + 1];
            if (next == null) return null;
            if (next.StartsWith("--", StringComparison.Ordinal)) return null;
            i++;
            return next;
        }
    }

    /// <summary>
    /// Internal test hooks: env var overrides for timers that the protocol fixes for
    /// production use. Defaults are exactly the values in PROTOCOL.md / UI-SPEC.md.
    /// </summary>
    internal static class Env
    {
        public static double Seconds(string name, double fallback)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(v)) return fallback;
                double d;
                if (double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0)
                    return d;
            }
            catch (Exception)
            {
            }
            return fallback;
        }
    }

    internal static class TitleUtil
    {
        public const string Placeholder = "DeepSeek Harness";

        /// <summary>
        /// Turns a raw DSH main-window title such as
        /// "检查 llama.cpp 安装情况 — DeepSeek Harness" into "检查 llama.cpp 安装情况".
        /// Returns null when nothing useful remains.
        /// </summary>
        public static string CleanWindowTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string t = raw.Trim();

            string[] suffixes = new string[]
            {
                " \u2014 DeepSeek Harness", // em dash, as written by DSH
                " - DeepSeek Harness",      // hyphen fallback
                " \u2013 DeepSeek Harness"  // en dash fallback
            };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (t.EndsWith(suffixes[i], StringComparison.Ordinal))
                {
                    t = t.Substring(0, t.Length - suffixes[i].Length).Trim();
                    break;
                }
            }

            if (t.Length == 0) return null;
            if (string.Equals(t, Placeholder, StringComparison.OrdinalIgnoreCase)) return null;
            return t;
        }
    }
}