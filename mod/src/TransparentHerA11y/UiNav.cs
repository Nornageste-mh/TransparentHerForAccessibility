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
    ///   PageUp/PageDown  切换面板组（默认只导航最上层那一组）
    ///
    /// === 设计约束（每条都对应一次实测故障）===
    ///
    /// A) 【只导航最上层面板组】
    ///    实测存档界面一次能扫到 13~20 个控件，其中只有 5 个是存档槽，
    ///    其余是设置页签、画廊等无关按钮，读屏用户要在一堆噪声里找目标。
    ///    现在按「Canvas 下的顶层祖先」分组，默认只进入渲染层级最高的
    ///    那一组，用 PageUp/PageDown 手动切换。
    ///
    /// B) 【排序按完整渲染路径，而不是只看屏幕坐标】
    ///    确认框是覆盖层：LoadSlotUI.OnSaveSlotClicked 直接
    ///    confirmLoadPanel.SetActive(true)，背后存档槽按钮仍然 active。
    ///    只按坐标排会让弹窗按钮混在底层按钮中间。
    ///    现在按兄弟序号路径逐级比较（Unity UI 中后渲染的在上层）。
    ///
    /// C) 【标签必须拼接多个文本】
    ///    SaveSlot 有 saveNameText / saveTimeText / chapterText 三个字段，
    ///    只取「子物体里第一个 TMP」会让 5 个空槽位读出完全相同的
    ///    「无存档」，用户无法区分，表现就像「每页只能选一个槽」。
    ///    现在把不同的文本拼起来（最多 3 段）。
    ///
    /// D) 【绝不定时重扫 + 场景切换必须复位】
    ///    v1.4 曾在激活按钮后 0.35 秒重扫界面。「开始游戏」「读档」会立刻
    ///    SceneManager.LoadSceneAsync，重扫恰好落在场景销毁/激活瞬间，
    ///    对正在销毁的 Selectable 调用 FindObjectsOfTypeAll 会触发
    ///    无 C# 异常的原生崩溃。
    ///    现在只在激活后的「下一帧」重扫（旧场景仍完整存活），
    ///    且必须通过 SceneStable 门禁。
    ///
    /// E) 【恢复视觉反馈】
    ///    用 EventSystem.SetSelectedGameObject 让游戏自己的按钮高亮态生效。
    ///    为避免 Unity 的 StandaloneInputModule 再对回车/空格自动提交
    ///    （那会和我们的直接 Invoke 重复触发），导航模式期间把
    ///    sendNavigationEvents 置 false —— 但退出时必须还原，
    ///    场景切换时也一并还原，否则新场景 UI 提交会失效。
    /// </summary>
    internal static class UiNav
    {
        private class Group
        {
            public Transform Root;
            public int CanvasOrder;
            public int SiblingIndex;
            public readonly List<Selectable> Items = new List<Selectable>();
        }

        private static readonly List<Group> Groups = new List<Group>();
        private static int _groupIndex;
        private static List<Selectable> Items { get { return Groups[_groupIndex].Items; } }

        private static bool _active;
        private static int _index;
        private static int _pendingRescanFrame = -1;

        // 视觉反馈用：导航模式期间关掉 Unity 自带的导航提交
        private static bool _savedSendNav = true;
        private static bool _sendNavSaved;

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
            if (Groups.Count == 0 || Items.Count == 0)
            {
                Speech.Speak("当前界面上没有可操作的项目。", true);
                return;
            }

            _active = true;
            _index = 0;
            SuppressUnitySubmit(true);
            AnnounceGroup(true);
        }

        private static void ExitInternal(bool announce)
        {
            _active = false;
            Groups.Clear();
            _index = 0;
            _pendingRescanFrame = -1;
            SuppressUnitySubmit(false);
            if (announce)
            {
                try { Speech.Speak("已退出导航模式。", true); } catch { }
            }
        }

        /// <summary>
        /// 导航模式期间关掉 Unity 的导航提交事件，避免 StandaloneInputModule
        /// 对回车/空格二次提交（与我们直接 Invoke 冲突）。退出/切场景必须还原。
        /// </summary>
        private static void SuppressUnitySubmit(bool suppress)
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return;
                if (suppress)
                {
                    if (!_sendNavSaved) { _savedSendNav = es.sendNavigationEvents; _sendNavSaved = true; }
                    es.sendNavigationEvents = false;
                }
                else if (_sendNavSaved)
                {
                    es.sendNavigationEvents = _savedSendNav;
                    _sendNavSaved = false;
                }
            }
            catch { _sendNavSaved = false; }
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

        // ================= 扫描与分组 =================

        private static void Scan()
        {
            int keepGroupSibling = (Groups.Count > 0 && _groupIndex >= 0 && _groupIndex < Groups.Count)
                ? Groups[_groupIndex].SiblingIndex : int.MinValue;
            Groups.Clear();
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

                    Group g = GroupOf(s);
                    if (g != null) g.Items.Add(s);
                }

                // 组排序：Canvas 层级高的、兄弟序号靠后的（在更上层）排前面
                Groups.Sort((a, b) =>
                {
                    if (a.CanvasOrder != b.CanvasOrder) return b.CanvasOrder.CompareTo(a.CanvasOrder);
                    return b.SiblingIndex.CompareTo(a.SiblingIndex);
                });
                Groups.RemoveAll(g => g.Items.Count == 0);
                for (int i = 0; i < Groups.Count; i++) SortWithin(Groups[i]);

                _groupIndex = 0;
                if (keepGroupSibling != int.MinValue)
                {
                    int found = Groups.FindIndex(g => g.SiblingIndex == keepGroupSibling);
                    if (found >= 0) _groupIndex = found;
                }

                if (Plugin.Log != null)
                {
                    var sb = new StringBuilder();
                    sb.Append("[UiNav] 扫描到 ").Append(Groups.Count).Append(" 组：");
                    for (int i = 0; i < Groups.Count && i < 6; i++)
                    {
                        sb.Append(Groups[i].Root != null ? Groups[i].Root.name : "?")
                          .Append('(').Append(Groups[i].Items.Count).Append(") ");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("扫描界面控件失败: " + e.Message);
                Groups.Clear();
            }
        }

        /// <summary>取该控件在 Canvas 下的顶层祖先作为分组依据。</summary>
        private static Group GroupOf(Selectable s)
        {
            try
            {
                Canvas c = s.GetComponentInParent<Canvas>();
                if (c == null) return null;
                Transform canvasT = c.transform;

                Transform top = s.transform;
                Transform t = s.transform;
                while (t != null && t != canvasT)
                {
                    top = t;
                    t = t.parent;
                }
                if (t == null) return null;   // 不在该 Canvas 下

                Group g = Groups.Find(x => x.Root == top);
                if (g == null)
                {
                    g = new Group
                    {
                        Root = top,
                        CanvasOrder = c.sortingOrder,
                        SiblingIndex = top.GetSiblingIndex()
                    };
                    Groups.Add(g);
                }
                return g;
            }
            catch { return null; }
        }

        /// <summary>
        /// 组内排序：按兄弟序号路径逐级比较（后渲染者在上层），
        /// 同一层级再按屏幕位置（先上后下、同行先左后右）。
        /// </summary>
        private static void SortWithin(Group g)
        {
            g.Items.Sort((a, b) =>
            {
                if (a == null || b == null) return 0;

                int cmp = ComparePath(a.transform, b.transform, g.Root);
                if (cmp != 0) return cmp;

                RectTransform ra = a.transform as RectTransform;
                RectTransform rb = b.transform as RectTransform;
                if (ra == null || rb == null) return 0;
                float ya = ra.position.y, yb = rb.position.y;
                if (Mathf.Abs(ya - yb) > 24f) return yb.CompareTo(ya);
                return ra.position.x.CompareTo(rb.position.x);
            });
        }

        /// <summary>
        /// 从组根往下逐级比较兄弟序号，值大者（更靠上层）排前面。
        /// 返回负数表示 a 应排在 b 之前。
        /// </summary>
        private static int ComparePath(Transform a, Transform b, Transform root)
        {
            List<int> pa = PathIndices(a, root);
            List<int> pb = PathIndices(b, root);
            int n = Mathf.Min(pa.Count, pb.Count);
            for (int i = 0; i < n; i++)
            {
                if (pa[i] != pb[i]) return pb[i].CompareTo(pa[i]);
            }
            return pb.Count.CompareTo(pa.Count);   // 更深的（更靠内层）在后
        }

        private static List<int> PathIndices(Transform t, Transform root)
        {
            var list = new List<int>();
            Transform cur = t;
            while (cur != null && cur != root)
            {
                list.Add(cur.GetSiblingIndex());
                cur = cur.parent;
            }
            list.Reverse();
            return list;
        }

        // ================= 描述 =================

        /// <summary>
        /// 拼接该控件及其子物体里的文本。
        /// 只取第一个会踩坑：SaveSlot 的 saveNameText / saveTimeText /
        /// chapterText 是三个字段，5 个空槽的第一个文本都是「无存档」，
        /// 读出来完全一样，用户无法区分。
        /// </summary>
        private static string TextOf(Selectable s)
        {
            var parts = new List<string>();
            try
            {
                TextMeshProUGUI[] all = s.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < all.Length && parts.Count < 3; i++)
                {
                    if (all[i] == null) continue;
                    string txt = all[i].text;
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    txt = txt.Replace("\n", " ").Replace("\r", " ").Trim();
                    if (txt.Length == 0) continue;
                    bool dup = false;
                    for (int j = 0; j < parts.Count; j++)
                        if (parts[j] == txt) { dup = true; break; }
                    if (!dup) parts.Add(txt);
                }
            }
            catch { }

            if (parts.Count == 0) return s.gameObject.name;
            return string.Join("，", parts.ToArray());
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

        private static void AnnounceGroup(bool withCount)
        {
            if (Groups.Count == 0 || Items.Count == 0) return;
            string prefix = withCount
                ? ("导航模式，第 " + (_groupIndex + 1) + " 组，共 " + Items.Count + " 项。")
                : "";
            Announce(prefix);
        }

        private static void Announce(string prefix)
        {
            if (!_active || Items.Count == 0) return;
            _index = Mathf.Clamp(_index, 0, Items.Count - 1);
            Selectable s = Items[_index];
            if (s == null) { ExitInternal(false); return; }

            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && s.gameObject != null) es.SetSelectedGameObject(s.gameObject);
            }
            catch { }

            Speech.Speak(prefix + Describe(s) + "。" + (_index + 1) + " / " + Items.Count, true);
        }

        private static void SwitchGroup(int dir)
        {
            if (Groups.Count <= 1)
            {
                Speech.Speak("只有一个面板组。", true);
                return;
            }
            _groupIndex = (_groupIndex + dir + Groups.Count) % Groups.Count;
            _index = 0;
            AnnounceGroup(true);
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
                    SuppressUnitySubmit(false);
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
                    if (_active || Groups.Count > 0) ExitInternal(false);
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
                    if (Groups.Count == 0 || Items.Count == 0) { ExitInternal(false); return; }
                    _index = Mathf.Clamp(_index, 0, Items.Count - 1);
                }
            }

            if (Input.GetKeyDown(KeyCode.Tab)) { Toggle(); return; }
            if (!_active) return;
            if (Groups.Count == 0 || Items.Count == 0) { ExitInternal(false); return; }

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                _index = (_index - 1 + Items.Count) % Items.Count;
                Announce(null);
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                _index = (_index + 1) % Items.Count;
                Announce(null);
            }
            else if (Input.GetKeyDown(KeyCode.PageDown)) SwitchGroup(1);
            else if (Input.GetKeyDown(KeyCode.PageUp)) SwitchGroup(-1);
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Adjust(-1f);
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Adjust(1f);
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
                     || Input.GetKeyDown(KeyCode.Space))
                Activate();
            else if (Input.GetKeyDown(KeyCode.Home)) { _index = 0; Announce(null); }
            else if (Input.GetKeyDown(KeyCode.End)) { _index = Items.Count - 1; Announce(null); }
        }
    }
}
