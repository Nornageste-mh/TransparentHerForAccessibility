using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TransparentHerA11y
{
    /// <summary>
    /// 菜单 / 存档 / 设置等界面的键盘导航与朗读。
    ///
    /// 游戏原生的 UI 几乎只能鼠标操作（大量 IPointerEnterHandler，未设
    /// firstSelectedGameObject，也没有 Navigation 配置）。本模块在运行时
    /// 扫描当前激活 Canvas 下的所有 Selectable，自己维护焦点顺序：
    ///
    ///   Tab          进入 / 退出导航模式
    ///   上 / 下      上一项 / 下一项
    ///   左 / 右      调整滑条（其它控件无作用）
    ///   回车 / 空格  激活（按钮点击、开关切换、输入框聚焦）
    ///   Home / End   第一项 / 最后一项
    ///
    /// === 设计约束（v1.5 重写，全部是崩溃修复）===
    ///
    /// 1) 【绝不定时自动重扫】
    ///    v1.4 曾在激活按钮后安排 0.35 秒后重扫界面。而「开始游戏」「读档」
    ///    这类按钮会立刻触发 SceneManager.LoadSceneAsync，场景加载通常快于
    ///    0.35 秒，重扫恰好落在场景销毁/激活的瞬间 —— 对正在销毁的
    ///    Selectable 调用 Resources.FindObjectsOfTypeAll 并访问其 rect/scene
    ///    会触发「无任何 C# 异常」的原生崩溃。
    ///    实测表现：鼠标进入游戏一切正常，纯键盘进入必崩。
    ///    现在改为激活后直接退出导航模式，需要时由用户按 Tab 重新扫描。
    ///
    /// 2) 【不修改 EventSystem.sendNavigationEvents】
    ///    那是全局状态；场景切换后若不还原，新场景的 UI 提交会一直失效。
    ///    现在完全不碰它。游戏自身从不调用 SetSelectedGameObject（已核对
    ///    反编译源码），所以不存在「Unity 自动提交」与「我们直接 Invoke」
    ///    重复触发的问题。
    ///
    /// 3) 【场景切换强制复位】
    ///    主动记录 activeScene.handle，一旦变化就立刻退出导航模式、清空
    ///    控件引用，并在随后的一段时间内拒绝扫描。
    /// </summary>
    internal static class UiNav
    {
        private static bool _active;
        private static readonly List<Selectable> Items = new List<Selectable>();
        private static int _index;

        // 场景切换防护
        private static int _lastSceneHandle = int.MinValue;
        private static float _sceneChangedAt = float.NegativeInfinity;
        private const float SceneSettleSeconds = 1.5f;

        public static bool Active { get { return _active; } }

        // ================= 进入 / 退出 =================

        public static void Toggle()
        {
            if (_active) ExitInternal(true);
            else Enter();
        }

        private static void Enter()
        {
            if (!SceneStable())
            {
                Speech.Speak("场景正在切换，请稍候再试。", true);
                return;
            }

            Scan();
            if (Items.Count == 0)
            {
                Speech.Speak("当前界面上没有可操作的项目。", true);
                return;
            }

            _active = true;
            _index = 0;
            Speech.Speak("导航模式，共 " + Items.Count + " 项。", true);
            Announce();
        }

        private static void ExitInternal(bool announce)
        {
            _active = false;
            Items.Clear();
            _index = 0;
            if (announce)
            {
                try { Speech.Speak("已退出导航模式。", true); } catch { }
            }
        }

        /// <summary>场景是否处于「可以安全扫描」的状态。</summary>
        private static bool SceneStable()
        {
            try
            {
                Scene sc = SceneManager.GetActiveScene();
                if (!sc.isLoaded) return false;
                return Time.realtimeSinceStartup - _sceneChangedAt >= SceneSettleSeconds;
            }
            catch { return false; }
        }

        // ================= 扫描 =================

        private static void Scan()
        {
            Items.Clear();
            if (!SceneStable()) return;

            try
            {
                Selectable[] all = Resources.FindObjectsOfTypeAll<Selectable>();
                for (int i = 0; i < all.Length; i++)
                {
                    Selectable s = all[i];
                    if (s == null) continue;                        // Unity 伪空：已销毁对象在此拦下
                    if (!s.isActiveAndEnabled) continue;
                    if (!s.gameObject.scene.IsValid()) continue;    // 排除预制体资源

                    RectTransform rt = s.transform as RectTransform;
                    if (rt == null) continue;
                    if (rt.rect.width < 1f || rt.rect.height < 1f) continue;

                    CanvasGroup cg = s.GetComponentInParent<CanvasGroup>();
                    if (cg != null && cg.alpha < 0.05f) continue;

                    Items.Add(s);
                }
                SortByPosition();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("扫描界面控件失败: " + e.Message);
                Items.Clear();
            }
        }

        /// <summary>按屏幕位置排序：先上后下，同一行内先左后右。</summary>
        private static void SortByPosition()
        {
            Items.Sort((a, b) =>
            {
                if (a == null || b == null) return 0;
                float ya = ((RectTransform)a.transform).position.y;
                float yb = ((RectTransform)b.transform).position.y;
                if (Mathf.Abs(ya - yb) > 24f) return yb.CompareTo(ya);
                float xa = ((RectTransform)a.transform).position.x;
                float xb = ((RectTransform)b.transform).position.x;
                return xa.CompareTo(xb);
            });
        }

        // ================= 描述 =================

        private static string TextOf(Selectable s)
        {
            try
            {
                TextMeshProUGUI tmp = s.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                    return tmp.text.Replace("\n", " ").Trim();
            }
            catch { }
            return s.gameObject.name;
        }

        private static string Describe(Selectable s)
        {
            var sb = new StringBuilder();
            sb.Append(TextOf(s));

            Toggle t = s as Toggle;
            Slider sl = s as Slider;
            TMP_InputField inf = s as TMP_InputField;

            if (t != null) sb.Append("，开关，").Append(t.isOn ? "开" : "关");
            else if (sl != null)
                sb.Append("，滑条，").Append(Mathf.RoundToInt(sl.value)).Append(" / ")
                  .Append(Mathf.RoundToInt(sl.maxValue));
            else if (inf != null)
            {
                sb.Append("，输入框");
                if (!string.IsNullOrEmpty(inf.text)) sb.Append("，当前内容 ").Append(inf.text);
            }
            else if (s is Button) sb.Append("，按钮");
            else sb.Append("，").Append(s.GetType().Name);

            if (!s.interactable) sb.Append("，不可用");
            return sb.ToString();
        }

        private static void Announce()
        {
            if (!_active || Items.Count == 0) return;
            _index = Mathf.Clamp(_index, 0, Items.Count - 1);
            Selectable s = Items[_index];
            if (s == null) { ExitInternal(false); return; }

            Speech.Speak(Describe(s) + "。" + (_index + 1) + " / " + Items.Count, true);
        }

        // ================= 操作 =================

        private static void Activate()
        {
            if (!_active || Items.Count == 0) return;
            Selectable s = Items[_index];
            if (s == null) { ExitInternal(false); return; }

            if (!s.interactable)
            {
                Speech.Speak("该项当前不可用。", true);
                return;
            }

            try
            {
                TMP_InputField inf = s as TMP_InputField;
                if (inf != null)
                {
                    // 输入框是唯一必须动 EventSystem 的场景：不选中就没法打字
                    EventSystem es = EventSystem.current;
                    if (es != null) es.SetSelectedGameObject(inf.gameObject);
                    inf.ActivateInputField();
                    ExitInternal(false);
                    Speech.Speak("已进入输入框，直接打字即可。按 Tab 返回导航。", true);
                    return;
                }

                Toggle t = s as Toggle;
                if (t != null)
                {
                    t.isOn = !t.isOn;
                    Speech.Speak(t.isOn ? "开" : "关", false);
                    // 开关也可能改变界面甚至触发场景切换，一律退出导航模式
                    ExitInternal(false);
                    Speech.Speak("按 Tab 重新扫描界面。", false);
                    return;
                }

                Slider sl = s as Slider;
                if (sl != null)
                {
                    Speech.Speak("滑条请用左右方向键调整。", true);
                    return;
                }

                Button b = s as Button;
                if (b != null)
                {
                    // 关键顺序：先退出导航模式并清空引用，再触发点击。
                    // 「开始游戏」「读档」会立刻 LoadSceneAsync，
                    // 若此刻仍持有旧场景的控件引用就会出问题。
                    string label = TextOf(s);
                    ExitInternal(false);
                    Speech.Speak("已激活 " + label, false);
                    b.onClick.Invoke();
                    return;
                }

                ExitInternal(false);
                ExecuteEvents.Execute(s.gameObject, new BaseEventData(EventSystem.current),
                    ExecuteEvents.submitHandler);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("激活控件失败: " + e.Message);
            }
        }

        private static void Adjust(float dir)
        {
            if (Items.Count == 0) return;
            Slider sl = Items[_index] as Slider;
            if (sl == null) return;
            try
            {
                float step = (sl.maxValue - sl.minValue) / 20f;
                if (sl.wholeNumbers) step = Mathf.Max(1f, Mathf.Round(step));
                sl.value = Mathf.Clamp(sl.value + dir * step, sl.minValue, sl.maxValue);
                Speech.Speak(Mathf.RoundToInt(sl.value) + " / " + Mathf.RoundToInt(sl.maxValue), false);
            }
            catch (Exception e) { Plugin.Log.LogError("调整滑条失败: " + e.Message); }
        }

        // ================= 每帧 =================

        public static void Update()
        {
            // ---- 场景切换防护：必须放在最前面 ----
            try
            {
                Scene sc = SceneManager.GetActiveScene();
                if (sc.handle != _lastSceneHandle)
                {
                    _lastSceneHandle = sc.handle;
                    _sceneChangedAt = Time.realtimeSinceStartup;
                    if (_active || Items.Count > 0) ExitInternal(false);
                    return;
                }
            }
            catch { }

            if (Input.GetKeyDown(KeyCode.Tab)) { Toggle(); return; }
            if (!_active) return;
            if (Items.Count == 0) { ExitInternal(false); return; }

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                _index = (_index - 1 + Items.Count) % Items.Count;
                Announce();
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                _index = (_index + 1) % Items.Count;
                Announce();
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Adjust(-1f);
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Adjust(1f);
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
                     || Input.GetKeyDown(KeyCode.Space))
                Activate();
            else if (Input.GetKeyDown(KeyCode.Home)) { _index = 0; Announce(); }
            else if (Input.GetKeyDown(KeyCode.End)) { _index = Items.Count - 1; Announce(); }
        }
    }
}
