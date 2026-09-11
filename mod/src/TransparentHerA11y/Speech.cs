using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace TransparentHerA11y
{
    /// <summary>
    /// 语音输出抽象层。按可用性依次挑选后端：
    ///
    ///   1. Tolk          —— 若 Tolk.dll 存在。一套接口覆盖
    ///                       NVDA / JAWS / SuperNova / ZoomText / System Access，
    ///                       并可用 SAPI 兜底，同时支持语音与盲文输出。
    ///   2. NVDA          —— nvdaControllerClient.dll，本项目实测可用。
    ///   3. SAPI          —— Windows 内置语音，无需任何读屏软件。
    ///
    /// 之所以自带 NVDA / SAPI 两级而不强依赖 Tolk：Tolk 官方不提供预编译
    /// 二进制，只有 MSVC 工程；而在只装了 NVDA 的机器上，JAWS / SuperNova /
    /// ZoomText 等分支根本无法验证。与其塞一份没测过的互操作代码，不如让
    /// 每一级都可独立工作，Tolk 作为「有则更好」的增强。
    ///
    /// 把 Tolk.dll 及 Tolk 的依赖 DLL（nvdaControllerClient64.dll、SAAPI64.dll）
    /// 放进 BepInEx\plugins\ 即可自动启用。
    /// </summary>
    internal static class Speech
    {
        public enum Backend { None, Tolk, Nvda, Sapi }

        private static Backend _backend = Backend.None;
        private static bool _inited;
        public static string LastError = "";

        public static Backend Current { get { return _backend; } }

        public static string BackendName
        {
            get
            {
                switch (_backend)
                {
                    case Backend.Tolk: return _tolkReader != null ? ("Tolk (" + _tolkReader + ")") : "Tolk";
                    case Backend.Nvda: return "NVDA";
                    case Backend.Sapi: return "SAPI";
                    default: return "无";
                }
            }
        }

        // ================= 初始化 =================

        public static void Init(ManualLogSource log)
        {
            if (_inited) return;
            _inited = true;

            if (TryTolk(log)) { _backend = Backend.Tolk; return; }
            if (TryNvda(log)) { _backend = Backend.Nvda; return; }
            if (TrySapi(log)) { _backend = Backend.Sapi; return; }

            _backend = Backend.None;
            log.LogWarning("没有可用的语音后端（Tolk / NVDA / SAPI 均不可用）。");
        }

        /// <summary>定期重试：NVDA 可能后启动，Tolk 也可能中途可用。</summary>
        public static bool Ready()
        {
            if (_backend != Backend.None) return true;
            if (UnityEngine.Time.realtimeSinceStartup < _nextRetry) return false;
            _nextRetry = UnityEngine.Time.realtimeSinceStartup + RetryInterval;

            _inited = false;
            Init(Plugin.Log);
            return _backend != Backend.None;
        }

        private static float _nextRetry;
        private const float RetryInterval = 10f;

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
                LastError = "Tolk 不可用: " + e.Message;
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
                LastError = "NVDA 不可用: " + e.Message;
                return false;
            }
        }

        // ================= SAPI =================

        // SPF_ASYNC 必须置位：SAPI 默认同步朗读，会阻塞 Unity 主线程导致游戏卡死
        private const int SPF_ASYNC = 1;
        private const int SPF_PURGEBEFORESPEAK = 2;

        private static object _sapi;

        private static bool TrySapi(ManualLogSource log)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t == null)
                {
                    LastError = "SAPI: 找不到 ProgID SAPI.SpVoice";
                    return false;
                }
                _sapi = Activator.CreateInstance(t);
                if (_sapi == null)
                {
                    LastError = "SAPI: 创建 SpVoice 失败";
                    return false;
                }
                log.LogInfo("语音后端: SAPI（未检测到读屏软件，使用系统语音）");
                return true;
            }
            catch (Exception e)
            {
                LastError = "SAPI 不可用: " + e.Message;
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

            // 后端不可用时定期重试（NVDA 可能后启动，或读屏中途被打开）
            if (_backend == Backend.None && !Ready()) return;

            try
            {
                switch (_backend)
                {
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
                LastError = "朗读失败: " + e.Message;
                _backend = Backend.None;
                _inited = false;
            }
        }

        public static void Stop()
        {
            if (_backend == Backend.None) return;
            try
            {
                switch (_backend)
                {
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
            try { if (_tolkLoaded) Tolk_Unload(); } catch { }
            _tolkLoaded = false;
        }
    }
}
