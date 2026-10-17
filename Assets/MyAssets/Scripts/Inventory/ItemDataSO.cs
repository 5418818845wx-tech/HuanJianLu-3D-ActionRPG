using MyGame.Combat;
using UnityEngine;

namespace MyGame.Inventory
{
    /// <summary>
    /// 物品使用效果类型
    ///
    /// 扩展方式：在此追加枚举值，并在 ItemUseSystem 的 IsEffectUseful / ApplyEffect
    /// 两个 switch 里各加一个分支即可，背包与 UI 都不用改
    /// </summary>
    public enum EItemUseType
    {
        /// <summary>不可使用：任务材料、货币等只能躺着的东西</summary>
        None,

        /// <summary>回复生命值：回复量见 ItemDataSO.UseAmount</summary>
        Heal,
    }

    /// <summary>
    /// 物品配置资产（只读，运行时绝不修改）
    ///
    /// 与任务的分工完全一致：
    /// - 本资产只放**配置**：名字、描述、堆叠上限
    /// - "玩家有几个"属于动态状态，存在 InventoryManager 里，绝不写回这里
    ///
    /// 这样改物品描述、调堆叠上限都不会影响老存档
    /// </summary>
    [CreateAssetMenu(fileName = "ItemData", menuName = "游戏数据/物品（Item Data）", order = 1)]
    public class ItemDataSO : ScriptableObject
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("物品唯一 ID：任务里的「收集」目标按此 ID 匹配。命名规范 Item_ + 名字（如 Item_Herb）")]
        [SerializeField] private string m_id = "Item_Unnamed";

        [Tooltip("显示名（背包格子与详情栏使用）")]
        [SerializeField] private string m_displayName = "未命名物品";

        [Tooltip("图标（背包格子显示）。留空则退回显示文字占位")]
        [SerializeField] private Sprite m_icon;

        [Tooltip("详情大图（背包详情栏显示，一般给武器配立绘）。留空则不显示大图")]
        [SerializeField] private Sprite m_detailImage;

        [Tooltip("描述文本（背包详情栏使用）")]
        [SerializeField, TextArea] private string m_description = string.Empty;

        [Tooltip("单格堆叠上限（0 或负数表示不限）")]
        [SerializeField] private int m_maxStack = 99;

        [Header("使用效果")]
        [Tooltip("使用效果类型：None = 不可使用（金币、任务材料等）")]
        [SerializeField] private EItemUseType m_useType = EItemUseType.None;

        [Tooltip("效果数值：Heal 时为回复的生命值")]
        [SerializeField, Min(0f)] private float m_useAmount = 0f;

        [Header("武器（可选）")]
        [Tooltip("填上它就是一把武器：背包里点「装备」会把它换到手上（装备不消耗物品）。" +
                 "留空则是普通物品。注意两边是同一件事的两种描述，不能再配使用效果")]
        [SerializeField] private WeaponDataSO m_weapon;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>物品唯一 ID（为空时退化为资产名，方便早期省略填写）</summary>
        public string Id => string.IsNullOrEmpty(m_id) ? name : m_id;

        /// <summary>显示名（未填时退化为资产名）</summary>
        public string DisplayName => string.IsNullOrEmpty(m_displayName) ? name : m_displayName;

        /// <summary>图标（未配置时为 null，UI 退回文字占位）</summary>
        public Sprite Icon => m_icon;

        /// <summary>详情大图（未配置时为 null，详情栏不显示大图）</summary>
        public Sprite DetailImage => m_detailImage;

        /// <summary>描述文本</summary>
        public string Description => m_description;

        /// <summary>单格堆叠上限（0 或负数表示不限）</summary>
        public int MaxStack => m_maxStack;

        /// <summary>使用效果类型</summary>
        public EItemUseType UseType => m_useType;

        /// <summary>效果数值（Heal 时为回复量）</summary>
        public float UseAmount => m_useAmount;

        /// <summary>
        /// 配置层面是否"可被使用"。
        /// 注意这是静态判断：满血时喝回复药仍然"可被使用"，只是当前用不上——
        /// 那种运行时判断归 ItemUseSystem，UI 不该算
        /// </summary>
        public bool CanUse => m_useType != EItemUseType.None && m_useAmount > 0f;

        /// <summary>对应的武器配置（不是武器时为 null）</summary>
        public WeaponDataSO Weapon => m_weapon;

        /// <summary>是否是一把武器（背包里会显示「装备」而不是「使用」）</summary>
        public bool IsWeapon => m_weapon != null;
    }
}
