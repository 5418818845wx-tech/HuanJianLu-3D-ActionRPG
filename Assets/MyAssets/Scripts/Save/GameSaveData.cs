using System;
using System.Collections.Generic;
using MyGame.Combat;
using MyGame.Equipment;
using MyGame.Inventory;
using MyGame.Quest;
using UnityEngine;

namespace MyGame.Save
{
    /// <summary>
    /// 一次存档的完整内容（会被 JsonUtility 序列化成 JSON）
    ///
    /// 扩展方式：以后要存背包/血量/已解锁技能，就在这里加一个字段，
    /// 并在 SaveManager 的收集/还原方法里各补一段。老存档缺字段会取默认值，不会报错。
    /// </summary>
    [Serializable]
    public class GameSaveData
    {
        /// <summary>
        /// 当前程序支持的存档版本（就是下面 version 字段的默认值）。
        /// 读档时拿它和文件里的版本比对：比它高说明是新版程序写的，本程序可能读不懂，直接拒绝
        /// </summary>
        public const int CurrentVersion = 5;

        /// <summary>
        /// 存档格式版本。以后结构发生**不兼容**变化时 +1，
        /// 读档时据此决定要不要走迁移逻辑。
        ///
        /// 版本历史：
        /// - 1：任务 + 背包 + 玩家位置
        /// - 2：新增装备（v1 存档没有这项，读档时保留开局装备而不是把玩家扒光）
        /// - 3：新增玩家等级与经验（v2 及更老存档没有这项，按 1 级处理）
        /// - 4：新增技能等级（v3 及更老存档没有这项，所有技能按 1 级处理）
        /// - 5：新增新手引导进度（v4 及更老存档视为"引导已完成"，不打扰老玩家）
        /// </summary>
        public int version = CurrentVersion;

        /// <summary>存档时间（UTC，ISO 8601），用于存档界面显示"最后保存于…"</summary>
        public string savedAtUtc;

        // ====================================================================
        // 玩家
        // ====================================================================

        /// <summary>玩家位置</summary>
        public Vector3 playerPosition;

        /// <summary>玩家朝向（只存绕 Y 轴的偏航角，俯仰/翻滚对角色没意义）</summary>
        public float playerYaw;

        /// <summary>
        /// 玩家等级（v3 新增）。老存档没有这个字段时反序列化为默认值 1，
        /// 正好等于"新号"，不需要额外迁移
        /// </summary>
        public int playerLevel = 1;

        /// <summary>当前等级内已累积的经验（v3 新增）</summary>
        public int playerExp;

        // ====================================================================
        // 任务
        // ====================================================================

        /// <summary>所有已接取过的任务（含已完成），顺序即接取顺序</summary>
        public List<QuestSaveEntry> quests = new List<QuestSaveEntry>();

        // ====================================================================
        // 背包
        // ====================================================================

        /// <summary>背包内容（只存物品 ID 与数量，配置在 ItemDataSO 里）</summary>
        public List<InventorySaveEntry> inventory = new List<InventorySaveEntry>();

        // ====================================================================
        // 装备
        // ====================================================================

        /// <summary>装备状态（槽位 + 当前手持，由 EquipmentSystem 自己解释）</summary>
        public EquipmentSaveData equipment = new EquipmentSaveData();

        // ====================================================================
        // 技能
        // ====================================================================

        /// <summary>
        /// 技能等级（技能 ID → 等级，由 SkillUpgradeSystem 自己解释）。
        /// v4 新增；老存档为空列表，等于所有技能都是 1 级
        /// </summary>
        public List<SkillLevelEntry> skillLevels = new List<SkillLevelEntry>();

        // ====================================================================
        // 新手引导
        // ====================================================================

        /// <summary>
        /// 引导进度：0 起表示"当前进行到第几步"，大于等于总步数表示已完成。
        /// v5 新增。新游戏时是默认值 0（从第一步开始）
        /// </summary>
        public int guideStepIndex;

        /// <summary>玩家是否跳过过引导（跳过之后不再播）</summary>
        public bool guideSkipped;
    }
}
