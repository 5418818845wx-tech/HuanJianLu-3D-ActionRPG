using System;
using System.Collections.Generic;
using MyGame.Combat;
using MyGame.Inventory;
using UnityEngine;

namespace MyGame.Equipment
{
    /// <summary>
    /// 装备系统：管"手上拿的是哪把武器"，同时是**背包与战斗之间唯一的桥**
    ///
    /// 为什么需要这一层：
    /// - 背包只认识"物品 ID"，不认识 WeaponDataSO
    /// - 战斗只认识 WeaponDataSO，不知道物品的存在
    /// 两边各说各的语言，硬要互相认识就会让背包依赖战斗、或战斗依赖背包。
    /// 装备系统做翻译：物品 → 武器配置，并把结果写进 WeaponSystem 的槽位
    ///
    /// 装备 ≠ 消耗：
    /// 装上武器**不从背包扣掉**（物品还在，只是"被装在身上"）。
    /// 所以一把武器可以反复装备，卖掉它也不影响当前手持状态
    ///
    /// 挂载位置：玩家身上（与 WeaponSystem 同一物体），因为装备本来就是"这个角色的状态"
    /// </summary>
    [DisallowMultipleComponent]
    public class EquipmentSystem : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("初始装备")]
        [Tooltip("槽位 1（主手）开局装备的武器物品。留空则该槽位开局是空的")]
        [SerializeField] private ItemDataSO m_slot1Item;

        [Tooltip("槽位 2（副手）开局装备的武器物品")]
        [SerializeField] private ItemDataSO m_slot2Item;

        [Header("组件引用")]
        [Tooltip("武器系统（留空则在本物体与子物体上自动查找）")]
        [SerializeField] private WeaponSystem m_weaponSystem;

        // ====================================================================
        // 单例与对外事件
        // ====================================================================

        /// <summary>全局单例（本作只有一个玩家）</summary>
        public static EquipmentSystem Instance { get; private set; }

        /// <summary>装备发生变化（换装、读档后触发；UI 订阅它刷新）</summary>
        public event Action EquipmentChanged;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>两个槽位各装的是哪件物品（null = 空槽）</summary>
        private readonly ItemDataSO[] m_slots = new ItemDataSO[WeaponSystem.k_slotCount];

        /// <summary>是否已经发过初始装备（见 EnsureInitialized 的说明）</summary>
        private bool m_isInitialized;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>槽位数量</summary>
        public int SlotCount => m_slots.Length;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 EquipmentSystem 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;

            if (m_weaponSystem == null)
            {
                m_weaponSystem = GetComponent<WeaponSystem>();
            }
            if (m_weaponSystem == null)
            {
                m_weaponSystem = GetComponentInChildren<WeaponSystem>();
            }
        }

        private void Start()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// 首次使用时把 Inspector 里的初始装备写进槽位。
        ///
        /// 为什么不能只靠 Start：
        /// - 不在 Awake 里发，是因为要等 WeaponSystem 的 Awake 缓存好各战斗子系统，
        ///   否则 SetSlotWeapon → ApplyWeaponData 会打到还没准备好的对象上
        /// - 但 Start 的触发时机在编辑器自动化 / 脚本重载等场景下并不可靠
        ///   （本项目用工具驱动进 Play 时就出现过 Start 未触发、开局赤手空拳的情况），
        ///   所以再加一道惰性保证：任何一次真正的使用都会先补上初始化
        /// </summary>
        private void EnsureInitialized()
        {
            if (m_isInitialized)
            {
                return;
            }

            m_isInitialized = true;

            m_slots[0] = m_slot1Item;
            m_slots[1] = m_slot2Item;

            ApplySlotsToWeaponSystem();
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

        /// <summary>某个槽位装的是哪件物品（越界或空槽返回 null）</summary>
        public ItemDataSO GetSlotItem(int slotIndex)
        {
            EnsureInitialized();
            return (slotIndex >= 0 && slotIndex < m_slots.Length) ? m_slots[slotIndex] : null;
        }

        /// <summary>这件物品是否已经装在某个槽位上</summary>
        public bool IsEquipped(string itemId)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            for (int i = 0; i < m_slots.Length; i++)
            {
                if (m_slots[i] != null && m_slots[i].Id == itemId)
                {
                    return true;
                }
            }

            return false;
        }

        // ====================================================================
        // 装备
        // ====================================================================

        /// <summary>
        /// 装备一件武器物品。
        /// 失败原因通过 failReason 返回（UI 拿去提示玩家），失败时不改变任何状态
        /// </summary>
        /// <param name="itemId">物品 ID</param>
        /// <param name="failReason">失败原因（成功时为 null）</param>
        public bool TryEquipItem(string itemId, out string failReason)
        {
            failReason = null;
            EnsureInitialized();

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                failReason = "场景中没有背包系统";
                return false;
            }

            if (inventory.GetCount(itemId) <= 0)
            {
                failReason = "背包里没有这件物品";
                return false;
            }

            ItemDataSO data = inventory.FindItem(itemId);
            if (data == null)
            {
                failReason = $"物品目录里找不到 {itemId}";
                return false;
            }

            if (!data.IsWeapon)
            {
                failReason = $"{data.DisplayName}不是武器";
                return false;
            }

            if (IsEquipped(itemId))
            {
                failReason = $"{data.DisplayName}已经装备在身上";
                return false;
            }

            if (m_weaponSystem == null)
            {
                failReason = "找不到武器系统";
                return false;
            }

            // 有空槽就先用空槽；两个都满了就替换主手（旧武器回到背包里，可以再装回来）
            int slot = FindFreeSlot();
            if (slot < 0)
            {
                slot = 0;
            }

            m_slots[slot] = data;
            ApplySlot(slot);

            Debug.Log($"[{GetType().Name}] 装备「{data.DisplayName}」到槽位 {slot + 1}", this);

            EquipmentChanged?.Invoke();
            return true;
        }

        // ====================================================================
        // 存档
        // ====================================================================

        /// <summary>导出装备状态（槽位 + 当前手持，都只存物品 ID）</summary>
        public EquipmentSaveData CaptureSaveData()
        {
            EnsureInitialized();

            EquipmentSaveData data = new EquipmentSaveData();

            for (int i = 0; i < m_slots.Length; i++)
            {
                data.slotItemIds.Add(m_slots[i] != null ? m_slots[i].Id : string.Empty);
            }

            data.heldItemId = GetHeldItemId();
            return data;
        }

        /// <summary>
        /// 用存档覆盖装备（覆盖而非合并：读档语义就是"回到存档那一刻"）。
        /// 槽位先还原、再还原"手上是哪把"，否则还原过程中兜底切换会把结果带偏
        /// </summary>
        public void ApplySaveData(EquipmentSaveData data)
        {
            EnsureInitialized();

            InventoryManager inventory = InventoryManager.Instance;
            List<string> slotIds = data != null ? data.slotItemIds : null;

            for (int i = 0; i < m_slots.Length; i++)
            {
                string id = (slotIds != null && i < slotIds.Count) ? slotIds[i] : string.Empty;
                m_slots[i] = ResolveSlotItem(inventory, id);
            }

            ApplySlotsToWeaponSystem();

            if (data != null)
            {
                ApplyHeldItem(data.heldItemId);
            }

            EquipmentChanged?.Invoke();
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>当前手上那把对应的物品 ID（手持的不是物品武器、或空手时返回空字符串）</summary>
        private string GetHeldItemId()
        {
            if (m_weaponSystem == null)
            {
                return string.Empty;
            }

            ItemDataSO held = GetSlotItem((int)m_weaponSystem.CurrentWeaponIndex);
            return held != null ? held.Id : string.Empty;
        }

        /// <summary>
        /// 还原"手上是哪把"：按物品 ID 找到它在哪个槽位，再让武器系统切过去。
        /// 找不到（那把已经不在槽位里）就什么都不做，交给武器系统自己兜底
        /// </summary>
        private void ApplyHeldItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || m_weaponSystem == null)
            {
                return;
            }

            for (int i = 0; i < m_slots.Length; i++)
            {
                if (m_slots[i] != null && m_slots[i].Id == itemId)
                {
                    m_weaponSystem.ForceSelectSlot(i);
                    return;
                }
            }
        }

        /// <summary>把一件存档里的 ID 还原成可装备的物品（不是武器、或已不在背包里都算空槽）</summary>
        private ItemDataSO ResolveSlotItem(InventoryManager inventory, string itemId)
        {
            if (inventory == null || string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            ItemDataSO data = inventory.FindItem(itemId);

            if (data == null || !data.IsWeapon)
            {
                Debug.LogWarning($"[{GetType().Name}] 读档跳过装备 {itemId}：物品目录里没有它或它不是武器", this);
                return null;
            }

            if (!inventory.Contains(itemId))
            {
                // 存档里装了、背包里却没有，说明这份存档不自洽（改过配置或被改过档）
                Debug.LogWarning($"[{GetType().Name}] 读档跳过装备 {itemId}：背包里没有这件物品", this);
                return null;
            }

            return data;
        }

        /// <summary>找第一个空槽（都满了返回 -1）</summary>
        private int FindFreeSlot()
        {
            for (int i = 0; i < m_slots.Length; i++)
            {
                if (m_slots[i] == null)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>把所有槽位同步到武器系统</summary>
        private void ApplySlotsToWeaponSystem()
        {
            for (int i = 0; i < m_slots.Length; i++)
            {
                ApplySlot(i);
            }
        }

        /// <summary>把单个槽位同步到武器系统</summary>
        private void ApplySlot(int slotIndex)
        {
            if (m_weaponSystem == null)
            {
                return;
            }

            ItemDataSO item = m_slots[slotIndex];
            m_weaponSystem.SetSlotWeapon(slotIndex, item != null ? item.Weapon : null);
        }
    }
}
