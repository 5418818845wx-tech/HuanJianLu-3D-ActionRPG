using System;
using System.Collections.Generic;
using MyGame.Combat;
using UnityEngine;

namespace MyGame.Dungeon
{
    /// <summary>
    /// 秘境的配置资产：一张秘境"名片"，描述它是谁、去哪、给什么奖励
    ///
    /// 与其它数据资产（ItemDataSO / DropTableSO / WeaponDataSO）分工一致：
    /// - 本资产只放**配置**：显示名、场景名、固定奖励、随机奖励表
    /// - "这次通了关、发了几个"属于运行时结果，由 DungeonManager 摇完直接塞进背包，绝不写回这里
    ///
    /// 为什么奖励要分「固定」和「随机」两拨：
    /// - 固定奖励是保底（必给的金币/材料），让玩家"打了就有收获"，不空手而归
    /// - 随机奖励复用已有的 DropTableSO，直接沿用战斗中那套"每条独立判定概率"的抽法，
    ///   不再为秘境单独发明一套权重表，配置与维护都更省事
    ///
    /// 创建方式：右键 → 创建 → 游戏数据 → 秘境（Dungeon）
    /// </summary>
    [CreateAssetMenu(fileName = "Dungeon", menuName = "游戏数据/秘境（Dungeon）", order = 10)]
    public class DungeonDefinitionSO : ScriptableObject
    {
        /// <summary>
        /// 一条"必给"的固定奖励：某件物品给几个
        ///
        /// 用嵌套类而不是两个平行 List（物品 ID 列表 + 数量列表）：
        /// 平行列表一旦有一边漏填/错位就会"金币发成了草药"，
        /// 成对的结构体让编辑器里一眼看清"这个东西给这么多"
        /// </summary>
        [Serializable]
        public class FixedReward
        {
            [Tooltip("物品 ID：与 ItemDataSO.Id 一致（如 Item_Gold / Item_Herb）")]
            public string ItemId;

            [Tooltip("发放数量（少于等于 0 时该条被跳过）")]
            [Min(0)] public int Amount = 1;
        }

        // ====================================================================
        // 基础信息
        // ====================================================================

        [Header("基础信息")]
        [Tooltip("秘境唯一 ID：用于存档与程序内定位。留空则退化为资产名")]
        [SerializeField] private string m_dungeonId = "Dungeon_Unnamed";

        [Tooltip("显示名（面板列表与交互提示用，如 松林试炼场）")]
        [SerializeField] private string m_displayName = "未命名秘境";

        [Tooltip("描述文本（面板里展示秘境背景与玩法）")]
        [SerializeField, TextArea] private string m_description = string.Empty;

        [Tooltip("要加载的场景名（必须已在 Build Settings 中注册）")]
        [SerializeField] private string m_sceneName = string.Empty;

        [Tooltip("推荐等级（仅用于展示，不做任何限制）")]
        [SerializeField, Min(1)] private int m_recommendedLevel = 1;

        [Tooltip("图标（面板列表显示）。留空则退回文字占位")]
        [SerializeField] private Sprite m_icon;

        // ====================================================================
        // 奖励配置
        // ====================================================================

        [Header("固定奖励（必给，保底）")]
        [Tooltip("通关后无条件发放的奖励清单，可为空（表示只靠随机奖励）")]
        [SerializeField] private List<FixedReward> m_fixedRewards = new();

        [Header("随机奖励（复用掉落表）")]
        [Tooltip("随机奖励用的掉落表。留空表示没有随机奖励")]
        [SerializeField] private DropTableSO m_randomDropTable;

        [Tooltip("从掉落表摇几次。每次独立判定，所以实际到手数量会浮动。填 0 表示不摇")]
        [SerializeField, Min(0)] private int m_randomRolls = 0;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>秘境唯一 ID（未填时退化为资产名，方便早期省略填写）</summary>
        public string DungeonId => string.IsNullOrEmpty(m_dungeonId) ? name : m_dungeonId;

        /// <summary>显示名（未填时退化为资产名，保证 UI 不出现空白标题）</summary>
        public string DisplayName => string.IsNullOrEmpty(m_displayName) ? name : m_displayName;

        /// <summary>描述文本</summary>
        public string Description => m_description;

        /// <summary>要加载的场景名</summary>
        public string SceneName => m_sceneName;

        /// <summary>推荐等级（仅展示）</summary>
        public int RecommendedLevel => m_recommendedLevel;

        /// <summary>图标（未配置时为 null，UI 退回文字占位）</summary>
        public Sprite Icon => m_icon;

        /// <summary>固定奖励清单</summary>
        public List<FixedReward> FixedRewards => m_fixedRewards;

        /// <summary>随机奖励的掉落表（未配置时为 null）</summary>
        public DropTableSO RandomDropTable => m_randomDropTable;

        /// <summary>随机奖励的摇奖次数</summary>
        public int RandomRolls => m_randomRolls;
    }
}
