using MyGame.Combat;
using MyGame.Quest;
using UnityEngine;

namespace MyGame.Inventory
{
    /// <summary>
    /// 任务奖励的「真实发放」实现：把奖励写进背包
    ///
    /// 为什么金币也走背包：
    /// 金币本质上就是一种物品——有数量、要存读档、要在 UI 上显示。
    /// 让它走背包，就免费获得了"数量管理 + 存档 + 堆叠上限"三件事，
    /// 不需要再造一套货币系统，也不会有第二份需要存读档的数值
    ///
    /// 挂载位置：任一常驻物体（建议与 InventoryManager 同一物体），
    /// 然后拖到 QuestManager 的「奖励发放」槽位上
    /// </summary>
    [DisallowMultipleComponent]
    public class InventoryQuestRewardHandler : MonoBehaviour, IQuestRewardHandler
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("金币对应的物品 ID（背包的物品目录里必须有它，否则发奖会失败）")]
        [SerializeField] private string m_goldItemId = "Item_Gold";

        // ====================================================================
        // IQuestRewardHandler 实现
        // ====================================================================

        /// <summary>发放一条奖励</summary>
        public void GrantReward(QuestRewardData reward, QuestDataSO sourceQuest)
        {
            if (reward == null)
            {
                return;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 InventoryManager，奖励未发放", this);
                return;
            }

            string questTitle = sourceQuest != null ? sourceQuest.Title : "未知任务";

            switch (reward.Type)
            {
                case EQuestRewardType.Gold:
                    GrantItem(inventory, m_goldItemId, reward.Amount, questTitle);
                    break;

                case EQuestRewardType.Item:
                    GrantItem(inventory, reward.Id, reward.Amount, questTitle);
                    break;

                case EQuestRewardType.Exp:
                    GrantExp(reward.Amount, questTitle);
                    break;
            }
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>把一种物品放进背包并记日志</summary>
        private void GrantItem(InventoryManager inventory, string itemId, int amount, string questTitle)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                Debug.LogWarning($"[{GetType().Name}] 《{questTitle}》的奖励没有填物品 ID，已跳过", this);
                return;
            }

            int added = inventory.AddItem(itemId, amount);
            Debug.Log($"[{GetType().Name}] 《{questTitle}》奖励已发放：{itemId} ×{added}", this);
        }

        /// <summary>
        /// 发放经验：交给 PlayerLevelSystem 累积（可能连续升好几级）。
        ///
        /// 经验不做"溢出补偿"：等级系统按曲线扣减需求，多出来的继续留在下一级里，
        /// 一次交任务跨级是正常行为
        /// </summary>
        private void GrantExp(int amount, string questTitle)
        {
            if (amount <= 0)
            {
                return;
            }

            PlayerLevelSystem levelSystem = PlayerLevelSystem.Instance;
            if (levelSystem == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 《{questTitle}》奖励经验 ×{amount} 未发放：场景中没有 PlayerLevelSystem", this);
                return;
            }

            levelSystem.AddExp(amount);
            Debug.Log($"[{GetType().Name}] 《{questTitle}》奖励已发放：经验 ×{amount}（当前等级 {levelSystem.Level}）", this);
        }
    }
}
