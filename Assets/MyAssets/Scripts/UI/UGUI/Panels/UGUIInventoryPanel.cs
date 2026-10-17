using System.Collections.Generic;
using MyGame.Audio;
using MyGame.Equipment;
using MyGame.Inventory;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 背包面板（UGUI 版）：左侧物品网格 + 右侧详情侧栏
    ///
    /// 绑定方式说明（本面板是"两种方式混用"的典型）：
    /// - 「使用」「关闭」按钮是固定的 → Inspector 拖曳绑定
    /// - 物品格子是**运行时按数据生成的** → 只能用代码绑定
    ///   （格子数量取决于背包里有多少物品，Unity 编辑器里根本不存在这些物体，没法拖）
    ///
    /// 数据来源：
    /// 订阅 InventoryManager.ItemsChanged，把「ItemDataSO + 数量」翻译成展示用的 InventoryItemData。
    /// 面板不认识背包的内部字典，背包也不认识 UI——两边只通过一个事件和一个只读导出方法通信
    /// </summary>
    public class UGUIInventoryPanel : UGUIPanel
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("网格")]
        [Tooltip("格子容器（建议挂 GridLayoutGroup 自动排列）")]
        [SerializeField] private RectTransform m_gridContainer;

        [Tooltip("格子 prefab（根节点需有 Button + Image，子物体含 Icon 与 Count 两个文本）")]
        [SerializeField] private GameObject m_slotPrefab;

        [Tooltip("格子背景常态颜色")]
        [SerializeField] private Color m_slotNormalColor = new Color(0.18f, 0.20f, 0.24f, 1f);

        [Tooltip("格子选中时的高亮颜色")]
        [SerializeField] private Color m_slotSelectedColor = new Color(0.28f, 0.35f, 0.45f, 1f);

        [Header("详情侧栏")]
        [Tooltip("物品名称文本")]
        [SerializeField] private TextMeshProUGUI m_itemNameText;

        [Tooltip("物品描述文本")]
        [SerializeField] private TextMeshProUGUI m_itemDescText;

        [Tooltip("详情大图（显示武器立绘；物品没配大图时自动隐藏）")]
        [SerializeField] private Image m_itemDetailImage;

        [Tooltip("属性行容器（建议挂 VerticalLayoutGroup）")]
        [SerializeField] private RectTransform m_statsContainer;

        [Tooltip("单条属性文本 prefab（一个 TextMeshProUGUI 即可）")]
        [SerializeField] private GameObject m_statRowPrefab;

        [Tooltip("「使用 / 装备」按钮（未选中或该物品没有效果时会被禁用）")]
        [SerializeField] private Button m_useButton;

        [Tooltip("按钮上的文字（武器显示「装备」，其他显示「使用」）")]
        [SerializeField] private TextMeshProUGUI m_useButtonLabel;

        // ====================================================================
        // 文案常量
        // ====================================================================

        private const string k_noSelectionName = "未选择物品";
        private const string k_noSelectionDesc = "从左侧选择一件物品查看详情";
        private const string k_useLabel = "使用";
        private const string k_equipLabel = "装备";

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前展示的物品列表</summary>
        private readonly List<InventoryItemData> m_items = new();

        /// <summary>与 m_items 一一对应的格子按钮（用于切换高亮）</summary>
        private readonly List<Button> m_slotButtons = new();

        /// <summary>格子里的图标文本（仅在物品没配 Icon 时作为文字占位显示）</summary>
        private readonly List<TextMeshProUGUI> m_slotIcons = new();

        /// <summary>属性行实例（重建详情时销毁重建）</summary>
        private readonly List<GameObject> m_statRows = new();

        /// <summary>当前选中格子的索引（-1 表示未选中）</summary>
        private int m_selectedIndex = -1;

        /// <summary>
        /// 最近一次使用失败的提示（显示在描述区顶部）。
        /// 换选中或背包内容变化时清空——因为提示只对"刚刚那次点击"有意义
        /// </summary>
        private string m_useHint = string.Empty;

        /// <summary>从背包取数据的临时容器（复用，避免每次刷新都产生垃圾）</summary>
        private readonly List<ItemDataSO> m_ownedItems = new();

        /// <summary>与 m_ownedItems 一一对应的持有数量</summary>
        private readonly List<int> m_ownedCounts = new();

        // ====================================================================
        // 对外只读访问 / 事件
        // ====================================================================

        /// <summary>当前选中的物品（未选中时为 null）</summary>
        public InventoryItemData SelectedItem
        {
            get
            {
                bool isValid = m_selectedIndex >= 0 && m_selectedIndex < m_items.Count;
                return isValid ? m_items[m_selectedIndex] : null;
            }
        }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.Inventory;
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：订阅背包与装备变化 → 拉取数据 → 重建格子</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                // 面板是常驻物体、只切换显隐，所以订阅放在 OnEnter/OnExit，与显隐严格对称
                inventory.ItemsChanged += HandleItemsChanged;
            }

            // 装备状态一变（换装、读档），武器那条的「已装备」标记要跟着变
            EquipmentSystem equipment = EquipmentSystem.Instance;
            if (equipment != null)
            {
                equipment.EquipmentChanged += HandleEquipmentChanged;
            }

            RefreshFromInventory();
        }

        /// <summary>面板退出显示：退订并清理属性行实例</summary>
        public override void OnExit()
        {
            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                inventory.ItemsChanged -= HandleItemsChanged;
            }

            EquipmentSystem equipment = EquipmentSystem.Instance;
            if (equipment != null)
            {
                equipment.EquipmentChanged -= HandleEquipmentChanged;
            }

            ClearStatRows();
            base.OnExit();
        }

        // ====================================================================
        // 数据来源
        // ====================================================================

        /// <summary>背包变化（可能发生在面板打开期间）：重新拉一遍数据</summary>
        private void HandleItemsChanged()
        {
            RefreshFromInventory();
        }

        /// <summary>装备变化：物品本身没变，只要刷新右侧详情的状态标记</summary>
        private void HandleEquipmentChanged()
        {
            RefreshDetail();
        }

        /// <summary>把背包内容翻译成展示数据，并刷新网格与侧栏</summary>
        private void RefreshFromInventory()
        {
            // 记住刷新前选中的是谁：用掉一件物品会触发刷新重建列表，
            // 不记住的话每喝一瓶药水都要重新点一遍格子
            InventoryItemData previous = SelectedItem;
            string previousId = previous != null ? previous.Id : string.Empty;

            m_items.Clear();

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                inventory.CopyItemsTo(m_ownedItems, m_ownedCounts);

                for (int i = 0; i < m_ownedItems.Count; i++)
                {
                    ItemDataSO item = m_ownedItems[i];
                    m_items.Add(new InventoryItemData(
                        item.Id,
                        item.DisplayName,
                        item.Description,
                        m_ownedCounts[i],
                        item.CanUse,
                        item.IsWeapon,
                        BuildStatLines(item))
                    {
                        // 图标/大图走对象初始化器（见 InventoryItemData.Icon 的注释）
                        Icon = item.Icon,
                        DetailImage = item.DetailImage,
                    });
                }
            }

            RebuildSlots();
            SelectItem(IndexOfItem(previousId));
        }

        /// <summary>按物品 ID 找回它在当前列表里的下标（找不到返回 -1）</summary>
        private int IndexOfItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return -1;
            }

            for (int i = 0; i < m_items.Count; i++)
            {
                if (m_items[i].Id == itemId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>按物品配置生成详情栏的属性行</summary>
        private static string[] BuildStatLines(ItemDataSO item)
        {
            List<string> lines = new List<string>(3);

            // 先写"这东西是什么/能干什么"，再写堆叠规则——玩家最关心的是前者
            if (item.IsWeapon)
            {
                lines.Add($"武器：{item.Weapon.WeaponName}");
            }

            switch (item.UseType)
            {
                case EItemUseType.Heal:
                    lines.Add($"使用：回复 {item.UseAmount:0.#} 点生命");
                    break;
            }

            lines.Add(item.MaxStack > 0 ? $"堆叠上限 {item.MaxStack}" : "可无限堆叠");

            return lines.ToArray();
        }

        // ====================================================================
        // 网格生成
        // ====================================================================

        /// <summary>
        /// 按 m_items 重建网格
        /// 每次重建都会销毁旧格子，所以格子上的按钮回调随物体一起释放，不会累积
        /// </summary>
        private void RebuildSlots()
        {
            if (m_gridContainer == null || m_slotPrefab == null)
            {
                return;
            }

            // 销毁旧格子
            int i = 0;
            while (i < m_slotButtons.Count)
            {
                if (m_slotButtons[i] != null)
                {
                    Destroy(m_slotButtons[i].gameObject);
                }
                i = i + 1;
            }

            m_slotButtons.Clear();
            m_slotIcons.Clear();

            // 生成新格子
            for (int index = 0; index < m_items.Count; index++)
            {
                CreateSlot(index);
            }
        }

        /// <summary>创建单个物品格子</summary>
        private void CreateSlot(int index)
        {
            InventoryItemData item = m_items[index];

            GameObject go = Instantiate(m_slotPrefab, m_gridContainer);
            Button button = go.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 格子 prefab 上没有 Button 组件，格子无法点击", go);
                return;
            }

            // 图标：优先显示美术图标；物品没配图标时才退回文字占位
            Image iconImage = null;
            Transform iconImgTf = go.transform.Find("IconImage");
            if (iconImgTf != null)
            {
                iconImage = iconImgTf.GetComponent<Image>();
                if (iconImage != null)
                {
                    iconImage.sprite = item.Icon;
                    iconImage.enabled = item.Icon != null;
                }
            }

            // 文字占位（无图标时显示物品名，有图标时清空避免压在图上）
            TextMeshProUGUI iconText = null;
            Transform iconTf = go.transform.Find("Icon");
            if (iconTf != null)
            {
                iconText = iconTf.GetComponent<TextMeshProUGUI>();
                if (iconText != null)
                {
                    iconText.text = item.Icon == null ? item.Name : string.Empty;
                }
            }

            // 数量角标：数量 > 1 才显示
            Transform countTf = go.transform.Find("Count");
            if (countTf != null)
            {
                TextMeshProUGUI countText = countTf.GetComponent<TextMeshProUGUI>();
                if (countText != null)
                {
                    bool showCount = item.Count > 1;
                    countText.gameObject.SetActive(showCount);

                    // 数量为 1 时清空文本：prefab 里预置的占位数字虽然看不见，
                    // 但留着它，下次显示若漏了赋值就会露出旧数字
                    countText.text = showCount ? item.Count.ToString() : string.Empty;
                }
            }

            // 代码绑定：格子是运行时生成的，无法在 Inspector 里拖曳
            // 捕获的 index 是循环变量副本，且格子重建时会被销毁，不会造成泄漏
            int capturedIndex = index;
            button.onClick.AddListener(() => HandleSlotClicked(capturedIndex));

            m_slotButtons.Add(button);
            m_slotIcons.Add(iconText);
        }

        // ====================================================================
        // 选中与详情
        // ====================================================================

        /// <summary>选中指定索引的物品（-1 表示取消选中）</summary>
        private void SelectItem(int index)
        {
            m_selectedIndex = index;

            // 换选中就清掉上一次的使用提示：那句话只对刚刚那次点击有意义
            m_useHint = string.Empty;

            // 刷新格子高亮与数量角标颜色
            for (int i = 0; i < m_slotButtons.Count; i++)
            {
                if (m_slotButtons[i] == null)
                {
                    continue;
                }

                Image img = m_slotButtons[i].targetGraphic as Image;
                if (img != null)
                {
                    img.color = (i == index) ? m_slotSelectedColor : m_slotNormalColor;
                }
            }

            RefreshDetail();
        }

        /// <summary>刷新右侧详情</summary>
        private void RefreshDetail()
        {
            InventoryItemData item = SelectedItem;

            // 未选中：显示占位提示并禁用主操作按钮
            if (item == null)
            {
                if (m_itemNameText != null) { m_itemNameText.text = k_noSelectionName; }
                if (m_itemDescText != null) { m_itemDescText.text = k_noSelectionDesc; }

                // 没选中就不显示大图（不然会留着上一个物品的立绘）
                if (m_itemDetailImage != null) { m_itemDetailImage.enabled = false; }

                ClearStatRows();
                SetUseButtonEnabled(false);
                RefreshActionLabel(null);
                return;
            }

            if (m_itemNameText != null)
            {
                m_itemNameText.text = $"{item.Name}  x{item.Count}";
            }

            // 详情大图：只有配了立绘的物品（一般是武器）才显示
            if (m_itemDetailImage != null)
            {
                m_itemDetailImage.sprite = item.DetailImage;
                m_itemDetailImage.enabled = item.DetailImage != null;
            }

            if (m_itemDescText != null)
            {
                // 提示行只对"刚刚那次点击"有意义，颜色标红让它和正文描述区分开
                string hintLine = string.IsNullOrEmpty(m_useHint)
                    ? string.Empty
                    : $"<color=#FF6B6B>{m_useHint}</color>\n";

                m_itemDescText.text = hintLine + item.Description + BuildEquipMark(item);
            }

            // 重建属性行
            ClearStatRows();
            if (m_statsContainer != null && m_statRowPrefab != null)
            {
                foreach (string stat in item.Stats)
                {
                    GameObject row = Instantiate(m_statRowPrefab, m_statsContainer);
                    TextMeshProUGUI tmp = row.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        tmp.text = stat;
                    }
                    m_statRows.Add(row);
                }
            }

            // 没有效果的东西（金币、任务材料）置灰；武器则永远可以装备
            SetUseButtonEnabled(item.CanUse || item.IsWeapon);
            RefreshActionLabel(item);
        }

        /// <summary>武器且已装在身上时，在描述下面补一行状态</summary>
        private static string BuildEquipMark(InventoryItemData item)
        {
            if (!item.IsWeapon)
            {
                return string.Empty;
            }

            EquipmentSystem equipment = EquipmentSystem.Instance;
            return (equipment != null && equipment.IsEquipped(item.Id))
                ? "\n<color=#7FD67F>已装备</color>"
                : string.Empty;
        }

        /// <summary>按钮文字随物品类型切换：武器是「装备」，其他是「使用」</summary>
        private void RefreshActionLabel(InventoryItemData item)
        {
            if (m_useButtonLabel == null)
            {
                return;
            }

            bool isWeapon = item != null && item.IsWeapon;
            m_useButtonLabel.text = isWeapon ? k_equipLabel : k_useLabel;
        }

        /// <summary>清空属性行实例</summary>
        private void ClearStatRows()
        {
            int i = 0;
            while (i < m_statRows.Count)
            {
                if (m_statRows[i] != null)
                {
                    Destroy(m_statRows[i]);
                }
                i = i + 1;
            }

            m_statRows.Clear();
        }

        /// <summary>设置「使用」按钮的可用状态</summary>
        private void SetUseButtonEnabled(bool enabled)
        {
            if (m_useButton != null)
            {
                m_useButton.interactable = enabled;
            }
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>格子点击（代码绑定）：选中该格子</summary>
        private void HandleSlotClicked(int index)
        {
            SelectItem(index);
            AudioManager.Play(ESfx.Inv_Select, 0.7f);
        }

        /// <summary>「使用 / 装备」按钮回调（Inspector 拖曳绑定）：按物品类型分派给对应系统</summary>
        public void HandleUseClicked()
        {
            InventoryItemData item = SelectedItem;
            if (item == null)
            {
                return;
            }

            // 面板只转发点击：能不能用、用了会怎样，全由系统层决定
            string failReason;
            bool success = item.IsWeapon
                ? TryEquip(item.Id, out failReason)
                : TryUse(item.Id, out failReason);

            if (success)
            {
                // 成功音按物品类型分派：装备武器与喝药水是两种不同的手感
                AudioManager.Play(item.IsWeapon ? ESfx.Inv_Equip : ESfx.Inv_UsePotion);
                // 成功路径不需要额外处理：扣数量会触发 ItemsChanged，面板自动重拉数据
                return;
            }

            // 失败音：用"不可用"音，和点击音区分开
            AudioManager.Play(ESfx.UI_Disabled, 0.8f);

            // 失败原因写在描述区（玩家在游戏里看不到 Console），并保留选中
            m_useHint = failReason;
            RefreshDetail();
        }

        /// <summary>装备（武器走这条）</summary>
        private static bool TryEquip(string itemId, out string failReason)
        {
            EquipmentSystem equipment = EquipmentSystem.Instance;

            if (equipment == null)
            {
                failReason = "场景中没有装备系统";
                return false;
            }

            return equipment.TryEquipItem(itemId, out failReason);
        }

        /// <summary>使用（消耗品走这条）</summary>
        private static bool TryUse(string itemId, out string failReason)
        {
            ItemUseSystem useSystem = ItemUseSystem.Instance;

            if (useSystem == null)
            {
                failReason = "场景中没有物品使用系统";
                return false;
            }

            return useSystem.TryUse(itemId, out failReason);
        }

        /// <summary>「关闭」按钮回调（Inspector 拖曳绑定）</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.Inventory);
        }
    }
}
