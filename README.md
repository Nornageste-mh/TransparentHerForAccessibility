# TransparentHerForAccessibility

本仓库是**专门针对 Steam 游戏《透明的她与真实的我》（TransparentHer）** 的屏幕阅读器
辅助模组（游戏内 BepInEx 插件，通过 Tolk / NVDA Controller Client / SAPI 朗读）。

> ## ⚠️ 郑重警告（请务必阅读）
>
> - 本项目是 **Vibe coding 产物**（AI 辅助生成），**非官方**，与游戏开发商
>   **可味玩 KawayiPlay** 无关。
> - **请务必支持正版：本辅助仅面向已在 Steam 购买《透明的她与真实的我》的玩家。**
>   **强烈要求每一位使用者通过 Steam 购买正版游戏。** 我们坚决反对任何形式的盗版、
>   破解、未授权传播；请勿将本工具用于协助获取或游玩盗版副本，请尊重开发者的劳动成果。
> - 本项目**不保证可用、不保证稳定**，代码可能存在各种问题（兼容性、稳定性、安全性等），
>   **无任何维护承诺**。使用风险自负，仅供个人学习/研究参考，请勿用于商业或分发牟利。
> - **建议在完全理解代码的前提下再使用**，自行承担一切后果。
> - 本仓库**不包含任何游戏资源**，只发布运行时补丁代码与文档。
> - 如您是《透明的她与真实的我》的开发者或版权方，认为本仓库构成侵权，请联系我们移除。

---

## 说明

本辅助的作用：让使用读屏软件的玩家，也能基本正常地游玩《透明的她与真实的我》。

- 朗读**没有配音**的剧情文本（旁白、主角「我」、无配音配角），有配音的台词只放语音并打断朗读
- 朗读手机（WeSay）聊天消息
- 朗读选项，并用数字键 `1`-`9` 选择（游戏原本的选项按钮按屏幕坐标散落摆放，读屏无法定位）
- 限时选择延长倒计时、并提供「沉默」键
- 主菜单 / 存读档 / 设置 / 画廊的键盘导航与朗读（游戏原本这些界面几乎只能鼠标点）

游戏版本：Unity 2022.3.43f1c1，Mono 后端。同时兼容**通过 Steam 购买的正式版**，
以及官方发布的**独立试玩版**（可直接运行，不需要 Steam）。

---

## 安装（仅限正版玩家）

**最快的装法**：到 [Releases](../../releases) 下载整合包 zip，解压后把里面的东西
**整体**拷进游戏根目录（有 `TransparentHer.exe` 的那一层）。zip 内容就是下文的
`mod\package\`，另多一个 `licenses\` —— 第三方组件的许可证与来源说明，**别删**。

想自己构建、或只想往已有的 BepInEx 里加一个插件，看下面。

补丁由 BepInEx 在运行时挂载，**不修改任何游戏文件**，所以安装就是「拷文件」。
把 `mod\package\` 里的东西**全部复制进游戏根目录**（有 `TransparentHer.exe` 的那一层），
保持目录结构不变。三条容易踩的坑：

- `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version` 必须在**游戏根目录**
- `BepInEx\core\` 里 16 个文件一个都不能少
- `nvdaControllerClient.dll` 放游戏根目录（Mono 查找 DLL 时先看应用目录）

如果已经装过别的 BepInEx 模组，只需要两个文件：
`TransparentHerA11y.dll` → `BepInEx\plugins\`，`nvdaControllerClient.dll` → 游戏根目录。
**不要**覆盖对方已有的 `winhttp.dll` 和 `BepInEx\core\`。

卸载：删掉游戏目录下的 `winhttp.dll` 即完全失效；连同 `BepInEx\` 一并删掉就彻底干净。
存档在 `%USERPROFILE%\AppData\LocalLow\Kawayi Play\TransparentHer\`，不受影响。

详细步骤、按键表、配置说明、故障排查见 [`mod/package/安装说明.txt`](mod/package/安装说明.txt)。

> 再次提醒：请通过 Steam 购买正版《透明的她与真实的我》后再使用本辅助。

---

## 主要快捷键

| 快捷键 | 功能 |
| --- | --- |
| `1` - `9` | 选择对应编号的选项（剧情选项、手机选项通用） |
| `0` | 限时选择里「立刻沉默」（什么都不做，直接继续） |
| `Backspace` | 重读当前这一句 |
| `Tab` | 进入 / 退出界面导航模式 |
| `↑` `↓` | 上一项 / 下一项 |
| `←` `→` | 调整滑条 |
| `回车` / `空格` | 导航模式下激活控件；否则交给游戏推进剧情 |
| `Home` / `End` | 第一项 / 最后一项 |
| `PageUp` / `PageDown` | 切换面板组 |

**游戏原生占用的键**（模组不会去抢，也不要拿来当重读键）：
`A` 自动模式、`F` 快进、`P` 主菜单、`R` 历史回顾、`Ctrl` 快进、
`空格`/`回车`/小键盘回车 推进、`Esc` 菜单。

后四个模组自用的键都可以在配置里改或关掉。

---

## 已知问题 / 注意事项

- 通过读取游戏运行时信息工作，**游戏更新后可能失效**（补丁点是按游戏内方法名挂的）
- 游戏把一部分界面文字**画成了图片**（设置界面的行名、页签名，标题画面的
  `START` / `EXIT`），读屏永远拿不到。模组按 Unity 对象名做了中文映射，
  见 `UiNav.NameAlias`，每条的依据都写在注释里
- 限时选择里，游戏自带的**倒计时音效固定 8 秒**，不会跟着拉长的倒计时变长
  （那是音频资源本身的长度）
- 未覆盖所有界面与交互，下列内容还没做：历史回顾面板（`R`）朗读、
  CG / Spine 画面口述、视频口述影像、手机贴纸的内容描述、未配音台词的 TTS 预生成
- 语音朗读依赖所选后端（Tolk / NVDA / SAPI），中文需要中文语音

---

## 构建

```powershell
cd mod
.\build.ps1
```

会自动下载 BepInEx 5.4.23.5 (win x64) 与 NVDA Controller Client (x64)，
编译插件并组装 `mod\package\`。第三方二进制不入库，全靠这个脚本复现。
需要 .NET SDK（本项目用 10.0.301 验证过）。

> `build.ps1` 与 `tools\check-staged.ps1` 是 `.ps1`，必须保存为
> **UTF-8 带 BOM** —— Windows PowerShell 5.1 读无 BOM 的脚本会按 GBK 解码，
> 中文注释变乱码并直接语法错误。提交闸门会拦住这种情况。

### 目录结构

```
.
├─ mod/
│  ├─ build.ps1                 下依赖 → 编译 → 组包
│  ├─ src/TransparentHerA11y/
│  │  ├─ Plugin.cs              补丁与朗读逻辑
│  │  ├─ Speech.cs              Tolk / NVDA / SAPI 后端调度
│  │  ├─ Nvda.cs                NVDA Controller Client 封装
│  │  └─ UiNav.cs               界面键盘导航与朗读
│  └─ package/                  安装包（直接拷进游戏根目录）
│     └─ 安装说明.txt           ← 用户文档，先看这个
│
└─ （以下目录不入库，见 .gitignore）
   extracted/    从游戏提取的剧本文本（版权内容）
   decompiled/   反编译的游戏代码（版权内容）
```

### 模组做了什么

| 功能 | 挂载点 |
|---|---|
| 朗读无配音剧情文本 | `DialogueSceneManager.StartTyping`、`Ending2DialogueManager.StartTyping` |
| 朗读手机（WeSay）消息 | `PhoneDialogueManager.AddMessage` |
| 手机表情提示 | `PhoneDialogueManager.HandleExpressionMessage` |
| 朗读选项 | `GenerateReplyButton`、`GenerateSelectionButton`、`HandleChoiceMessages` |
| 数字键 1-9 选择选项 | 反射调用 `OnReplyButtonClicked` / `OnSelectionButtonClicked` |
| 限时选择延长倒计时 + 沉默键 | `AutoDestroyAfterTime`（只改 `ref float delay`）、`FadingSlider.FadeSliderOverTime` |
| 菜单 / 存档 / 设置键盘导航 | 无挂载点，`UiNav` 每帧扫描 `Selectable` |
| 按键归属：回车 / 空格 | `DialogueButtonClicked`（两个管理器各一份） |

**判定有无配音的方式**：用 `StartTyping` 收到的最终文本反查 `DialogueScene`，
读其 `VoiceFilename` 字段是否为空。

### 几条踩过的坑（改之前请先读）

- **绝不对迭代器方法用「Prefix 返回 false 跳过」**：那会让它返回 `null`，
  而调用方是 `StartCoroutine(...)`。`AutoDestroyAfterTime` 只能改 `ref` 参数。
- **界面重扫不能定时**：激活按钮后 0.35 秒重扫会撞上场景销毁，触发无 C# 异常的
  原生崩溃（鼠标进入正常、纯键盘进入必崩）。现在只扫「下一帧」并加场景稳定门禁。
- **`EventSystem.sendNavigationEvents` 常驻关闭**：游戏自己读 `Input`，不依赖
  uGUI 的选中态；不关的话，鼠标点过的按钮会在按空格推进剧情时被重复点击。
- **限时选择的倒计时是一条剧情分支，不是计时器**：到点会走「沉默」分支。
  把它设成永不超时等于删掉这个选项。
- **找特定控件别用词边界正则**：`ExitButton` / `QuitGame` 是驼峰拼接，
  `\b(exit|quit)\b` 两头都会错。直接拿实查到的对象名全等比较。
- **`Enum.TryParse("0")` 会成功但得到 `KeyCode.None`**（数值 0），不是 `Alpha0`。
  按键配置项要先自己映射单个数字。
- **别用 PowerShell 的文本 cmdlet 改源码**：`Get-Content -Raw` 在
  Windows PowerShell 5.1 下按 ANSI 代码页读取无 BOM 的 UTF-8，中文会变乱码。

---

## 如何复现分析

分析过程用 Python（需 `UnityPy`）扫描 Addressables bundle、定位并导出 TextAsset，
反编译游戏逻辑（需 `ilspycmd`）：

```powershell
pip install UnityPy
dotnet tool install --global ilspycmd
ilspycmd -p -o decompiled "<游戏目录>\TransparentHer_Data\Managed\Assembly-CSharp.dll"
```

生成的 `extracted/`、`decompiled/` 均为游戏版权内容，**已被 .gitignore 排除，
请勿提交或分发**。

---

## 合规说明

- 在绝大部分国家和地区，为自己使用而修改你**合法拥有**的软件通常是允许的；
  但把**修改后的软件提供给第三方**，以及**绕过技术保护措施**，
  在绝大部分国家和地区都是**不被允许**的。
  各国规定不尽相同，请以你所在地的法律为准。
- 本模组因此**不包含任何游戏资源**，只发布补丁代码；整合包（BepInEx 运行时 +
  补丁 + 说明）通过本仓库的 Releases 提供，其中**不含游戏本体、不含任何游戏资源**。
- 模组不绕过任何技术保护措施，不修改、不替换、不再分发游戏文件。

---

## 许可

补丁代码与文档：见仓库内说明。第三方组件：
BepInEx 5.4.23.5（LGPL-2.1）、NVDA Controller Client（LGPL-2.1）。
