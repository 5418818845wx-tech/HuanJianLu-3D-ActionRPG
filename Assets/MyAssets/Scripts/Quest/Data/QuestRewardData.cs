using System;
using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 单条任务奖励的配置数据（属于 QuestDataSO，只读）
    ///
    /// 说明：这里只描述"奖什么、奖多少"，具体怎么发由 IQuestRewardHandler 决定。
    /// 项目当前还没有金币/经验/背包数据系统（步骤八未做），
    /// 所以默认使用 LogQuestRewardHandler（只打日志 + 抛事件），
    /// 等背包与属性系统就绪后写一个实现类挂到 QuestManager 上即可，本文件无需修改
    /// </summary>
    [Serializable]
    public class QuestRewardData
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("奖励类型：金币 / 经验 / 物品")]
        [SerializeField] private EQuestRewardType m_type = EQuestRewardType.Gold;

        [Tooltip("物品 ID（仅物品类型使用；金币/经验可留空）")]
        [SerializeField] private string m_id = string.Empty;

        [Tooltip("数量")]
        [SerializeField, Min(0)] private int m_amount = 1;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>奖励类型</summary>
        public EQuestRewardType Type => m_type;

        /// <summary>物品 ID（金币/经验类型为空）</summary>
        public string Id => m_id;

        /// <summary>数量</summary>
        public int Amount => m_amount;

        // ====================================================================
        // 对外方法
        // ====================================================================

        /// <summary>奖励的展示文本，如"金币 ×200"</summary>
        public string GetDisplayText()
        {
            switch (m_type)
            {
                case EQuestRewardType.Gold:
                    return $"金币 ×{m_amount}";

                case EQuestRewardType.Exp:
                    return $"经验 ×{m_amount}";

                case EQuestRewardType.Item:
                    string itemName = string.IsNullOrEmpty(m_id) ? "未知物品" : m_id;
                    return $"{itemName} ×{m_amount}";

                default:
                    return $"奖励 ×{m_amount}";
            }
        }
    }
}
