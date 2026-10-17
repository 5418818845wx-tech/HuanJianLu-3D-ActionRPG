using System.Collections.Generic;
using MyGame.Combat;
using MyGame.Inventory;
using MyGame.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyGame.Dungeon
{
    /// <summary>
    /// 秘境管理器：秘境场景里的"裁判 + 发奖员"
    ///
    /// 职责（两件事）：
    /// 1. 充当秘境注册表——面板从这里读"本作有哪些秘境"（AllDungeons）
    ///    （运行时没有 AssetDatabase，面板无法自己把资产全捞出来，所以必须有人持有清单）
    /// 2. 监听战斗结束——胜利且处于秘境中时发放奖励，失败不发
    ///
    /// 为什么用 FindFirstObjectByType 找 CombatWaveManager 而不是 Inspector 拖引用：
    /// 波次管理器往往由关卡搭建者随意摆放，手动拖引用容易漏配；
    /// 场景里本就应该最多只有一个波次管理器，运行时按类型找一次最省心
    ///
    /// 挂载位置：秘境场景中一个常驻物体（与其它管理器同层）。大世界不需要挂它
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonManager : MonoBehaviour
    {
        // ====================================================================
        // 常量
        // ====================================================================

        /// <summary>回程场景找不到时用的兜底场景名（与 Build Settings 中注册的一致）</summary>
        private const string k_defaultReturnScene = "MainScene";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("秘境注册表")]
        [Tooltip("本作全部秘境资产的清单。面板的秘境列表就是从这里读的，" +
                 "新增秘境记得加到这里（留空则面板显示「未配置秘境」）")]
        [SerializeField] private List<DungeonDefinitionSO> m_allDungeons = new();

        // ====================================================================
        // 单例
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 DungeonManager）</summary>
        public static DungeonManager Instance { get; private set; }

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>波次管理器引用（订阅其战斗结束事件用）</summary>
        private CombatWaveManager m_waveManager;

        /// <summary>
        /// 随机奖励的摇奖结果缓冲区：复用一个 List，避免每次结算都新建列表产生 GC
        /// （与 DropSystem 的 m_rollBuffer 同一考虑）
        /// </summary>
        private readonly List<DropResult> m_rollBuffer = new();

        // ====================================================================
        // 对外只读访问
        // ====================================================================

        /// <summary>全部已注册的秘境（供秘境面板展示列表）</summary>
        public IReadOnlyList<DungeonDefinitionSO> AllDungeons => m_allDungeons;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 DungeonManager 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            // 订阅战斗结束：波次管理器负责判定胜负，本类只关心"赢了就发奖"
            m_waveManager = FindFirstObjectByType<CombatWaveManager>();

            if (m_waveManager == null)
            {
                // 本管理器随 GameRoot 同时存在于大世界与秘境两个场景，
                // 而大世界本就没有战斗，属正常情况不该刷警告。
                // 只有"人在秘境里却找不到波次管理器"才是真的配错了，这时才提示
                if (DungeonRunContext.IsInDungeon)
                {
                    Debug.LogWarning($"[{GetType().Name}] 秘境场景中没有 CombatWaveManager，通关奖励不会被触发", this);
                }

                return;
            }

            m_waveManager.CombatEnded += HandleCombatEnded;
        }

        private void OnDisable()
        {
            // 必须成对解绑：否则场景卸载后事件仍持有本对象，回调时访问已销毁的物体就会报错
            if (m_waveManager != null)
            {
                m_waveManager.CombatEnded -= HandleCombatEnded;
                m_waveManager = null;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 战斗结算 → 发奖
        // ====================================================================

        /// <summary>战斗结束回调：秘境且胜利时发奖，其余情况什么都不做</summary>
        private void HandleCombatEnded(CombatResult result)
        {
            // 不在秘境里（例如大世界的试炼场）：不是本系统的业务，直接放行
            if (!DungeonRunContext.IsInDungeon)
            {
                return;
            }

            if (!result.IsVictory)
            {
                Debug.Log($"[{GetType().Name}] 秘境「{DungeonRunContext.ActiveDungeon.DisplayName}」挑战失败，不发放奖励", this);
                return;
            }

            // 已发过奖就别再发：同一场战斗的结束事件可能因重试/重复订阅被触发多次
            if (DungeonRunContext.HasCompleted)
            {
                Debug.LogWarning($"[{GetType().Name}] 本次秘境奖励已发放过，忽略重复结算", this);
                return;
            }

            GrantRewards(DungeonRunContext.ActiveDungeon);

            // 发完立刻打标记，保证"发奖"与"防重"是同一步完成的
            DungeonRunContext.MarkCompleted();
        }

        /// <summary>发放秘境奖励：先发固定奖励，再按掉落表摇若干次随机奖励</summary>
        private void GrantRewards(DungeonDefinitionSO dungeon)
        {
            if (dungeon == null)
            {
                return;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 InventoryManager，秘境奖励无法入包", this);
                return;
            }

            // 汇总条目，最后拼成一句中文日志，方便核对到底发了什么
            List<string> summary = new();

            // 1) 固定奖励：逐条发放（保底）
            List<DungeonDefinitionSO.FixedReward> fixedRewards = dungeon.FixedRewards;
            if (fixedRewards != null)
            {
                for (int i = 0; i < fixedRewards.Count; i++)
                {
                    DungeonDefinitionSO.FixedReward reward = fixedRewards[i];
                    if (reward == null)
                    {
                        continue;
                    }

                    GrantItem(inventory, reward.ItemId, reward.Amount, summary);
                }
            }

            // 2) 随机奖励：按掉落表摇 m_randomRolls 次，每次独立判定
            DropTableSO table = dungeon.RandomDropTable;
            int rolls = dungeon.RandomRolls;
            if (table != null && rolls > 0)
            {
                for (int i = 0; i < rolls; i++)
                {
                    m_rollBuffer.Clear();
                    table.Roll(m_rollBuffer);

                    for (int j = 0; j < m_rollBuffer.Count; j++)
                    {
                        DropResult drop = m_rollBuffer[j];
                        GrantItem(inventory, drop.ItemId, drop.Amount, summary);
                    }
                }
            }

            string summaryText = summary.Count > 0 ? string.Join("、", summary) : "无";
            Debug.Log($"[{GetType().Name}] 秘境「{dungeon.DisplayName}」通关，奖励已发放：{summaryText}", this);
        }

        /// <summary>把一件物品放进背包并弹出获得提示，同时记进日志汇总</summary>
        private void GrantItem(InventoryManager inventory, string itemId, int amount, List<string> summary)
        {
            if (string.IsNullOrEmpty(itemId) || amount <= 0)
            {
                return;
            }

            // AddItem 返回"实际入包数量"（可能因堆叠上限而少于请求量），提示要报实际值
            int added = inventory.AddItem(itemId, amount);
            ItemGainNotifier.Notify(itemId, added);

            summary.Add($"{ResolveDisplayName(inventory, itemId)}×{added}");
        }

        /// <summary>查物品显示名，查不到就用 ID 兜底（保证日志与提示不空白）</summary>
        private static string ResolveDisplayName(InventoryManager inventory, string itemId)
        {
            ItemDataSO data = inventory != null ? inventory.FindItem(itemId) : null;
            return data != null ? data.DisplayName : itemId;
        }

        // ====================================================================
        // 回程
        // ====================================================================

        /// <summary>
        /// 从秘境返回大世界：加载进入秘境前记录的那个场景。
        /// 记录为空或异常时回退到主菜单场景，保证按钮永远有去处、不会把玩家卡在秘境里
        /// </summary>
        public void ReturnToOverworld()
        {
            string sceneName = DungeonRunContext.ReturnSceneName;
            if (string.IsNullOrEmpty(sceneName))
            {
                sceneName = k_defaultReturnScene;
            }

            Debug.Log($"[{GetType().Name}] 离开秘境，返回场景 {sceneName}", this);

            // 回程即结束本次秘境：清掉上下文，避免大世界把之后的战斗误判成秘境结算
            DungeonRunContext.Clear();

            SceneLoader.Load(sceneName);
        }
    }
}
