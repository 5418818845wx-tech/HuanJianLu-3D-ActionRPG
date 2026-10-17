using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 默认奖励发放实现：只打日志，不做任何数值变更
    ///
    /// 用途：在金币/经验/背包数据系统（步骤八）就绪之前，让任务链路的"交付 → 发奖"
    /// 环节可以先跑通、可验证。等真实系统就绪后，把 QuestManager 的
    /// "奖励发放实现"槽位换成新写的实现类即可，本类无需删除
    /// </summary>
    public class LogQuestRewardHandler : IQuestRewardHandler
    {
        /// <inheritdoc />
        public void GrantReward(QuestRewardData reward, QuestDataSO sourceQuest)
        {
            if (reward == null)
            {
                return;
            }

            string questTitle = sourceQuest != null ? sourceQuest.Title : "未知任务";
            Debug.Log($"[任务奖励] 来自《{questTitle}》→ {reward.GetDisplayText()}（当前为占位实现，未写入任何数值系统）");
        }
    }
}
