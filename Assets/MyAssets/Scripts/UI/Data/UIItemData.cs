using System.Collections.Generic;
using UnityEngine;

namespace MyGame.UI
{
    /// <summary>
    /// 背包物品数据（运行时视图模型）
    ///
    /// 为什么单独放一个文件：
    /// 数据类会被多个系统共用（背包 UI、商店 UI、掉落系统、存档系统），
    /// 不应该挂在某一个 UI 脚本里——否则换 UI 实现（如 UI Toolkit 换 UGUI）时
    /// 删掉旧 UI 脚本会把数据类一起删掉，牵连其他系统。
    ///
    /// 说明：纯 C# 数据类，只承载 UI 展示所需字段。
    /// 配置来自 ItemDataSO，数量来自 InventoryManager —— 本类只做"翻译"，不持有任何数据源
    /// </summary>
    public class InventoryItemData
    {
        /// <summary>物品 ID（点「使用」时要回传给使用系统）</summary>
        public string Id { get; }

        /// <summary>物品名称</summary>
        public string Name { get; }

        /// <summary>物品描述（详情侧栏展示）</summary>
        public string Description { get; }

        /// <summary>
        /// 物品图标（来自 ItemDataSO.Icon）。
        /// 用可写属性而不是构造参数：本类的构造函数末尾是 params 数组，
        /// 在它后面加参数会破坏所有现有调用点；对象初始化器写法更安全
        /// </summary>
        public Sprite Icon { get; set; }

        /// <summary>
        /// 详情大图（来自 ItemDataSO.DetailImage，一般是武器立绘）。
        /// 为 null 时详情栏不显示大图区
        /// </summary>
        public Sprite DetailImage { get; set; }

        /// <summary>持有数量</summary>
        public int Count { get; }

        /// <summary>配置层面是否可被使用（不可用时「使用」按钮置灰）</summary>
        public bool CanUse { get; }

        /// <summary>是否是一把武器（按钮会显示「装备」而不是「使用」）</summary>
        public bool IsWeapon { get; }

        /// <summary>属性行文本（如 "攻击 +12"），每项占一行</summary>
        public IReadOnlyList<string> Stats { get; }

        public InventoryItemData(string id, string name, string description, int count, bool canUse, bool isWeapon, params string[] stats)
        {
            Id = id;
            Name = name;
            Description = description;
            Count = count;
            CanUse = canUse;
            IsWeapon = isWeapon;
            Stats = stats;
        }
    }

    /// <summary>
    /// 商店商品数据（运行时视图模型）
    ///
    /// 说明：纯 C# 数据类，只承载 UI 展示所需字段。
    /// 配置来自 ShopStockDataSO，库存与价格由商店系统在运行时提供
    /// </summary>
    public class ShopItemData
    {
        /// <summary>商品对应的物品 ID（点「购买」时要回传给商店系统）</summary>
        public string Id { get; }

        /// <summary>商品名称</summary>
        public string Name { get; }

        /// <summary>商品描述</summary>
        public string Description { get; }

        /// <summary>售价（金币）</summary>
        public int Price { get; }

        /// <summary>属性行文本（如 "攻击 +12"），每项占一行</summary>
        public IReadOnlyList<string> Stats { get; }

        public ShopItemData(string id, string name, string description, int price, params string[] stats)
        {
            Id = id;
            Name = name;
            Description = description;
            Price = price;
            Stats = stats;
        }
    }
}
