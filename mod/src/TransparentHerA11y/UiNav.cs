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
    ///   Tab          进入 / 退出导航模式
    ///   上 / 下      上一项 / 下一项
    ///   左 / 右      调整滑条
    ///   回车 / 空格  激活（按钮点击、开关切换、输入框聚焦）
    ///   Home / End   第一项 / 最后一项
    ///
    /// === 设计约束（每条都对应一次实测故障）===
    ///
    /// A) 【不按屏幕位置简单排序，必须先按渲染层级】
    ///    游戏的确认弹窗是「覆盖层」：LoadSlotUI.OnSaveSlotClicked 里
    ///    直接 confirmLoadPanel.SetActive(true)，而它后面的存档槽按钮
    ///    仍然全部 active。只按屏幕坐标排序的话，弹窗按钮会混在一堆底层
    ///    按钮中间，读屏用户根本定位不到「是 / 否」。
    ///    现在排序键为：Canvas.sortingOrder 降序 → 面板兄弟序号降序
    ///    → 屏幕 Y 降序 → 屏幕 X 升序，
    ///    即「最上层面板的控件排在最前」，同面板内再按位置排。
    ///
    /// B) 【绝不定时重扫 + 场景切换必须复位】
    ///    v1.4 曾在激活按钮后 0.35 秒重扫界面。「开始游戏」「读档」会立刻
    ///    SceneManager.LoadSceneAsync，重扫恰好落在场景销毁/激活瞬间，
    ///    对正在销毁的 Selectable 调用 FindObjectsOfTypeAll 并访问其
    ///    rect/scene 会触发无 C# 异常的原生崩溃。
    ///    现在：激活后只安排「下一帧」重扫（此刻旧场景仍完整存活，
    ///    距真正卸载还有几十帧），且必须通过场景稳定门禁。
    ///
    /// C) 【不修改 EventSystem.sendNavigationEvents】
    ///    全局状态，跨场景不还原会让新场景 UI 提交失效。已核对反编译源码：
    ///    游戏自身从不调用 SetSelectedGameObject，所以不会与我们的直接
    ///    Invoke 重复触发。
    ///
    /// D) 【扫描只由显式按键或激活后下一帧触发，且必须过 SceneStable】
    ///
    /// E) 【不做尺寸与 CanvasGroup 过滤】
    ///    曾跳过 rect < 1x1 或父级 CanvasGroup.alpha < 0.05 的控件，
    ///    但 UIManager.OpenUI 会把面板 alpha 从 0 淡入到 1，刚弹出的面板
    ///    会被误杀。改由 isActiveAndEnabled 判定即可 —— UIManager 关闭
    ///    面板时是 SetActive(false)，已经足够。
    /// </summary>
    internal static class UiNav
    {
        private static bool _active;
        private static readonly List<Selectable> Items = new List<Selectable>();
        private static int _index;
        private static int _pendingRescanFrame = -1;

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
            _pendingRescanFrame = -1;
            if (announce)
            {
                try { Speech.Speak("已退出导航模式。", true); } catch { }
            }
        }

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
                    if (s == null) continue;                       // Unity 伪空：已销毁对象在此拦下
                    if (!s.isActiveAndEnabled) continue;
                    if (!s.gameObject.scene.IsValid()) continue;   // 排除预制体资源
                    if (!(s.transform is RectTransform)) continue; // 只处理 UI
                    Items.Add(s);
                }
                SortByRenderOrder();
                if (Plugin.Log != null)
                    Plugin.Log.LogInfo("[UiNav] 扫描到 " + Items.Count + " 个可操作项。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("扫描界面控件失败: " + e.Message);
                Items.Clear();
            }
        }

        /// <summary>
        /// 渲染层级 → 屏幕位置 排序。
        /// Unity UI 中后渲染的（兄弟序号更大的）显示在上层，故降序排在最前，
        /// 这样覆盖层弹窗的按钮会排在一堆底层按钮之前。
        /// </summary>
        private static void SortByRenderOrder()
        {
            Items.Sort((a, b) =>
            {
                if (a == null || b == null) return 0;

                int ca = CanvasOrder(a), cb = CanvasOrder(b);
                if (ca != cb) return cb.CompareTo(ca);          // Canvas 层级高的在前

                int pa = PanelIndex(a), pb = PanelIndex(b);
                if (pa != pb) return pb.CompareTo(pa);          // 面板靠后的在前（覆盖层）

                RectTransform ra = a.transform as RectTransform;
                RectTransform rb = b.transform as RectTransform;
                if (ra == null || rb == null) return 0;

                float ya = ra.position.y, yb = rb.position.y;
                if (Mathf.Abs(ya - yb) > 24f) return yb.CompareTo(ya);
                return ra.position.x.CompareTo(rb.position.x);
            });
        }

        private static int CanvasOrder(Selectable s)
        {
            try
            {
                Canvas c = s.GetComponentInParent<Canvas>();
                return c != null ? c.sortingOrder : 0;
            }
            catch { return 0; }
        }

        /// <summary>该控件所属面板在 Canvas 下的兄弟序号（越大越靠上层）。</summary>
        private static int PanelIndex(Selectable s)
        {
            try
            {
                Canvas c = s.GetComponentInParent<Canvas>();
                if (c == null) return 0;
                Transform canvasT = c.transform;
                Transform prev = s.transform;
                Transform t = s.transform;
                while (t != null && t != canvasT)
                {
                    prev = t;
                    t = t.parent;
                }
                return (t == null) ? 0 : prev.GetSiblingIndex();
            }
            catch { return 0; }
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
                    RequestRescanNextFrame();
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
                    string label = TextOf(s);
                    Speech.Speak("已激活 " + label, false);
                    b.onClick.Invoke();
                    // 下一帧重扫：此刻旧场景仍完整存活；
                    // 若该按钮触发场景切换，离真正卸载还有几十帧
                    RequestRescanNextFrame();
                    return;
                }

                ExecuteEvents.Execute(s.gameObject, new BaseEventData(EventSystem.current),
                    ExecuteEvents.submitHandler);
                RequestRescanNextFrame();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("激活控件失败: " + e.Message);
            }
        }

        private static void RequestRescanNextFrame()
        {
            _pendingRescanFrame = Time.frameCount + 1;
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

            // ---- 激活后的下一帧重扫（唯一允许的自动扫描）----
            if (_pendingRescanFrame >= 0 && Time.frameCount >= _pendingRescanFrame)
            {
                _pendingRescanFrame = -1;
                if (_active)
                {
                    if (!SceneStable()) { ExitInternal(false); return; }
                    Scan();
                    if (Items.Count == 0) { ExitInternal(false); return; }
                    _index = Mathf.Clamp(_index, 0, Items.Count - 1);
                }
            }

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
