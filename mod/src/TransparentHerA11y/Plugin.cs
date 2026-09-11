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
    [BepInPlugin(Guid, "TransparentHer A11y Reader", "1.1.0")]
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
            // 注意：游戏原生占用了以下按键，不要选它们
            //   A=自动  F=快进  P=打开主菜单  R=打开历史回顾/语音收藏
            //   Ctrl=快进  空格/回车/小键盘回车=推进  Esc=菜单
            CfgRepeatKey = Config.Bind("朗读", "重读按键", "Backspace",
                "重新朗读当前这一句的按键。填 KeyCode 名称，例如 Backspace、Tab、Q、F1、Home。\n" +
                "留空则关闭这个功能。\n" +
                "警告：R 已被游戏用作「打开历史回顾」，P 是「打开主菜单」，A 是自动，F 是快进，不要填这些。");

            // 提前探测 DLL，方便在日志里看出问题
            try
            {
                Nvda.Speak(" ", false);
                Log.LogInfo("NVDA 控制器 DLL: " + (Nvda.DllOk ? "已加载" : "未加载"));
                if (!string.IsNullOrEmpty(Nvda.LastError))
                    Log.LogWarning("NVDA: " + Nvda.LastError);
            }
            catch (Exception e)
            {
                Log.LogWarning("NVDA 探测异常: " + e.Message);
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
            try { Reader.Update(); }
            catch (Exception e) { Log.LogError("Update 异常: " + e.Message); }
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
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
            Nvda.Speak(text, interrupt);
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
                    Nvda.Stop();
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

        public static void BeginChoiceBatch()
        {
            Choices.Clear();
        }

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
        }

        public static void OnPhoneChoices(PhoneDialogueManager mgr)
        {
            try
            {
                Choices.Clear();
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
                    sb.Append("按数字键选择。");

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
                Nvda.Speak(_lastSpoken, true);
            }
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
            Reader.BeginChoiceBatch();
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "ExecuteSelectionLogic", new Type[] { typeof(DialogueScene) })]
        [HarmonyPrefix]
        internal static void BeginSelection()
        {
            Reader.BeginChoiceBatch();
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

        [HarmonyPatch(typeof(PhoneDialogueManager), "StopAndClearDialogue", new Type[] { })]
        [HarmonyPrefix]
        internal static void ClearPhoneChoices()
        {
            Reader.ClearChoices();
        }
    }
}
