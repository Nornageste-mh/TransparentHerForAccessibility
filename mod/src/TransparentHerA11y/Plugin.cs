using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TransparentHerA11y
{
    [BepInPlugin(Guid, "TransparentHer A11y Reader", "0.5.2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "transparenther.a11y.reader";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CfgReadUnvoiced;
        internal static ConfigEntry<bool> CfgSpeakWhenUnknown;
        internal static ConfigEntry<bool> CfgReadPhone;
        internal static ConfigEntry<bool> CfgSpeakName;
        internal static ConfigEntry<bool> CfgReadPhoneSticker;
        internal static ConfigEntry<bool> CfgReadChoices;
        internal static ConfigEntry<bool> CfgChoiceHotkeys;
        internal static ConfigEntry<float> CfgRealTimeSeconds;
        internal static ConfigEntry<string> CfgSilenceKey;
        internal static ConfigEntry<bool> CfgMenuNav;
        internal static ConfigEntry<string> CfgRepeatKey;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            CfgReadUnvoiced = Config.Bind("朗读", "朗读无配音剧情", true,
                "朗读没有配音的剧情文本（旁白、主角「我」等）。有配音的台词不朗读，只放语音。");
            CfgSpeakWhenUnknown = Config.Bind("朗读", "无法判定时也朗读", true,
                "当无法确定当前行是否有配音时，选择朗读而不是跳过。");
            CfgSpeakName = Config.Bind("朗读", "朗读时带上说话人", true,
                "在台词前加上说话人名字。旁白无名时不加。");
            CfgReadPhone = Config.Bind("朗读", "朗读手机消息", true,
                "朗读手机（WeSay）聊天消息。");
            CfgReadPhoneSticker = Config.Bind("朗读", "朗读手机表情", true,
                "手机聊天里的表情/图片消息提示为「图片」。");
            CfgReadChoices = Config.Bind("朗读", "朗读选项", true,
                "出现选项时朗读全部选项内容。");
            CfgChoiceHotkeys = Config.Bind("朗读", "数字键选择选项", true,
                "用数字键 1-9 选择对应选项（剧情选项与手机选项通用）。");
            // 注意：游戏的「实时」限时选择是一个真分支，不是计时器装饰。
            // 8 秒到点会执行 AutoDestroyAfterTime 的收尾：销毁选项按钮，然后
            // 用这一批最后一条的 ToNumber 继续剧情 —— 那就是「什么都不做」的
            // 沉默分支。所以倒计时只能被「拉长」，绝不能被取消，否则玩家就
            // 永远拿不到沉默这个选项了（v0.5.0 就是这样，v0.5.2 改回）。
            CfgRealTimeSeconds = Config.Bind("朗读", "限时选择时长", 20f,
                "游戏共 246 处「实时」限时选择，原本 8 秒到点会自动替你选择——\n" +
                "也就是这一批里的「沉默」分支（什么都不做）。\n" +
                "这个值就是倒计时的秒数：读屏念完选项需要更多时间，所以默认放宽到 20 秒。\n" +
                "填 8 即完全恢复游戏原版节奏。范围 1-600。\n" +
                "注意：游戏自带的倒计时音效固定 8 秒，不会跟着变长；\n" +
                "倒计时进度条会跟着这个值走。\n" +
                "不想等就按「沉默按键」立刻选沉默。");
            CfgSilenceKey = Config.Bind("朗读", "沉默按键", "0",
                "在限时选择里立刻选择「沉默」（什么都不做），不必等倒计时走完。\n" +
                "填 KeyCode 名称，例如 0、Alpha0、Keypad0、Z。留空则关闭。\n" +
                "选项本身用 1-9，所以 0 不会冲突。");
            // 注意：游戏原生占用了以下按键，不要选它们
            //   A=自动  F=快进  P=打开主菜单  R=打开历史回顾/语音收藏
            //   Ctrl=快进  空格/回车/小键盘回车=推进  Esc=菜单
            CfgRepeatKey = Config.Bind("朗读", "重读按键", "Backspace",
                "重新朗读当前这一句的按键。填 KeyCode 名称，例如 Backspace、Tab、Q、F1、Home。\n" +
                "留空则关闭这个功能。\n" +
                "警告：R 已被游戏用作「打开历史回顾」，P 是「打开主菜单」，A 是自动，F 是快进，不要填这些。");
            CfgMenuNav = Config.Bind("朗读", "菜单键盘导航", true,
                "让主菜单 / 存读档 / 设置 / 画廊等界面可以用键盘操作并被朗读。\n" +
                "游戏原本这些界面几乎只能鼠标点。\n" +
                "  Tab          进入 / 退出导航模式\n" +
                "  上 / 下      上一项 / 下一项\n" +
                "  左 / 右      调整滑条\n" +
                "  回车 / 空格  激活（按钮点击、开关切换、输入框聚焦）\n" +
                "  Home / End   跳到第一项 / 最后一项\n" +
                "回车 / 空格的归属：只有「导航模式下且有选中项」时才是激活控件；\n" +
                "其余情况（非导航模式、或导航模式下没有可用项）一律归还给游戏，\n" +
                "也就是照常推进剧情。");

            // 挑选语音后端：Tolk > NVDA > SAPI
            try
            {
                Speech.Init(Log);
                if (Speech.Current == Speech.Backend.None)
                    Log.LogWarning("语音不可用: " + Speech.LastError);
            }
            catch (Exception e)
            {
                Log.LogWarning("语音初始化异常: " + e.Message);
            }

            _harmony = new Harmony(Guid);
            try
            {
                _harmony.PatchAll(typeof(Patches));
                Log.LogInfo("Harmony 补丁已应用。");
            }
            catch (Exception e)
            {
                Log.LogError("Harmony 补丁失败: " + e);
            }

            Log.LogInfo("TransparentHer A11y Reader 已加载。");
        }

        private void Update()
        {
            try
            {
                // 无条件执行：uGUI 那条「回车/空格 → submit 给当前选中对象」的
                // 通路必须一直关着。否则鼠标点过 / 导航过 / 游戏自己 Select() 过的
                // 控件，会在玩家按空格推进剧情时被顺手再点一次。
                // 详见 UiNav.KeepUnitySubmitOff 的注释。
                UiNav.KeepUnitySubmitOff();

                // 导航模式优先：开启时按键归它处理，避免与选项数字键互相干扰
                if (CfgMenuNav != null && CfgMenuNav.Value) UiNav.Update();
                if (!UiNav.Active) Reader.Update();
            }
            catch (Exception e) { Log.LogError("Update 异常: " + e.Message); }
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { Speech.Shutdown(); } catch { }
        }
    }

    /// <summary>一条可选择的选项。</summary>
    internal class ChoiceEntry
    {
        public string Label;
        public Action Select;
    }

    /// <summary>朗读核心逻辑。</summary>
    internal static class Reader
    {
        private static readonly List<ChoiceEntry> Choices = new List<ChoiceEntry>();
        private static string _lastSpoken = "";
        private static float _lastSpeakTime;
        private static MethodInfo _onReplyClicked;
        private static MethodInfo _onSelectionClicked;
        private static FieldInfo _ending2Index;

        static Reader()
        {
            try
            {
                _onReplyClicked = AccessTools.Method(typeof(DialogueSceneManager), "OnReplyButtonClicked",
                    new Type[] { typeof(string) });
                _onSelectionClicked = AccessTools.Method(typeof(DialogueSceneManager), "OnSelectionButtonClicked",
                    new Type[] { typeof(string), typeof(int) });
            }
            catch { }

            try
            {
                _ending2Index = AccessTools.Field(typeof(Ending2DialogueManager), "currentDialogueIndex");
            }
            catch { }
        }

        // ---------------- 输出 ----------------

        private static void Say(string text, bool interrupt = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;

            // 同一句在极短时间内重复，跳过（防止同一行被多次触发）
            if (text == _lastSpoken && Time.realtimeSinceStartup - _lastSpeakTime < 0.4f) return;

            _lastSpoken = text;
            _lastSpeakTime = Time.realtimeSinceStartup;
            Speech.Speak(text, interrupt);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // 去掉全角/半角方括号包裹的装饰，保留内容
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\r' || c == '\n' || c == '\t') { sb.Append(' '); continue; }
                if (c == '「' || c == '」' || c == '『' || c == '』') { sb.Append(' '); continue; }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static string Compose(string speaker, string body)
        {
            body = Clean(body);
            if (string.IsNullOrEmpty(body)) return "";
            if (Plugin.CfgSpeakName != null && Plugin.CfgSpeakName.Value)
            {
                speaker = Clean(speaker);
                if (!string.IsNullOrEmpty(speaker)) return speaker + "：" + body;
            }
            return body;
        }

        // ---------------- 剧情文本 ----------------

        public static void OnStoryText(string text)
        {
            try
            {
                if (Plugin.CfgReadUnvoiced == null || !Plugin.CfgReadUnvoiced.Value) return;
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;

                DialogueScene scene = ResolveScene_Dialogue(text);
                HandleStoryLine(scene, text);
            }
            catch (Exception e) { Plugin.Log.LogError("OnStoryText: " + e.Message); }
        }

        public static void OnStoryTextEnding2(string text)
        {
            try
            {
                if (Plugin.CfgReadUnvoiced == null || !Plugin.CfgReadUnvoiced.Value) return;
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;

                DialogueScene scene = ResolveScene_Ending2(text);
                HandleStoryLine(scene, text);
            }
            catch (Exception e) { Plugin.Log.LogError("OnStoryTextEnding2: " + e.Message); }
        }

        private static void HandleStoryLine(DialogueScene scene, string text)
        {
            if (scene != null)
            {
                bool voiced = !string.IsNullOrEmpty(scene.VoiceFilename);
                if (voiced)
                {
                    // 有配音：不朗读，并打断上一句未读完的朗读，避免与语音重叠
                    Speech.Stop();
                    _lastSpoken = "";
                    return;
                }
                Say(Compose(scene.CharacterName, text));
            }
            else
            {
                // 定位不到场景数据，按配置决定
                if (Plugin.CfgSpeakWhenUnknown == null || Plugin.CfgSpeakWhenUnknown.Value)
                    Say(Clean(text));
            }
        }

        /// <summary>用传入的最终文本反查当前 DialogueScene（拿 VoiceFilename / CharacterName）。</summary>
        private static DialogueScene Match(List<DialogueScene> list, int index, string text,
                                           Func<string, string> replaceName)
        {
            if (list == null || list.Count == 0) return null;

            // 快路径：索引位置直接比对
            if (index >= 0 && index < list.Count)
            {
                DialogueScene s = list[index];
                if (s != null && TextMatches(s, text, replaceName)) return s;
            }
            // 慢路径：线性查找
            for (int i = 0; i < list.Count; i++)
            {
                DialogueScene s = list[i];
                if (s != null && TextMatches(s, text, replaceName)) return s;
            }
            return null;
        }

        private static bool TextMatches(DialogueScene s, string text, Func<string, string> replaceName)
        {
            if (s.Dialogue == text) return true;
            if (replaceName != null && !string.IsNullOrEmpty(s.Dialogue))
            {
                try { if (replaceName(s.Dialogue) == text) return true; }
                catch { }
            }
            return false;
        }

        private static DialogueScene ResolveScene_Dialogue(string text)
        {
            DialogueSceneManager m = DialogueSceneManager.Instance;
            if (m == null || m.currentDialogueSceneData == null) return null;
            return Match(m.currentDialogueSceneData.dialogueScenes, m.currentDialogueIndex, text, m.ReplaceName);
        }

        private static DialogueScene ResolveScene_Ending2(string text)
        {
            Ending2DialogueManager m = Ending2DialogueManager.Instance;
            if (m == null || m.currentDialogueSceneData == null) return null;
            int idx = -1;
            try { if (_ending2Index != null) idx = (int)_ending2Index.GetValue(m); } catch { }
            return Match(m.currentDialogueSceneData.dialogueScenes, idx, text, m.ReplaceName);
        }

        // ---------------- 手机消息 ----------------

        public static void OnPhoneMessage(string messageText, bool isLeft, PhoneDialogue dialogue)
        {
            try
            {
                if (Plugin.CfgReadPhone == null || !Plugin.CfgReadPhone.Value) return;
                if (string.IsNullOrEmpty(messageText) || messageText.Trim().Length == 0) return;

                string speaker = "";
                if (dialogue != null)
                    speaker = isLeft ? dialogue.LeftName : dialogue.RightName;
                Say(Compose(speaker, messageText));
            }
            catch (Exception e) { Plugin.Log.LogError("OnPhoneMessage: " + e.Message); }
        }

        public static void OnPhoneSticker(PhoneDialogue dialogue)
        {
            try
            {
                if (Plugin.CfgReadPhoneSticker == null || !Plugin.CfgReadPhoneSticker.Value) return;
                if (dialogue == null) return;
                bool isLeft = !string.IsNullOrEmpty(dialogue.LeftText);
                string speaker = isLeft ? dialogue.LeftName : dialogue.RightName;
                Say(Compose(speaker, "图片"));
            }
            catch (Exception e) { Plugin.Log.LogError("OnPhoneSticker: " + e.Message); }
        }

        // ---------------- 选项 ----------------

        public static void BeginChoiceBatch(bool realTime)
        {
            Choices.Clear();
            _realTimeBatch = realTime;
        }

        private static bool _realTimeBatch;

        public static void AddStoryChoice(DialogueScene scene)
        {
            try
            {
                if (scene == null) return;
                string label = Clean(scene.ChoiceText);
                if (string.IsNullOrEmpty(label)) return;

                bool disabled = string.IsNullOrEmpty(scene.ToNumber) || scene.ToNumber == "0.0";
                string to = scene.ToNumber;
                int number = scene.Number;

                Choices.Add(new ChoiceEntry
                {
                    Label = label,
                    Select = disabled ? (Action)null : () => InvokeChoice(to, number)
                });
            }
            catch (Exception e) { Plugin.Log.LogError("AddStoryChoice: " + e.Message); }
        }

        private static void InvokeChoice(string toNumber, int number)
        {
            try
            {
                DialogueSceneManager m = DialogueSceneManager.Instance;
                if (m == null) return;

                // 「选择」型走 OnSelectionButtonClicked，其余（「实时」）走 OnReplyButtonClicked
                if (_onSelectionClicked != null && IsSelectionBatch(m))
                    _onSelectionClicked.Invoke(m, new object[] { toNumber, number });
                else if (_onReplyClicked != null)
                    _onReplyClicked.Invoke(m, new object[] { toNumber });
            }
            catch (Exception e) { Plugin.Log.LogError("选择失败: " + e.Message); }
        }

        private static bool IsSelectionBatch(DialogueSceneManager m)
        {
            try { return m.isSelectionActive; } catch { return false; }
        }

        public static void ClearChoices()
        {
            Choices.Clear();
            _silenceToNumber = null;
        }

        /// <summary>
        /// 倒计时秒数（游戏原版是 8）。
        ///
        /// 关键约束：这里只能返回一个**有限**的值。
        /// 到点后 AutoDestroyAfterTime 会销毁按钮并用本批最后一条的 ToNumber
        /// 继续剧情 —— 那正是游戏的「沉默」分支。把它变成无穷大（v0.5.0 的
        /// 86400 秒）等于删掉这个分支，玩家就再也没法「什么都不做」了。
        /// </summary>
        public static float RealTimeTimeoutSeconds()
        {
            float v = Plugin.CfgRealTimeSeconds != null ? Plugin.CfgRealTimeSeconds.Value : 8f;
            if (v < 1f) v = 1f;
            if (v > 600f) v = 600f;
            return v;
        }

        /// <summary>
        /// 「沉默」分支的目标行号。由 AutoDestroyAfterTime 的前缀捕获 ——
        /// 它就是在 ExecuteRealTimeLogic 里 StartCoroutine 那一刻同步跑到的，
        /// 因此早于本轮选项的朗读，PressSilence 与倒计时走的是同一条路径。
        /// </summary>
        private static string _silenceToNumber;

        public static bool SilenceAvailable()
        {
            return !string.IsNullOrEmpty(_silenceToNumber);
        }

        public static void SetSilenceTarget(DialogueScene defaultScene)
        {
            try
            {
                _silenceToNumber = defaultScene != null ? defaultScene.ToNumber : null;
                if (string.IsNullOrEmpty(_silenceToNumber) || _silenceToNumber == "0.0")
                    _silenceToNumber = null;
            }
            catch { _silenceToNumber = null; }
        }

        /// <summary>立刻走「沉默」分支，等价于原版 8 秒到点后的自动选择。</summary>
        public static void PressSilence()
        {
            string to = _silenceToNumber;
            if (string.IsNullOrEmpty(to))
            {
                Say("现在没有可沉默的限时选择。", false);
                return;
            }
            if (_onReplyClicked == null)
            {
                Say("沉默失败：找不到游戏接口。", false);
                return;
            }
            try
            {
                DialogueSceneManager m = DialogueSceneManager.Instance;
                if (m == null) return;
                Say("沉默。", false);
                _onReplyClicked.Invoke(m, new object[] { to });
            }
            catch (Exception e) { Plugin.Log.LogError("沉默失败: " + e.Message); }
        }

        public static void OnPhoneChoices(PhoneDialogueManager mgr)
        {
            try
            {
                Choices.Clear();
                _realTimeBatch = false;
                if (mgr == null || mgr.choiceParentPrefab == null) return;

                Transform parent = mgr.choiceParentPrefab;
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform child = parent.GetChild(i);
                    if (child == null) continue;
                    var tmp = child.GetComponentInChildren<TextMeshProUGUI>();
                    string label = tmp != null ? Clean(tmp.text) : "";
                    if (string.IsNullOrEmpty(label)) continue;

                    GameObject go = child.gameObject;
                    Choices.Add(new ChoiceEntry
                    {
                        Label = label,
                        Select = () =>
                        {
                            try
                            {
                                var b = go.GetComponent<Button>();
                                if (b != null) b.onClick.Invoke();
                            }
                            catch (Exception e) { Plugin.Log.LogError("手机选项点击失败: " + e.Message); }
                        }
                    });
                }
            }
            catch (Exception e) { Plugin.Log.LogError("OnPhoneChoices: " + e.Message); }
        }

        /// <summary>一批选项生成完毕后调用，统一朗读。</summary>
        public static void AnnounceChoices()
        {
            try
            {
                if (Plugin.CfgReadChoices == null || !Plugin.CfgReadChoices.Value) return;
                if (Choices.Count == 0) return;

                var sb = new StringBuilder();
                sb.Append("共 ").Append(Choices.Count).Append(" 个选项。");
                for (int i = 0; i < Choices.Count; i++)
                {
                    sb.Append("选项 ").Append(i + 1).Append("：").Append(Choices[i].Label).Append("。");
                }
                if (Plugin.CfgChoiceHotkeys != null && Plugin.CfgChoiceHotkeys.Value)
                {
                    if (_realTimeBatch)
                    {
                        int sec = Mathf.RoundToInt(RealTimeTimeoutSeconds());
                        sb.Append("这是限时选择：").Append(sec).Append(" 秒内不选，就算作沉默。");
                        if (SilenceAvailable() && SilenceKeyCode() != KeyCode.None)
                            sb.Append("按 ").Append(SilenceKeyText()).Append(" 可以立刻沉默。");
                        sb.Append("按数字键选择。");
                    }
                    else
                    {
                        sb.Append("按数字键选择。");
                    }
                }

                Say(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogError("AnnounceChoices: " + e.Message); }
        }

        // ---------------- 按键 ----------------

        public static void Update()
        {
            // 在输入框里打字时不抢按键
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null)
                {
                    if (es.currentSelectedGameObject.GetComponent<TMP_InputField>() != null) return;
                }
            }
            catch { }

            if (Plugin.CfgChoiceHotkeys != null && Plugin.CfgChoiceHotkeys.Value && Choices.Count > 0)
            {
                for (int i = 0; i < 9 && i < Choices.Count; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
                    {
                        ChoiceEntry c = Choices[i];
                        if (c.Select != null)
                        {
                            int n = i + 1;
                            Say("已选择 " + n, false);
                            try { c.Select(); } catch (Exception e) { Plugin.Log.LogError("执行选项: " + e.Message); }
                        }
                        else
                        {
                            Say("选项 " + (i + 1) + " 不可选。", false);
                        }
                        break;
                    }
                }
            }

            KeyCode rk = RepeatKeyCode();
            if (rk != KeyCode.None && Input.GetKeyDown(rk) && !string.IsNullOrEmpty(_lastSpoken))
            {
                Speech.Speak(_lastSpoken, true);
            }

            // 沉默键：只在这轮限时选择还没结束时有效
            KeyCode sk = SilenceKeyCode();
            if (sk != KeyCode.None && SilenceAvailable() && Input.GetKeyDown(sk))
            {
                PressSilence();
            }
        }

        private static KeyCode _silenceKey = KeyCode.Alpha0;
        private static string _silenceKeyText = "0";
        private static bool _silenceKeyParsed;

        /// <summary>读取配置里的沉默按键；解析失败回退到 0。</summary>
        private static KeyCode SilenceKeyCode()
        {
            if (_silenceKeyParsed) return _silenceKey;
            _silenceKeyParsed = true;

            string s = Plugin.CfgSilenceKey != null ? Plugin.CfgSilenceKey.Value : "";
            if (s == null || s.Trim().Length == 0)
            {
                _silenceKey = KeyCode.None;
                return _silenceKey;
            }
            s = s.Trim();
            KeyCode parsed;
            if (Enum.TryParse(s, true, out parsed))
            {
                _silenceKey = parsed;
            }
            else
            {
                Plugin.Log.LogWarning("沉默按键 \"" + s + "\" 无法识别，回退为 0。");
                _silenceKey = KeyCode.Alpha0;
            }
            _silenceKeyText = s;
            return _silenceKey;
        }

        private static string SilenceKeyText()
        {
            if (!_silenceKeyParsed) SilenceKeyCode();
            return _silenceKeyText;
        }

        private static KeyCode _repeatKey = KeyCode.Backspace;
        private static bool _repeatKeyParsed;

        /// <summary>读取配置里的重读按键；解析失败则回退 Backspace。</summary>
        private static KeyCode RepeatKeyCode()
        {
            if (_repeatKeyParsed) return _repeatKey;
            _repeatKeyParsed = true;

            string s = Plugin.CfgRepeatKey != null ? Plugin.CfgRepeatKey.Value : "";
            if (string.IsNullOrEmpty(s) || s.Trim().Length == 0)
            {
                _repeatKey = KeyCode.None;
                return _repeatKey;
            }
            KeyCode parsed;
            if (Enum.TryParse(s.Trim(), true, out parsed))
            {
                _repeatKey = parsed;
            }
            else
            {
                Plugin.Log.LogWarning("重读按键 \"" + s + "\" 无法识别，回退为 Backspace。");
                _repeatKey = KeyCode.Backspace;
            }
            return _repeatKey;
        }
    }

    // ================= Harmony 补丁 =================

    internal static class Patches
    {
        // ---- 剧情文本：两个管理器都挂 ----

        [HarmonyPatch(typeof(DialogueSceneManager), "StartTyping", new Type[] { typeof(string) })]
        [HarmonyPostfix]
        internal static void StoryText(string text)
        {
            Reader.OnStoryText(text);
        }

        [HarmonyPatch(typeof(Ending2DialogueManager), "StartTyping", new Type[] { typeof(string) })]
        [HarmonyPostfix]
        internal static void StoryTextEnding2(string text)
        {
            Reader.OnStoryTextEnding2(text);
        }

        // ---- 手机消息 ----

        [HarmonyPatch(typeof(PhoneDialogueManager), "AddMessage")]
        [HarmonyPatch(new Type[] { typeof(string), typeof(bool), typeof(PhoneDialogue) })]
        [HarmonyPostfix]
        internal static void PhoneMessage(string messageText, bool isLeft, PhoneDialogue dialogue)
        {
            Reader.OnPhoneMessage(messageText, isLeft, dialogue);
        }

        [HarmonyPatch(typeof(PhoneDialogueManager), "HandleExpressionMessage", new Type[] { typeof(PhoneDialogue) })]
        [HarmonyPostfix]
        internal static void PhoneSticker(PhoneDialogue dialogue)
        {
            Reader.OnPhoneSticker(dialogue);
        }

        // ---- 选项 ----

        [HarmonyPatch(typeof(DialogueSceneManager), "ExecuteRealTimeLogic", new Type[] { typeof(DialogueScene) })]
        [HarmonyPrefix]
        internal static void BeginRealTime()
        {
            Reader.BeginChoiceBatch(true);
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "ExecuteSelectionLogic", new Type[] { typeof(DialogueScene) })]
        [HarmonyPrefix]
        internal static void BeginSelection()
        {
            Reader.BeginChoiceBatch(false);
        }

        /// <summary>
        /// 「实时」限时选择的倒计时（AutoDestroyAfterTime 协程）。
        ///
        /// 这里只延长 delay 参数，绝不能返回 false 跳过方法：
        /// AutoDestroyAfterTime 是迭代器方法，跳过它会让它返回 null，
        /// 而调用方是 StartCoroutine(AutoDestroyAfterTime(...)) —— 传入
        /// null 会直接抛异常。原写法（v0.2.0）就有这个隐患。
        ///
        /// 但延长也必须是**有限**的。8 秒到点后这个方法会销毁选项按钮、
        /// 并用 defaultScene.ToNumber 继续剧情 —— 那就是游戏的沉默分支。
        /// v0.5.0 把它设成 86400 秒，等于把沉默这个选项从游戏里删掉了，
        /// 玩家再也没法「什么都不做」。现在改成可配置的有限秒数（默认 20），
        /// 同时把 defaultScene 记下来供「沉默按键」立即触发同一条路径。
        ///
        /// 这个前缀在 StartCoroutine 那一刻同步执行，也就是 ExecuteRealTimeLogic
        /// 内部、早于它的 Postfix（朗读选项），所以朗读时沉默目标已经就位。
        /// </summary>
        [HarmonyPatch(typeof(DialogueSceneManager), "AutoDestroyAfterTime")]
        [HarmonyPatch(new Type[] { typeof(float), typeof(DialogueScene) })]
        [HarmonyPrefix]
        internal static void ExtendRealTimeTimeout(ref float delay, DialogueScene defaultScene)
        {
            Reader.SetSilenceTarget(defaultScene);
            delay = Reader.RealTimeTimeoutSeconds();
        }

        /// <summary>
        /// 倒计时进度条（FadingSlider.FadeSliderOverTime）原本写死 8 秒，
        /// 和上面被拉长的 delay 对不上：条走完了剧情却还停着，看起来像卡死。
        /// 这里把它的时长改成同一个值。FadingSlider 全游戏只用在限时选择的
        /// 计时条上（DialogueSceneManager.rtcTimer / Ending2DialogueManager.rtcTimer）。
        ///
        /// 同样只改参数不跳过：它是迭代器方法。
        /// </summary>
        [HarmonyPatch(typeof(FadingSlider), "FadeSliderOverTime", new Type[] { typeof(float) })]
        [HarmonyPrefix]
        internal static void MatchFadeToTimeout(ref float duration)
        {
            duration = Reader.RealTimeTimeoutSeconds();
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "GenerateReplyButton", new Type[] { typeof(DialogueScene) })]
        [HarmonyPostfix]
        internal static void ReplyButton(DialogueScene scene)
        {
            Reader.AddStoryChoice(scene);
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "GenerateSelectionButton")]
        [HarmonyPatch(new Type[] { typeof(DialogueScene), typeof(int) })]
        [HarmonyPostfix]
        internal static void SelectionButton(DialogueScene scene)
        {
            Reader.AddStoryChoice(scene);
        }

        // 批次结束：ExecuteXXXLogic 返回后朗读
        [HarmonyPatch(typeof(DialogueSceneManager), "ExecuteRealTimeLogic", new Type[] { typeof(DialogueScene) })]
        [HarmonyPostfix]
        internal static void EndRealTime()
        {
            Reader.AnnounceChoices();
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "ExecuteSelectionLogic", new Type[] { typeof(DialogueScene) })]
        [HarmonyPostfix]
        internal static void EndSelection()
        {
            Reader.AnnounceChoices();
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "ClearAllReplyButtons", new Type[] { })]
        [HarmonyPrefix]
        internal static void ClearStoryChoices()
        {
            Reader.ClearChoices();
        }

        // ---- 手机选项 ----

        [HarmonyPatch(typeof(PhoneDialogueManager), "HandleChoiceMessages", new Type[] { })]
        [HarmonyPostfix]
        internal static void PhoneChoices(PhoneDialogueManager __instance)
        {
            Reader.OnPhoneChoices(__instance);
            Reader.AnnounceChoices();
        }

        // ---- 按键归属：拦住游戏自己那次多余的推进 ----

        /// <summary>
        /// 回车/空格在导航模式里归我们（激活选中控件）。但游戏的两个管理器
        /// 各自在 Update 里也读同一个按键推进剧情，于是同一次按键会做两件事：
        /// 既激活了控件，又推进了一句剧情。
        ///
        /// 这里只拦「那一帧的那一次按键」，并且放行我们自己激活控件时引发的
        /// 推进（例如全屏热区按钮）。判定全部在 UiNav.BlockGameAdvance 里，
        /// 不依赖两个 Update 谁先执行。
        ///
        /// 注意：DialogueButtonClicked 返回 void，Prefix 返回 false 跳过它是安全的。
        /// （对比 AutoDestroyAfterTime 是迭代器方法，跳过它会让调用方拿到 null。）
        /// </summary>
        [HarmonyPatch(typeof(DialogueSceneManager), "DialogueButtonClicked", new Type[] { })]
        [HarmonyPrefix]
        internal static bool BlockAdvanceDialog()
        {
            return !UiNav.BlockGameAdvance;
        }

        [HarmonyPatch(typeof(Ending2DialogueManager), "DialogueButtonClicked", new Type[] { })]
        [HarmonyPrefix]
        internal static bool BlockAdvanceEnding2()
        {
            return !UiNav.BlockGameAdvance;
        }

        [HarmonyPatch(typeof(PhoneDialogueManager), "StopAndClearDialogue", new Type[] { })]
        [HarmonyPrefix]
        internal static void ClearPhoneChoices()
        {
            Reader.ClearChoices();
        }
    }
}
