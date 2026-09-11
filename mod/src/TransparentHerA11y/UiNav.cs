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
    /// === 回车 / 空格的归属（状态机，见 F）===
    ///
    ///   非导航模式                      → 不归我们，交给游戏推进剧情
    ///   导航模式 + 当前没有可用控件      → 退出导航，交回游戏推进剧情
    ///   导航模式 + 当前有可用控件        → 归我们，激活该控件
    ///
    ///   判定点是「本帧开始时导航模式是否有效且选中项还活着」，
    ///   而不是「EventSystem 里有没有选中对象」—— 后者会被鼠标点击和
    ///   游戏的 AutoFocusInputField 干扰，不能用来决定按键归属。
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
    ///    v0.3.0 曾在激活按钮后 0.35 秒重扫界面。「开始游戏」「读档」会立刻
    ///    SceneManager.LoadSceneAsync，重扫恰好落在场景销毁/激活瞬间，
    ///    对正在销毁的 Selectable 调用 FindObjectsOfTypeAll 会触发
    ///    无 C# 异常的原生崩溃。
    ///    现在只在激活后的「下一帧」重扫（旧场景仍完整存活），
    ///    且必须通过 SceneStable 门禁。
    ///
    /// E) 【恢复视觉反馈】
    ///    用 EventSystem.SetSelectedGameObject 让游戏自己的按钮高亮态生效。
    ///
    /// F) 【按键归属必须是显式的，不能靠「有没有选中对象」推断】
    ///    实测：退出导航模式后按空格推进剧情，会把上一次导航到的那个按钮
    ///    又点一遍。原因是 EventSystem.currentSelectedGameObject 在我们退出后
    ///    仍然指着那个按钮，而 StandaloneInputModule 一见回车/空格就向它发
    ///    submit。鼠标点过的按钮同理（Selectable.OnPointerDown 会自动选中自己）。
    ///
    ///    现在改成两件事：
    ///      1) 常驻把 EventSystem.sendNavigationEvents 置 false（见 KeepUnitySubmitOff），
    ///         让 uGUI 那条 submit 通路彻底不存在；
    ///      2) 回车/空格在我们自己手里时才算「提交」（见 Update 里的状态机），
    ///         并且同一帧拦住游戏自身那次多余的推进（见 BlockGameAdvance）。
    ///
    ///    为什么常驻关闭 submit 是安全的：全游戏代码里没有任何一处
    ///    SetSelectedGameObject，游戏自己并不依赖 EventSystem 选中态；
    ///    它的剧情推进、输入框确认、Esc 返回全部是自己读 Input.GetKeyDown。
    ///    鼠标点按走 pointer 事件，不受这个开关影响。
    ///    游戏自己播视频时也用同一个开关关掉提交（VideoPanelManager）。
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

        // 视觉反馈用：我们自己借 EventSystem 选中过的对象，退出时要清掉
        private static GameObject _selectedByUs;

        // 「提交键」状态机：本帧是否已由我们处理，以及是否正在执行我们发起的动作
        private static int _submitHandledFrame = -1;
        private static bool _inOurActivation;

        // 上一次朗读过的控件。重扫后如果这个位置换了别的控件，必须重新播报 ——
        // 否则玩家以为还停在刚才听的那一项上，按下去却是另一个东西。
        private static Selectable _announcedItem;

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
            AnnounceGroup(true);
        }

        private static void ExitInternal(bool announce)
        {
            _active = false;
            Groups.Clear();
            _index = 0;
            _pendingRescanFrame = -1;
            _announcedItem = null;
            ClearQuitConfirm();
            ReleaseSelection();
            if (announce)
            {
                try { Speech.Speak("已退出导航模式。", true); } catch { }
            }
        }

        /// <summary>
        /// 常驻关掉 uGUI 的键盘导航/提交通路。
        ///
        /// 为什么是常驻而不是「只在导航模式里」：
        ///   游戏的推进剧情是自己在 Update 里读 Input.GetKeyDown(空格/回车) 的。
        ///   而 StandaloneInputModule 也会在回车/空格时向「当前选中对象」发一次
        ///   submit。只要之前有任何控件被选中过——鼠标点过、游戏自己的
        ///   AutoFocusInputField.Select()、或者我们退出导航后残留的选中态——
        ///   按空格推进剧情就会顺手把那个控件再点一次。
        ///
        ///   本模组的语义是：回车/空格只在导航模式里、且有选中项时才算提交，
        ///   其余一律归还给游戏。所以这条通路必须一直关着。
        ///
        /// 代价与安全性：
        ///   - 失去 uGUI 原生的方向键导航 —— 那正是本导航模式要替代的东西。
        ///   - 鼠标点按走 pointer 事件，不受影响。
        ///   - 输入框打字走 TMP_InputField 自己读 Input，不受影响；
        ///     游戏确认输入框也是自己读 Input.GetKeyDown(Return)。
        ///   - 游戏全代码没有一处 SetSelectedGameObject，本就不依赖选中态。
        ///   - 新场景的 EventSystem 默认是 true，所以每帧都要重申一次。
        ///
        /// 由 Plugin.Update 无条件调用（不受「菜单键盘导航」开关影响）：
        /// 这条通路一旦松开，鼠标点过的按钮就会在按空格推进剧情时被重复点击。
        /// </summary>
        internal static void KeepUnitySubmitOff()
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return;
                if (es.sendNavigationEvents) es.sendNavigationEvents = false;
            }
            catch { }
        }

        /// <summary>清掉我们自己设的选中态，去掉高亮、也不给 uGUI 留提交目标。</summary>
        private static void ReleaseSelection()
        {
            GameObject go = _selectedByUs;
            _selectedByUs = null;
            if (go == null) return;   // 已被销毁的也算 null，直接跳过
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject == go)
                    es.SetSelectedGameObject(null);
            }
            catch { }
        }

        /// <summary>把某个对象设成当前选中（用于让游戏自己的高亮态生效）。</summary>
        private static void SelectByUs(GameObject go)
        {
            if (go == null) return;
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return;
                es.SetSelectedGameObject(go);
                _selectedByUs = go;
            }
            catch { }
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
        /// <summary>把一段文本压成可朗读的一行（去换行、去空白）。</summary>
        private static string Norm(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return "";
            return txt.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
        }

        /// <summary>控件自己子树里的文本（最多 3 段）。没有则返回空串。</summary>
        private static string OwnTextOf(Selectable s)
        {
            var parts = new List<string>();
            try
            {
                TextMeshProUGUI[] all = s.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < all.Length && parts.Count < 3; i++)
                {
                    if (all[i] == null) continue;
                    string txt = Norm(all[i].text);
                    if (txt.Length == 0) continue;
                    bool dup = false;
                    for (int j = 0; j < parts.Count; j++)
                        if (parts[j] == txt) { dup = true; break; }
                    if (!dup) parts.Add(txt);
                }
            }
            catch { }
            return parts.Count == 0 ? "" : string.Join("，", parts.ToArray());
        }

        /// <summary>
        /// 设置面板里那些「美术字标签」的对象名 → 中文。
        ///
        /// 游戏的设置面板把行名和页签名**画成了图片**，TMP 里没有对应文字，
        /// 所以只能退回对象名 —— 而对象名是 ControlButton、General 这种。
        /// 这里按对象名给出中文。每一条都要有依据，不要凭感觉往里加：
        ///   - 三个页签：游戏自带本地化表里有 config.tab.general / .volume / .shortcut，
        ///     对应画面上画的 SYSTEM / SOUND / SHORTCUTS（见设置面板截图）。
        ///   - 五个单选行的行名：依据设置面板截图逐行对照
        ///     （窗口分辨率 / 画面模式 / 快进模式 / 指针隐藏 / 画面位于最前）。
        ///   - 角色音量：本地化表里 config.title.charactervolume = 角色音量。
        /// </summary>
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "General",  "通用" },
            { "Volume",   "音量" },
            { "Shortcut", "快捷键" },

            { "ScreenPixelOption", "窗口分辨率" },
            { "FullScreenOption",  "画面模式" },
            { "FastForwordOption", "快进模式" },
            { "HideCursorOption",  "指针隐藏" },
            { "TopWindowOption",   "画面位于最前" },

            { "CharacterVolumePanel", "角色音量" },
        };

        /// <summary>
        /// 没有信息量的样板对象名。上溯找「有意义的名字」时要跳过它们，
        /// 否则永远停在 Slider / Toggle / ControlButton 上。
        /// </summary>
        private static bool IsBoilerplateName(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            switch (n.ToLowerInvariant())
            {
                case "button": case "controlbutton": case "toggle": case "slider":
                case "text": case "image": case "textimage": case "rawimage":
                case "panel": case "option": case "options": case "labels":
                case "background": case "checkmark": case "line": case "staticpic":
                case "content": case "item": case "root": case "group":
                case "fill": case "handle": case "area":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 从 parent 的子树里取第一段**不属于 exclude 子树**的文本。
        ///
        /// 两个排除条件都很重要：
        ///   - 排除自己那一支，否则可能读到自己内部的字；
        ///   - 排除落在**别的控件**（按钮/开关/滑条）里的文本，
        ///     那是那个控件的标签，不是这一行的标签。
        /// 另外只接受在层级中处于激活状态的文本，免得读到隐藏面板的字。
        ///
        /// 这里同时认 TMP 和旧版 UnityEngine.UI.Text：设置面板的 Text 节点
        /// 从场景里读不到静态文字（运行期才由本地化表填进去），无法确认是哪一种，
        /// 两种都认最省事，也不会有副作用。
        /// </summary>
        private static string FirstTextOutside(Transform parent, Transform exclude)
        {
            try
            {
                var cands = new List<Component>();
                try { cands.AddRange(parent.GetComponentsInChildren<TextMeshProUGUI>(true)); }
                catch { }
                try { cands.AddRange(parent.GetComponentsInChildren<Text>(true)); }
                catch { }

                for (int i = 0; i < cands.Count; i++)
                {
                    Component c = cands[i];
                    if (c == null) continue;
                    if (!c.gameObject.activeInHierarchy) continue;

                    Transform tt = c.transform;
                    if (tt == exclude || tt.IsChildOf(exclude)) continue;

                    // 从这段文字往上走，只要在本行范围内遇到别的 Selectable，就说明
                    // 它属于那个控件，不是行标签。
                    bool other = false;
                    Transform cur = tt;
                    while (cur != null && cur != parent)
                    {
                        if (cur.GetComponent<Selectable>() != null) { other = true; break; }
                        cur = cur.parent;
                    }
                    if (other) continue;

                    string txt = TextOn(c);
                    if (txt.Length > 0) return txt;
                }
            }
            catch { }
            return "";
        }

        /// <summary>取组件上的文本，TMP 与旧版 Text 都认。</summary>
        private static string TextOn(Component c)
        {
            TextMeshProUGUI tmp = c as TextMeshProUGUI;
            if (tmp != null) return Norm(tmp.text);
            Text legacy = c as Text;
            if (legacy != null) return Norm(legacy.text);
            return "";
        }

        /// <summary>同一个「行」里的标签文本：从自己往上找，最多两层。</summary>
        private static string RowTextOf(Selectable s)
        {
            try
            {
                Transform t = s.transform;
                for (int up = 0; up < 2 && t != null; up++)
                {
                    Transform p = t.parent;
                    if (p == null) break;
                    string found = FirstTextOutside(p, t);
                    if (found.Length > 0) return found;
                    t = p;
                }
            }
            catch { }
            return "";
        }

        /// <summary>从自己往上（最多 4 层）找第一个命中中文别名表的祖先名。</summary>
        private static string AliasAncestorOf(Selectable s)
        {
            try
            {
                Transform t = s.transform;
                for (int up = 0; up < 4 && t != null; up++)
                {
                    string n = t.gameObject.name;
                    string alias;
                    if (n != null && NameAlias.TryGetValue(n, out alias)) return alias;
                    t = t.parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 控件的可读标签。
        ///
        /// === 为什么要分三层找（设置面板实测结构）===
        ///
        ///   TextSpeed        [行]
        ///     Text            ← 中文「文本显示速度」，是控件的**兄弟**，不是子节点
        ///     Slider          ← 控件，子树里只有 Background / Fill Area / Handle
        ///   ScreenPixel1080  [单选项]
        ///     Text            ← 「1280×720」
        ///     Toggle          ← 控件
        ///
        /// 只查控件自己的子树，slider 和 toggle 都会一个字都找不到，
        /// 于是退回对象名，读出「Slider」「Toggle」——只有类型，没有用途。
        ///
        ///   1) 自己子树有文本 → 直接用（存档槽、按钮等绝大多数情况走这条，行为不变）
        ///   2) 同一行的兄弟文本 → 用，并在前面补上所属行名（窗口分辨率、画面模式…）
        ///   3) 都没有 → 从自己往上找第一个有意思的名字，跳过样板名
        /// </summary>
        private static string TextOf(Selectable s)
        {
            string own = OwnTextOf(s);
            if (own.Length > 0) return own;

            string row = AliasAncestorOf(s);
            string near = RowTextOf(s);
            if (near.Length > 0)
            {
                if (row.Length > 0 && row != near) return row + "，" + near;
                return near;
            }

            if (row.Length > 0) return row;

            // 最后一层：跳过 Slider / Toggle / ControlButton 这类样板名
            try
            {
                Transform t = s.transform;
                for (int up = 0; up < 4 && t != null; up++)
                {
                    string n = t.gameObject.name;
                    if (!IsBoilerplateName(n)) return n;
                    t = t.parent;
                }
            }
            catch { }

            return s.gameObject.name;
        }

        /// <summary>滑条当前值的说法。0-1 范围的条按百分比念，否则念 N / M。</summary>
        private static string SliderValueText(Slider sl)
        {
            if (sl.minValue >= -0.001f && sl.maxValue <= 1.001f)
                return Mathf.RoundToInt(Mathf.Clamp01(sl.value) * 100f) + "%";
            return Mathf.RoundToInt(sl.value) + " / " + Mathf.RoundToInt(sl.maxValue);
        }

        private static string Describe(Selectable s)
        {
            var sb = new StringBuilder();
            sb.Append(TextOf(s));

            Toggle t = s as Toggle;
            Slider sl = s as Slider;
            TMP_InputField inf = s as TMP_InputField;

            if (t != null) sb.Append("，开关，").Append(t.isOn ? "开" : "关");
            else if (sl != null) sb.Append("，滑条，").Append(SliderValueText(sl));
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

            _announcedItem = s;
            SelectByUs(s.gameObject);

            // 光标移到别处 = 放弃刚才那次退出确认
            ClearQuitConfirm();

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

        /// <summary>当前选中项；导航模式未生效或该项已失效时返回 null。</summary>
        private static Selectable CurrentItem()
        {
            if (!_active) return null;
            if (_groupIndex < 0 || _groupIndex >= Groups.Count) return null;
            if (Items.Count == 0) return null;
            int i = Mathf.Clamp(_index, 0, Items.Count - 1);
            Selectable s = Items[i];
            return s == null ? null : s;   // Unity 伪空：已销毁对象在此拦下
        }

        /// <summary>
        /// 本帧游戏自身那次「推进剧情」是否该被拦掉。
        ///
        /// DialogueSceneManager / Ending2DialogueManager 的 Update 都是在
        /// Input.GetKeyDown(空格/回车) 成立后紧接着调 DialogueButtonClicked()。
        /// 只要那次按键已经归我们（或即将归我们），这次调用就该拦掉，
        /// 剧情才只动一次。
        ///
        /// 两种判据都要有，因为两个 Update 谁先执行是不确定的：
        ///   - 我们已经跑过：本帧接管过提交键 → 拦。
        ///   - 我们还没跑：键正处于按下状态、且我们有可激活的选中项 → 拦。
        ///
        /// 我们自己激活控件时引发的推进要放行 —— 那个按钮本来就是干这个的
        /// （例如全屏热区按钮）。
        /// </summary>
        internal static bool BlockGameAdvance
        {
            get
            {
                if (_inOurActivation) return false;

                try { if (_submitHandledFrame == Time.frameCount) return true; }
                catch { return false; }

                try
                {
                    if (!Input.GetKeyDown(KeyCode.Return)
                        && !Input.GetKeyDown(KeyCode.KeypadEnter)
                        && !Input.GetKeyDown(KeyCode.Space)) return false;
                }
                catch { return false; }

                return CurrentItem() != null;
            }
        }

        // ================= 退出确认 =================
        //
        // 只针对「按一下就把游戏关掉、而游戏自己不给确认框」的那一个控件：
        // 标题画面角落里的 EXIT。
        //
        // 标题画面有两个同类按钮，美术字分别是 START 和 EXIT，只差一个单词，
        // 而区分它们真正靠的是「哪一个是整块大面板、哪一个是角落小图标」——
        // 这种视觉信息读屏拿不到。按错一次整个会话直接没了。
        //
        // 游戏只在标题画面这一处不给确认框：剧情中的「返回标题」、手机菜单的
        // 「退出游戏」游戏自己都会弹原生确认框，我们不能重复问。
        //
        // 靠**对象名精确匹配**，名字是从游戏资源里实查的，不是猜的：
        //   标题场景 level1        StartButton / ExitButton / Title / QuitGame
        //   剧情场景 level3-5      MenuButton / QuitGame / Exit / ...   ← 没有 ExitButton
        // 完整版与试玩版都是这个结果，所以「名字 == ExitButton」只命中标题那一个。
        //
        // 踩过的坑（v0.5.4）：用正则 \b(exit|quit)\b 匹配，结果反了 ——
        //   ExitButton / QuitGame 是驼峰拼接，单词后面紧跟字母，根本没有 \b 词边界，
        //   于是标题那个 ExitButton 没命中；反而命中了剧情里名字就叫 Exit 的按钮
        //   （手机菜单的「退出游戏」，游戏自己有确认框）。
        //   教训：这种判定别用词边界，也别用「包含」，直接拿实查到的名字比。

        private static Selectable _pendingQuit;
        private static float _pendingQuitAt;
        private const float QuitConfirmSeconds = 8f;

        private static bool NeedsQuitConfirm(Selectable s)
        {
            if (Plugin.CfgQuitConfirm == null || !Plugin.CfgQuitConfirm.Value) return false;
            try
            {
                string cfg = Plugin.CfgQuitNames != null ? Plugin.CfgQuitNames.Value : "ExitButton";
                if (string.IsNullOrEmpty(cfg)) return false;

                string name = s.gameObject.name ?? "";
                string[] wants = cfg.Split(new char[] { ',', '，' });
                for (int i = 0; i < wants.Length; i++)
                {
                    string want = wants[i].Trim();
                    if (want.Length == 0) continue;
                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static bool QuitConfirmArmed(Selectable s)
        {
            if (_pendingQuit == null) return false;
            if (Time.realtimeSinceStartup - _pendingQuitAt > QuitConfirmSeconds)
            {
                ClearQuitConfirm();
                return false;
            }
            return _pendingQuit == s;
        }

        private static void ArmQuitConfirm(Selectable s)
        {
            _pendingQuit = s;
            _pendingQuitAt = Time.realtimeSinceStartup;
            // 留痕：万一配错了名字，日志里能看出到底拦的是哪个控件。
            try
            {
                Plugin.Log.LogInfo("[UiNav] 退出确认：「" + s.gameObject.name + "」标签「"
                    + TextOf(s) + "」场景 " + SceneManager.GetActiveScene().name);
            }
            catch { }
            Speech.Speak("这是退出游戏。再按一次回车或空格确认退出，按别的键取消。", true);
        }

        private static void ClearQuitConfirm()
        {
            _pendingQuit = null;
        }

        private static void Activate(Selectable s)
        {
            if (s == null) { ExitInternal(false); return; }

            // 留痕：崩溃排查用。原生崩溃不会在日志里留下任何异常，
            // 只有我们自己事前写下的这一行能指明最后碰的是哪个控件。
            try
            {
                Plugin.Log.LogInfo("[UiNav] 激活 " + s.GetType().Name + "「" + TextOf(s) + "」场景 "
                    + SceneManager.GetActiveScene().name);
            }
            catch { }

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
                    // 先退出导航（ReleaseSelection 会清掉上一个高亮），
                    // 再把焦点交给输入框，否则刚设的焦点会被自己清掉。
                    ExitInternal(false);
                    SelectByUs(inf.gameObject);
                    inf.ActivateInputField();
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
                    _inOurActivation = true;
                    try { b.onClick.Invoke(); }
                    finally { _inOurActivation = false; }
                    // 下一帧重扫：此刻旧场景仍完整存活；
                    // 若该按钮触发场景切换，离真正卸载还有几十帧
                    RequestRescanNextFrame();
                    return;
                }

                _inOurActivation = true;
                try
                {
                    ExecuteEvents.Execute(s.gameObject, new BaseEventData(EventSystem.current),
                        ExecuteEvents.submitHandler);
                }
                finally { _inOurActivation = false; }
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
                Speech.Speak(SliderValueText(sl), false);
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
                    Selectable before = _announcedItem;
                    Scan();
                    if (Groups.Count == 0 || Items.Count == 0) { ExitInternal(false); return; }
                    _index = Mathf.Clamp(_index, 0, Items.Count - 1);

                    // 列表重建后 _index 还停在原来的序号上，但那个位置上可能已经换了
                    // 别的控件（弹窗、二级菜单、翻页都会这样）。这时必须重新播报，
                    // 否则玩家按下去的是他从没听过的东西 —— 主菜单 START/EXIT 那类
                    // 只差一个单词的按钮，听错一次就出事。
                    Selectable now = CurrentItem();
                    if (now != before) Announce("界面已更新。");
                }
            }

            if (Input.GetKeyDown(KeyCode.Tab)) { Toggle(); return; }

            // 全部控件失效时先退出导航，把按键还给游戏
            if (_active && (Groups.Count == 0 || Items.Count == 0)) ExitInternal(false);

            // ---- 回车 / 空格：整块状态机唯一的判定点 ----
            //
            //   非导航模式            CurrentItem() 为 null → 什么都不做，游戏自己推进剧情
            //   导航模式 + 没有可用项  CurrentItem() 为 null → 同上
            //   导航模式 + 有可用项    → 我们接管，激活它，并拦住游戏同一帧的推进
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
                || Input.GetKeyDown(KeyCode.Space))
            {
                Selectable target = CurrentItem();
                if (target != null)
                {
                    _submitHandledFrame = Time.frameCount;

                    // 标题画面的 EXIT 一按就关游戏，而游戏自己不给确认框。
                    // 读屏玩家分不清它和旁边的 START（美术字，只差一个单词），
                    // 所以这里补一道二次确认。详见 NeedsQuitConfirm。
                    if (NeedsQuitConfirm(target) && !QuitConfirmArmed(target))
                    {
                        ArmQuitConfirm(target);
                        return;
                    }
                    ClearQuitConfirm();

                    Activate(target);
                }
                return;
            }

            if (!_active) return;

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
            else if (Input.GetKeyDown(KeyCode.Home)) { _index = 0; Announce(null); }
            else if (Input.GetKeyDown(KeyCode.End)) { _index = Items.Count - 1; Announce(null); }
        }
    }
}
