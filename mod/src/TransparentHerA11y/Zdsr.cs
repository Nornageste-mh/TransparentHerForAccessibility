using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
        /// 争渡接口现在能不能用：dll 能加载，且争渡读屏正在运行。
        /// 每次都重新问一次状态，所以争渡中途启动、中途退出都能被发现。
        /// </summary>
        public static bool TryInit(ManualLogSource log)
        {
            if (_fatal) { LastError = "争渡读屏接口版本不匹配，本次游戏不再尝试"; return false; }
            if (UnityEngine.Time.realtimeSinceStartup < _retryAt) return false;

            if (_module == IntPtr.Zero && !Load(log)) return false;

            if (!_inited)
            {
                int rc;
                try { rc = _initTts(0, IntPtr.Zero, 0); }   // 0 = 走读屏通道（用争渡自己的语音与设置）
                catch (Exception e)
                {
                    _fatal = true;
                    LastError = "InitTTS 异常: " + e.Message;
                    return false;
                }

                if (rc != 0)
                {
                    if (rc == 1) _fatal = true;             // 版本不匹配：没救了
                    LastError = "InitTTS 返回 " + rc + "（" + InitText(rc) + "）";
                    _retryAt = UnityEngine.Time.realtimeSinceStartup + RetrySeconds;
                    return false;
                }
                _inited = true;
            }

            int st;
            try { st = _getState(); }
            catch (Exception e)
            {
                _fatal = true;
                LastError = "GetSpeakState 异常: " + e.Message;
                return false;
            }

            LastStateText = "GetSpeakState 返回 " + st + "（" + StateText(st) + "）";

            if (st == 3 || st == 4) { LastError = ""; return true; }

            if (st == 1)
            {
                _fatal = true;
                LastError = "争渡读屏接口版本不匹配（" + LastStateText + "）";
                return false;
            }

            if (st == 2)
            {
                // 接口说「争渡没运行」。但**别急着下结论**：v0.6.0 preview 的实机日志里，
                // 玩家明明开着争渡，GetSpeakState() 照样返回 2。
                // 所以这里再补一刀：直接念一句试探文本，看接口收不收（收下返回 0）。
                // 收下就说明通道是通的 —— 那多半只是接口的「找读屏」那一环没认出来。
                int rcProbe = Speak(ProbeText, false);
                if (rcProbe == 0)
                {
                    _inited = true;
                    LastError = "";
                    log.LogWarning("[Speech] 争渡接口报告「没有运行」（GetSpeakState=2），"
                        + "但 Speak 一句试探文本返回 0（成功）—— 已按「争渡可用」处理，"
                        + "这句试探文本如果能听到，就是它念的。"
                        + "若实际没有声音，把配置「语音后端」改成 SAPI 即可改用系统语音。");
                    return true;
                }

                _inited = false;                      // 争渡起来之后重新初始化一次
                _retryAt = UnityEngine.Time.realtimeSinceStartup + RetrySeconds;
                LastError = "争渡读屏没有运行（" + LastStateText + "，试探 Speak=" + rcProbe + "）";

                if (_failCount <= 1 || _failCount % 10 == 0)
                    log.LogWarning("[Speech] " + LastError + "。" + ReaderProcesses());
                _failCount++;

                return false;
            }

            _retryAt = UnityEngine.Time.realtimeSinceStartup + RetrySeconds;
            LastError = LastStateText;
            if (_failCount <= 1 || _failCount % 10 == 0)
                log.LogWarning("[Speech] 争渡接口状态异常：" + LastStateText + "。" + ReaderProcesses());
            _failCount++;
            return false;
        }

        /// <summary>
        /// 把「当前进程里有没有看起来像读屏的进程」写进日志。
        /// 这行是给人看的：如果这里明明列出了争渡的进程，而接口仍说「没运行」，
        /// 就能确定问题出在接口自己的识别环节，而不是玩家没开读屏。
        /// </summary>
        private static string ReaderProcesses()
        {
            var found = new List<string>();
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    string n = null;
                    try { n = p.ProcessName; } catch { }
                    if (string.IsNullOrEmpty(n)) continue;

                    string l = n.ToLowerInvariant();
                    if (l.IndexOf("zdsr", StringComparison.Ordinal) < 0
                        && !l.StartsWith("zd", StringComparison.Ordinal)
                        && l.IndexOf("nvda", StringComparison.Ordinal) < 0
                        && l.IndexOf("争渡", StringComparison.Ordinal) < 0) continue;

                    string path = "";
                    try { path = p.MainModule != null ? p.MainModule.FileName : ""; } catch { }
                    found.Add(n + "(" + p.Id + (path.Length > 0 ? " " + path : "") + ")");
                }
            }
            catch { }

            return found.Count > 0
                ? "当前进程里像读屏的有：" + string.Join("、", found.ToArray())
                : "当前进程里没看到像读屏的进程（名字里带 zdsr / zd / nvda 的一个都没有）";
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
            log.LogInfo("[Speech] 已加载争渡读屏接口: " + path);
            return true;
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
                case 2: return "争渡读屏没有运行";
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
                case 2: return "争渡读屏没有运行";
                default: return "未知";
            }
        }
    }
}
