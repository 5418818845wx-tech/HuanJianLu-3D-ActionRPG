using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyGame.Guide
{
    /// <summary>
    /// 单个引导步骤的完成条件。
    ///
    /// 设计原则：**条件全部可以"轮询判断"**，不要求被引导的系统为引导专门发事件。
    /// 原因：引导是一次性的、低频的系统，为了它去五个模块里加事件，
    /// 会让"引导"这个可删可换的模块渗进核心代码；而轮询只是每帧读几个只读属性，
    /// 代价远小于耦合（项目里任务追踪 HUD 也是这么做的）
    /// </summary>
    public enum EGuideCondition
    {
        /// <summary>累计移动一定距离（米）。Amount = 距离</summary>
        MoveDistance,

        /// <summary>闪避一次</summary>
        Dodge,

        /// <summary>攻击一次（进入普攻状态）</summary>
        Attack,

        /// <summary>释放一次技能</summary>
        CastSkill,

        /// <summary>获得任意物品一次（采集 / 拾取都会触发）</summary>
        Collect,

        /// <summary>与任意 NPC 对话一次</summary>
        TalkToNpc,

        /// <summary>接取任务。TargetId = 任务 ID</summary>
        AcceptQuest,

        /// <summary>击杀敌人。Amount = 数量（不填则 1）</summary>
        KillEnemy,

        /// <summary>交付（完成）任务。TargetId = 任务 ID</summary>
        TurnInQuest,

        /// <summary>手动确认：玩家点 HUD 上的「完成本步」按钮才推进（用于纯说明性的步骤）</summary>
        Manual,
    }

    /// <summary>
    /// 一条引导步骤
    /// </summary>
    [Serializable]
    public struct GuideStep
    {
        [Tooltip("步骤标题（HUD 上加重显示，2~6 个字最好）")]
        public string Title;

        [Tooltip("步骤说明（告诉玩家具体怎么做）")]
        [TextArea(2, 4)]
        public string Description;

        [Tooltip("完成条件")]
        public EGuideCondition Condition;

        [Tooltip("条件参数：任务 ID / 物品 ID 等（不用则留空）")]
        public string TargetId;

        [Tooltip("条件数量：移动距离（米）/ 击杀数量等（不用则填 0）")]
        public int Amount;
    }

    /// <summary>
    /// 引导序列资产：一整条新手引导（或关卡流程）的步骤表
    ///
    /// 为什么"一个资产装一整条序列"而不是"一步一个资产"：
    /// 步骤天然有序、且总是整体使用；一步一个资产会产生十几个小文件，
    /// 还得再维护一个"序列资产"来引用它们，反而更麻烦
    ///
    /// 挂载方式：由场景里的 GuideSystem 引用（Inspector 拖入）
    /// </summary>
    [CreateAssetMenu(fileName = "GuideSequence", menuName = "游戏数据/引导序列（Guide Sequence）", order = 4)]
    public class GuideSequenceSO : ScriptableObject
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("序列 ID（存档按此记录进度，改 ID 等于换一条新引导）")]
        [SerializeField] private string m_id = "Guide_Main";

        [Tooltip("步骤列表，按顺序执行")]
        [SerializeField] private List<GuideStep> m_steps = new();

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>序列 ID</summary>
        public string Id => string.IsNullOrEmpty(m_id) ? name : m_id;

        /// <summary>步骤数量</summary>
        public int StepCount => m_steps.Count;

        /// <summary>全部步骤（只读）</summary>
        public IReadOnlyList<GuideStep> Steps => m_steps;

        /// <summary>取某一步（越界返回空步骤，调用方不用做边界判断）</summary>
        public GuideStep GetStep(int index)
        {
            if (index < 0 || index >= m_steps.Count)
            {
                return default;
            }

            return m_steps[index];
        }
    }
}
