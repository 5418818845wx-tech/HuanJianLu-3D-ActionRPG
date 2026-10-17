using System.Collections.Generic;
using MyGame.Inventory;
using MyGame.Shop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 商店面板（UGUI 版）：左侧条目列表 + 右侧详情 + 底部操作按钮
    ///
    /// 两种模式共用同一套列表与详情，靠页签切换：
    /// - 购买：列表来自本店的**进货表**，价格是买价
    /// - 出售：列表来自**玩家背包**里这家店肯收的东西，价格是收价
    ///
    /// 交互流程：
    /// 1. 剧本里写 &lt;&lt;open_shop "Shop_General"&gt;&gt; → ShopYarnBridge 调 OpenShop(shopId)
    /// 2. OpenShop：记住店 ID → 按「购买」模式建列表 → 读当前金币 → 显示面板
    /// 3. 点击页签：切换模式 → 重建列表 → 回到未选中
    /// 4. 点击条目：高亮 + 右侧刷新详情；买不起的条目会压暗
    /// 5. 点击底部按钮：交给 ShopSystem 做校验与钱货交换 → 刷新金币与可售状态
    /// 6. 点击「关闭」：关闭面板
    ///
    /// 绑定方式：
    /// - 条目是**运行时按数据生成的** → 只能代码绑定
    /// - 页签、操作、关闭按钮是固定的 → Inspector 拖曳绑定
    ///
    /// 职责边界：面板**不做任何经济规则判断**——"钱够不够、扣多少、给什么"全在 ShopSystem 里。
    /// 面板只负责显示，以及把玩家的点击转成一次系统调用
    /// </summary>
    public class UGUIShopPanel : UGUIPanel
    {
        /// <summary>面板的两种模式（纯显示状态，只在本面板内部使用）</summary>
        private enum EShopMode
        {
            /// <summary>购买：列表是店里的货</summary>
            Buy,

            /// <summary>出售：列表是玩家背包里这家店肯收的东西</summary>
            Sell,
        }

        // ====================================================================
        // 文案常量
        // ====================================================================

        private const string k_noSelectionName = "未选择商品";
        private const string k_noSelectionDesc = "从左侧选择一件商品查看详情";
        private const string k_noSellSelectionName = "未选择物品";
        private const string k_noSellSelectionDesc = "从左侧选择要卖的东西";
        private const string k_nothingToSellDesc = "背包里没有这家店要收的东西";
        private const string k_buyLabel = "购买";
        private const string k_sellLabel = "出售";
        private const string k_buyPricePrefix = "售价：";
        private const string k_sellPricePrefix = "收购价：";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("页签")]
        [Tooltip("「购买」页签按钮")]
        [SerializeField] private Button m_buyTabButton;

        [Tooltip("「出售」页签按钮")]
        [SerializeField] private Button m_sellTabButton;

        [Tooltip("页签常态背景色")]
        [SerializeField] private Color m_tabNormalColor = new Color(0.14f, 0.15f, 0.19f, 1f);

        [Tooltip("页签选中背景色")]
        [SerializeField] private Color m_tabSelectedColor = new Color(0.28f, 0.35f, 0.45f, 1f);

        [Header("商品列表")]
        [Tooltip("条目容器（建议挂 VerticalLayoutGroup）")]
        [SerializeField] private RectTransform m_listContainer;

        [Tooltip("商品条目 prefab（根节点需有 Button + Image，子物体为 Name 与 Price 两个文本）")]
        [SerializeField] private GameObject m_entryPrefab;

        [Tooltip("条目常态背景色")]
        [SerializeField] private Color m_entryNormalColor = new Color(0.18f, 0.20f, 0.24f, 1f);

        [Tooltip("条目选中时的高亮背景色")]
        [SerializeField] private Color m_entrySelectedColor = new Color(0.28f, 0.35f, 0.45f, 1f);

        [Tooltip("买不起的条目背景色（压暗提示不可购买）")]
        [SerializeField] private Color m_entryUnaffordableColor = new Color(0.12f, 0.13f, 0.15f, 1f);

        [Tooltip("价格文本常态颜色（默认金黄）")]
        [SerializeField] private Color m_priceNormalColor = new Color(1f, 0.769f, 0f, 1f);

        [Tooltip("买不起时的价格文本颜色（默认红色）")]
        [SerializeField] private Color m_priceUnaffordableColor = new Color(0.886f, 0.267f, 0.267f, 1f);

        [Header("金币")]
        [Tooltip("金币文本，如「金币  1280」")]
        [SerializeField] private TextMeshProUGUI m_goldText;

        [Header("详情侧栏")]
        [Tooltip("商品名称文本")]
        [SerializeField] private TextMeshProUGUI m_itemNameText;

        [Tooltip("商品描述文本")]
        [SerializeField] private TextMeshProUGUI m_itemDescText;

        [Tooltip("属性行容器（建议挂 VerticalLayoutGroup）")]
        [SerializeField] private RectTransform m_statsContainer;

        [Tooltip("单行文本 prefab（这里复用 StatRow.prefab）")]
        [SerializeField] private GameObject m_statRowPrefab;

        [Tooltip("售价文本")]
        [SerializeField] private TextMeshProUGUI m_itemPriceText;

        [Tooltip("「购买 / 出售」按钮（未选中时会被禁用，文字随模式切换）")]
        [SerializeField] private Button m_actionButton;

        [Tooltip("操作按钮上的文字（模式切换时改它的 text）")]
        [SerializeField] private TextMeshProUGUI m_actionLabel;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前展示的条目列表（购买=店内货，出售=背包里可卖的）</summary>
        private readonly List<ShopItemData> m_items = new();

        /// <summary>与 m_items 一一对应的条目按钮（用于切换高亮与可购状态）</summary>
        private readonly List<Button> m_entryButtons = new();

        /// <summary>与 m_items 一一对应的条目价格文本（用于切换"买不起"配色）</summary>
        private readonly List<TextMeshProUGUI> m_entryPrices = new();

        /// <summary>属性行实例（重建详情时销毁重建）</summary>
        private readonly List<GameObject> m_statRows = new();

        /// <summary>从背包取数据的临时容器（出售模式用，复用避免每次刷新产生垃圾）</summary>
        private readonly List<ItemDataSO> m_ownedItems = new();

        /// <summary>与 m_ownedItems 一一对应的持有数量</summary>
        private readonly List<int> m_ownedCounts = new();

        /// <summary>当前选中条目索引（-1 表示未选中）</summary>
        private int m_selectedIndex = -1;

        /// <summary>当前这家店的 ID（OpenShop 写入，交易时回传给 ShopSystem）</summary>
        private string m_shopId = string.Empty;

        /// <summary>当前模式：购买 / 出售</summary>
        private EShopMode m_mode = EShopMode.Buy;

        /// <summary>玩家当前金币（每次刷新都从商店系统读，面板不自己记账）</summary>
        private int m_gold;

        // ====================================================================
        // 对外只读访问 / 事件
        // ====================================================================

        /// <summary>玩家当前金币</summary>
        public int Gold => m_gold;

        /// <summary>当前是否处于出售模式</summary>
        public bool IsSelling => m_mode == EShopMode.Sell;

        /// <summary>当前选中的条目（未选中时为 null）</summary>
        public ShopItemData SelectedItem
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
            m_panelID = EUIPanelID.Shop;
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：订阅钱包变化 → 读金币 → 重建列表并回到未选中状态</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 商店开着的时候钱包也可能变（比如任务发奖），所以订阅背包事件而不是只在开店时读一次
            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                inventory.ItemsChanged += HandleWalletChanged;
            }

            RefreshGoldFromSystem();
            RebuildData();
            RebuildList();

            // 每次打开都回到「未选中」状态，避免残留上次的高亮
            SelectItem(-1);
        }

        /// <summary>面板退出显示：退订并清理动态生成的实例</summary>
        public override void OnExit()
        {
            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                inventory.ItemsChanged -= HandleWalletChanged;
            }

            ClearStatRows();
            base.OnExit();
        }

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>
        /// 打开某家商店：记住店 ID → 按「购买」模式建数据 → 读当前金币 → 显示面板。
        /// 由 ShopYarnBridge 在剧本执行 &lt;&lt;open_shop&gt;&gt; 时调用。
        /// 每次开店都从购买模式开始（玩家找商人通常是来买东西的）
        /// </summary>
        public void OpenShop(string shopId)
        {
            ShopSystem shopSystem = ShopSystem.Instance;
            ShopStockDataSO shop = shopSystem != null ? shopSystem.FindShop(shopId) : null;

            if (shop == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 找不到商店「{shopId}」，面板未打开", this);
                return;
            }

            m_shopId = shopId;
            m_mode = EShopMode.Buy;
            RebuildData();
            RefreshGoldFromSystem();
            RefreshModeVisuals();

            if (m_manager != null)
            {
                m_manager.ShowPanel(EUIPanelID.Shop);
            }
        }

        // ====================================================================
        // 数据来源
        // ====================================================================

        /// <summary>按当前模式重建条目数据</summary>
        private void RebuildData()
        {
            m_items.Clear();

            if (m_mode == EShopMode.Buy)
            {
                BuildBuyItems();
            }
            else
            {
                BuildSellItems();
            }
        }

        /// <summary>购买模式：把进货表翻译成展示用的商品数据</summary>
        private void BuildBuyItems()
        {
            ShopStockDataSO shop = FindCurrentShop();
            if (shop == null)
            {
                return;
            }

            IReadOnlyList<ShopStockDataSO.Entry> entries = shop.Entries;
            if (entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                ShopStockDataSO.Entry entry = entries[i];
                ItemDataSO item = entry != null ? entry.Item : null;

                if (item == null)
                {
                    continue;
                }

                m_items.Add(new ShopItemData(
                    item.Id,
                    item.DisplayName,
                    item.Description,
                    entry.BuyPrice,
                    BuildStackStatLines(item)));
            }
        }

        /// <summary>
        /// 出售模式：把"背包里这家店肯收的东西"翻译成展示数据。
        /// 这里是 **面板过滤显示**（不收的东西不列出来），不是经济规则——
        /// 真正允许不允许卖仍由 ShopSystem 判定
        /// </summary>
        private void BuildSellItems()
        {
            ShopStockDataSO shop = FindCurrentShop();
            InventoryManager inventory = InventoryManager.Instance;

            if (shop == null || inventory == null)
            {
                return;
            }

            string goldItemId = ShopSystem.Instance != null ? ShopSystem.Instance.GoldItemId : string.Empty;

            inventory.CopyItemsTo(m_ownedItems, m_ownedCounts);

            for (int i = 0; i < m_ownedItems.Count; i++)
            {
                ItemDataSO item = m_ownedItems[i];
                if (item == null || item.Id == goldItemId)
                {
                    // 金币不是商品，不列进出售清单
                    continue;
                }

                int sellPrice = shop.FindSellPrice(item.Id);
                if (sellPrice <= 0)
                {
                    // -1 = 这家店不认这件货；0 = 认但不收。对玩家来说都是"卖不了"
                    continue;
                }

                m_items.Add(new ShopItemData(
                    item.Id,
                    item.DisplayName,
                    item.Description,
                    sellPrice,
                    BuildSellStatLines(item, m_ownedCounts[i])));
            }
        }

        /// <summary>按 ID 找当前这家店的进货表</summary>
        private ShopStockDataSO FindCurrentShop()
        {
            ShopSystem shopSystem = ShopSystem.Instance;
            return shopSystem != null ? shopSystem.FindShop(m_shopId) : null;
        }

        /// <summary>购买模式的属性行：只看堆叠规则</summary>
        private static string[] BuildStackStatLines(ItemDataSO item)
        {
            return new string[] { item.MaxStack > 0 ? $"堆叠上限 {item.MaxStack}" : "可无限堆叠" };
        }

        /// <summary>出售模式的属性行：先写"你有几个"，再写堆叠规则</summary>
        private static string[] BuildSellStatLines(ItemDataSO item, int ownedCount)
        {
            return new string[]
            {
                $"持有 ×{ownedCount}",
                item.MaxStack > 0 ? $"堆叠上限 {item.MaxStack}" : "可无限堆叠",
            };
        }

        /// <summary>从商店系统读取当前金币（面板不自己记账，避免两边不一致）</summary>
        private void RefreshGoldFromSystem()
        {
            ShopSystem shopSystem = ShopSystem.Instance;
            m_gold = shopSystem != null ? shopSystem.GetGold() : 0;
        }

        /// <summary>钱包变化：刷新金币显示，并按模式决定刷新粒度</summary>
        private void HandleWalletChanged()
        {
            RefreshGoldFromSystem();

            if (!IsVisible)
            {
                return;
            }

            if (m_mode == EShopMode.Sell)
            {
                // 出售模式下背包本身变了（数量减少甚至物品消失），必须整体重建列表。
                // 重建后按 ID 找回原来选中的东西，连卖几件不用重点
                RebuildListPreservingSelection();
                return;
            }

            // 购买模式下货架没变，只需要刷新"买得起/买不起"
            RefreshEntryColors();
            RefreshDetail();
        }

        // ====================================================================
        // 列表生成
        // ====================================================================

        /// <summary>
        /// 重建列表并尽量保住原来选中的那条（找不回来就回到未选中）。
        /// 出售模式下**数据本身就是从背包算出来的**，所以必须先 RebuildData 再重建列表——
        /// 漏掉前一步的话，卖光了列表里还会留着"持有 ×N"的旧数字
        /// </summary>
        private void RebuildListPreservingSelection()
        {
            ShopItemData previous = SelectedItem;
            string previousId = previous != null ? previous.Id : string.Empty;

            RebuildData();
            RebuildList();
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

        /// <summary>
        /// 按 m_items 重建条目列表
        /// 每次重建都会销毁旧条目，所以条目上的按钮回调随物体一起释放，不会累积
        /// </summary>
        private void RebuildList()
        {
            ClearEntries();

            if (m_listContainer != null && m_entryPrefab != null)
            {
                for (int index = 0; index < m_items.Count; index++)
                {
                    CreateEntry(index);
                }
            }

            RefreshEntryColors();
        }

        /// <summary>创建单条商品条目：商品名 + 价格</summary>
        private void CreateEntry(int index)
        {
            ShopItemData item = m_items[index];

            GameObject entry = Instantiate(m_entryPrefab, m_listContainer);

            Image background = entry.GetComponent<Image>();
            if (background != null)
            {
                background.color = m_entryNormalColor;
            }

            TextMeshProUGUI name = FindChildText(entry.transform, "Name");
            if (name != null)
            {
                name.text = item.Name;
            }

            TextMeshProUGUI price = FindChildText(entry.transform, "Price");
            if (price != null)
            {
                price.text = item.Price.ToString();
            }

            // 代码绑定：条目是运行时生成的，无法在 Inspector 里拖曳
            // 捕获的 index 是循环变量副本，且条目重建时会被销毁，不会造成泄漏
            Button button = entry.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 商品条目 prefab 上没有 Button 组件，条目无法点击", entry);
            }
            else
            {
                int capturedIndex = index;
                button.onClick.AddListener(() => HandleEntryClicked(capturedIndex));
            }

            m_entryButtons.Add(button);
            m_entryPrices.Add(price);
        }

        /// <summary>销毁已生成的条目并清空引用列表</summary>
        private void ClearEntries()
        {
            int i = 0;
            while (i < m_entryButtons.Count)
            {
                if (m_entryButtons[i] != null)
                {
                    Destroy(m_entryButtons[i].gameObject);
                }
                i = i + 1;
            }

            m_entryButtons.Clear();
            m_entryPrices.Clear();
        }

        // ====================================================================
        // 选中与详情
        // ====================================================================

        /// <summary>选中指定索引的商品（-1 表示取消选中）</summary>
        private void SelectItem(int index)
        {
            m_selectedIndex = index;

            RefreshEntryColors();
            RefreshDetail();
        }

        /// <summary>刷新金币文本</summary>
        private void RefreshGold()
        {
            if (m_goldText != null)
            {
                m_goldText.text = $"金币  {m_gold}";
            }
        }

        /// <summary>
        /// 切换购买 / 出售模式：换模式等于换了整份列表，所以数据、条目、选中全部重来。
        /// 重复点同一个页签直接忽略，避免无谓地销毁重建一遍
        /// </summary>
        private void SetMode(EShopMode mode)
        {
            if (m_mode == mode)
            {
                return;
            }

            m_mode = mode;
            RefreshModeVisuals();
            RebuildData();
            RebuildList();

            // 列表整份换了，旧索引毫无意义，直接回到未选中
            SelectItem(-1);
        }

        /// <summary>刷新页签高亮与操作按钮文字（切模式或开店时调用）</summary>
        private void RefreshModeVisuals()
        {
            SetTabSelected(m_buyTabButton, m_mode == EShopMode.Buy);
            SetTabSelected(m_sellTabButton, m_mode == EShopMode.Sell);

            if (m_actionLabel != null)
            {
                m_actionLabel.text = m_mode == EShopMode.Buy ? k_buyLabel : k_sellLabel;
            }
        }

        /// <summary>设置一个页签的选中样式（只改底色，不碰 Button 的交互状态）</summary>
        private void SetTabSelected(Button tab, bool selected)
        {
            if (tab == null)
            {
                return;
            }

            Image image = tab.targetGraphic as Image;
            if (image != null)
            {
                image.color = selected ? m_tabSelectedColor : m_tabNormalColor;
            }
        }

        /// <summary>刷新各条目配色：选中高亮、买不起压暗、价格变红（出售模式没有"买不起"）</summary>
        private void RefreshEntryColors()
        {
            for (int i = 0; i < m_entryButtons.Count; i++)
            {
                bool affordable = m_mode == EShopMode.Sell
                                  || (i < m_items.Count && m_gold >= m_items[i].Price);

                if (m_entryButtons[i] != null)
                {
                    Image img = m_entryButtons[i].targetGraphic as Image;
                    if (img != null)
                    {
                        if (i == m_selectedIndex)
                        {
                            img.color = m_entrySelectedColor;
                        }
                        else
                        {
                            img.color = affordable ? m_entryNormalColor : m_entryUnaffordableColor;
                        }
                    }
                }

                if (i < m_entryPrices.Count && m_entryPrices[i] != null)
                {
                    m_entryPrices[i].color = affordable ? m_priceNormalColor : m_priceUnaffordableColor;
                }
            }
        }

        /// <summary>刷新右侧详情：描述 / 属性 / 价格，并同步操作按钮可用性（文案与颜色随模式变化）</summary>
        private void RefreshDetail()
        {
            RefreshGold();

            ShopItemData item = SelectedItem;
            bool isSelling = m_mode == EShopMode.Sell;

            // 未选中：显示占位提示并禁用操作按钮
            if (item == null)
            {
                if (m_itemNameText != null)
                {
                    m_itemNameText.text = isSelling ? k_noSellSelectionName : k_noSelectionName;
                }

                if (m_itemDescText != null)
                {
                    // 出售模式下如果一件可卖的都没有，直接说清楚，别让玩家以为是面板坏了
                    bool nothingToSell = isSelling && m_items.Count == 0;
                    m_itemDescText.text = nothingToSell
                        ? k_nothingToSellDesc
                        : (isSelling ? k_noSellSelectionDesc : k_noSelectionDesc);
                }

                if (m_itemPriceText != null) { m_itemPriceText.text = string.Empty; }

                ClearStatRows();
                SetActionButtonEnabled(false);
                return;
            }

            if (m_itemNameText != null)
            {
                m_itemNameText.text = item.Name;
            }

            if (m_itemDescText != null)
            {
                m_itemDescText.text = item.Description;
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

            if (m_itemPriceText != null)
            {
                m_itemPriceText.text = (isSelling ? k_sellPricePrefix : k_buyPricePrefix) + item.Price;
                m_itemPriceText.color = m_priceNormalColor;
            }

            // 只有"买"才有钱够不够的问题；"卖"只要有货就能卖
            SetActionButtonEnabled(isSelling || m_gold >= item.Price);
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

        /// <summary>设置「购买 / 出售」按钮的可用状态</summary>
        private void SetActionButtonEnabled(bool enabled)
        {
            if (m_actionButton != null)
            {
                m_actionButton.interactable = enabled;
            }
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>条目点击（代码绑定）：选中该条目</summary>
        private void HandleEntryClicked(int index)
        {
            SelectItem(index);
        }

        /// <summary>「购买」页签回调（Inspector 拖曳绑定）：切到购买模式</summary>
        public void HandleBuyTabClicked()
        {
            SetMode(EShopMode.Buy);
        }

        /// <summary>「出售」页签回调（Inspector 拖曳绑定）：切到出售模式</summary>
        public void HandleSellTabClicked()
        {
            SetMode(EShopMode.Sell);
        }

        /// <summary>
        /// 「购买 / 出售」按钮回调（Inspector 拖曳绑定）：按当前模式分派给商店系统。
        /// 面板不判断"能不能成交"：经济规则只有一处，就在 ShopSystem 里
        /// </summary>
        public void HandleActionClicked()
        {
            ShopItemData item = SelectedItem;
            if (item == null)
            {
                return;
            }

            ShopSystem shopSystem = ShopSystem.Instance;
            if (shopSystem == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 ShopSystem，无法交易", this);
                return;
            }

            string failReason;
            bool success = m_mode == EShopMode.Buy
                ? shopSystem.TryBuy(m_shopId, item.Id, out failReason)
                : shopSystem.TrySell(m_shopId, item.Id, out failReason);

            if (!success)
            {
                Debug.LogWarning($"[{GetType().Name}] 交易失败：{failReason}", this);
                return;
            }

            // 钱货交换会触发背包的 ItemsChanged，金币 / 列表 / 详情已由 HandleWalletChanged 刷新
        }

        /// <summary>「关闭」按钮回调（Inspector 拖曳绑定）</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.Shop);
        }

        // ====================================================================
        // 辅助方法
        // ====================================================================

        /// <summary>按名字查找子物体上的文本组件（找不到时返回 null，不报错）</summary>
        private TextMeshProUGUI FindChildText(Transform root, string childName)
        {
            Transform child = root.Find(childName);
            return child != null ? child.GetComponent<TextMeshProUGUI>() : null;
        }
    }
}
