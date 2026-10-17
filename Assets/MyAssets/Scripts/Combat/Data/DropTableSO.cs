using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 单条掉落配置：掉什么、掉几个、多大概率掉
    /// </summary>
    [Serializable]
    public struct DropEntry
    {
        [Tooltip("物品 ID：与 ItemDataSO.Id 一致（如 Item_Gold / Item_Herb）")]
        public string ItemId;

        [Tooltip("掉落数量下限（含）")]
        [Min(1)] public int MinAmount;

        [Tooltip("掉落数量上限（含）。等于下限时就是固定数量")]
        [Min(1)] public int MaxAmount;

        [Tooltip("掉落概率（0~1）。1 = 必掉，0.3 = 三成几率")]
        [Range(0f, 1f)] public float Chance;
    }

    /// <summary>
    /// 一次摇奖的结果：某件物品掉几个
    ///
    /// 为什么用结构体：结果会被高频创建（每次击杀都要摇），
    /// 结构体不产生 GC 垃圾，也不会被外部误改配置
    /// </summary>
    public struct DropResult
    {
        /// <summary>掉落物品 ID</summary>
        public string ItemId;

        /// <summary>掉落数量（恒大于 0）</summary>
        public int Amount;
    }

    /// <summary>
    /// 掉落表资产：一张表描述"这种敌人死了会掉什么"
    ///
    /// 与战斗的其它数据资产（SkillDataSO / WeaponDataSO）分工一致：
    /// - 本资产只放**配置**：掉什么、概率多少、数量区间
    /// - "实际掉了什么"是运行时结果，由 DropSystem 摇完直接生成拾取物，不写回这里
    ///
    /// 判定方式：**每条独立摇一次**。
    /// 想表达"必掉金币，另外三成概率再掉个材料"，就写两行：
    ///   1) Item_Gold 数量 5~10 概率 1
    ///   2) Item_Herb 数量 1~1  概率 0.3
    /// 而不是做"整表权重抽一件"——那种抽法会强制玩家每次只能拿到一样东西
    /// </summary>
    [CreateAssetMenu(fileName = "DropTable", menuName = "游戏数据/掉落表（Drop Table）", order = 2)]
    public class DropTableSO : ScriptableObject
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("掉落条目：每条独立判定概率，可能同时掉出多件")]
        [SerializeField] private List<DropEntry> m_entries = new();

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>全部掉落条目</summary>
        public List<DropEntry> Entries => m_entries;

        // ====================================================================
        // 摇奖
        // ====================================================================

        /// <summary>
        /// 摇一次掉落，把命中的条目追加进 results。
        ///
        /// 为什么不直接在这里发物品：
        /// 掉落表是"配置"，发物品是"运行时行为"。混在一起后，
        /// 想改成掉地上、掉宝箱里、还是直接进背包，都得回来改这张表
        ///
        /// 传入外部列表而不是返回新列表：击杀是高频道事件，
        /// 复用同一个 List 可以避免每次击杀都产生 GC 垃圾
        /// </summary>
        /// <param name="results">结果输出列表（调用方负责先 Clear）</param>
        public void Roll(List<DropResult> results)
        {
            if (results == null)
            {
                return;
            }

            for (int i = 0; i < m_entries.Count; i++)
            {
                DropEntry entry = m_entries[i];

                // 没填物品 ID 的条目直接跳过：这是配置笔误，不是"掉了件无名物"
                if (string.IsNullOrEmpty(entry.ItemId))
                {
                    continue;
                }

                // 概率判定：Chance >= 1 时 Random.value 恒小于它，等价于必掉
                if (entry.Chance < 1f && UnityEngine.Random.value > entry.Chance)
                {
                    continue;
                }

                // 数量区间：Random.Range 的 int 版本上限是**不含**的，所以 +1
                int min = Mathf.Max(1, entry.MinAmount);
                int max = Mathf.Max(min, entry.MaxAmount);
                int amount = UnityEngine.Random.Range(min, max + 1);

                results.Add(new DropResult { ItemId = entry.ItemId, Amount = amount });
            }
        }
    }
}
