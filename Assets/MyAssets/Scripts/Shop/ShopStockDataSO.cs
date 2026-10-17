using System;
using System.Collections.Generic;
using MyGame.Inventory;
using UnityEngine;

namespace MyGame.Shop
{
    /// <summary>
    /// 一个商店的进货表（只读配置资产）
    ///
    /// 为什么价格不写在 ItemDataSO 里：
    /// 同一件物品在不同商店的售价可以不同（偏远村庄卖得更贵），
    /// 价格属于"这家店"，不属于"这件货"。
    /// 所以这里用 商品 + 售价 的组合来描述货架
    /// </summary>
    [CreateAssetMenu(fileName = "ShopStock", menuName = "游戏数据/商店进货表（Shop Stock）", order = 2)]
    public class ShopStockDataSO : ScriptableObject
    {
        /// <summary>一条在售商品</summary>
        [Serializable]
        public class Entry
        {
            [Tooltip("商品（物品配置资产）")]
            [SerializeField] private ItemDataSO m_item;

            [Tooltip("买价：玩家要付多少金币")]
            [SerializeField, Min(1)] private int m_buyPrice = 10;

            [Tooltip("收价：玩家卖给这家店能拿多少金币。0 表示这家店不收这件货")]
            [SerializeField, Min(0)] private int m_sellPrice = 0;

            /// <summary>商品配置</summary>
            public ItemDataSO Item => m_item;

            /// <summary>买价</summary>
            public int BuyPrice => m_buyPrice;

            /// <summary>收价（0 表示不收）</summary>
            public int SellPrice => m_sellPrice;
        }

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("商店唯一 ID：剧本里用 <<open_shop \"Shop_Xxx\">> 打开，必须与剧本里写的一致")]
        [SerializeField] private string m_id = "Shop_Unnamed";

        [Tooltip("商店显示名（给玩家看）")]
        [SerializeField] private string m_displayName = "杂货铺";

        [Tooltip("在售商品")]
        [SerializeField] private List<Entry> m_entries = new();

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>商店唯一 ID（为空时退化为资产名）</summary>
        public string Id => string.IsNullOrEmpty(m_id) ? name : m_id;

        /// <summary>商店显示名</summary>
        public string DisplayName => m_displayName;

        /// <summary>在售商品</summary>
        public IReadOnlyList<Entry> Entries => m_entries;

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>按物品 ID 查买价（不在货架上返回 -1）</summary>
        public int FindBuyPrice(string itemId)
        {
            Entry entry = FindEntry(itemId);
            return entry != null ? entry.BuyPrice : -1;
        }

        /// <summary>
        /// 按物品 ID 查收价。
        /// 返回 -1 表示这家店**根本不认**这件货（不在进货表里），
        /// 返回 0 表示认但当前不收——两者对玩家的说法不同，所以不合并
        /// </summary>
        public int FindSellPrice(string itemId)
        {
            Entry entry = FindEntry(itemId);
            return entry != null ? entry.SellPrice : -1;
        }

        /// <summary>按物品 ID 找进货表里的一行（找不到返回 null）</summary>
        private Entry FindEntry(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            for (int i = 0; i < m_entries.Count; i++)
            {
                Entry entry = m_entries[i];

                if (entry == null || entry.Item == null)
                {
                    continue;
                }

                if (entry.Item.Id == itemId)
                {
                    return entry;
                }
            }

            return null;
        }
    }
}
