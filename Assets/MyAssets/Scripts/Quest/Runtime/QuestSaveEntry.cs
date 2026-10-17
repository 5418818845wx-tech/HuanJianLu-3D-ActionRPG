using System;

namespace MyGame.Quest
{
    /// <summary>
    /// 单条任务的存档快照
    ///
    /// 设计原则（存档的通用规则）：
    /// - **只存动态状态，不存静态配置**。任务的标题/目标/奖励都在 QuestDataSO 里，
    ///   存档里只留一个任务 ID，读档时按 ID 把配置找回来。
    ///   好处：以后策划改了任务描述、数值，老存档不用迁移就能生效
    /// - 枚举存**整数**而不是名字：以后往 EQuestStatus 中间插新值不会让老存档错位
    ///   （顺序只能在末尾追加，这是这种写法的代价，写在这里当约定）
    /// </summary>
    [Serializable]
    public class QuestSaveEntry
    {
        /// <summary>任务 ID（读档时据此找回 QuestDataSO）</summary>
        public string questId;

        /// <summary>任务状态（EQuestStatus 的整数值）</summary>
        public int status;

        /// <summary>每个目标已累计的进度，下标与 QuestDataSO.Objectives 一一对应</summary>
        public int[] objectiveProgress;
    }
}
