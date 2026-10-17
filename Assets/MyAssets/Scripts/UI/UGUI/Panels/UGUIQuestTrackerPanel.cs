using System.Collections.Generic;
using MyGame.Quest;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 任务追踪 HUD（UGUI 版）：屏幕左上角常驻显示"进行中任务 + 目标进度"
    /// （如"击败 Goblin（2/3）"）
    ///
    /// 架构定位：本面板是纯 UI，只订阅 QuestManager 的 QuestLogChanged 事件刷新显示，
    /// 不参与任何任务逻辑判断（判断与数据变更全在 QuestManager）
    ///
    /// 显示规则：
    /// - 只显示未交付的任务（InProgress / ReadyToTurnIn）
    /// - 没有任何进行中任务（或场景里还没有 QuestManager）时，只隐藏 Window 子物体，
    ///   面板根物体保持激活（轮询补绑与事件订阅都依赖根物体活着）
    /// - 已达成目标置灰；任务可交付时左侧竖线变黄色并追加黄色"可交付"提示
    ///
    /// 与旧 UI Toolkit 版的差异：
    /// - 条目全部运行时代码生成（VerticalLayoutGroup 自动排版），无需制作条目 prefab
    /// - 显隐控制 Window 子物体的 SetActive，而不是 USS 的 display
    /// </summary>
    public class UGUIQuestTrackerPanel : UGUIPanel
    {
        // ====================================================================
        // 文案常量
        // ====================================================================

        /// <summary>
        /// 任务可交付时追加的提示文本
        /// 注意：不加 ▶/● 等装饰符号——SmileySans 源字体不含这些字形（动态字库也无法补字），
        /// 仅靠黄色文本 + 左侧竖线变黄表达状态
        /// </summary>
        private const string k_readyHintText = "可交付";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("层级引用")]
        [Tooltip("追踪窗口底板（无进行中任务时隐藏此物体，根面板保持激活）")]
        [SerializeField] private GameObject m_window;

        [Tooltip("任务条目容器（需挂 VerticalLayoutGroup + 可选 ContentSizeFitter）")]
        [SerializeField] private RectTransform m_listContainer;

        [Header("配色（与旧 DesignSystem.uss 保持一致）")]
        [Tooltip("进行中任务左侧竖线颜色（主蓝）")]
        [SerializeField] private Color m_inProgressBarColor = new Color(0.157f, 0.471f, 0.941f, 1f);

        [Tooltip("可交付任务左侧竖线颜色（强调黄）")]
        [SerializeField] private Color m_readyBarColor = new Color(1f, 0.769f, 0f, 1f);

        [Tooltip("任务标题文本颜色")]
        [SerializeField] private Color m_titleTextColor = new Color(0.941f, 0.941f, 0.941f, 1f);

        [Tooltip("未达成目标文本颜色（白 65%）")]
        [SerializeField] private Color m_objectiveTextColor = new Color(1f, 1f, 1f, 0.65f);

        [Tooltip("已达成目标文本颜色（白 30%）")]
        [SerializeField] private Color m_objectiveDoneTextColor = new Color(1f, 1f, 1f, 0.30f);

        [Tooltip("可交付提示文本颜色")]
        [SerializeField] private Color m_hintTextColor = new Color(1f, 0.769f, 0f, 1f);

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>已绑定的任务管理器（null 表示场景中尚未出现）</summary>
        private QuestManager m_questManager;

        /// <summary>是否已订阅任务事件，避免重复订阅</summary>
        private bool m_isSubscribed;

        /// <summary>当前生成的任务条目实例（刷新时整批销毁重建）</summary>
        private readonly List<GameObject> m_entries = new();

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（硬编码防止 Inspector 漏配）
            m_panelID = EUIPanelID.QuestTracker;
        }

        private void Update()
        {
            // 常驻面板在 OnEnter 后始终可见；不可见时不做轮询
            if (!IsVisible)
            {
                return;
            }

            // QuestManager 可能晚于本面板初始化（场景物体创建顺序不定）：未绑定时持续尝试
            if (!m_isSubscribed)
            {
                TryBindQuestManager();
            }
        }

        protected override void OnDestroy()
        {
            UnbindQuestManager();
            base.OnDestroy();
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：尝试绑定任务管理器并首次刷新</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            TryBindQuestManager();
            Refresh();
        }

        /// <summary>面板退出显示：解绑事件并销毁动态条目（常驻面板一般不会走到这里）</summary>
        public override void OnExit()
        {
            UnbindQuestManager();
            ClearEntries();
            base.OnExit();
        }

        // ====================================================================
        // 绑定与事件
        // ====================================================================

        /// <summary>绑定任务管理器并订阅其数据变化事件（可安全重复调用）</summary>
        private void TryBindQuestManager()
        {
            if (m_questManager != null || m_isSubscribed)
            {
                return;
            }

            m_questManager = QuestManager.Instance;
            if (m_questManager == null)
            {
                return;
            }

            // 只订阅"整体变化"一个事件即可覆盖接取/进度/交付/放弃四种情况
            m_questManager.QuestLogChanged += HandleQuestLogChanged;
            m_isSubscribed = true;

            // 补绑场景（QuestManager 晚于 UI 创建）下，接取等事件可能早已广播完毕，
            // 这里必须立刻按当前状态刷一次，否则 HUD 会一直空白到下一次任务变化
            Refresh();
        }

        /// <summary>解除任务事件订阅（可安全重复调用）</summary>
        private void UnbindQuestManager()
        {
            if (m_questManager != null && m_isSubscribed)
            {
                m_questManager.QuestLogChanged -= HandleQuestLogChanged;
            }

            m_isSubscribed = false;
        }

        /// <summary>任务数据变化回调：重建追踪列表</summary>
        private void HandleQuestLogChanged()
        {
            Refresh();
        }

        // ====================================================================
        // 刷新显示
        // ====================================================================

        /// <summary>按 QuestManager 当前状态重建追踪列表</summary>
        private void Refresh()
        {
            ClearEntries();

            // 依赖未就绪：整块隐藏
            if (m_listContainer == null || m_questManager == null)
            {
                SetWindowVisible(false);
                return;
            }

            IReadOnlyList<QuestState> states = m_questManager.States;

            for (int i = 0; i < states.Count; i++)
            {
                QuestState state = states[i];

                // 已交付完成的任务不再追踪（它们只出现在任务日志里）
                if (state.Status == EQuestStatus.Completed)
                {
                    continue;
                }

                CreateEntry(state);
            }

            // 没有进行中的任务时整块隐藏，不占用屏幕空间
            SetWindowVisible(m_entries.Count > 0);
        }

        /// <summary>
        /// 创建单条任务条目：左侧状态竖线 +（标题 / 各目标进度 / 可交付提示）
        ///
        /// 层级（全部由代码生成，排版依赖容器上的 LayoutGroup）：
        /// Entry (HorizontalLayoutGroup)
        /// ├─ StateBar (Image，宽 3px 的竖线)
        /// └─ TextBlock (VerticalLayoutGroup)
        ///    ├─ Title (TMP)
        ///    ├─ Objective... (TMP，每个目标一行)
        ///    └─ Hint (TMP，仅可交付时)
        /// </summary>
        private void CreateEntry(QuestState state)
        {
            bool isReady = state.Status == EQuestStatus.ReadyToTurnIn;

            // 条目根：横向布局（竖线 | 文本块）
            GameObject entry = CreateUiObject("Entry", m_listContainer);
            HorizontalLayoutGroup entryLayout = entry.AddComponent<HorizontalLayoutGroup>();
            entryLayout.spacing = 6f;
            entryLayout.childAlignment = TextAnchor.UpperLeft;
            entryLayout.childControlWidth = true;
            entryLayout.childControlHeight = true;
            entryLayout.childForceExpandWidth = false;
            entryLayout.childForceExpandHeight = true;

            // 左侧状态竖线：3px 宽，颜色随状态变化
            GameObject stateBar = CreateUiObject("StateBar", entry.transform);
            Image barImage = stateBar.AddComponent<Image>();
            barImage.color = isReady ? m_readyBarColor : m_inProgressBarColor;
            barImage.raycastTarget = false;
            LayoutElement barLayout = stateBar.AddComponent<LayoutElement>();
            barLayout.preferredWidth = 3f;
            barLayout.minWidth = 3f;

            // 右侧文本块：纵向排列，占满剩余宽度
            GameObject textBlock = CreateUiObject("TextBlock", entry.transform);
            VerticalLayoutGroup textLayout = textBlock.AddComponent<VerticalLayoutGroup>();
            textLayout.spacing = 2f;
            textLayout.childAlignment = TextAnchor.UpperLeft;
            textLayout.childControlWidth = true;
            textLayout.childControlHeight = true;
            textLayout.childForceExpandWidth = true;
            textLayout.childForceExpandHeight = false;
            LayoutElement blockLayout = textBlock.AddComponent<LayoutElement>();
            blockLayout.flexibleWidth = 1f;

            // 任务标题
            CreateText("Title", textBlock.transform, state.Data.Title, 14f, m_titleTextColor, FontStyles.Bold);

            // 各目标进度行
            IReadOnlyList<QuestObjectiveData> objectives = state.Data.Objectives;
            for (int i = 0; i < objectives.Count; i++)
            {
                bool done = state.IsObjectiveDone(i);
                Color color = done ? m_objectiveDoneTextColor : m_objectiveTextColor;
                CreateText(
                    "Objective",
                    textBlock.transform,
                    BuildObjectiveText(state, i, objectives[i]),
                    12f,
                    color,
                    FontStyles.Normal);
            }

            // 目标全部达成时提示玩家去找 NPC 交付
            if (isReady)
            {
                CreateText("Hint", textBlock.transform, k_readyHintText, 12f, m_hintTextColor, FontStyles.Normal);
            }

            m_entries.Add(entry);
        }

        /// <summary>
        /// 目标显示文本：需要多个的目标附上进度，如"击败 Goblin（2/3）"
        /// </summary>
        private string BuildObjectiveText(QuestState state, int index, QuestObjectiveData objective)
        {
            if (objective.RequiredAmount <= 1)
            {
                return objective.Description;
            }

            return $"{objective.Description}（{state.GetProgress(index)}/{objective.RequiredAmount}）";
        }

        /// <summary>销毁所有动态生成的条目</summary>
        private void ClearEntries()
        {
            for (int i = 0; i < m_entries.Count; i++)
            {
                if (m_entries[i] != null)
                {
                    Destroy(m_entries[i]);
                }
            }

            m_entries.Clear();
        }

        /// <summary>控制追踪窗口显隐（隐藏时整块 HUD 不占屏幕）</summary>
        private void SetWindowVisible(bool visible)
        {
            if (m_window != null && m_window.activeSelf != visible)
            {
                m_window.SetActive(visible);
            }
        }

        // ====================================================================
        // 辅助方法
        // ====================================================================

        /// <summary>创建一个带 RectTransform 的空子物体</summary>
        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>
        /// 创建一行 TMP 文本
        /// 字体留空：TextMeshProUGUI 会自动回退到 TMP Settings 里配置的全局默认字体
        /// </summary>
        private static TextMeshProUGUI CreateText(
            string name,
            Transform parent,
            string content,
            float fontSize,
            Color color,
            FontStyles fontStyle)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = fontSize;
            text.color = color;
            text.fontStyle = fontStyle;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = true;
            text.raycastTarget = false;

            // TMP 文本不主动撑父布局宽度之外的部分，高度由文本行数自动撑开
            ContentSizeFitter fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return text;
        }
    }
}
