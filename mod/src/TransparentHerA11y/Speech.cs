using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace TransparentHerA11y
{
    /// <summary>
    /// 语音输出抽象层。按可用性依次挑选后端：
    ///
    ///   1. 争渡读屏 —— ZDSRAPI_x64.dll（Zdsr.cs）。国内玩家的常用读屏，
    ///                  它没有 NVDA 那套接口，只能走自己的 ZDSRAPI。
    ///   2. Tolk     —— 若 Tolk.dll 存在。一套接口覆盖 NVDA / JAWS / SuperNova /
    ///                  ZoomText / System Access，并可用 SAPI 兜底。
    ///   3. NVDA     —— nvdaControllerClient.dll，本项目实测可用。
    ///   4. SAPI     —— Windows 内置语音，无需任何读屏软件。
    ///
    /// 之所以自带 NVDA / SAPI 两级而不强依赖 Tolk：Tolk 官方不提供预编译
    /// 二进制，只有 MSVC 工程；而在只装了 NVDA 的机器上，JAWS / SuperNova /
    /// ZoomText 等分支根本无法验证。与其塞一份没测过的互操作代码，不如让
    /// 每一级都可独立工作，Tolk 作为「有则更好」的增强。
    ///
    /// 把 Tolk.dll 及 Tolk 的依赖 DLL（nvdaControllerClient64.dll、SAAPI64.dll）
    /// 放进 BepInEx\plugins\ 即可自动启用。
    ///
    /// 后端可以随时被换掉：某一个后端失败时立刻降级到下一个（不让整局游戏
    /// 变成静音），争渡 / NVDA 中途启动时又会被 Tick() 换回来。
    /// 配置「语音后端」可以强制指定某一个，用于排查问题。
    /// </summary>
    internal static class Speech
    {
        public enum Backend { None, Zdsr, Tolk, Nvda, Sapi }

        private static Backend _backend = Backend.None;
        private static bool _inited;
        private static ManualLogSource _log;
        private static float _nextTick;
        private const float TickInterval = 10f;

        public static string LastError = "";

        public static Backend Current { get { return _backend; } }

        public static string BackendName { get { return NameOf(_backend); } }

        public static string NameOf(Backend b)
        {
            switch (b)
            {
                case Backend.Zdsr: return "争渡读屏";
                case Backend.Tolk: return _tolkReader != null ? ("Tolk (" + _tolkReader + ")") : "Tolk";
                case Backend.Nvda: return "NVDA";
                case Backend.Sapi: return "SAPI";
                default: return "无";
            }
        }

        // ================= 初始化 =================

        public static void Init(ManualLogSource log)
        {
            if (_inited) return;
            _inited = true;
            _log = log;

            Backend[] order = Order(log);

            // 逐个试，把每个后端的结论都记下来。玩家机器上到底是哪一级不通，
            // 光看一行「语音不可用」是查不出来的 —— v0.5.8 就吃过这个亏。
            var reasons = new List<string>();
            foreach (Backend b in order)
            {
                if (TryBackend(b, log)) { _backend = b; return; }
                reasons.Add(NameOf(b) + " → " + (LastError.Length > 0 ? LastError : "不可用"));
            }

            _backend = Backend.None;
            LastError = string.Join("；", reasons.ToArray());
            log.LogWarning("没有可用的语音后端。逐个结论：" + LastError);
        }

        /// <summary>后端不可用时定期重试：读屏可能后启动，Tolk 也可能中途可用。</summary>
        public static bool Ready()
        {
            if (_backend != Backend.None) return true;
            if (UnityEngine.Time.realtimeSinceStartup < _nextRetry) return false;
            _nextRetry = UnityEngine.Time.realtimeSinceStartup + RetryInterval;

            _inited = false;
            Init(_log != null ? _log : Plugin.Log);
            return _backend != Backend.None;
        }

        private static float _nextRetry;
        private const float RetryInterval = 10f;

        /// <summary>
        /// 每 10 秒看一眼「有没有更合适的后端上线了」，比如玩家先开游戏、
        /// 后开争渡读屏（或后开 NVDA）。当前已经是最优先的后端时什么都不做。
        /// </summary>
        public static void Tick(ManualLogSource log)
        {
            if (_backend == Backend.None) return;   // 没后端时由 Ready() 负责重试
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + TickInterval;

            Backend[] order = Order(null);
            int cur = Array.IndexOf(order, _backend);
            if (cur <= 0) return;                   // 已经是链上最优先的（或强制模式里不在链上）

            for (int i = 0; i < cur; i++)
            {
                if (!TryBackend(order[i], log)) continue;

                string from = NameOf(_backend), to = NameOf(order[i]);
                _backend = order[i];
                log.LogInfo("语音后端切换: " + from + " → " + to);
                // 用新后端说一句：既告诉玩家换了后端，也等于当场听一次它响不响
                Speak("已切换到" + to + "。", true);
                return;
            }
        }

        /// <summary>
        /// 后端优先级。配置「语音后端」填了具体值时只试那一个。
        /// log 为 null 时不打印配置写错的告警（Tick 里每 10 秒一次会刷屏）。
        /// </summary>
        private static Backend[] Order(ManualLogSource log)
        {
            string cfg = Plugin.CfgSpeechBackend != null ? Plugin.CfgSpeechBackend.Value : "";
            string t = cfg != null ? cfg.Trim() : "";
            if (t.Length == 0) return Default;

            string u = t.ToUpperInvariant();
            if (u == "自动" || u == "AUTO") return Default;

            Backend forced;
            if (ParseBackend(u, out forced)) return new[] { forced };

            if (log != null)
                log.LogWarning("配置「语音后端」的值认不出来：" + t
                    + "（可用：自动 / 争渡 / Tolk / NVDA / SAPI），已按「自动」处理。");
            return Default;
        }

        private static readonly Backend[] Default =
            { Backend.Zdsr, Backend.Tolk, Backend.Nvda, Backend.Sapi };

        private static bool ParseBackend(string upper, out Backend b)
        {
            b = Backend.None;
            if (upper == null) return false;
            if (upper.Contains("争渡") || upper.Contains("ZDSR")) { b = Backend.Zdsr; return true; }
            if (upper.Contains("TOLK")) { b = Backend.Tolk; return true; }
            if (upper.Contains("NVDA")) { b = Backend.Nvda; return true; }
            if (upper.Contains("SAPI")) { b = Backend.Sapi; return true; }
            return false;
        }

        private static bool TryBackend(Backend b, ManualLogSource log)
        {
            switch (b)
            {
                case Backend.Zdsr: return TryZdsr(log);
                case Backend.Tolk: return TryTolk(log);
                case Backend.Nvda: return TryNvda(log);
                case Backend.Sapi: return TrySapi(log);
            }
            return false;
        }

        // ================= 争渡读屏 =================

        private static bool TryZdsr(ManualLogSource log)
        {
            try
            {
                if (!Zdsr.TryInit(log)) { LastError = Zdsr.LastError; return false; }
                log.LogInfo("语音后端: 争渡读屏（" + Zdsr.DllPath + "）");
                return true;
            }
            catch (Exception e)
            {
                LastError = "争渡不可用: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // ================= Tolk =================

        private const string TolkDll = "Tolk.dll";

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Load();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_IsLoaded();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Unload();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool trySAPI);

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr Tolk_DetectScreenReader();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_HasSpeech();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_HasBraille();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_Output(string str, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_Silence();

        private static bool _tolkLoaded;
        private static string _tolkReader;
        private static bool _tolkHasBraille;

        public static bool TolkHasBraille { get { return _tolkHasBraille; } }

        private static bool TryTolk(ManualLogSource log)
        {
            try
            {
                Tolk_TrySAPI(true);   // 无读屏时用 SAPI 兜底
                Tolk_Load();
                if (!Tolk_IsLoaded()) return false;

                _tolkLoaded = true;

                IntPtr p = Tolk_DetectScreenReader();
                _tolkReader = p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
                _tolkHasBraille = Tolk_HasBraille();

                log.LogInfo("语音后端: Tolk，读屏=" + (_tolkReader ?? "(SAPI 兜底)")
                    + (_tolkHasBraille ? "，支持盲文" : ""));
                return true;
            }
            catch (Exception e)
            {
                LastError = "Tolk 不可用: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // ================= NVDA =================

        private static bool TryNvda(ManualLogSource log)
        {
            try
            {
                if (!Nvda.DllOk) Nvda.Probe();
                if (!Nvda.DllOk)
                {
                    LastError = Nvda.LastError;
                    return false;
                }
                if (!Nvda.TestRunning())
                {
                    LastError = Nvda.LastError;
                    return false;
                }
                log.LogInfo("语音后端: NVDA");
                return true;
            }
            catch (Exception e)
            {
                LastError = "NVDA 不可用: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // ================= SAPI =================

        // SPF_ASYNC 必须置位：SAPI 默认同步朗读，会阻塞 Unity 主线程导致游戏卡死
        private const int SPF_ASYNC = 1;
        private const int SPF_PURGEBEFORESPEAK = 2;

        /// <summary>SpVoice 的 CLSID。ProgID 查不到时用它再试一次。</summary>
        private static readonly Guid SpVoiceClsid = new Guid("96749377-3391-11D2-9EE3-00C04F797396");

        private static object _sapi;

        private static bool TrySapi(ManualLogSource log)
        {
            try
            {
                Type t = null;
                string why = "";
                try { t = Type.GetTypeFromProgID("SAPI.SpVoice"); }
                catch (Exception e) { why = "GetTypeFromProgID: " + e.Message; }

                if (t == null)
                {
                    try { t = Type.GetTypeFromCLSID(SpVoiceClsid); }
                    catch (Exception e) { why = "GetTypeFromCLSID: " + e.Message; }
                }

                if (t == null)
                {
                    LastError = "SAPI: 找不到 SAPI.SpVoice" + (why.Length > 0 ? "（" + why + "）" : "");
                    return false;
                }

                _sapi = Activator.CreateInstance(t);
                if (_sapi == null)
                {
                    LastError = "SAPI: 创建 SpVoice 失败";
                    return false;
                }

                // 先读一个属性，验证「后期绑定」这条路真的通。Mono 对 IDispatch
                // 的支持如果不完整，会在这一行暴露出来，而不是等玩家需要朗读的
                // 时候静默失败 —— 那正是「机器上装了争渡但什么都没念」的典型症状：
                // 后端选了 SAPI，Speak 却一直抛异常，玩家只听到一片安静。
                object vol = _sapi.GetType().InvokeMember("Volume", BindingFlags.GetProperty, null, _sapi, null);

                log.LogInfo("语音后端: SAPI（没检测到读屏软件，用系统语音朗读，音量 " + vol + "）");
                return true;
            }
            catch (Exception e)
            {
                LastError = "SAPI 不可用: " + e.GetType().Name + ": " + e.Message;
                _sapi = null;
                return false;
            }
        }

        private static void SapiCall(string method, params object[] args)
        {
            if (_sapi == null) return;
            _sapi.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, _sapi, args);
        }

        // ================= 对外接口 =================

        public static void Speak(string text, bool interrupt)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 后端不可用时定期重试（读屏可能后启动，或读屏中途被打开）
            if (_backend == Backend.None && !Ready()) return;

            try
            {
                switch (_backend)
                {
                    case Backend.Zdsr:
                        {
                            int rc = Zdsr.Speak(text, interrupt);
                            if (rc != 0)
                            {
                                // 争渡退出、或接口版本不匹配：立刻降级，
                                // 不要让剩下的剧情一直念不出来。
                                Zdsr.NoteFailure(rc);
                                LogFailure(Zdsr.LastError);
                                _backend = Backend.None;
                                _inited = false;
                            }
                        }
                        break;

                    case Backend.Tolk:
                        // Tolk_Output 同时走语音与盲文
                        Tolk_Output(text, interrupt);
                        break;

                    case Backend.Nvda:
                        Nvda.Speak(text, interrupt);
                        break;

                    case Backend.Sapi:
                        {
                            int flags = SPF_ASYNC | (interrupt ? SPF_PURGEBEFORESPEAK : 0);
                            SapiCall("Speak", text, flags);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                LogFailure("朗读失败（" + BackendName + "）: " + e.GetType().Name + ": " + e.Message);
                _backend = Backend.None;
                _inited = false;
            }
        }

        private static string _lastFailMsg = "";
        private static float _lastFailAt;

        /// <summary>
        /// 朗读失败要写进日志（v0.5.8 把这些失败全吞了，玩家那边只表现为
        /// 「一片安静」，日志里毫无线索），但同一句话不重复刷屏。
        /// </summary>
        private static void LogFailure(string msg)
        {
            LastError = msg;
            if (_log == null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (msg == _lastFailMsg && now - _lastFailAt < 30f) return;
            _lastFailMsg = msg;
            _lastFailAt = now;
            _log.LogWarning("[Speech] " + msg + "，改用其它后端。");
        }

        public static void Stop()
        {
            if (_backend == Backend.None) return;
            try
            {
                switch (_backend)
                {
                    case Backend.Zdsr:
                        Zdsr.Stop();
                        break;
                    case Backend.Tolk:
                        Tolk_Silence();
                        break;
                    case Backend.Nvda:
                        Nvda.Stop();
                        break;
                    case Backend.Sapi:
                        // 用 SPF_PURGEBEFORESPEAK 朗读空串 = 清空队列
                        SapiCall("Speak", "", SPF_ASYNC | SPF_PURGEBEFORESPEAK);
                        break;
                }
            }
            catch
            {
                _backend = Backend.None;
                _inited = false;
            }
        }

        public static void Shutdown()
        {
            try { if (_backend == Backend.Zdsr) Zdsr.Stop(); } catch { }
            try { if (_tolkLoaded) Tolk_Unload(); } catch { }
            _tolkLoaded = false;
        }
    }
}
