using System;
using System.Collections.Generic;
using MyGame.Inventory;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 一条技能等级存档项：技能 ID → 等级
    /// 只存动态状态（等级），技能本身的配置永远从 SkillDataSO 资产读
    /// </summary>
    [Serializable]
    public class SkillLevelEntry
    {
        /// <summary>技能 ID（对应 SkillDataSO.Id）</summary>
        public string SkillId;

        /// <summary>已升到的等级（1 = 初始等级）</summary>
        public int Level = 1;
    }

    /// <summary>
    /// 技能升级系统：管"每个技能几级了"，并把等级换算成实际战斗数值
    ///
    /// 三条职责，与本项目其它系统同一套路：
    /// 1. **只存动态状态**：等级存在这里，技能配置（CD/倍率/特效）永远从 SkillDataSO 读
    /// 2. **唯一入口**：升级只能走 TryUpgrade，内部负责校验等级上限与扣金币
    /// 3. **自己负责自己的存档**：CaptureSaveData / ApplySaveData 供 SaveManager 调用
    ///
    /// 为什么升级消耗金币而不是技能点：
    /// 金币已经是"走背包的物品"（Item_Gold），复用它就免费获得了
    /// 数量管理 + 存档 + UI 显示；再引入一套"技能点"等于多一份要存读档的数值，
    /// 却换不来任何新玩法
    ///
    /// 等级带来的效果（都按"绝对值重算"，不是累加）：
    /// - 伤害倍率：每级 +15%
    /// - 冷却时间：每级 -8%，但不会低于原值的 50%
    ///
    /// 挂载位置：场景中一个常驻物体（与其它管理器同层，例如 InventorySystem 所在物体）
    /// </summary>
    [DisallowMultipleComponent]
    public class SkillUpgradeSystem : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("金币")]
        [Tooltip("升级消耗的货币物品 ID（背包的物品目录里必须有它）")]
        [SerializeField] private string m_goldItemId = "Item_Gold";

        [Header("等级与消耗")]
        [Tooltip("技能等级上限")]
        [SerializeField, Min(1)] private int m_maxLevel = 5;

        [Tooltip("1 级升 2 级的花费（金币）")]
        [SerializeField, Min(1)] private int m_baseUpgradeCost = 300;

        [Tooltip("每级花费的成长系数（1.6 = 每级贵 60%）")]
        [SerializeField, Min(1f)] private float m_costGrowth = 1.6f;

        [Header("等级收益")]
        [Tooltip("每级提升的伤害倍率比例（0.15 = 每级 +15%）")]
        [SerializeField, Range(0f, 1f)] private float m_damageBonusPerLevel = 0.15f;

        [Tooltip("每级缩短的冷却比例（0.08 = 每级 -8%）")]
        [SerializeField, Range(0f, 0.5f)] private float m_cooldownReductionPerLevel = 0.08f;

        [Tooltip("冷却缩短的下限系数（0.5 = 冷却最多减半，防止高等级技能无 CD）")]
        [SerializeField, Range(0.1f, 1f)] private float m_minCooldownFactor = 0.5f;

        // ====================================================================
        // 单例与事件
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个）</summary>
        public static SkillUpgradeSystem Instance { get; private set; }

        /// <summary>某个技能升级成功（参数：技能 ID、新等级）。UI 订阅它刷新显示</summary>
        public event Action<string, int> SkillUpgraded;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>技能 ID → 当前等级（不变量：未记录的技能视为 1 级）</summary>
        private readonly Dictionary<string, int> m_levels = new();

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>技能等级上限</summary>
        public int MaxLevel => m_maxLevel;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>取技能当前等级（没有记录的一律视为 1 级）</summary>
        public int GetLevel(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
            {
                return 1;
            }

            return m_levels.TryGetValue(skillId, out int level) ? Mathf.Max(1, level) : 1;
        }

        /// <summary>是否已达等级上限</summary>
        public bool IsMaxLevel(string skillId)
        {
            return GetLevel(skillId) >= m_maxLevel;
        }

        /// <summary>从当前等级升到下一级需要的金币（满级返回 0）</summary>
        public int GetUpgradeCost(string skillId)
        {
            if (IsMaxLevel(skillId))
            {
                return 0;
            }

            int level = GetLevel(skillId);
            float raw = m_baseUpgradeCost * Mathf.Pow(m_costGrowth, level - 1);

            // 至少 1 金币：防止配置成 0 导致"免费升级"
            return Mathf.Max(1, Mathf.RoundToInt(raw));
        }

        /// <summary>
        /// 按等级换算最终伤害倍率。
        /// 语义是"从基础值重算"，所以读档、改配置都会得到同一结果
        /// </summary>
        public float GetDamageMultiplier(string skillId, float baseMultiplier)
        {
            int gained = GetLevel(skillId) - 1;
            return baseMultiplier * (1f + gained * m_damageBonusPerLevel);
        }

        /// <summary>
        /// 按等级换算最终冷却时间。
        /// 下限由 m_minCooldownFactor 兜住：否则等级堆高后冷却会趋近 0，技能变成无脑连发
        /// </summary>
        public float GetCooldown(string skillId, float baseCooldown)
        {
            int gained = GetLevel(skillId) - 1;
            float factor = Mathf.Max(m_minCooldownFactor, 1f - gained * m_cooldownReductionPerLevel);
            return baseCooldown * factor;
        }

        // ====================================================================
        // 升级
        // ====================================================================

        /// <summary>
        /// 尝试升级一个技能：校验等级上限 → 校验金币 → 扣钱 → 升级。
        /// 失败时通过 failReason 说明原因（UI 直接拿去提示玩家）
        /// </summary>
        /// <param name="skillId">技能 ID（SkillDataSO.Id）</param>
        /// <param name="failReason">失败原因（成功时为 null）</param>
        public bool TryUpgrade(string skillId, out string failReason)
        {
            failReason = null;

            if (string.IsNullOrEmpty(skillId))
            {
                failReason = "技能没有 ID，无法升级";
                return false;
            }

            if (IsMaxLevel(skillId))
            {
                failReason = $"已达到最高等级 Lv.{m_maxLevel}";
                return false;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                failReason = "场景中没有背包系统";
                return false;
            }

            int cost = GetUpgradeCost(skillId);
            if (inventory.GetCount(m_goldItemId) < cost)
            {
                failReason = $"金币不足（需要 {cost}）";
                return false;
            }

            // 先扣钱：扣到的数量不足说明中途出了岔子，必须原额退回，不能吞钱
            int paid = inventory.RemoveItem(m_goldItemId, cost);
            if (paid < cost)
            {
                inventory.AddItem(m_goldItemId, paid);
                failReason = "扣款失败";
                return false;
            }

            int newLevel = GetLevel(skillId) + 1;
            m_levels[skillId] = newLevel;

            Debug.Log($"[{GetType().Name}] 技能「{skillId}」升级到 Lv.{newLevel}，花费 {paid} 金币", this);

            SkillUpgraded?.Invoke(skillId, newLevel);
            return true;
        }

        // ====================================================================
        // 存档
        // ====================================================================

        /// <summary>导出技能等级（供 SaveManager 收集）</summary>
        public List<SkillLevelEntry> CaptureSaveData()
        {
            List<SkillLevelEntry> result = new();

            foreach (KeyValuePair<string, int> pair in m_levels)
            {
                result.Add(new SkillLevelEntry { SkillId = pair.Key, Level = pair.Value });
            }

            return result;
        }

        /// <summary>
        /// 用存档覆盖技能等级（覆盖而非合并：读档语义就是"回到存档那一刻"）
        /// </summary>
        public void ApplySaveData(List<SkillLevelEntry> entries)
        {
            m_levels.Clear();

            if (entries != null)
            {
                foreach (SkillLevelEntry entry in entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.SkillId))
                    {
                        continue;
                    }

                    m_levels[entry.SkillId] = Mathf.Clamp(entry.Level, 1, m_maxLevel);
                }
            }

            Debug.Log($"[{GetType().Name}] 已还原 {m_levels.Count} 个技能的等级", this);
        }
    }
}
