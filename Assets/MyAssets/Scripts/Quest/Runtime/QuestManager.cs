using System;
using System.Collections.Generic;
using MyGame.Audio;
using MyGame.Combat;
using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 任务管理器：整条任务链路的唯一写入口（场景单例）
    ///
    /// 架构定位（单一数据流）：
    ///   ┌── 输入侧 ──────────────────────────────────────────────┐
    ///   │ EnemyAI.AnyEnemyDied  → 击杀目标进度                     │
    ///   │ QuestGiver 交互       → 对话目标进度 / 接取 / 交付        │
    ///   │ 背包、区域触发器（后续）→ 直接调用 ReportProgress         │
    ///   └───────────────────────┬────────────────────────────────┘
    ///                           ▼
    ///              QuestManager.ReportProgress(类型, 目标ID, 数量)
    ///                           ▼
    ///         QuestState 进度累加 → 目标达成 → 任务可交付 → 交付发奖
    ///                           ▼
    ///   ┌── 输出侧（事件驱动，Manager 不引用任何 UI 类型）──────────┐
    ///   │ QuestAccepted / QuestProgressed / QuestReadyToTurnIn      │
    ///   │ QuestCompleted / QuestLogChanged / RewardGranted          │
    ///   └──────────────────────────────────────────────────────────┘
    ///
    /// 三条硬性设计约束：
    /// 1. 进度只走 ReportProgress，任何系统都不要直接改 QuestState
    /// 2. 只有"已接取"的任务才累计进度（避免玩家先打怪、再接任务时进度瞬间完成）
    /// 3. 运行时状态只存在 QuestState 里，绝不写回 QuestDataSO 资产
    ///
    /// 挂载位置：场景中新建一个空物体（如 QuestSystem），挂本脚本，全场唯一
    /// </summary>
    [DisallowMultipleComponent]
    public class QuestManager : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("奖励发放")]
        [Tooltip("实现 IQuestRewardHandler 的组件。留空则使用默认的日志实现（只打日志不改变数值）")]
        [SerializeField] private MonoBehaviour m_rewardHandlerBehaviour;

        [Header("进度来源")]
        [Tooltip("是否自动订阅 EnemyAI 的全局击杀事件（用于完成击杀类任务）")]
        [SerializeField] private bool m_trackEnemyKills = true;

        [Header("任务目录")]
        [Tooltip("本作全部任务资产的清单。作用有两个：1) 读档时按存档里的任务 ID 把配置找回来；" +
                 "2) 给剧本里的 quest_xxx 函数提供 ID → 资产的查表。新增任务记得加到这里")]
        [SerializeField] private List<QuestDataSO> m_questCatalog = new();

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>
        /// 所有已接取过的任务（含已交付）。
        /// 任务数量在 Demo 规模下是个位数，用 List + 线性查找换取最简单可读的实现；
        /// 若后续任务量级上升，可在此处替换为 Dictionary&lt;string, QuestState&gt; 而不影响外部接口
        /// </summary>
        private readonly List<QuestState> m_states = new();

        /// <summary>奖励发放实现（构造时确定，运行时不变）</summary>
        private IQuestRewardHandler m_rewardHandler;

        // ====================================================================
        // 单例与只读访问
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 QuestManager）</summary>
        public static QuestManager Instance { get; private set; }

        /// <summary>所有已接取过的任务（含已交付），顺序即接取顺序</summary>
        public IReadOnlyList<QuestState> States => m_states;

        // ====================================================================
        // 对外事件
        // ====================================================================

        /// <summary>任务被接取</summary>
        public event Action<QuestState> QuestAccepted;

        /// <summary>任务有目标获得进度：(任务, 目标下标)</summary>
        public event Action<QuestState, int> QuestProgressed;

        /// <summary>任务目标全部达成，可以交付了</summary>
        public event Action<QuestState> QuestReadyToTurnIn;

        /// <summary>任务已交付完成（奖励已发放）</summary>
        public event Action<QuestState> QuestCompleted;

        /// <summary>单条奖励已发放（后续做"获得物品"飘字/提示可订阅）</summary>
        public event Action<QuestRewardData, QuestDataSO> RewardGranted;

        /// <summary>
        /// 任务数据整体发生变化（接取 / 进度 / 交付 / 放弃）
        /// UI（任务日志、追踪 HUD）只需订阅这一个事件即可保持刷新，无需分别处理上面几个
        /// </summary>
        public event Action QuestLogChanged;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            // 单例设置（场景中只允许一个 QuestManager）
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 QuestManager 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;

            // 奖励发放实现：Inspector 挂了就用挂的，没挂就用默认日志实现
            m_rewardHandler = m_rewardHandlerBehaviour as IQuestRewardHandler ?? new LogQuestRewardHandler();

            if (m_rewardHandlerBehaviour != null && m_rewardHandlerBehaviour is not IQuestRewardHandler)
            {
                Debug.LogWarning(
                    $"[{GetType().Name}] {m_rewardHandlerBehaviour.GetType().Name} 未实现 IQuestRewardHandler，已回退到默认日志实现",
                    this);
            }
        }

        private void OnEnable()
        {
            // 订阅全局击杀事件：任务系统是全局系统，用静态事件比逐个敌人订阅更省事
            if (m_trackEnemyKills)
            {
                EnemyAI.AnyEnemyDied += HandleEnemyDied;
            }
        }

        private void OnDisable()
        {
            // 取消订阅（未订阅时移除也是安全空操作），防止物体禁用后回调访问已销毁对象
            EnemyAI.AnyEnemyDied -= HandleEnemyDied;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 对外接口：查询
        // ====================================================================

        /// <summary>取指定 ID 的任务状态（未接取过则返回 null）</summary>
        public QuestState GetQuestState(string questId)
        {
            if (string.IsNullOrEmpty(questId))
            {
                return null;
            }

            for (int i = 0; i < m_states.Count; i++)
            {
                if (m_states[i].Id == questId)
                {
                    return m_states[i];
                }
            }

            return null;
        }

        /// <summary>任务是否已接取且尚未交付</summary>
        public bool IsQuestActive(string questId)
        {
            QuestState state = GetQuestState(questId);
            return state != null && state.Status != EQuestStatus.Completed;
        }

        /// <summary>任务是否已交付完成</summary>
        public bool IsQuestCompleted(string questId)
        {
            QuestState state = GetQuestState(questId);
            return state != null && state.Status == EQuestStatus.Completed;
        }

        /// <summary>
        /// 收集所有"目标已达成、等待交付"的任务（QuestGiver 用）：
        /// 结果写入调用方传入的列表，避免每次交互都产生新集合
        /// </summary>
        public void CollectReadyToTurnInQuests(List<QuestState> results)
        {
            if (results == null)
            {
                return;
            }

            results.Clear();

            for (int i = 0; i < m_states.Count; i++)
            {
                if (m_states[i].Status == EQuestStatus.ReadyToTurnIn)
                {
                    results.Add(m_states[i]);
                }
            }
        }

        /// <summary>
        /// 判断能否接取任务，不能接取时通过 reason 给出可读原因（QuestGiver 用它决定是否显示任务）
        /// </summary>
        public bool CanAcceptQuest(QuestDataSO quest, out string reason)
        {
            if (quest == null)
            {
                reason = "任务资产为空";
                return false;
            }

            if (IsQuestActive(quest.Id))
            {
                reason = "任务已接取";
                return false;
            }

            if (IsQuestCompleted(quest.Id))
            {
                reason = "任务已完成";
                return false;
            }

            // 前置任务校验：必须全部已交付
            IReadOnlyList<string> prerequisites = quest.PrerequisiteQuestIds;
            if (prerequisites != null)
            {
                for (int i = 0; i < prerequisites.Count; i++)
                {
                    if (!IsQuestCompleted(prerequisites[i]))
                    {
                        reason = $"需先完成任务 {prerequisites[i]}";
                        return false;
                    }
                }
            }

            reason = string.Empty;
            return true;
        }

        // ====================================================================
        // 对外接口：接取 / 放弃 / 交付
        // ====================================================================

        /// <summary>
        /// 接取任务：成功后任务进入 InProgress，并立即检查是否已满足目标
        /// （例如"收集 0 个"这类边界配置，接取瞬间就该是可交付状态）
        /// </summary>
        public bool AcceptQuest(QuestDataSO quest)
        {
            if (!CanAcceptQuest(quest, out string reason))
            {
                Debug.LogWarning($"[{GetType().Name}] 无法接取任务 {(quest != null ? quest.Title : "null")}：{reason}", this);
                return false;
            }

            QuestState state = new QuestState(quest);
            m_states.Add(state);

            Debug.Log($"[{GetType().Name}] 接取任务：{quest.Title}", this);
            AudioManager.Play(ESfx.Quest_Accept);
            QuestAccepted?.Invoke(state);

            // 接取后立即结算一次"是否已全部达成"
            CheckReadyToTurnIn(state);
            QuestLogChanged?.Invoke();

            return true;
        }

        /// <summary>放弃任务：直接丢弃运行时状态，任务可被重新接取</summary>
        public bool AbandonQuest(string questId)
        {
            for (int i = 0; i < m_states.Count; i++)
            {
                if (m_states[i].Id != questId)
                {
                    continue;
                }

                if (m_states[i].Status == EQuestStatus.Completed)
                {
                    Debug.LogWarning($"[{GetType().Name}] 任务 {questId} 已完成，无法放弃", this);
                    return false;
                }

                Debug.Log($"[{GetType().Name}] 放弃任务：{m_states[i].Data.Title}", this);
                m_states.RemoveAt(i);
                QuestLogChanged?.Invoke();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 交付任务：仅允许在 ReadyToTurnIn 状态下交付，发放奖励并置为 Completed
        /// </summary>
        public bool TurnInQuest(string questId)
        {
            QuestState state = GetQuestState(questId);

            if (state == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 交付失败：未接取任务 {questId}", this);
                return false;
            }

            if (state.Status != EQuestStatus.ReadyToTurnIn)
            {
                Debug.LogWarning($"[{GetType().Name}] 交付失败：任务 {questId} 目标尚未全部达成", this);
                return false;
            }

            state.MarkCompleted();
            GrantRewards(state.Data);

            Debug.Log($"[{GetType().Name}] 任务完成：{state.Data.Title}", this);
            AudioManager.Play(ESfx.Quest_Complete);
            QuestCompleted?.Invoke(state);
            QuestLogChanged?.Invoke();

            return true;
        }

        // ====================================================================
        // 对外接口：任务目录查询
        // ====================================================================

        /// <summary>按任务 ID 取配置资产（任务目录里没有则返回 null）</summary>
        public QuestDataSO FindQuestById(string questId)
        {
            if (string.IsNullOrEmpty(questId))
            {
                return null;
            }

            for (int i = 0; i < m_questCatalog.Count; i++)
            {
                QuestDataSO quest = m_questCatalog[i];
                if (quest != null && quest.Id == questId)
                {
                    return quest;
                }
            }

            return null;
        }

        // ====================================================================
        // 对外接口：存档
        // ====================================================================

        /// <summary>
        /// 导出当前任务进度（供 SaveManager 写盘）。
        /// 只导出动态状态：标题/目标/奖励都在 QuestDataSO 里，不进存档，
        /// 所以以后改任务描述或数值，老存档不用迁移就能生效
        /// </summary>
        public List<QuestSaveEntry> CaptureSaveData()
        {
            List<QuestSaveEntry> entries = new List<QuestSaveEntry>(m_states.Count);

            for (int i = 0; i < m_states.Count; i++)
            {
                QuestState state = m_states[i];

                QuestSaveEntry entry = new QuestSaveEntry
                {
                    questId = state.Id,
                    status = (int)state.Status,
                    objectiveProgress = new int[state.ObjectiveCount],
                };

                for (int k = 0; k < state.ObjectiveCount; k++)
                {
                    entry.objectiveProgress[k] = state.GetProgress(k);
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// 用存档数据覆盖当前进度（供 SaveManager 读档）。
        /// 是**覆盖**而不是合并：读档的语义就是"回到存档那一刻"，
        /// 合并会让状态无法解释（比如任务已经交付过，又冒出个半截进度）
        /// </summary>
        public void ApplySaveData(List<QuestSaveEntry> entries)
        {
            m_states.Clear();

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RestoreState(entries[i]);
                }
            }

            Debug.Log($"[{GetType().Name}] 已还原 {m_states.Count} 条任务进度", this);

            // 通知任务日志/追踪 HUD 刷新
            QuestLogChanged?.Invoke();
        }

        /// <summary>还原单条任务：找回配置 → 重建实例 → 还原进度与状态</summary>
        private void RestoreState(QuestSaveEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.questId))
            {
                return;
            }

            QuestDataSO quest = FindQuestById(entry.questId);
            if (quest == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 读档跳过任务 {entry.questId}：任务目录里没有它（改过 ID 或删过资产？）", this);
                return;
            }

            QuestState state = new QuestState(quest);

            // 进度用 TryAddProgress 灌进去，而不是直接改内部数组：
            // 这样进度会被正确裁剪在需求上限内，也不会破坏 QuestState 的封装
            if (entry.objectiveProgress != null)
            {
                int count = Mathf.Min(entry.objectiveProgress.Length, state.ObjectiveCount);
                for (int k = 0; k < count; k++)
                {
                    state.TryAddProgress(k, entry.objectiveProgress[k]);
                }
            }

            // 状态只按存档值前进；若存档说"待交付"但目标其实没达成，就留在 InProgress，
            // 避免出现"进度不满却能交付"的脏状态
            EQuestStatus status = (EQuestStatus)entry.status;
            if (status == EQuestStatus.Completed)
            {
                state.MarkCompleted();
            }
            else if (status == EQuestStatus.ReadyToTurnIn && state.IsAllObjectivesDone)
            {
                state.MarkReadyToTurnIn();
            }

            m_states.Add(state);
        }

        // ====================================================================
        // 对外接口：进度上报（所有系统唯一的进度入口）
        // ====================================================================

        /// <summary>
        /// 上报一次目标进度：会分发给所有"进行中"任务里匹配的目标
        ///
        /// 调用示例：
        /// - 击杀（已由 EnemyAI 事件自动接入）：ReportProgress(Kill, "Goblin", 1)
        /// - 采集（步骤八背包系统接入）：ReportProgress(Collect, "Herb", 1)
        /// - 对话（QuestGiver 已接入）：ReportProgress(Talk, "Npc_Pharmacist", 1)
        /// - 到达（后续区域触发器）：ReportProgress(Reach, "Area_TrainingGround", 1)
        ///
        /// 注意：只有"已接取"的任务会累计进度，未接取的任务不记录（避免提前刷满）
        /// </summary>
        public void ReportProgress(EQuestObjectiveType type, string targetId, int amount = 1)
        {
            if (amount <= 0)
            {
                return;
            }

            bool anyChanged = false;

            // 用下标循环而非 foreach：事件回调里可能接取/放弃任务导致集合变化
            for (int i = 0; i < m_states.Count; i++)
            {
                QuestState state = m_states[i];

                if (state.Status != EQuestStatus.InProgress)
                {
                    continue;
                }

                if (ApplyProgressToQuest(state, type, targetId, amount))
                {
                    anyChanged = true;
                }
            }

            // 进度消息量可能很大（每次击杀一条），所以统一在末尾只广播一次 UI 刷新
            if (anyChanged)
            {
                QuestLogChanged?.Invoke();
            }
        }

        // ====================================================================
        // 内部实现：进度结算
        // ====================================================================

        /// <summary>
        /// 把一次上报套用到单个任务上，返回该任务是否有任何变化
        /// </summary>
        private bool ApplyProgressToQuest(QuestState state, EQuestObjectiveType type, string targetId, int amount)
        {
            bool anyChanged = false;
            IReadOnlyList<QuestObjectiveData> objectives = state.Data.Objectives;

            for (int i = 0; i < objectives.Count; i++)
            {
                QuestObjectiveData objective = objectives[i];

                if (objective.Type != type || !IsTargetMatch(objective.TargetId, targetId))
                {
                    continue;
                }

                // 已达成的目标不再重复累加（TryAddProgress 内部也会拦截）
                int previousProgress = state.GetProgress(i);
                state.TryAddProgress(i, amount);

                if (state.GetProgress(i) == previousProgress)
                {
                    continue;
                }

                anyChanged = true;
                // 目标推进音：杀怪/采集/交付每次推进都响一下，给玩家即时反馈
                AudioManager.Play(ESfx.Quest_Progress, 0.8f);
                QuestProgressed?.Invoke(state, i);
            }

            if (anyChanged)
            {
                CheckReadyToTurnIn(state);
            }

            return anyChanged;
        }

        /// <summary>
        /// 检查任务是否已满足交付条件，满足则置为 ReadyToTurnIn 并广播
        /// </summary>
        private void CheckReadyToTurnIn(QuestState state)
        {
            if (state.Status != EQuestStatus.InProgress || !state.IsAllObjectivesDone)
            {
                return;
            }

            state.MarkReadyToTurnIn();

            Debug.Log($"[{GetType().Name}] 任务目标已达成，可交付：{state.Data.Title}", this);
            QuestReadyToTurnIn?.Invoke(state);
        }

        /// <summary>
        /// 目标 ID 匹配规则：
        /// - 配置里留空 → 匹配任意目标（用于"击杀任意敌人 ×3"这类任务）
        /// - 否则忽略大小写比较，避免配置时手抖打错大小写导致任务无法完成
        /// </summary>
        private bool IsTargetMatch(string objectiveTargetId, string reportedTargetId)
        {
            if (string.IsNullOrEmpty(objectiveTargetId))
            {
                return true;
            }

            return string.Equals(objectiveTargetId, reportedTargetId, StringComparison.OrdinalIgnoreCase);
        }

        // ====================================================================
        // 内部实现：奖励发放
        // ====================================================================

        /// <summary>逐条发放奖励，并广播"已发奖"事件</summary>
        private void GrantRewards(QuestDataSO quest)
        {
            IReadOnlyList<QuestRewardData> rewards = quest.Rewards;

            if (rewards == null || rewards.Count == 0)
            {
                return;
            }

            for (int i = 0; i < rewards.Count; i++)
            {
                QuestRewardData reward = rewards[i];
                m_rewardHandler.GrantReward(reward, quest);
                RewardGranted?.Invoke(reward, quest);
            }
        }

        // ====================================================================
        // 输入侧：击杀事件接入
        // ====================================================================

        /// <summary>
        /// 敌人死亡回调：把击杀翻译成任务进度
        /// 后续要接入击杀以外的来源（采集/到达）时，按同样思路补一个回调即可
        /// </summary>
        private void HandleEnemyDied(EnemyAI enemy)
        {
            if (enemy == null)
            {
                return;
            }

            ReportProgress(EQuestObjectiveType.Kill, enemy.EnemyId, 1);
        }
    }
}
