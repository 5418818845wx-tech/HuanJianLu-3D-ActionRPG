using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 对话面板（UGUI 版）：屏幕底部的对话框，显示说话人、正文与选项
    ///
    /// 定位：本面板是「UI 层」，不含任何对话流程逻辑
    /// - 显示什么由外部对话系统通过 ShowLine() 注入
    /// - 玩家选了哪一项通过 ChoiceSelected 事件回抛给外部
    /// 这样对话流程（分支、条件、任务触发）可以独立演进，不受 UI 改动影响
    ///
    /// 典型用法（外部对话系统）：
    ///   var panel = UGUIManager.Instance.GetPanel&lt;UGUIDialoguePanel&gt;(EUIPanelID.Dialogue);
    ///   panel.ChoiceSelected += OnChoiceSelected;
    ///   UGUIManager.Instance.ShowPanel(EUIPanelID.Dialogue);
    ///   panel.ShowLine("药师 阿禾", "你来得正好。", new[] { "需要帮忙吗？", "我先走了" });
    ///
    /// 选项约定：
    /// - 传入选项数组 → 每个选项一个按钮，点击后 ChoiceSelected 收到对应索引（0 起）
    /// - 传入空数组 → 自动显示一个"继续"按钮，点击后 ChoiceSelected 收到 -1
    ///
    /// 遮罩说明：遮罩只是压暗背景并拦截点击，**不**做「点击关闭」——
    /// 对话必须靠选择选项推进，避免玩家误点把剧情跳掉
    /// </summary>
    public class UGUIDialoguePanel : UGUIPanel
    {
        // ====================================================================
        // 文案常量
        // ====================================================================

        /// <summary>无选项时的默认按钮文本，点击后回抛索引 -1</summary>
        private const string k_continueText = "继续";

        /// <summary>"继续"选项对应的索引（与真实选项索引区分开）</summary>
        private const int k_continueIndex = -1;

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("对话内容")]
        [Tooltip("说话人名字文本")]
        [SerializeField] private TextMeshProUGUI m_speakerText;

        [Tooltip("对话正文文本")]
        [SerializeField] private TextMeshProUGUI m_textText;

        [Header("选项")]
        [Tooltip("选项按钮容器（建议挂 VerticalLayoutGroup）")]
        [SerializeField] private RectTransform m_choicesContainer;

        [Tooltip("选项按钮 prefab（根节点需有 Button + Image，子物体 Label 为文本）")]
        [SerializeField] private GameObject m_choicePrefab;

        // ====================================================================
        // 对外事件
        // ====================================================================

        /// <summary>
        /// 玩家选择了一个选项：参数为选项索引（"继续"为 -1）
        /// 由外部对话系统订阅，决定下一步显示什么或关闭面板
        /// </summary>
        public event Action<int> ChoiceSelected;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>动态生成的选项按钮实例</summary>
        private readonly List<GameObject> m_choiceButtons = new();

        /// <summary>是否已经由外部注入过内容（注入后不再自动播放演示对话）</summary>
        private bool m_hasExternalLine;

        /// <summary>演示对话当前行索引</summary>
        private int m_demoLineIndex;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.Dialogue;
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>
        /// 面板进入显示：若外部尚未注入内容，则播放一段演示对话，便于单独预览效果
        /// </summary>
        public override void OnEnter()
        {
            base.OnEnter();

            if (!m_hasExternalLine)
            {
                ShowDemoLine(0);
            }
        }

        /// <summary>面板退出显示：清理选项按钮并重置演示进度，下次打开从头播放</summary>
        public override void OnExit()
        {
            ClearChoices();
            m_demoLineIndex = 0;

            base.OnExit();
        }

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>
        /// 显示一句话（外部对话系统调用）
        /// </summary>
        /// <param name="speaker">说话人名字</param>
        /// <param name="text">对话正文</param>
        /// <param name="choices">选项文本列表；传 null 或空数组时显示"继续"按钮</param>
        public void ShowLine(string speaker, string text, IReadOnlyList<string> choices)
        {
            // 一旦外部注入内容，就永久停用演示对话，避免二者互相覆盖
            m_hasExternalLine = true;

            ApplyLine(speaker, text, choices);
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>把一句话套用到界面：设置文本并重建选项按钮</summary>
        private void ApplyLine(string speaker, string text, IReadOnlyList<string> choices)
        {
            if (m_speakerText != null)
            {
                m_speakerText.text = speaker;
            }

            if (m_textText != null)
            {
                m_textText.text = text;
            }

            RebuildChoices(choices);
        }

        /// <summary>
        /// 重建选项按钮
        /// 每次重建都会销毁旧按钮，其回调随物体一并释放，不会累积
        /// </summary>
        private void RebuildChoices(IReadOnlyList<string> choices)
        {
            ClearChoices();

            if (m_choicesContainer == null || m_choicePrefab == null)
            {
                return;
            }

            // 无选项：给一个"继续"按钮，让玩家能推进对话
            if (choices == null || choices.Count == 0)
            {
                AddChoiceButton(k_continueText, k_continueIndex);
                return;
            }

            for (int i = 0; i < choices.Count; i++)
            {
                AddChoiceButton(choices[i], i);
            }
        }

        /// <summary>创建单个选项按钮</summary>
        private void AddChoiceButton(string label, int index)
        {
            GameObject button = Instantiate(m_choicePrefab, m_choicesContainer);

            Transform labelTransform = button.transform.Find("Label");
            TextMeshProUGUI tmp = labelTransform != null ? labelTransform.GetComponent<TextMeshProUGUI>() : null;
            if (tmp != null)
            {
                tmp.text = label;
            }

            // 代码绑定：选项是运行时生成的，无法在 Inspector 里拖曳
            // 捕获的 index 是参数副本，且按钮重建时会被销毁，不会造成泄漏
            Button btn = button.GetComponent<Button>();
            if (btn == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 选项 prefab 上没有 Button 组件，选项无法点击", button);
            }
            else
            {
                int capturedIndex = index;
                btn.onClick.AddListener(() => HandleChoiceClicked(capturedIndex));
            }

            m_choiceButtons.Add(button);
        }

        /// <summary>销毁已生成的选项按钮</summary>
        private void ClearChoices()
        {
            int i = 0;
            while (i < m_choiceButtons.Count)
            {
                if (m_choiceButtons[i] != null)
                {
                    Destroy(m_choiceButtons[i]);
                }
                i = i + 1;
            }

            m_choiceButtons.Clear();
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>选项点击：把索引回抛给外部；演示模式下自动推进</summary>
        private void HandleChoiceClicked(int index)
        {
            // 把选择结果抛给外部对话系统（未订阅时 Invoke 为空操作）
            ChoiceSelected?.Invoke(index);

            // 演示模式：没有外部系统接管时自行推进，避免点了没反应
            if (!m_hasExternalLine)
            {
                AdvanceDemo(index);
            }
        }

        // ====================================================================
        // 演示对话（接入真实对话系统后整体删除）
        // ====================================================================

        /// <summary>演示对话的单行数据</summary>
        private class DemoLine
        {
            public string Speaker;
            public string Text;
            public string[] Choices;
        }

        /// <summary>演示对话内容：两轮问答，最后一轮选完自动关闭面板</summary>
        private DemoLine[] m_demoLines;

        /// <summary>播放演示对话的第 index 行；越界表示对话结束，关闭面板</summary>
        private void ShowDemoLine(int index)
        {
            if (m_demoLines == null)
            {
                m_demoLines = new DemoLine[]
                {
                    new DemoLine
                    {
                        Speaker = "药师 阿禾",
                        Text = "你来得正好。试炼场那边最近不太平，好几位旅人都挂了彩。",
                        Choices = new string[] { "需要我做些什么？", "我只是路过而已" },
                    },
                    new DemoLine
                    {
                        Speaker = "药师 阿禾",
                        Text = "去清掉那几名游荡的敌兵吧。带上这几瓶药水，路上当心。",
                        Choices = new string[] { "交给我" },
                    },
                };
            }

            // 越界 = 对话结束
            if (index < 0 || index >= m_demoLines.Length)
            {
                m_demoLineIndex = 0;
                m_manager?.HidePanel(EUIPanelID.Dialogue);
                return;
            }

            m_demoLineIndex = index;
            DemoLine line = m_demoLines[index];
            ApplyLine(line.Speaker, line.Text, line.Choices);
        }

        /// <summary>演示模式下推进对话：任意选项都进入下一行</summary>
        private void AdvanceDemo(int choiceIndex)
        {
            ShowDemoLine(m_demoLineIndex + 1);
        }
    }
}
