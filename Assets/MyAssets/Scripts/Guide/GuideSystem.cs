using System;
using KinematicCharacterController.Examples;
using MyGame.Combat;
using MyGame.Dungeon;
using MyGame.Inventory;
using MyGame.Quest;
using MyGame.UI;
using UnityEngine;

namespace MyGame.Guide
{
    /// <summary>
    /// 引导系统：按顺序推进引导步骤，负责"什么时候算完成、什么时候播下一步、进度怎么存"
    ///
    /// 它在整条链路里的位置（这也是"关卡流程串联"的骨架）：
    ///   玩家操作（移动/攻击/采集/对话…）
    ///        ↓ 轮询只读状态（不改动任何系统）
    ///   本系统判断当前步骤是否达成
    ///        ↓ 推进步骤
    ///   引导 HUD 刷新提示文字
    ///
    /// 为什么条件用轮询而不是订阅事件：
    /// 为了一段新手引导，去战斗/背包/对话/任务五个系统里各加一套事件，
    /// 会让"引导"这个随时可能删掉或替换的模块渗进核心代码。
    /// 轮询只读属性的成本极低（每帧读几个 bool），换来的是核心系统对引导毫不知情
    ///
    /// 进度存档：只存一个"当前第几步"整数。新游戏时是默认值 0（从第一步开始），
    /// 读档时按存档恢复；已完成/已跳过就不再播
    ///
    /// 挂载位置：场景中一个常驻物体（与其它管理器同层）
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(k_executionOrder)]
    public class GuideSystem : MonoBehaviour
    {
        /// <summary>
        /// 执行顺序排在 SaveManager（100）之后：
        /// 读档是 SaveManager 在 Start 里做的，本系统必须等它把引导进度还原完，
        /// 才知道这一局到底该不该播（新游戏 vs 已完成的老存档）
        /// </summary>
        private const int k_executionOrder = 110;

        /// <summary>视为"传送"的单帧位移阈值（米）。超过就不计入移动距离，避免读档传送刷满进度</summary>
        private const float k_teleportThreshold = 1.5f;

        /// <summary>Animator 中的闪避参数名</summary>
        private static readonly int s_isDodgingHash = Animator.StringToHash("IsDodging");

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("引导内容")]
        [Tooltip("引导序列资产。留空则本系统不工作")]
        [SerializeField] private GuideSequenceSO m_sequence;

        [Header("玩家绑定")]
        [Tooltip("玩家未找到时的重试间隔（秒）。玩家可能由网络系统延迟生成")]
        [SerializeField] private float m_playerLookupInterval = 0.5f;

        // ====================================================================
        // 单例与事件
        // ====================================================================

        /// <summary>全局单例</summary>
        public static GuideSystem Instance { get; private set; }

        /// <summary>当前步骤变化（HUD 订阅它刷新文字）</summary>
        public event Action StepChanged;

        /// <summary>整条引导结束（完成或跳过都会触发；HUD 订阅它隐藏自己）</summary>
        public event Action SequenceFinished;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前步骤索引（== 步数表示已全部完成）</summary>
        private int m_stepIndex;

        /// <summary>玩家是否跳过了引导</summary>
        private bool m_skipped;

        /// <summary>是否正在播放引导</summary>
        private bool m_running;

        /// <summary>当前步骤的配置</summary>
        private GuideStep m_currentStep;

        /// <summary>本步已累计的移动距离（米）</summary>
        private float m_traveled;

        /// <summary>本步已击杀数</summary>
        private int m_killCount;

        /// <summary>本步是否获得过物品</summary>
        private bool m_collected;

        /// <summary>上一帧玩家位置（用于算位移）</summary>
        private Vector3 m_lastPlayerPosition;

        /// <summary>是否已有上一帧位置（第一帧不算位移）</summary>
        private bool m_hasLastPosition;

        /// <summary>已绑定的玩家物体</summary>
        private GameObject m_player;

        /// <summary>玩家的战斗状态机（判断"攻击了 / 放技能了"）</summary>
        private Fighter m_fighter;

        /// <summary>玩家的 Animator（判断闪避）</summary>
        private Animator m_animator;

        /// <summary>下一次尝试绑定玩家的时间点</summary>
        private float m_nextPlayerLookupTime;

        /// <summary>是否已订阅背包变化</summary>
        private bool m_subscribedInventory;

        // ====================================================================
        // 只读访问（HUD 与存档使用）
        // ====================================================================

        /// <summary>是否正在播放引导</summary>
        public bool IsRunning => m_running;

        /// <summary>当前步骤索引（0 起）</summary>
        public int StepIndex => m_stepIndex;

        /// <summary>步骤总数</summary>
        public int StepCount => m_sequence != null ? m_sequence.StepCount : 0;

        /// <summary>当前步骤配置</summary>
        public GuideStep CurrentStep => m_currentStep;

        /// <summary>本步进度文本（如"2 / 3"），无进度概念时返回空串</summary>
        public string ProgressText
        {
            get
            {
                switch (m_currentStep.Condition)
                {
                    case EGuideCondition.KillEnemy:
                        return $"{m_killCount} / {Mathf.Max(1, m_currentStep.Amount)}";

                    case EGuideCondition.MoveDistance:
                        return $"{Mathf.FloorToInt(m_traveled)} / {Mathf.Max(1, m_currentStep.Amount)} 米";

                    default:
                        return string.Empty;
                }
            }
        }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            // 静态事件：击杀计数（DropSystem 也是这么用的）
            EnemyAI.AnyEnemyDied += HandleEnemyDied;
        }

        private void OnDisable()
        {
            EnemyAI.AnyEnemyDied -= HandleEnemyDied;
            UnsubscribeInventory();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Start()
        {
            if (m_sequence == null || m_sequence.StepCount == 0)
            {
                Debug.LogWarning($"[{GetType().Name}] 未配置引导序列，引导功能不生效", this);
                enabled = false;
                return;
            }

            // 秘境（副本）里不播引导：
            // GameRoot 在两个场景里各有一份，副本里也会新建一个 GuideSystem，
            // 而引导进度是实例字段、不跨场景携带，于是进副本会从第 1 步重播一遍。
            // 引导只属于大世界的新手流程，副本里应该干净地只做战斗。
            //
            // 第二个条件是为了"直接在副本场景里按 Play 测试"这种情况：
            // 那时没有走过秘境入口，DungeonRunContext 是空的，但场景里有波次管理器，
            // 同样说明这是战斗场景，不该弹新手引导
            if (DungeonRunContext.IsInDungeon || FindFirstObjectByType<CombatWaveManager>() != null)
            {
                m_running = false;
                enabled = false;
                SequenceFinished?.Invoke();
                return;
            }

            // 走到这里说明 SaveManager（更早执行）已经处理完读档：
            // - 新游戏：m_stepIndex 还是默认 0 → 从第一步开始播
            // - 读档：进度已由 ApplySaveData 还原，已完成/已跳过就不播
            if (m_skipped || m_stepIndex >= m_sequence.StepCount)
            {
                m_running = false;
                SequenceFinished?.Invoke();
                return;
            }

            m_running = true;
            BeginStep(m_stepIndex);
        }

        private void Update()
        {
            if (!m_running)
            {
                return;
            }

            EnsurePlayerBound();
            EnsureInventorySubscribed();
            UpdateCondition();
        }

        // ====================================================================
        // 玩家绑定
        // ====================================================================

        /// <summary>按间隔查找玩家并缓存所需组件（玩家可能晚于本物体生成）</summary>
        private void EnsurePlayerBound()
        {
            if (m_player != null)
            {
                return;
            }

            if (Time.unscaledTime < m_nextPlayerLookupTime)
            {
                return;
            }

            m_nextPlayerLookupTime = Time.unscaledTime + m_playerLookupInterval;

            m_player = GameObject.FindGameObjectWithTag("Player");
            if (m_player == null)
            {
                return;
            }

            m_fighter = m_player.GetComponent<Fighter>();
            m_animator = m_player.GetComponentInChildren<Animator>();

            // 刚绑定时不把"绑定瞬间的位置差"算成移动
            m_lastPlayerPosition = m_player.transform.position;
        }

        // ====================================================================
        // 步骤推进
        // ====================================================================

        /// <summary>开始某一步：重置该步的计数并通知 HUD</summary>
        private void BeginStep(int index)
        {
            m_stepIndex = index;
            m_currentStep = m_sequence.GetStep(index);

            // 重置本步的进度数据
            m_traveled = 0f;
            m_killCount = 0;
            m_collected = false;
            m_hasLastPosition = false;

            Debug.Log($"[{GetType().Name}] 第 {index + 1}/{m_sequence.StepCount} 步：{m_currentStep.Title}", this);

            StepChanged?.Invoke();
        }

        /// <summary>当前步骤达成，推进到下一步或结束整条引导</summary>
        private void CompleteStep()
        {
            int next = m_stepIndex + 1;

            if (next >= m_sequence.StepCount)
            {
                Finish(false);
                return;
            }

            BeginStep(next);
        }

        /// <summary>
        /// 玩家点「完成本步」按钮（仅 Manual 条件用）
        /// </summary>
        public void ConfirmManualStep()
        {
            if (m_running && m_currentStep.Condition == EGuideCondition.Manual)
            {
                CompleteStep();
            }
        }

        /// <summary>
        /// 跳过引导（HUD 上的「跳过引导」按钮）
        /// </summary>
        public void Skip()
        {
            if (!m_running)
            {
                return;
            }

            Debug.Log($"[{GetType().Name}] 玩家跳过了新手引导", this);
            Finish(true);
        }

        /// <summary>
        /// 结束引导并落盘进度。
        ///
        /// 无论"走完"还是"跳过"，都把 stepIndex 推到 StepCount（= 已完成）：
        /// 存档只认 "stepIndex >= 步数" 这个条件，若完成时停在最后一步的索引上，
        /// 下次读档会被判成"还差最后一步"，把最后一步重播一遍
        /// </summary>
        private void Finish(bool skipped)
        {
            m_running = false;
            m_skipped = skipped;
            m_stepIndex = m_sequence != null ? m_sequence.StepCount : 0;

            SequenceFinished?.Invoke();

            // 立刻存一次：否则玩家做完引导直接退出，下次进来又得重看一遍
            SaveManagerProxy.SaveNow();
        }

        // ====================================================================
        // 条件判断
        // ====================================================================

        private void UpdateCondition()
        {
            switch (m_currentStep.Condition)
            {
                case EGuideCondition.MoveDistance:
                    if (UpdateTravelDistance())
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.Dodge:
                    if (m_animator != null && m_animator.GetBool(s_isDodgingHash))
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.Attack:
                    if (m_fighter != null && m_fighter.CurrentState == ECombatState.NormalAttacking)
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.CastSkill:
                    if (m_fighter != null && m_fighter.CurrentState == ECombatState.SkillCasting)
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.Collect:
                    if (IsCollectSatisfied())
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.TalkToNpc:
                    // "正在对话"用对话面板是否可见来判断：不依赖对话系统暴露内部状态
                    if (UGUIManager.Instance != null && UGUIManager.Instance.IsPanelVisible(EUIPanelID.Dialogue))
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.AcceptQuest:
                    if (IsQuestActive(m_currentStep.TargetId))
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.KillEnemy:
                    if (m_killCount >= Mathf.Max(1, m_currentStep.Amount))
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.TurnInQuest:
                    if (IsQuestCompleted(m_currentStep.TargetId))
                    {
                        CompleteStep();
                    }
                    break;

                case EGuideCondition.Manual:
                    // 等玩家点按钮，这里不做任何判断
                    break;
            }
        }

        /// <summary>累计玩家水平位移，达到要求返回 true（单帧大位移视为传送，忽略）</summary>
        private bool UpdateTravelDistance()
        {
            if (m_player == null)
            {
                return false;
            }

            Vector3 position = m_player.transform.position;

            if (!m_hasLastPosition)
            {
                m_lastPlayerPosition = position;
                m_hasLastPosition = true;
                return false;
            }

            // 只算水平距离：上下阶梯、掉落不该算作"走路"
            Vector3 delta = position - m_lastPlayerPosition;
            delta.y = 0f;
            float distance = delta.magnitude;

            m_lastPlayerPosition = position;

            if (distance > k_teleportThreshold)
            {
                return false;   // 传送（读档 / 调试）不算
            }

            m_traveled += distance;

            return m_traveled >= Mathf.Max(1, m_currentStep.Amount);
        }

        /// <summary>
        /// 「获得物品」条件是否满足。
        /// 指定了物品 ID 就按背包里的数量判断（支持"采满 3 株"这种目标）；
        /// 没指定则只要本步期间获得过任意物品就算完成
        /// </summary>
        private bool IsCollectSatisfied()
        {
            if (!string.IsNullOrEmpty(m_currentStep.TargetId))
            {
                InventoryManager inventory = InventoryManager.Instance;
                return inventory != null
                    && inventory.GetCount(m_currentStep.TargetId) >= Mathf.Max(1, m_currentStep.Amount);
            }

            return m_collected;
        }

        private static bool IsQuestActive(string questId)
        {
            QuestManager questManager = QuestManager.Instance;
            return questManager != null && !string.IsNullOrEmpty(questId) && questManager.IsQuestActive(questId);
        }

        private static bool IsQuestCompleted(string questId)
        {
            QuestManager questManager = QuestManager.Instance;
            return questManager != null && !string.IsNullOrEmpty(questId) && questManager.IsQuestCompleted(questId);
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>有敌人死亡：本步需要击杀数时累加</summary>
        private void HandleEnemyDied(EnemyAI enemy)
        {
            if (m_running && m_currentStep.Condition == EGuideCondition.KillEnemy)
            {
                m_killCount++;
            }
        }

        /// <summary>背包内容变化：只要有物品增加就算"获得物品"</summary>
        private void HandleItemsChanged()
        {
            if (m_running && m_currentStep.Condition == EGuideCondition.Collect)
            {
                m_collected = true;
            }
        }

        private void EnsureInventorySubscribed()
        {
            if (m_subscribedInventory)
            {
                return;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                return;
            }

            inventory.ItemsChanged += HandleItemsChanged;
            m_subscribedInventory = true;
        }

        private void UnsubscribeInventory()
        {
            if (!m_subscribedInventory)
            {
                return;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                inventory.ItemsChanged -= HandleItemsChanged;
            }

            m_subscribedInventory = false;
        }

        // ====================================================================
        // 存档
        // ====================================================================

        /// <summary>
        /// 用存档覆盖进度（由 SaveManager 在读档时调用）。
        /// stepIndex 语义：0 起表示当前步；>= 步数表示已完成
        /// </summary>
        public void ApplySaveData(int stepIndex, bool skipped)
        {
            m_skipped = skipped;
            m_stepIndex = Mathf.Max(0, stepIndex);
        }

        /// <summary>导出当前进度（供 SaveManager 收集）</summary>
        public void CaptureSaveData(out int stepIndex, out bool skipped)
        {
            stepIndex = m_stepIndex;
            skipped = m_skipped;
        }

        /// <summary>
        /// 直接标记为"已完成"。
        /// 用途：老存档（没有引导字段的版本）不该被补播一遍引导，
        /// 读档时按已完成处理，避免打断已经玩过的玩家
        /// </summary>
        public void MarkAsCompleted()
        {
            m_skipped = false;
            m_stepIndex = m_sequence != null ? m_sequence.StepCount : 0;
        }

        /// <summary>
        /// 触发一次存档的小包装：引导模块不该直接依赖 SaveManager 的类型，
        /// 但"完成引导立刻存一次"又是必须的（否则进度会丢）
        /// </summary>
        private static class SaveManagerProxy
        {
            public static void SaveNow()
            {
                MyGame.Save.SaveManager saveManager = MyGame.Save.SaveManager.Instance;
                if (saveManager != null)
                {
                    saveManager.SaveGame();
                }
            }
        }
    }
}
