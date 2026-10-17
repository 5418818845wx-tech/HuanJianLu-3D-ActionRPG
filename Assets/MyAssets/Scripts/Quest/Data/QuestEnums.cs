namespace MyGame.Quest
{
    /// <summary>
    /// 任务分类：主线 / 支线
    /// 命名沿用项目既有习惯（E 前缀，与 ETeam / EAttackType 保持一致）
    /// </summary>
    public enum EQuestCategory
    {
        /// <summary>主线任务</summary>
        Main,

        /// <summary>支线任务</summary>
        Side,
    }

    /// <summary>
    /// 任务目标类型
    ///
    /// 扩展方式：在此追加枚举值，并在 QuestManager.ApplyProgressToQuest 对应的
    /// ReportProgress 调用方（击杀/采集/对话/到达系统）里上报即可，Manager 本体无需改动
    /// </summary>
    public enum EQuestObjectiveType
    {
        /// <summary>击杀：TargetId 对应 EnemyAI.EnemyId（如 Enemy_Dummy）</summary>
        Kill,

        /// <summary>收集：TargetId 对应物品 ID（由步骤八的背包系统上报）</summary>
        Collect,

        /// <summary>对话：TargetId 对应 QuestGiver.NpcId</summary>
        Talk,

        /// <summary>到达：TargetId 对应区域 ID（由后续的区域内触发器上报）</summary>
        Reach,
    }

    /// <summary>
    /// 任务状态：一条任务从接取到交付的完整生命周期
    ///
    /// InProgress → （所有目标达成）→ ReadyToTurnIn → （找 NPC 交付）→ Completed
    /// </summary>
    public enum EQuestStatus
    {
        /// <summary>未接取（仅存在于任务资产里，不占用运行时状态）</summary>
        NotAccepted,

        /// <summary>进行中：正在累计目标进度</summary>
        InProgress,

        /// <summary>目标已全部达成，等待向 NPC 交付</summary>
        ReadyToTurnIn,

        /// <summary>已交付完成（奖励已发放）</summary>
        Completed,
    }

    /// <summary>任务奖励类型</summary>
    public enum EQuestRewardType
    {
        /// <summary>金币</summary>
        Gold,

        /// <summary>经验值</summary>
        Exp,

        /// <summary>物品：Id 对应物品 ID</summary>
        Item,
    }
}
