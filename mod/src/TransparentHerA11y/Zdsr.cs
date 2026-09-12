using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace TransparentHerA11y
{
    /// <summary>
    /// 争渡读屏（ZDSR）语音后端。
    ///
    /// 争渡读屏官方给第三方程序提供了一组语音接口（ZDSRAPI）：32 位是
    /// ZDSRAPI.dll，64 位是 ZDSRAPI_x64.dll，导出四个函数 ——
    ///     InitTTS(type, channelName, keyDownInterrupt)
    ///     Speak(text, interrupt)
    ///     GetSpeakState()
    ///     StopSpeak()
    /// 本作是 64 位 Unity，所以用 ZDSRAPI_x64.dll。
    ///
    /// 两条实测结论（用 64 位接口 dll 在本机实跑验证，没有争渡在运行时也无副作用）：
    ///
    ///   1. **InitTTS 在争渡没运行时照样返回 0**。所以「初始化成功」不能当作
    ///      「能朗读」——真正的判据是 GetSpeakState()：
    ///          1 = 接口版本不匹配
    ///          2 = 争渡读屏没有运行
    ///          3 = 正在朗读
    ///          4 = 空闲（争渡在运行）
    ///      只有 3 / 4 才说明争渡真的在，这时候 Speak 才有人听。
    ///   2. 接口 dll 只依赖 kernel32 / user32（用 CreateToolhelp32Snapshot 找
    ///      争渡进程、EnumWindows 找它的窗口、SendMessageTimeoutW 把文本送过去），
    ///      不依赖自己被放在哪里，所以可以从任意目录加载 —— 这也正是官方文档
    ///      说的用法：「应用程序把接口 dll 和 ini 文件放入自己应用目录下调用即可」。
    ///      ZDSRAPI.ini 是可选的，里面的设置会覆盖 InitTTS 的参数。
    ///
    /// 因为 dll 平时躺在争渡自己的安装目录里（不在游戏目录），这里不能像
    /// nvdaControllerClient.dll 那样用 DllImport 静态绑定，只能
    /// LoadLibraryExW + GetProcAddress 动态取函数地址。
    ///
    /// 另外：本 mod **不重新分发** 争渡的接口 dll（那是争渡读屏的组件），
    /// 只从玩家自己装的争渡目录里加载。
    /// </summary>
    internal static class Zdsr
    {
        /// <summary>64 位游戏用 x64 接口，32 位游戏用 32 位接口。</summary>
        private static readonly string DllName = IntPtr.Size == 8 ? "ZDSRAPI_x64.dll" : "ZDSRAPI.dll";

        /// <summary>让 dll 自己的目录成为它依赖项的搜索起点。</summary>
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        /// <summary>争渡没在运行时，隔多久再问一次（玩家可能中途启动争渡）。</summary>
        private const float RetrySeconds = 10f;

        /// <summary>找不到 dll 时，隔多久再找一遍（玩家可能中途把文件拷进游戏目录）。</summary>
        private const float SearchRetrySeconds = 60f;

        // ================= Win32 =================

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        // ================= 接口函数 =================

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitTtsFn(int type, IntPtr channelName, int keyDownInterrupt);

        /// <summary>text 是 UTF-16（wchar_t*），由封送层负责转换与释放。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SpeakFn([MarshalAs(UnmanagedType.LPWStr)] string text, int interrupt);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetSpeakStateFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StopSpeakFn();

        private static IntPtr _module;
        private static InitTtsFn _initTts;
        private static SpeakFn _speak;
        private static GetSpeakStateFn _getState;
        private static StopSpeakFn _stop;

        private static bool _inited;         // InitTTS 是否已经成功调用过
        private static bool _fatal;          // 版本不匹配之类：本次游戏进程内不再试
        private static float _retryAt;       // 冷却到期时间（realtimeSinceStartup）
        private static bool _missingLogged;  // 「找不到 dll」只详细提醒一次
        private static int _failCount;       // 状态判断失败的次数（控制日志频率）
        private static int _channel = -1;    // 正在用的通道：0 读屏通道；1 独立通道

        /// <summary>独立通道的名字（type=1 时用）。</summary>
        private const string ChannelName = "TransparentHerA11y";

        /// <summary>
        /// 争渡明明开着、接口却说「没运行」时用来试探的那句话。
        /// 试探成功（Speak 返回 0）就会真的念出来，所以它得是一句像样的话。
        /// </summary>
        private const string ProbeText = "争渡读屏已连接到无障碍朗读模组。";

        /// <summary>已加载的接口 dll 路径（日志用）。</summary>
        public static string DllPath = "";

        /// <summary>最近一次失败的原因，供 Speech 汇总进日志。</summary>
        public static string LastError = "";

        /// <summary>最近一次状态查询的原文，日志用。</summary>
        public static string LastStateText = "";

        public static bool Loaded { get { return _module != IntPtr.Zero; } }

        // ================= 探测 =================

        /// <summary>
        /// 争渡接口现在能不能用。
        ///
        /// 判据**不是** InitTTS 的返回码 —— 实测争渡没运行时它照样返回 0。
        /// 而是 GetSpeakState（3 / 4 才算争渡在运行），再加一次试探朗读兜底。
        ///
        /// 通道要试两条：
        ///   type=0 读屏通道 —— 走争渡自己的语音与设置，最理想；但它要求
        ///                     争渡读屏本体在运行（接口文档里返回码 2 的意思是
        ///                     「争渡没有运行**或没有授权**」）。
        ///   type=1 独立通道 —— 争渡接口自己开的一条通道。实测有玩家机器上
        ///                     只跑着 ZDSRDaemon.exe（守护进程）而没有
        ///                     ZDSRMain_x64.exe（读屏本体），读屏通道一直是
        ///                     「没有运行」；独立通道是这种情况下唯一的机会。
        /// </summary>
        public static bool TryInit(ManualLogSource log)
        {
            if (_fatal) { LastError = "争渡读屏接口版本不匹配，本次游戏不再尝试"; return false; }
            if (UnityEngine.Time.realtimeSinceStartup < _retryAt) return false;

            if (_module == IntPtr.Zero && !Load(log)) return false;

            if (_inited)
            {
                // 已经初始化过：只复查状态，争渡中途退出/启动都能发现
                int st = State();
                if (st == 3 || st == 4) { LastError = ""; return true; }
                if (st == 1) { _fatal = true; LastError = "争渡读屏接口版本不匹配（" + LastStateText + "）"; return false; }
                _inited = false;   // 状态掉了，重新走一遍初始化
            }

            string why0, why1;
            if (TryChannel(0, null, log, out why0)) { _channel = 0; _inited = true; LastError = ""; return true; }
            if (TryChannel(1, ChannelName, log, out why1)) { _channel = 1; _inited = true; LastError = ""; return true; }

            _inited = false;
            _retryAt = UnityEngine.Time.realtimeSinceStartup + RetrySeconds;
            LastError = "读屏通道：" + why0 + "；独立通道：" + why1;

            if (_failCount <= 1 || _failCount % 10 == 0)
                log.LogWarning("[Speech] " + LastError + "。" + Diagnostics());
            _failCount++;
            return false;
        }

        /// <summary>
        /// 初始化一个通道，并判断「现在能不能真的用它说话」。
        /// why 里带回结论，供日志逐条列出。
        /// </summary>
        private static bool TryChannel(int type, string name, ManualLogSource log, out string why)
        {
            why = "";
            string tag = type == 0 ? "读屏通道" : "独立通道";

            int rc = InitChannel(type, name);
            if (rc != 0)
            {
                if (rc == 1) _fatal = true;
                why = "InitTTS=" + rc + "（" + InitText(rc) + "）";
                return false;
            }

            int st = State();
            if (st == 3 || st == 4)
            {
                if (type == 1)
                    log.LogWarning("[Speech] 读屏通道用不了，已改用争渡的**独立通道**（InitTTS type=1）—— "
                        + "这条通道不需要争渡读屏本体在运行，声音可能和读屏自己的语音不完全一样。");
                return true;
            }

            if (st == 1)
            {
                _fatal = true;
                why = "接口版本不匹配（" + LastStateText + "）";
                return false;
            }

            // 状态说「没有运行或没有授权」。别急着下结论，直接念一句试探文本：
            // 接口肯收下（返回 0）就说明这条通道是通的，声音也确实出得去。
            int rcSpeak = Speak(ProbeText, false);
            if (rcSpeak == 0)
            {
                log.LogWarning("[Speech] " + tag + "的状态是「" + StateText(st) + "」，"
                    + "但试探朗读返回 0（成功）—— 已按可用处理。"
                    + "如果这句试探文本听不到，把配置「语音后端」改成 SAPI 用系统语音。");
                return true;
            }

            why = StateText(st) + "，试探 Speak=" + rcSpeak;
            return false;
        }

        private static int InitChannel(int type, string name)
        {
            try
            {
                IntPtr p = IntPtr.Zero;
                if (type != 0 && !string.IsNullOrEmpty(name)) p = Marshal.StringToHGlobalUni(name);
                try { return _initTts(type, p, 0); }
                finally { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
            }
            catch (Exception e)
            {
                _fatal = true;
                LastError = "InitTTS 异常: " + e.Message;
                return -1;
            }
        }

        private static int State()
        {
            try
            {
                int st = _getState();
                LastStateText = "GetSpeakState 返回 " + st + "（" + StateText(st) + "）";
                return st;
            }
            catch (Exception e)
            {
                _fatal = true;
                LastError = "GetSpeakState 异常: " + e.Message;
                return -1;
            }
        }

        /// <summary>
        /// 失败时的那一长串诊断。三段：
        ///   1. 争渡安装目录里都有哪些进程（按路径判断，不看名字 —— 读屏本体的
        ///      进程名各版本不一定一样），带窗口标题
        ///   2. 有没有「读屏本体」进程；只看到守护进程就直接点出来
        ///   3. 接口目录里的 ZDSRAPI.ini 写了什么（它的设置会覆盖 InitTTS 的参数）
        /// </summary>
        private static string Diagnostics()
        {
            var sb = new StringBuilder();

            var all = new List<string>();
            var mains = new List<string>();
            int pathDenied = 0;
            string installDir = null;
            try { installDir = Path.GetDirectoryName(DllPath); } catch { }

            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    string name = null, path = "", title = "";
                    int pid = 0;
                    try { name = p.ProcessName; pid = p.Id; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    try { path = p.MainModule != null ? p.MainModule.FileName : ""; } catch { }
                    try { title = p.MainWindowTitle ?? ""; } catch { }

                    bool byName = IsReaderName(name);
                    bool byPath = installDir != null && path.Length > 0
                        && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);

                    if (!byName && !byPath) continue;

                    // 读不到路径的，基本都是提权运行的进程（高完整性级别）
                    if (path.Length == 0) pathDenied++;
                    all.Add(name + "(" + pid + (title.Length > 0 ? " 「" + title + "」" : "") + ")");

                    // 「读屏本体」：名字里带 main，或者装在争渡目录里、但不是
                    // 守护进程 / 之多云 / 升级器。只看到守护进程是最常见的情况。
                    string l = name.ToLowerInvariant();
                    bool daemonish = l.IndexOf("daemon") >= 0 || l.IndexOf("cloud") >= 0
                        || l.IndexOf("updat") >= 0 || l.IndexOf("helper") >= 0;
                    if (!daemonish && (l.IndexOf("main") >= 0 || (byPath && byName))) mains.Add(name);
                }
            }
            catch { }

            sb.Append(all.Count > 0
                ? "争渡目录里的进程：" + string.Join("、", all.ToArray())
                : "争渡目录里一个进程都没有（争渡没在运行）");

            if (mains.Count == 0 && all.Count > 0)
            {
                sb.Append("。**只看到守护进程，没看到读屏本体**（一般是 ZDSRMain_x64.exe）—— "
                    + "争渡读屏的「读屏通道」要求读屏本体在运行。请从开始菜单/快捷方式启动争渡读屏，"
                    + "确认它真的在给你读屏，再回到游戏");
            }
            else if (mains.Count > 0)
            {
                sb.Append("。读屏本体在运行：" + string.Join("、", mains.ToArray())
                    + " —— 那接口还报 2，只剩两种可能：① 没有授权（接口文档里返回码 2 就是这么写的）；"
                    + "② 权限不对等，见下");
            }

            // 提权运行是这里最容易踩的坑：接口靠「往争渡的窗口发消息」来握手，
            // 而 Windows 的 UIPI 会直接挡掉「低权限进程 → 高权限窗口」的消息，
            // 于是接口永远认为读屏不在运行。实测玩家的争渡就是以管理员身份跑的
            // （读不到它的进程路径就是证据）。
            if (pathDenied > 0)
            {
                sb.Append("。【重要】有 " + pathDenied + " 个争渡进程读不到可执行文件路径，"
                    + "这一般说明它们以**管理员身份**运行。游戏没提权时，接口发过去的握手消息"
                    + "会被 Windows 的 UIPI 挡掉，接口就只能报「没有运行」。"
                    + "**请试试用管理员身份运行游戏**（正式版要先以管理员身份启动 Steam 再启动游戏），"
                    + "或者把争渡读屏改成普通权限启动");
            }

            sb.Append("。ZDSRAPI.ini：" + IniSummary(installDir));
            return sb.ToString();
        }

        private static bool IsReaderName(string name)
        {
            string l = name.ToLowerInvariant();
            return l.IndexOf("zdsr", StringComparison.Ordinal) >= 0
                || l.StartsWith("zd", StringComparison.Ordinal)
                || l.IndexOf("nvda", StringComparison.Ordinal) >= 0
                || l.IndexOf("争渡", StringComparison.Ordinal) >= 0;
        }

        /// <summary>把接口目录里的 ZDSRAPI.ini 有效内容读出来（它的设置会覆盖 InitTTS 参数）。</summary>
        private static string IniSummary(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "（不知道接口目录）";
            string path = Path.Combine(dir, "ZDSRAPI.ini");
            try
            {
                if (!File.Exists(path)) return "（接口目录里没有这个文件，用默认值）";

                var parts = new List<string>();
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("[")) continue;
                    parts.Add(line);
                }
                return parts.Count > 0 ? string.Join("，", parts.ToArray()) : "（只有注释，等于默认值）";
            }
            catch (Exception e) { return "（读取失败：" + e.Message + "）"; }
        }

        // ================= 朗读 =================

        /// <summary>
        /// 朗读一段文本，返回接口原始返回码：0 成功，1 版本不匹配，2 争渡没有运行。
        /// 本进程内没加载成功时返回 -1。
        /// </summary>
        public static int Speak(string text, bool interrupt)
        {
            if (_speak == null || string.IsNullOrEmpty(text)) return -1;
            try
            {
                return _speak(text, interrupt ? 1 : 0);
            }
            catch (Exception e)
            {
                _fatal = true;
                LastError = "Speak 异常: " + e.Message;
                return -1;
            }
        }

        /// <summary>停止当前朗读。</summary>
        public static void Stop()
        {
            if (_stop == null) return;
            try { _stop(); } catch { }
        }

        /// <summary>
        /// 朗读失败后记一笔。返回码 2（争渡退出/没运行）只是冷却一会儿，
        /// 1（版本不匹配）则本次游戏不再尝试 —— 两种情况都会让 Speech 立刻
        /// 降级到后面的后端，不至于把整局游戏变成静音。
        /// </summary>
        public static void NoteFailure(int rc)
        {
            _inited = false;
            if (rc == 1) _fatal = true;
            _retryAt = UnityEngine.Time.realtimeSinceStartup + (rc == 1 ? 1e9f : RetrySeconds);
            LastError = "Speak 返回 " + rc + "（" + SpeakText(rc) + "）";
        }

        // ================= 找 dll =================

        private static bool Load(ManualLogSource log)
        {
            List<string> searched;
            string path = FindDllPath(out searched);

            if (path == null)
            {
                LastError = "没有找到 " + DllName;
                _retryAt = UnityEngine.Time.realtimeSinceStartup + SearchRetrySeconds;
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    log.LogInfo("[Speech] " + LastError + "（已找过：" + string.Join("、", searched.ToArray()) + "）"
                        + "。如果要用争渡读屏朗读，把该文件从争渡的安装目录复制到游戏根目录，"
                        + "或在本 mod 配置里填「争渡接口 DLL 路径」。");
                }
                return false;
            }

            IntPtr m;
            try { m = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH); }
            catch (Exception e) { LastError = "加载 " + path + " 失败: " + e.Message; return false; }

            if (m == IntPtr.Zero)
            {
                LastError = "加载 " + path + " 失败（Win32 错误码 " + Marshal.GetLastWin32Error() + "）";
                _retryAt = UnityEngine.Time.realtimeSinceStartup + SearchRetrySeconds;
                return false;
            }

            InitTtsFn i = Bind<InitTtsFn>(m, "InitTTS");
            SpeakFn s = Bind<SpeakFn>(m, "Speak");
            GetSpeakStateFn g = Bind<GetSpeakStateFn>(m, "GetSpeakState");
            StopSpeakFn t = Bind<StopSpeakFn>(m, "StopSpeak");

            if (i == null || s == null || g == null)
            {
                LastError = path + " 里缺少必要函数（InitTTS / Speak / GetSpeakState），不是争渡的接口 dll？";
                try { FreeLibrary(m); } catch { }
                _fatal = true;
                return false;
            }

            _module = m;
            _initTts = i;
            _speak = s;
            _getState = g;
            _stop = t;
            DllPath = path;
            log.LogInfo("[Speech] 已加载争渡读屏接口: " + path + "（" + FileVersion(path) + "）");
            return true;
        }

        /// <summary>接口 dll 的文件版本。出问题时这一行很有用 —— 各版本的返回码不一样。</summary>
        private static string FileVersion(string path)
        {
            try
            {
                System.Diagnostics.FileVersionInfo v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (v != null && !string.IsNullOrEmpty(v.FileVersion)) return "版本 " + v.FileVersion;
            }
            catch { }
            return "版本未知";
        }

        private static T Bind<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) return null;
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        /// <summary>
        /// 依次在「配置指定路径 → 游戏根目录 / 插件目录 → 争渡安装目录」里找接口 dll。
        /// searched 会带上找过的位置，方便玩家把日志发回来定位问题。
        /// </summary>
        private static string FindDllPath(out List<string> searched)
        {
            searched = new List<string>();

            // 1) 配置里指定的完整路径（争渡装在非默认目录时用）
            try
            {
                string cfg = Plugin.CfgZdsrDll != null ? Plugin.CfgZdsrDll.Value : null;
                if (!string.IsNullOrEmpty(cfg))
                {
                    cfg = cfg.Trim().Trim('"');
                    searched.Add("配置项");
                    if (File.Exists(cfg)) return cfg;
                    LastError = "配置的争渡接口路径不存在: " + cfg;
                }
            }
            catch { }

            // 2) 游戏自己的目录（官方文档推荐：dll 放进程序目录即可）
            foreach (string dir in GameDirs())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                searched.Add(dir);
                try
                {
                    string p = Path.Combine(dir, DllName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }

            // 3) 争渡读屏自己的安装目录（默认在 程序目录\zdsr\ 下）
            return FindInZdsrInstall(searched);
        }

        private static string[] GameDirs()
        {
            var list = new List<string>();
            try { if (!string.IsNullOrEmpty(Paths.GameRootPath)) list.Add(Paths.GameRootPath); } catch { }
            try { if (!string.IsNullOrEmpty(Paths.PluginPath)) list.Add(Paths.PluginPath); } catch { }
            try { if (!string.IsNullOrEmpty(Paths.PluginPath)) list.Add(Path.Combine(Paths.PluginPath, "TransparentHerA11y")); } catch { }
            try { if (!string.IsNullOrEmpty(Paths.BepInExRootPath)) list.Add(Paths.BepInExRootPath); } catch { }
            return list.ToArray();
        }

        /// <summary>
        /// 在争渡的安装根目录里找。已知的目录结构是
        ///     程序目录\zdsr\zdsr\      ← 商业版
        ///     程序目录\zdsr\zdsr_yth\  ← 青春版
        ///     程序目录\zdsr\zdcloud\   ← 之多云
        /// 所以先按已知的两个子目录名找，再在 zdsr\ 下浅层搜一遍；
        /// 最后还有一道兜底：各常见安装根目录下凡是名字里带 zdsr / 争渡
        /// 的目录都翻一遍 —— 公益版等版本的目录名不一定叫 zdsr / zdsr_yth。
        /// </summary>
        private static string FindInZdsrInstall(List<string> searched)
        {
            var roots = new List<string>();
            foreach (Environment.SpecialFolder f in new[]
                     { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                try
                {
                    string s = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(s)) roots.Add(Path.Combine(s, "zdsr"));
                }
                catch { }
            }

            string[] prefer = { Path.Combine("zdsr", DllName), Path.Combine("zdsr_yth", DllName), DllName };
            foreach (string root in roots)
            {
                foreach (string rel in prefer)
                {
                    string p = Path.Combine(root, rel);
                    searched.Add(p);
                    try { if (File.Exists(p)) return p; } catch { }
                }
            }

            foreach (string root in roots)
            {
                searched.Add(root + "\\**");
                string p = SearchShallow(root, DllName, 4);
                if (p != null) return p;
            }

            foreach (string baseDir in GuessRoots())
            {
                string[] subs;
                try { subs = Directory.GetDirectories(baseDir); } catch { continue; }

                foreach (string sub in subs)
                {
                    string name;
                    try { name = Path.GetFileName(sub); } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.IndexOf("zdsr", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("争渡", StringComparison.Ordinal) < 0) continue;

                    searched.Add(sub + "\\**");
                    string p = SearchShallow(sub, DllName, 3);
                    if (p != null) return p;
                }
            }

            return null;
        }

        /// <summary>可能放争渡的顶层目录：两个 Program Files、用户目录、各固定磁盘根。</summary>
        private static List<string> GuessRoots()
        {
            var list = new List<string>();
            foreach (Environment.SpecialFolder f in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.LocalApplicationData,
                         Environment.SpecialFolder.ApplicationData,
                         Environment.SpecialFolder.UserProfile
                     })
            {
                try
                {
                    string s = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s);
                }
                catch { }
            }

            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string r = d.RootDirectory.FullName;
                        if (!list.Contains(r)) list.Add(r);
                    }
                    catch { }
                }
            }
            catch { }

            return list;
        }

        private static string SearchShallow(string dir, string name, int depth)
        {
            if (depth < 0 || string.IsNullOrEmpty(dir)) return null;
            try
            {
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);

                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string p = SearchShallow(sub, name, depth - 1);
                    if (p != null) return p;
                }
            }
            catch { }
            return null;
        }

        // ================= 文案 =================

        private static string InitText(int rc)
        {
            switch (rc)
            {
                case 0: return "成功";
                case 1: return "接口版本不匹配";
                default: return "未知";
            }
        }

        private static string StateText(int st)
        {
            switch (st)
            {
                case 1: return "接口版本不匹配";
                case 2: return "争渡读屏没有运行或没有授权";
                case 3: return "正在朗读";
                case 4: return "空闲";
                default: return "未知";
            }
        }

        private static string SpeakText(int rc)
        {
            switch (rc)
            {
                case 0: return "成功";
                case 1: return "接口版本不匹配";
                case 2: return "争渡读屏没有运行或没有授权";
                default: return "未知";
            }
        }
    }
}
