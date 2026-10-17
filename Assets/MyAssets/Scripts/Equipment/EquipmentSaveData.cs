using System;
using System.Collections.Generic;

namespace MyGame.Equipment
{
    /// <summary>
    /// 装备系统的存档片段（由 EquipmentSystem 自己导出/还原）
    ///
    /// 存两样东西：
    /// - 每个槽位装的是哪件物品
    /// - 当前手上拿的是哪件（否则读档会停在"另一只手"上，和存档那一刻对不上）
    ///
    /// 只存 ID，槽位对应的武器配置仍以物品资产为准
    ///
    /// ⚠️ [Serializable] 不能漏：JsonUtility 只认标了这个特性的普通类，
    /// 漏了它不会报错——字段会被**静默跳过**，存档里干脆没有这一段，
    /// 而读档时字段停在初始值（空数据），表现成"一读档装备全没了"
    /// </summary>
    [Serializable]
    public class EquipmentSaveData
    {
        /// <summary>各槽位的物品 ID（空字符串 = 空槽）</summary>
        public List<string> slotItemIds = new List<string>();

        /// <summary>当前手持的物品 ID（空字符串 = 空手）</summary>
        public string heldItemId = string.Empty;
    }
}
