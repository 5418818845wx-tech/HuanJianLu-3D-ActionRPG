using System.Collections.Generic;
using MyGame.Audio;
using MyGame.UI;
using UnityEngine;
using Yarn.Unity;

namespace MyGame.Dialogue
{
    /// <summary>
    /// Yarn Spinner 桥接层（Dialogue Presenter）：把 Yarn 的「台词 / 选项」转交给项目现有的 UGUIDialoguePanel
    ///
    /// 职责边界（重要）：
    /// - 本类只做「Yarn 事件 → 界面」的搬运，不含任何剧情判断
    /// - 剧本走向、分支、条件全部写在 .yarn 文本里，策划改剧情不需要碰 C#
    ///
    /// 工作流程：
    /// 1. Yarn 的 DialogueRunner 遇到一句台词 → 调 RunLineAsync → 本类把文本丢给对话面板并等玩家点「继续」
    /// 2. 遇到选项 → 调 RunOptionsAsync → 本类把选项丢给对话面板并等玩家点某一项，再把选中的 DialogueOption 还给 Runner
    /// 3. 对话整体开始/结束 → OnDialogueStartedAsync / OnDialogueCompleteAsync 负责开关面板
    /// 4. 台词上的行元数据（剧本行尾的 #accent 等）在这里被翻译成表现样式
    ///
    /// 与面板的约定（沿用 UGUI 迁移时定下的顺序）：
    /// 必须先 ShowLine 注入内容，再 ShowPanel 显示。
    /// 因为 UGUIDialoguePanel.OnEnter 里有一段「没接真实对话系统就播演示对话」的兜底，
    /// ShowLine 会把它的 m_hasExternalLine 置位，从而跳过演示对话，避免演示按钮和真实按钮同时出现
    /// </summary>
    [DisallowMultipleComponent]
    public class YarnDialoguePresenter : DialoguePresenterBase
    {
        /// <summary>「继续」按钮对应的索引（与 UGUIDialoguePanel 的约定一致）</summary>
        private const int k_continueIndex = -1;

        /// <summary>强调标签：剧本里在行尾写 #accent 就会命中它（Yarn 给的元数据不带 #）</summary>
        private const string k_accentTag = "accent";

        /// <summary>强调色（与追踪 HUD 的「可交付」黄一致）</summary>
        private const string k_accentOpenTag = "<color=#FFC400>";

        /// <summary>强调样式收尾（TMP 富文本必须闭合，否则会漏到后面的内容）</summary>
        private const string k_accentCloseTag = "</color>";

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>对话面板引用（对话开始时取一次）</summary>
        private UGUIDialoguePanel m_panel;

        /// <summary>等待玩家做选择的完成源（点选项/点继续时被填上结果）</summary>
        private YarnTaskCompletionSource<int> m_choiceSource;

        /// <summary>最近一句台词的说话人（显示选项时要把这句一起留在屏幕上）</summary>
        private string m_speaker = string.Empty;

        /// <summary>最近一句台词的正文（同上）</summary>
        private string m_text = string.Empty;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void OnDisable()
        {
            // 组件被禁用时兜底放行，避免 Yarn 的 Runner 永远等不到结果而卡死
            UnbindPanel();
            ReleasePendingChoice(k_continueIndex);
        }

        // ====================================================================
        // DialoguePresenterBase 实现
        // ====================================================================

        /// <summary>对话开始：先取到对话面板，真正显示交给第一句台词</summary>
        public override YarnTask OnDialogueStartedAsync()
        {
            m_panel = UGUIManager.Instance != null
                ? UGUIManager.Instance.GetPanel<UGUIDialoguePanel>(EUIPanelID.Dialogue)
                : null;

            if (m_panel == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 找不到 UGUIDialoguePanel，Yarn 台词将无法显示", this);
            }

            return YarnTask.CompletedTask;
        }

        /// <summary>对话结束：关闭面板并解绑所有回调</summary>
        public override YarnTask OnDialogueCompleteAsync()
        {
            UnbindPanel();
            ReleasePendingChoice(k_continueIndex);

            if (m_panel != null && m_panel.IsVisible && UGUIManager.Instance != null)
            {
                UGUIManager.Instance.HidePanel(EUIPanelID.Dialogue);
            }

            m_panel = null;
            return YarnTask.CompletedTask;
        }

        /// <summary>
        /// 显示一句台词：文本进面板，然后等玩家点「继续」。
        /// 返回的 Task 完成时，Runner 才会推进到剧本的下一句
        /// </summary>
        public override async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
        {
            m_speaker = line.CharacterName ?? string.Empty;

            // 正文必须用 TextWithoutCharacterName：
            // RawText 里带着「说话人:」前缀，直接用会和说话人栏重复显示
            m_text = line.TextWithoutCharacterName.Text;

            // 行元数据 → 演出：剧本里给这行打了 #accent，就用强调色显示
            // 这就是"文本驱动表现"的挂点，以后 #voice / #camera / #shake 都接在这里
            if (HasMetadata(line.Metadata, k_accentTag))
            {
                m_text = k_accentOpenTag + m_text + k_accentCloseTag;
            }

            // choices 传 null：面板会自动生成一个「继续」按钮
            ShowLine(m_speaker, m_text, null);

            await WaitForChoiceAsync();
        }

        /// <summary>
        /// 显示一组选项：把上一句台词留在屏幕上，选项作为按钮排出来，等玩家选一项
        /// </summary>
        public override async YarnTask<DialogueOption> RunOptionsAsync(DialogueOption[] dialogueOptions, LineCancellationToken token)
        {
            if (dialogueOptions == null || dialogueOptions.Length == 0)
            {
                return null;
            }

            List<string> labels = new List<string>(dialogueOptions.Length);
            for (int i = 0; i < dialogueOptions.Length; i++)
            {
                // 与台词同理：用去掉说话人前缀的正文，避免选项按钮上多出「名字:」
                labels.Add(dialogueOptions[i].Line.TextWithoutCharacterName.Text);
            }

            ShowLine(m_speaker, m_text, labels);

            int index = await WaitForChoiceAsync();

            // 玩家没选（对话被中断）或索引越界：交回 null，让 Runner 按自己的策略处理
            if (index < 0 || index >= dialogueOptions.Length)
            {
                return null;
            }

            return dialogueOptions[index];
        }

        // ====================================================================
        // 显示与等待
        // ====================================================================

        /// <summary>把一行内容交给对话面板，并确保面板处于显示状态</summary>
        private void ShowLine(string speaker, string text, IReadOnlyList<string> choices)
        {
            if (m_panel == null || UGUIManager.Instance == null)
            {
                return;
            }

            // 对话音：这一行带选项时用"选项出现"音，普通台词用"翻页"音
            bool hasChoices = choices != null && choices.Count > 0;
            AudioManager.Play(hasChoices ? ESfx.Dialogue_Choice : ESfx.Dialogue_Next, 0.7f);

            // 顺序不能颠倒：先注入内容（置位 m_hasExternalLine 跳过演示对话），再显示面板
            m_panel.ShowLine(speaker, text, choices);
            UGUIManager.Instance.ShowPanel(EUIPanelID.Dialogue);
        }

        /// <summary>
        /// 这一行的元数据里有没有指定标签。
        /// Yarn 传过来的元数据是「不带 # 的裸标签」（如剧本写 #accent，这里就收到 "accent"），
        /// 多个标签会被拆成多项；标签里不能含空格，否则会被拆开
        /// </summary>
        private static bool HasMetadata(string[] metadata, string tag)
        {
            if (metadata == null)
            {
                return false;
            }

            for (int i = 0; i < metadata.Length; i++)
            {
                if (metadata[i] == tag)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>等玩家点一个按钮：返回选项索引，点「继续」返回 -1</summary>
        private async YarnTask<int> WaitForChoiceAsync()
        {
            if (m_panel == null)
            {
                // 没有面板就别把 Runner 卡住，直接当「继续」放行
                return k_continueIndex;
            }

            // 上一轮的等待理论上已经结束；这里保险起见先放行，避免悬挂
            ReleasePendingChoice(k_continueIndex);

            m_choiceSource = new YarnTaskCompletionSource<int>();
            m_panel.ChoiceSelected += HandleChoiceSelected;

            return await m_choiceSource.Task;
        }

        /// <summary>面板选项被点击：解绑并唤醒等待</summary>
        private void HandleChoiceSelected(int index)
        {
            UnbindPanel();
            ReleasePendingChoice(index);
        }

        /// <summary>把等待中的完成源填上结果（可安全重复调用）</summary>
        private void ReleasePendingChoice(int index)
        {
            YarnTaskCompletionSource<int> source = m_choiceSource;
            m_choiceSource = null;

            source?.TrySetResult(index);
        }

        /// <summary>解绑面板的选项事件（可安全重复调用）</summary>
        private void UnbindPanel()
        {
            if (m_panel != null)
            {
                m_panel.ChoiceSelected -= HandleChoiceSelected;
            }
        }
    }
}
