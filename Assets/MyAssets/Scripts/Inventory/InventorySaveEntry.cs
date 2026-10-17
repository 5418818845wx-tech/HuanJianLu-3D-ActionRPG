using System;

namespace MyGame.Inventory
{
    /// <summary>
    /// 单条背包存档记录（会被 JsonUtility 序列化）
    ///
    /// 只存「ID + 数量」：物品的名字/描述/堆叠上限都在 ItemDataSO 里，
    /// 所以改配置不需要迁移老存档
    /// </summary>
    [Serializable]
    public class InventorySaveEntry
    {
        /// <summary>物品 ID（对应 ItemDataSO.Id）</summary>
        public string itemId;

        /// <summary>持有数量</summary>
        public int count;
    }
}
