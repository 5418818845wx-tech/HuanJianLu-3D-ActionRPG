using System;
using System.Collections.Generic;
using MyGame.Audio;
using MyGame.Inventory;
using UnityEngine;

namespace MyGame.Shop
{
    /// <summary>
    /// 商店系统：负责"买卖东西"的规则（钱够不够、钱货怎么换）
    ///
    /// 职责边界（很重要）：
    /// - 真正的数量增减全部交给 InventoryManager——**金币和商品都是物品**
    /// - 本类只做校验、商店 ID → 进货表的映射，以及"一进一出"的原子操作
    ///
    /// 为什么金币当成物品：金币有数量、要存读档、要显示，和物品完全一样。
    /// 走背包就免费拿到了这三件事，不必再造一套货币系统
    ///
    /// 买与卖是镜像的两个方法（TryBuy / TrySell）：
    /// 都遵守"先校验 → 失败不留痕 → 成功才动背包"，所以永远不会出现
    /// "钱扣了货没到"或"货交了钱没来"的半个交易
    ///
    /// 挂载位置：场景中一个独立的管理器物体（与其他管理器同层）
    /// </summary>
    [DisallowMultipleComponent]
    public class ShopSystem : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("商店目录")]
        [Tooltip("本作全部商店的进货表。新增商店记得加到这里（与任务目录同理）")]
        [SerializeField] private List<ShopStockDataSO> m_shopCatalog = new();

        [Tooltip("金币对应的物品 ID（背包的物品目录里必须有它）")]
        [SerializeField] private string m_goldItemId = "Item_Gold";

        // ====================================================================
        // 单例与对外事件
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 ShopSystem）</summary>
        public static ShopSystem Instance { get; private set; }

        /// <summary>一次交易成功（UI 订阅它刷新金币与可购状态）</summary>
        public event Action TransactionCompleted;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>金币对应的物品 ID</summary>
        public string GoldItemId => m_goldItemId;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 ShopSystem 实例，重复实例将被销毁", this);
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

        /// <summary>按 ID 找商店的进货表（找不到返回 null）</summary>
        public ShopStockDataSO FindShop(string shopId)
        {
            if (string.IsNullOrEmpty(shopId))
            {
                return null;
            }

            for (int i = 0; i < m_shopCatalog.Count; i++)
            {
                ShopStockDataSO shop = m_shopCatalog[i];
                if (shop != null && shop.Id == shopId)
                {
                    return shop;
                }
            }

            return null;
        }

        /// <summary>玩家当前金币（没有背包时返回 0）</summary>
        public int GetGold()
        {
            InventoryManager inventory = InventoryManager.Instance;
            return inventory != null ? inventory.GetCount(m_goldItemId) : 0;
        }

        // ====================================================================
        // 交易
        // ====================================================================

        /// <summary>
        /// 买一件商品：金币够就"扣钱 + 收货"。
        /// 失败时通过 failReason 说明原因（UI 直接拿去提示玩家），成功时返回 true
        /// </summary>
        public bool TryBuy(string shopId, string itemId, out string failReason)
        {
            bool success = TryBuyCore(shopId, itemId, out failReason);

            // 音效放在公开入口统一播：成功/失败（含各种提前 return）都能覆盖到，
            // 不用在每个失败分支里各写一遍
            AudioManager.Play(success ? ESfx.Shop_Buy : ESfx.Shop_Fail);
            return success;
        }

        /// <summary>购买的实际逻辑（音效由 TryBuy 统一处理）</summary>
        private bool TryBuyCore(string shopId, string itemId, out string failReason)
        {
            failReason = null;

            ShopStockDataSO shop = FindShop(shopId);
            if (shop == null)
            {
                failReason = $"找不到商店「{shopId}」";
                return false;
            }

            int price = shop.FindBuyPrice(itemId);
            if (price < 0)
            {
                failReason = $"「{itemId}」不在这家店的货架上";
                return false;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                failReason = "场景中没有背包系统";
                return false;
            }

            if (inventory.GetCount(m_goldItemId) < price)
            {
                failReason = "金币不足";
                return false;
            }

            int paid = inventory.RemoveItem(m_goldItemId, price);
            if (paid < price)
            {
                // 理论上不该发生（上面刚查过），但真发生了必须把钱还回去
                inventory.AddItem(m_goldItemId, paid);
                failReason = "扣款失败";
                return false;
            }

            int received = inventory.AddItem(itemId, 1);
            if (received <= 0)
            {
                // 背包满 → 退款。宁可交易失败，也不能出现"钱没了货没到"
                inventory.AddItem(m_goldItemId, paid);
                failReason = "背包已满，交易取消并已退款";
                return false;
            }

            Debug.Log($"[{GetType().Name}] 在「{shop.DisplayName}」购买 {itemId} ×{received}，花费 {paid} 金币", this);

            TransactionCompleted?.Invoke();
            return true;
        }

        /// <summary>
        /// 卖一件物品：这家店收就"交货 + 收钱"。
        /// 与 TryBuy 严格镜像：同样先校验、失败不留痕、成功才动背包
        /// </summary>
        /// <param name="shopId">商店 ID</param>
        /// <param name="itemId">要卖的物品 ID</param>
        /// <param name="failReason">失败原因（成功时为 null）</param>
        public bool TrySell(string shopId, string itemId, out string failReason)
        {
            bool success = TrySellCore(shopId, itemId, out failReason);

            // 与 TryBuy 同理：音效在公开入口统一播，覆盖成功与所有失败分支
            AudioManager.Play(success ? ESfx.Shop_Sell : ESfx.Shop_Fail);
            return success;
        }

        /// <summary>出售的实际逻辑（音效由 TrySell 统一处理）</summary>
        private bool TrySellCore(string shopId, string itemId, out string failReason)
        {
            failReason = null;

            ShopStockDataSO shop = FindShop(shopId);
            if (shop == null)
            {
                failReason = $"找不到商店「{shopId}」";
                return false;
            }

            int price = shop.FindSellPrice(itemId);
            if (price < 0)
            {
                failReason = $"「{itemId}」这家店不收";
                return false;
            }

            if (price <= 0)
            {
                failReason = $"「{itemId}」暂时没有收价";
                return false;
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                failReason = "场景中没有背包系统";
                return false;
            }

            if (itemId == m_goldItemId)
            {
                // 金币不能卖金币：既没意义，也会让"钱货交换"这套判断绕回去
                failReason = "金币不能出售";
                return false;
            }

            int handed = inventory.RemoveItem(itemId, 1);
            if (handed < 1)
            {
                failReason = "背包里没有这件物品";
                return false;
            }

            int gained = inventory.AddItem(m_goldItemId, price);
            if (gained < price)
            {
                // 理论上不该发生（金币不设堆叠上限），但真发生了必须把货还给玩家
                if (gained > 0)
                {
                    inventory.RemoveItem(m_goldItemId, gained);
                }

                inventory.AddItem(itemId, handed);
                failReason = "收款失败，交易取消";
                return false;
            }

            Debug.Log($"[{GetType().Name}] 在「{shop.DisplayName}」卖出 {itemId} ×{handed}，得到 {gained} 金币", this);

            TransactionCompleted?.Invoke();
            return true;
        }
    }
}
