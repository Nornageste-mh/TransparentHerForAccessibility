using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
    ///
    /// 进入导航模式时会临时把 EventSystem.sendNavigationEvents 置 false，
    /// 避免 Unity 自带的 InputModule 与我们的焦点管理互相打架；退出时还原。
    /// </summary>
    internal static class UiNav
    {
        private static bool _active;
        private static bool _savedSendNav;
        private static bool _sendNavSaved;

        private static readonly List<Selectable> Items = new List<Selectable>();
        private static int _index;
        private static float _rescanAt;

        /// <summary>导航模式是否开启。开启时按键归本模块处理。</summary>
        public static bool Active { get { return _active; } }

        // ================= 进入 / 退出 =================

        public static void Toggle()
        {
            if (_active) Exit();
            else Enter();
        }

        private static void Enter()
        {
            Scan();
            if (Items.Count == 0)
            {
                Speech.Speak("当前界面上没有可操作的项目。", true);
                return;
            }

            _active = true;
            _index = 0;

            try
            {
                EventSystem es = EventSystem.current;
                if (es != null)
                {
                    _savedSendNav = es.sendNavigationEvents;
                    _sendNavSaved = true;
                    es.sendNavigationEvents = false;   // 焦点由我们自己管
                }
            }
            catch { }

            Speech.Speak("导航模式，共 " + Items.Count + " 项。", true);
            Announce();
        }

        private static void Exit()
        {
            _active = false;
            RestoreSendNav();
            Speech.Speak("已退出导航模式。", true);
        }

        private static void RestoreSendNav()
        {
            if (!_sendNavSaved) return;
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null) es.sendNavigationEvents = _savedSendNav;
            }
            catch { }
            _sendNavSaved = false;
        }

        // ================= 扫描 =================

        private static void Scan()
        {
            Items.Clear();
            try
            {
                Selectable[] all = Resources.FindObjectsOfTypeAll<Selectable>();
                for (int i = 0; i < all.Length; i++)
                {
                    Selectable s = all[i];
                    if (s == null) continue;
                    if (!s.isActiveAndEnabled) continue;
                    // 排除预制体资源（不在场景里的）
                    if (!s.gameObject.scene.IsValid()) continue;

                    RectTransform rt = s.transform as RectTransform;
                    if (rt == null) continue;
                    if (rt.rect.width < 1f || rt.rect.height < 1f) continue;

                    // 不可见的（CanvasGroup alpha 0）跳过
                    CanvasGroup cg = s.GetComponentInParent<CanvasGroup>();
                    if (cg != null && cg.alpha < 0.05f) continue;

                    Items.Add(s);
                }
                SortByPosition();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("扫描界面控件失败: " + e.Message);
            }
        }

        /// <summary>按屏幕位置排序：先上后下，同一行内先左后右。</summary>
        private static void SortByPosition()
        {
            Items.Sort((a, b) =>
            {
                float ya = ((RectTransform)a.transform).position.y;
                float yb = ((RectTransform)b.transform).position.y;
                if (Mathf.Abs(ya - yb) > 24f) return yb.CompareTo(ya);  // 行不同，上方的在前
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
            if (Items.Count == 0) return;
            _index = Mathf.Clamp(_index, 0, Items.Count - 1);
            Selectable s = Items[_index];
            if (s == null) return;

            try
            {
                EventSystem es = EventSystem.current;
                if (es != null) es.SetSelectedGameObject(s.gameObject);
            }
            catch { }

            Speech.Speak(Describe(s) + "。" + (_index + 1) + " / " + Items.Count, true);
        }

        // ================= 操作 =================

        private static void Activate()
        {
            if (Items.Count == 0) return;
            Selectable s = Items[_index];
            if (s == null) return;

            if (!s.interactable)
            {
                Speech.Speak("该项当前不可用。", true);
                return;
            }

            try
            {
                Toggle t = s as Toggle;
                Slider sl = s as Slider;
                TMP_InputField inf = s as TMP_InputField;

                if (t != null)
                {
                    t.isOn = !t.isOn;
                    Speech.Speak(t.isOn ? "开" : "关", false);
                    _rescanAt = Time.realtimeSinceStartup + 0.25f;
                    return;
                }
                if (inf != null)
                {
                    // 聚焦输入框并退出导航模式，把键盘交还给输入
                    EventSystem es = EventSystem.current;
                    if (es != null) es.SetSelectedGameObject(inf.gameObject);
                    inf.ActivateInputField();
                    Exit();
                    Speech.Speak("已进入输入框，直接打字即可。按 Tab 返回导航。", true);
                    return;
                }
                if (sl != null)
                {
                    Speech.Speak("滑条请用左右方向键调整。", true);
                    return;
                }

                Button b = s as Button;
                if (b != null)
                {
                    b.onClick.Invoke();
                    // 面板可能变化，稍后重新扫描
                    _rescanAt = Time.realtimeSinceStartup + 0.35f;
                    return;
                }

                // 其它 Selectable：走 Unity 的提交
                ExecuteEvents.Execute(s.gameObject, new BaseEventData(EventSystem.current),
                    ExecuteEvents.submitHandler);
                _rescanAt = Time.realtimeSinceStartup + 0.35f;
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
            // 面板变化后重新扫描
            if (_active && _rescanAt > 0f && Time.realtimeSinceStartup >= _rescanAt)
            {
                _rescanAt = 0f;
                string keep = (_index >= 0 && _index < Items.Count && Items[_index] != null)
                    ? Items[_index].gameObject.name : null;
                Scan();
                if (keep != null)
                {
                    int found = Items.FindIndex(x => x != null && x.gameObject.name == keep);
                    if (found >= 0) _index = found;
                }
                if (Items.Count == 0) { Exit(); return; }
            }

            if (Input.GetKeyDown(KeyCode.Tab)) { Toggle(); return; }
            if (!_active) return;

            if (Items.Count == 0) { Exit(); return; }

            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.LeftShift))
            {
                _index = (_index - 1 + Items.Count) % Items.Count;
                Announce();
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.RightShift))
            {
                _index = (_index + 1) % Items.Count;
                Announce();
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Adjust(-1f);
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Adjust(1f);
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
                     || Input.GetKeyDown(KeyCode.Space))
                Activate();
            else if (Input.GetKeyDown(KeyCode.Home))
            {
                _index = 0; Announce();
            }
            else if (Input.GetKeyDown(KeyCode.End))
            {
                _index = Items.Count - 1; Announce();
            }
        }
    }
}
