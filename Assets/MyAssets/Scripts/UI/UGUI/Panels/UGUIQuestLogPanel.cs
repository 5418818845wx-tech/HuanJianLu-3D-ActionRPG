using System.Collections.Generic;
using MyGame.Quest;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 任务日志面板（UGUI 版）：L 键打开的居中弹窗，左侧任务列表 + 右侧任务详情
    ///
    /// 交互流程：
    /// 1. 玩家按 L → UGUIManager.TogglePanel(QuestLog)
    /// 2. OnEnter：按当前任务列表生成条目 → 默认无选中
    /// 3. 点击条目：高亮该条目 + 右侧刷新详情（描述/目标/奖励）
    /// 4. 点击「关闭」或遮罩：UGUIManager.HidePanel(QuestLog)
    ///
    /// 绑定方式：
    /// - 任务条目是**运行时按数据生成的** → 只能代码绑定
    /// - 「关闭」按钮是固定的 → Inspector 拖曳绑定
    ///
    /// 数据来源：
    /// 订阅 QuestManager.QuestLogChanged，把运行时的 QuestState 翻译成面板用的 QuestData 视图模型；
    /// 打开面板时（OnEnter）主动拉一次当前状态，保证"接取/推进发生在上次关闭之后"也不会漏。
    ///
    /// 注意：面板关闭时物体是停用的（SetActive(false)），Update 不会执行，
    /// 所以事件订阅与解绑都放在 OnEnter / OnExit 里，而不是像常驻 HUD 那样常驻订阅
    /// </summary>
    public class UGUIQuestLogPanel : UGUIPanel
    {
        // ====================================================================
        // 文案常量
        // ====================================================================

        private const string k_noSelectionName = "未选择任务";
        private const string k_noSelectionDesc = "从左侧选择一条任务查看详情";

        private const string k_mainBadgeText = "主线";

        private const string k_sideBadgeText = "支线";

        private const string k_doneBadgeText = "已完成";

        private const string k_objectiveDonePrefix = "[已完成] ";

        private const string k_objectivePendingPrefix = "[待完成] ";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("任务列表")]
        [Tooltip("条目容器（建议挂 VerticalLayoutGroup）")]
        [SerializeField] private RectTransform m_listContainer;

        [Tooltip("任务条目 prefab（根节点需有 Button + Image，子物体为 Badge 与 Name 两个文本）")]
        [SerializeField] private GameObject m_entryPrefab;

        [Tooltip("条目常态背景色")]
        [SerializeField] private Color m_entryNormalColor = new Color(0.18f, 0.20f, 0.24f, 1f);

        [Tooltip("条目选中时的高亮背景色")]
        [SerializeField] private Color m_entrySelectedColor = new Color(0.28f, 0.35f, 0.45f, 1f);

        [Header("详情侧栏")]
        [Tooltip("任务名文本")]
        [SerializeField] private TextMeshProUGUI m_questNameText;

        [Tooltip("任务描述文本")]
        [SerializeField] private TextMeshProUGUI m_questDescText;

        [Tooltip("任务目标容器（建议挂 VerticalLayoutGroup）")]
        [SerializeField] private RectTransform m_objectivesContainer;

        [Tooltip("单行文本 prefab（这里复用 StatRow.prefab）")]
        [SerializeField] private GameObject m_objectiveRowPrefab;

        [Tooltip("奖励文本")]
        [SerializeField] private TextMeshProUGUI m_rewardText;

        [Header("徽标配色")]
        [Tooltip("主线任务徽标颜色（默认金黄）")]
        [SerializeField] private Color m_badgeMainColor = new Color(1f, 0.769f, 0f, 1f);

        [Tooltip("支线任务徽标颜色（默认浅蓝）")]
        [SerializeField] private Color m_badgeSideColor = new Color(0.45f, 0.72f, 0.95f, 1f);

        [Tooltip("已完成任务徽标颜色（默认灰）")]
        [SerializeField] private Color m_badgeDoneColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        [Tooltip("已完成的任务目标文本颜色（默认灰）")]
        [SerializeField] private Color m_objectiveDoneColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前展示的任务列表</summary>
        private readonly List<QuestData> m_quests = new();

        /// <summary>与 m_quests 一一对应的条目按钮（用于切换高亮）</summary>
        private readonly List<Button> m_entryButtons = new();

        /// <summary>与 m_quests 一一对应的条目徽标文本</summary>
        private readonly List<TextMeshProUGUI> m_entryBadges = new();

        /// <summary>与 m_quests 一一对应的条目任务名文本</summary>
        private readonly List<TextMeshProUGUI> m_entryNames = new();

        /// <summary>目标行实例（重建详情时销毁重建）</summary>
        private readonly List<GameObject> m_objectiveRows = new();

        /// <summary>当前选中任务索引（-1 表示未选中）</summary>
        private int m_selectedIndex = -1;

        /// <summary>已绑定的任务管理器（null 表示场景中尚未出现）</summary>
        private QuestManager m_questManager;

        /// <summary>是否已订阅任务事件，避免重复订阅</summary>
        private bool m_isSubscribed;

        /// <summary>把 QuestState 翻译成 QuestData 时复用的临时列表（避免每次刷新都新建集合）</summary>
        private readonly List<QuestData> m_viewBuffer = new();

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.QuestLog;
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：绑定任务管理器并拉取当前真实数据</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 面板物体被 SetActive(true) 后 Awake 才首次执行，绑定放在这里最稳
            TryBindQuestManager();

            // 主动拉一次：接取/推进可能发生在上次关闭之后，只靠事件会漏
            RefreshFromQuestManager();
        }

        /// <summary>面板退出显示：解除订阅并清理动态生成的实例</summary>
        public override void OnExit()
        {
            UnbindQuestManager();
            ClearObjectiveRows();
            base.OnExit();
        }

        protected override void OnDestroy()
        {
            UnbindQuestManager();
            base.OnDestroy();
        }

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>
        /// 注入任务数据并立即刷新
        /// 若面板当前正在显示，会立即刷新列表
        /// </summary>
        public void SetQuests(IReadOnlyList<QuestData> quests)
        {
            m_quests.Clear();

            if (quests != null)
            {
                m_quests.AddRange(quests);
            }

            if (IsVisible)
            {
                RebuildList();
                SelectQuest(-1);
            }
        }

        // ====================================================================
        // 列表生成
        // ====================================================================

        /// <summary>
        /// 按 m_quests 重建任务列表
        /// 每次重建都会销毁旧条目，所以条目上的按钮回调随物体一起释放，不会累积
        /// </summary>
        private void RebuildList()
        {
            ClearEntries();

            if (m_listContainer == null || m_entryPrefab == null)
            {
                return;
            }

            for (int index = 0; index < m_quests.Count; index++)
            {
                CreateEntry(index);
            }
        }

        /// <summary>创建单条任务条目：类型徽标 + 任务名</summary>
        private void CreateEntry(int index)
        {
            QuestData quest = m_quests[index];

            GameObject entry = Instantiate(m_entryPrefab, m_listContainer);

            Image background = entry.GetComponent<Image>();
            if (background != null)
            {
                background.color = m_entryNormalColor;
            }

            TextMeshProUGUI badge = FindChildText(entry.transform, "Badge");
            if (badge != null)
            {
                badge.text = GetBadgeText(quest);
                badge.color = GetBadgeColor(quest);
            }

            TextMeshProUGUI name = FindChildText(entry.transform, "Name");
            if (name != null)
            {
                name.text = quest.Name;
            }

            // 代码绑定：条目是运行时生成的，无法在 Inspector 里拖曳
            // 捕获的 index 是循环变量副本，且条目重建时会被销毁，不会造成泄漏
            Button button = entry.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 任务条目 prefab 上没有 Button 组件，条目无法点击", entry);
            }
            else
            {
                int capturedIndex = index;
                button.onClick.AddListener(() => HandleEntryClicked(capturedIndex));
            }

            m_entryButtons.Add(button);
            m_entryBadges.Add(badge);
            m_entryNames.Add(name);
        }

        /// <summary>销毁已生成的条目并清空引用列表</summary>
        private void ClearEntries()
        {
            int i = 0;
            while (i < m_entryButtons.Count)
            {
                if (m_entryButtons[i] != null)
                {
                    Destroy(m_entryButtons[i].gameObject);
                }
                i = i + 1;
            }

            m_entryButtons.Clear();
            m_entryBadges.Clear();
            m_entryNames.Clear();
        }

        /// <summary>按任务状态取徽标文字</summary>
        private string GetBadgeText(QuestData quest)
        {
            if (quest.IsCompleted)
            {
                return k_doneBadgeText;
            }

            return quest.Type == EQuestType.Main ? k_mainBadgeText : k_sideBadgeText;
        }

        /// <summary>按任务状态取徽标颜色</summary>
        private Color GetBadgeColor(QuestData quest)
        {
            if (quest.IsCompleted)
            {
                return m_badgeDoneColor;
            }

            return quest.Type == EQuestType.Main ? m_badgeMainColor : m_badgeSideColor;
        }

        // ====================================================================
        // 选中与详情
        // ====================================================================

        /// <summary>选中指定索引的任务（-1 表示取消选中）</summary>
        private void SelectQuest(int index)
        {
            m_selectedIndex = index;

            for (int i = 0; i < m_entryButtons.Count; i++)
            {
                if (m_entryButtons[i] == null)
                {
                    continue;
                }

                Image img = m_entryButtons[i].targetGraphic as Image;
                if (img != null)
                {
                    img.color = (i == index) ? m_entrySelectedColor : m_entryNormalColor;
                }
            }

            RefreshDetail();
        }

        /// <summary>刷新右侧任务详情：描述 / 目标 / 奖励</summary>
        private void RefreshDetail()
        {
            QuestData quest = GetSelectedQuest();

            // 未选中：显示占位提示
            if (quest == null)
            {
                if (m_questNameText != null) { m_questNameText.text = k_noSelectionName; }
                if (m_questDescText != null) { m_questDescText.text = k_noSelectionDesc; }
                if (m_rewardText != null) { m_rewardText.text = string.Empty; }

                ClearObjectiveRows();
                return;
            }

            if (m_questNameText != null)
            {
                m_questNameText.text = quest.Name;
            }

            if (m_questDescText != null)
            {
                m_questDescText.text = quest.Description;
            }

            // 重建目标行
            ClearObjectiveRows();
            if (m_objectivesContainer != null && m_objectiveRowPrefab != null)
            {
                foreach (QuestObjective objective in quest.Objectives)
                {
                    GameObject row = Instantiate(m_objectiveRowPrefab, m_objectivesContainer);
                    TextMeshProUGUI tmp = row.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        tmp.text = objective.IsDone
                            ? k_objectiveDonePrefix + objective.Text
                            : k_objectivePendingPrefix + objective.Text;

                        // 未完成的行保持 prefab 自带的常态颜色，只压暗已完成的行
                        if (objective.IsDone)
                        {
                            tmp.color = m_objectiveDoneColor;
                        }
                    }
                    m_objectiveRows.Add(row);
                }
            }

            if (m_rewardText != null)
            {
                m_rewardText.text = string.IsNullOrEmpty(quest.Reward) ? string.Empty : $"奖励：{quest.Reward}";
            }
        }

        /// <summary>清空目标行实例</summary>
        private void ClearObjectiveRows()
        {
            int i = 0;
            while (i < m_objectiveRows.Count)
            {
                if (m_objectiveRows[i] != null)
                {
                    Destroy(m_objectiveRows[i]);
                }
                i = i + 1;
            }

            m_objectiveRows.Clear();
        }

        /// <summary>取当前选中的任务（未选中时返回 null）</summary>
        private QuestData GetSelectedQuest()
        {
            bool isValid = m_selectedIndex >= 0 && m_selectedIndex < m_quests.Count;
            return isValid ? m_quests[m_selectedIndex] : null;
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>条目点击（代码绑定）：选中该任务</summary>
        private void HandleEntryClicked(int index)
        {
            SelectQuest(index);
        }

        /// <summary>「关闭」按钮回调（Inspector 拖曳绑定）</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.QuestLog);
        }

        // ====================================================================
        // 辅助方法
        // ====================================================================

        /// <summary>按名字查找子物体上的文本组件（找不到时返回 null，不报错）</summary>
        private TextMeshProUGUI FindChildText(Transform root, string childName)
        {
            Transform child = root.Find(childName);
            return child != null ? child.GetComponent<TextMeshProUGUI>() : null;
        }

        // ====================================================================
        // 数据接入：QuestManager → 面板视图模型
        // ====================================================================

        /// <summary>绑定任务管理器并订阅其数据变化事件（可安全重复调用）</summary>
        private void TryBindQuestManager()
        {
            if (m_isSubscribed)
            {
                return;
            }

            // 场景物体创建顺序不定：QuestManager 可能晚于本面板，取不到就留给下次 OnEnter 再试
            m_questManager = QuestManager.Instance;
            if (m_questManager == null)
            {
                return;
            }

            // 只订阅"整体变化"一个事件即可覆盖接取 / 进度 / 交付 / 放弃四种情况
            m_questManager.QuestLogChanged += HandleQuestLogChanged;
            m_isSubscribed = true;
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

        /// <summary>任务数据变化回调：面板可见时重建列表</summary>
        private void HandleQuestLogChanged()
        {
            // 事件只在可见期间被订阅，这里是二次保险
            if (!IsVisible)
            {
                return;
            }

            RefreshFromQuestManager();
        }

        /// <summary>按 QuestManager 当前状态重建列表（一条任务都没接取时列表为空）</summary>
        private void RefreshFromQuestManager()
        {
            m_viewBuffer.Clear();

            if (m_questManager != null)
            {
                IReadOnlyList<QuestState> states = m_questManager.States;

                for (int i = 0; i < states.Count; i++)
                {
                    m_viewBuffer.Add(BuildViewData(states[i]));
                }
            }

            SetQuests(m_viewBuffer);
        }

        /// <summary>
        /// 把运行时状态 QuestState 翻译成面板展示用的 QuestData：
        /// 配置字段（标题/描述/目标/奖励）来自 QuestDataSO，进度与状态来自 QuestState，
        /// 本方法只做翻译，不持有任何数据源
        /// </summary>
        private QuestData BuildViewData(QuestState state)
        {
            QuestDataSO config = state.Data;

            EQuestType type = config.Category == EQuestCategory.Main ? EQuestType.Main : EQuestType.Side;

            QuestData view = new QuestData(config.Title, config.Description, type, config.GetRewardSummary());
            view.IsCompleted = state.Status == EQuestStatus.Completed;

            IReadOnlyList<QuestObjectiveData> objectives = config.Objectives;
            for (int i = 0; i < objectives.Count; i++)
            {
                view.Objectives.Add(new QuestObjective(
                    BuildObjectiveText(state, i, objectives[i]),
                    state.IsObjectiveDone(i)));
            }

            return view;
        }

        /// <summary>目标显示文本：需要多个的目标附上进度，如"击败 Enemy_Dummy ×2（1/2）"</summary>
        private string BuildObjectiveText(QuestState state, int index, QuestObjectiveData objective)
        {
            string description = objective.Description;

            if (objective.RequiredAmount <= 1)
            {
                return description;
            }

            return $"{description}（{state.GetProgress(index)}/{objective.RequiredAmount}）";
        }
    }
}
