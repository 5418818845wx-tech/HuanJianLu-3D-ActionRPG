using System;
using UnityEngine;

namespace MyGame.Inventory
{
    /// <summary>
    /// 开局赠送的一条物品（属于 InventoryManager 的配置，不是运行时状态）
    ///
    /// 为什么不做成字段直接写"金币 1000"：
    /// 以后要送初始装备、初始药水时，只需要在清单里多一行，
    /// 不用为每种东西各加一个字段，也不用改管理者代码
    /// </summary>
    [Serializable]
    public class InventoryStartingItem
    {
        [Tooltip("物品资产（按它的 ID 发放）")]
        [SerializeField] private ItemDataSO m_item;

        [Tooltip("发放数量")]
        [SerializeField, Min(1)] private int m_count = 1;

        /// <summary>物品资产</summary>
        public ItemDataSO Item => m_item;

        /// <summary>发放数量</summary>
        public int Count => m_count;
    }
}
