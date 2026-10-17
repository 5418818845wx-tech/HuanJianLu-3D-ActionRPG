using System;
using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 单条任务目标的配置数据（属于 QuestDataSO，只读，运行时不修改）
    ///
    /// 设计说明：用"类型 + 目标 ID + 数量"三元组描述一切目标，
    /// 好处是新增目标类型时无需改动数据结构与 UI，只需在上报端调用
    /// QuestManager.ReportProgress(类型, 目标ID, 数量) 即可
    /// </summary>
    [Serializable]
    public class QuestObjectiveData
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("目标类型：击杀 / 收集 / 对话 / 到达")]
        [SerializeField] private EQuestObjectiveType m_type = EQuestObjectiveType.Kill;

        [Tooltip("目标 ID：击杀填敌人 ID（EnemyAI.EnemyId），收集填物品 ID，" +
                 "对话填 NPC ID（QuestGiver.NpcId），到达填区域 ID。留空表示匹配任意目标")]
        [SerializeField] private string m_targetId = string.Empty;

        [Tooltip("需要达成的数量")]
        [SerializeField, Min(1)] private int m_requiredAmount = 1;

        [Tooltip("显示在任务日志/追踪 HUD 上的文本。留空则按类型自动生成")]
        [SerializeField] private string m_description = string.Empty;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>目标类型</summary>
        public EQuestObjectiveType Type => m_type;

        /// <summary>目标 ID（可为空，表示匹配任意目标）</summary>
        public string TargetId => m_targetId;

        /// <summary>需要达成的数量（至少为 1）</summary>
        public int RequiredAmount => Mathf.Max(1, m_requiredAmount);

        /// <summary>目标显示文本（未手填时按类型自动生成）</summary>
        public string Description =>
            string.IsNullOrEmpty(m_description) ? BuildDefaultDescription() : m_description;

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>按类型生成默认描述文本，省去手填大量重复文案</summary>
        private string BuildDefaultDescription()
        {
            // 未填目标 ID 时用"任意"占位，避免出现"击败 ×3"这种割裂文案
            string target = string.IsNullOrEmpty(m_targetId) ? "任意目标" : m_targetId;

            switch (m_type)
            {
                case EQuestObjectiveType.Kill:
                    return $"击败 {target} ×{RequiredAmount}";

                case EQuestObjectiveType.Collect:
                    return $"收集 {target} ×{RequiredAmount}";

                case EQuestObjectiveType.Talk:
                    return $"与 {target} 对话";

                case EQuestObjectiveType.Reach:
                    return $"到达 {target}";

                default:
                    return $"{target} ×{RequiredAmount}";
            }
        }
    }
}
