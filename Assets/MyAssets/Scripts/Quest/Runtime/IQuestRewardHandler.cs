namespace MyGame.Quest
{
    /// <summary>
    /// 任务奖励发放接口
    ///
    /// 为什么要有这层接口：
    /// 任务系统的职责是"判断该发什么奖励"，而不是"知道金币存在哪里、背包怎么加物品"。
    /// 把发放动作抽象成接口后：
    /// - 现在（无背包/属性系统）：用 LogQuestRewardHandler 只打日志，先跑通流程
    /// - 步骤八做完背包与角色属性后：新增一个 InventoryQuestRewardHandler 实现本接口，
    ///   挂到 QuestManager 的"奖励发放实现"槽位上即可，任务系统与 UI 一行都不用改
    /// </summary>
    public interface IQuestRewardHandler
    {
        /// <summary>
        /// 发放一条奖励
        /// </summary>
        /// <param name="reward">奖励数据（类型 + 物品 ID + 数量）</param>
        /// <param name="sourceQuest">来源任务（便于日志与埋点区分奖励出处）</param>
        void GrantReward(QuestRewardData reward, QuestDataSO sourceQuest);
    }
}
