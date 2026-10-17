using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 任务配置资产：一个 .asset 文件 = 一条任务模板
    ///
    /// 使用方式：Project 窗口右键 → Create → 游戏数据 → 任务（Quest Data）
    ///
    /// 重要约束（架构红线）：
    /// 1. 本资产只存"静态配置"，运行时的进度与状态一律存放在 QuestState 中，
    ///    绝不把运行时数据写回 ScriptableObject（否则编辑器里的配置会被运行时污染）
    /// 2. 任务的接取/推进/交付全部由 QuestManager 统一处理，本类不含任何逻辑分支
    /// </summary>
    [CreateAssetMenu(fileName = "QuestData", menuName = "游戏数据/任务（Quest Data）", order = 10)]
    public class QuestDataSO : ScriptableObject
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("基础信息")]
        [Tooltip("任务唯一 ID（如 Q_001_Main）。留空则退化为资产文件名，建议显式填写")]
        [SerializeField] private string m_id = string.Empty;

        [Tooltip("任务标题（显示在任务日志与追踪 HUD 上）")]
        [SerializeField] private string m_title = string.Empty;

        [Tooltip("任务描述（任务日志右侧正文）")]
        [SerializeField, TextArea(2, 5)] private string m_description = string.Empty;

        [Tooltip("任务分类：主线 / 支线")]
        [SerializeField] private EQuestCategory m_category = EQuestCategory.Main;

        [Header("任务目标（全部达成后才可交付）")]
        [Tooltip("目标列表：类型 + 目标 ID + 数量。留空的任务没有任何目标，会立即变为可交付")]
        [SerializeField] private List<QuestObjectiveData> m_objectives = new();

        [Header("任务奖励")]
        [Tooltip("奖励列表：交付任务时逐条发给 IQuestRewardHandler")]
        [SerializeField] private List<QuestRewardData> m_rewards = new();

        [Header("接取条件")]
        [Tooltip("前置任务 ID 列表：必须全部已交付，任务才会出现在 NPC 的可接取列表里")]
        [SerializeField] private List<string> m_prerequisiteQuestIds = new();

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>任务唯一 ID（未填写时退化为资产名，保证 ID 永不为空）</summary>
        public string Id => string.IsNullOrEmpty(m_id) ? name : m_id;

        /// <summary>任务标题（未填写时退化为资产名）</summary>
        public string Title => string.IsNullOrEmpty(m_title) ? name : m_title;

        /// <summary>任务描述</summary>
        public string Description => m_description;

        /// <summary>任务分类</summary>
        public EQuestCategory Category => m_category;

        /// <summary>任务目标列表</summary>
        public IReadOnlyList<QuestObjectiveData> Objectives => m_objectives;

        /// <summary>任务奖励列表</summary>
        public IReadOnlyList<QuestRewardData> Rewards => m_rewards;

        /// <summary>前置任务 ID 列表</summary>
        public IReadOnlyList<string> PrerequisiteQuestIds => m_prerequisiteQuestIds;

        // ====================================================================
        // 对外方法
        // ====================================================================

        /// <summary>
        /// 奖励摘要文本，如"金币 ×200、经验 ×150"（任务日志的"奖励"一行使用）
        /// </summary>
        public string GetRewardSummary()
        {
            if (m_rewards == null || m_rewards.Count == 0)
            {
                return "无";
            }

            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < m_rewards.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('、');
                }

                builder.Append(m_rewards[i].GetDisplayText());
            }

            return builder.ToString();
        }

        // ====================================================================
        // 编辑器校验
        // ====================================================================

#if UNITY_EDITOR
        /// <summary>
        /// 在 Inspector 中改数据时给出提示，避免出现"配错但运行时才发现"的情况
        /// </summary>
        private void OnValidate()
        {
            if (string.IsNullOrEmpty(m_id))
            {
                Debug.LogWarning($"[{name}] 任务 ID 为空，将退化为资产名。建议显式填写唯一 ID", this);
            }

            if (string.IsNullOrEmpty(m_title))
            {
                Debug.LogWarning($"[{name}] 任务标题为空，将退化为资产名", this);
            }

            if (m_objectives == null || m_objectives.Count == 0)
            {
                Debug.LogWarning($"[{name}] 任务没有任何目标，接取后会立即变为可交付状态", this);
            }
        }
#endif
    }
}
