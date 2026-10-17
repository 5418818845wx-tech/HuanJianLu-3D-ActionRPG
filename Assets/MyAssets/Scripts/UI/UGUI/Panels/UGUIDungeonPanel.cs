using System.Collections.Generic;
using MyGame.Dungeon;
using MyGame.Inventory;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 秘境面板（UGUI 版）：列出本作所有秘境，玩家选一个进入
    ///
    /// 交互流程：
    /// 1. 大世界点击秘境入口 → DungeonPortal 写入 PendingDungeon 并 ShowPanel(Dungeon)
    /// 2. OnEnter：消费 PendingDungeon（读走即清空），把它对应的条目置顶并高亮
    /// 3. 点某条「进入」：记录回程场景 → BeginRun → 加载秘境场景
    /// 4. 点「关闭」：HidePanel
    ///
    /// 数据来源：DungeonManager.AllDungeons（运行时没有 AssetDatabase，面板捞不到散落的资产，
    /// 只能由场景里的管理器持有清单——这也是为什么秘境注册表放在 DungeonManager 上）
    ///
    /// 界面全部由代码搭建（照抄 UGUIQuickBarPanel 的做法）：
    /// 条目数量跟着注册表走，摆进场景反而不好维护；
    /// 秘境对象由使用者自己创建并挂本脚本，面板不往场景里塞任何东西
    ///
    /// 【排版要点】文本高度统一用 GetPreferredValues() 现算，而不是读 preferredWidth/preferredHeight：
    /// 后者在编辑模式（对象停用、尚未参与布局）时取不到值，算出来的行高会是 0，
    /// 条目会整体叠在一起。GetPreferredValues 显式传入宽度，任何时候都能算出正确行数
    /// </summary>
    [DisallowMultipleComponent]
    public class UGUIDungeonPanel : UGUIPanel
    {
        // ====================================================================
        // 布局常量
        // ====================================================================

        private const float k_windowWidth = 780f;
        private const float k_windowHeight = 560f;
        private const float k_windowPadding = 22f;

        /// <summary>标题区高度：内容列表从这条线之下开始排</summary>
        private const float k_headerHeight = 66f;

        private const float k_entryPadX = 16f;
        private const float k_entryPadY = 12f;
        private const float k_entrySpacing = 12f;

        private const float k_iconSize = 64f;
        private const float k_iconGap = 14f;

        private const float k_enterButtonWidth = 104f;
        private const float k_enterButtonHeight = 40f;

        private const float k_lineGap = 6f;

        private const float k_titleFontSize = 28f;
        private const float k_nameFontSize = 22f;
        private const float k_descFontSize = 16f;
        private const float k_metaFontSize = 15f;
        private const float k_rewardFontSize = 15f;

        // ====================================================================
        // 配色（与其它面板保持一致的深色风格）
        // ====================================================================

        private static readonly Color s_windowColor = new(0.10f, 0.11f, 0.14f, 0.97f);
        private static readonly Color s_entryColor = new(0.17f, 0.19f, 0.23f, 1f);
        private static readonly Color s_entryHighlightColor = new(0.28f, 0.35f, 0.45f, 1f);
        private static readonly Color s_titleColor = new(0.95f, 0.93f, 0.85f, 1f);
        private static readonly Color s_nameColor = new(0.96f, 0.96f, 0.96f, 1f);
        private static readonly Color s_descColor = new(0.80f, 0.83f, 0.87f, 1f);
        private static readonly Color s_metaColor = new(0.85f, 0.78f, 0.55f, 1f);
        private static readonly Color s_rewardColor = new(0.62f, 0.85f, 0.70f, 1f);
        private static readonly Color s_buttonColor = new(0.24f, 0.32f, 0.44f, 1f);
        private static readonly Color s_buttonHighlightColor = new(0.35f, 0.55f, 0.78f, 1f);

        // ====================================================================
        // 美术素材（留空则用纯色占位）
        // ====================================================================

        [Header("美术素材（留空则用纯色占位）")]
        [Tooltip("窗口底板素材（需带九宫格边框）。留空则用半透明深色底板")]
        [SerializeField] private Sprite m_windowSprite;

        [Tooltip("条目底板素材。留空则用纯色，高亮时以颜色区分")]
        [SerializeField] private Sprite m_entrySprite;

        [Tooltip("按钮底板素材。留空则用纯色")]
        [SerializeField] private Sprite m_buttonSprite;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>条目容器（运行时往里塞秘境条目）</summary>
        private RectTransform m_content;

        /// <summary>本面板当前展示的所有动态物体（条目/占位文本），重排时统一销毁</summary>
        private readonly List<GameObject> m_entries = new();

        /// <summary>需要置顶并高亮的秘境（来自入口透传；消费一次即清理）</summary>
        private DungeonDefinitionSO m_highlightDungeon;

        /// <summary>列表排序缓冲：复用同一个 List，避免每次打开面板都新建集合</summary>
        private readonly List<DungeonDefinitionSO> m_orderBuffer = new();

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 面板 ID 硬编码设置，防止在 Inspector 里漏配
            m_panelID = EUIPanelID.Dungeon;

            BuildUI();
        }

        /// <summary>面板进入显示：消费入口标记并重建列表</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 读走即清：保证下次从别的途径（例如快捷键）打开面板时不会误高亮上一次的入口
            m_highlightDungeon = DungeonPortal.PendingDungeon;
            DungeonPortal.PendingDungeon = null;

            RebuildList();
        }

        // ====================================================================
        // 列表重建
        // ====================================================================

        /// <summary>按注册表重建秘境列表（置顶条目排在最前）</summary>
        private void RebuildList()
        {
            ClearEntries();

            if (m_content == null)
            {
                return;
            }

            List<DungeonDefinitionSO> ordered = CollectDungeons();

            // 没有任何秘境：显示一行占位提示，避免玩家看到一个空窗
            if (ordered.Count == 0)
            {
                CreatePlaceholder();
                return;
            }

            float topOffset = 0f;

            for (int i = 0; i < ordered.Count; i++)
            {
                CreateEntry(ordered[i], topOffset, out float entryHeight);

                // 下一个条目往下挪"本条高度 + 间距"
                topOffset += entryHeight + k_entrySpacing;
            }
        }

        /// <summary>
        /// 汇总要展示的秘境：高亮的那个排最前，其余按注册顺序跟在后面。
        /// 入口透传的秘境即使不在注册表里也会被带上，保证"点了入口却看不到它"不会发生
        /// </summary>
        private List<DungeonDefinitionSO> CollectDungeons()
        {
            m_orderBuffer.Clear();

            if (m_highlightDungeon != null)
            {
                m_orderBuffer.Add(m_highlightDungeon);
            }

            DungeonManager manager = DungeonManager.Instance;
            IReadOnlyList<DungeonDefinitionSO> source = manager != null ? manager.AllDungeons : null;

            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    DungeonDefinitionSO dungeon = source[i];

                    // 跳过空引用与已置顶的那个，避免重复
                    if (dungeon == null || dungeon == m_highlightDungeon)
                    {
                        continue;
                    }

                    m_orderBuffer.Add(dungeon);
                }
            }

            return m_orderBuffer;
        }

        /// <summary>创建单条秘境条目（图标 + 名称 + 描述 + 推荐等级 + 奖励预览 + 进入按钮）</summary>
        private void CreateEntry(DungeonDefinitionSO dungeon, float topOffset, out float entryHeight)
        {
            bool highlight = dungeon == m_highlightDungeon;

            RectTransform entry = CreateRect($"Dungeon_{dungeon.DungeonId}", m_content);
            entry.anchorMin = new Vector2(0f, 1f);
            entry.anchorMax = new Vector2(1f, 1f);
            entry.pivot = new Vector2(0.5f, 1f);
            entry.anchoredPosition = new Vector2(0f, -topOffset);

            Image entryBackground = entry.gameObject.AddComponent<Image>();
            entryBackground.color = highlight ? s_entryHighlightColor : s_entryColor;
            if (m_entrySprite != null)
            {
                // 保留上面的颜色当"高亮 tint"：贴了底图也能靠颜色区分选中态
                entryBackground.sprite = m_entrySprite;
                entryBackground.type = m_entrySprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
            }

            // 各种可用宽度：整窗宽 → 去内边距 → 再去图标与按钮留给标题
            float entryWidth = k_windowWidth - 2f * k_windowPadding;
            float innerWidth = entryWidth - 2f * k_entryPadX;
            float nameWidth = entryWidth - k_entryPadX - k_iconSize - k_iconGap - k_enterButtonWidth - k_entryPadX;

            // --- 文本内容 ---
            string nameStr = dungeon.DisplayName;
            string descStr = string.IsNullOrEmpty(dungeon.Description) ? "（暂无描述）" : dungeon.Description;
            string metaStr = $"推荐等级 Lv.{dungeon.RecommendedLevel}";
            string rewardStr = BuildRewardText(dungeon);

            // --- 先建文本组件并测量高度（GetPreferredValues 现算，编辑模式下也可靠）---
            TextMeshProUGUI nameText = CreateText("Name", entry, nameStr, k_nameFontSize, s_nameColor, FontStyles.Bold, false);
            float nameHeight = nameText.GetPreferredValues(nameStr, nameWidth, 0f).y;

            TextMeshProUGUI descText = CreateText("Description", entry, descStr, k_descFontSize, s_descColor, FontStyles.Normal, true);
            float descHeight = descText.GetPreferredValues(descStr, innerWidth, 0f).y;

            TextMeshProUGUI metaText = CreateText("Meta", entry, metaStr, k_metaFontSize, s_metaColor, FontStyles.Normal, false);
            float metaHeight = metaText.GetPreferredValues(metaStr, innerWidth, 0f).y;

            TextMeshProUGUI rewardText = CreateText("Reward", entry, rewardStr, k_rewardFontSize, s_rewardColor, FontStyles.Normal, true);
            float rewardHeight = rewardText.GetPreferredValues(rewardStr, innerWidth, 0f).y;

            // --- 汇总条目总高：上内边距 + 首行 + 描述 + 推荐等级 + 奖励 + 下内边距 ---
            float topRowHeight = Mathf.Max(k_iconSize, nameHeight);
            entryHeight = k_entryPadY + topRowHeight + k_lineGap + descHeight + k_lineGap + metaHeight + k_lineGap + rewardHeight + k_entryPadY;

            // 宽度拉伸（anchorMin.x=0 / anchorMax.x=1），sizeDelta.x 留 0 即铺满内容区
            entry.sizeDelta = new Vector2(0f, entryHeight);

            // --- 图标（可空）---
            if (dungeon.Icon != null)
            {
                RectTransform iconRect = CreateRect("Icon", entry);
                iconRect.anchorMin = new Vector2(0f, 1f);
                iconRect.anchorMax = new Vector2(0f, 1f);
                iconRect.pivot = new Vector2(0f, 1f);
                iconRect.anchoredPosition = new Vector2(k_entryPadX, -k_entryPadY);
                iconRect.sizeDelta = new Vector2(k_iconSize, k_iconSize);

                Image iconImage = iconRect.gameObject.AddComponent<Image>();
                iconImage.sprite = dungeon.Icon;
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
            }

            // --- 标题：图标右侧、进入按钮左侧 ---
            SetTopText(nameText, k_entryPadX + k_iconSize + k_iconGap, k_entryPadX + k_enterButtonWidth + k_iconGap, k_entryPadY, nameHeight);

            // --- 描述 / 推荐等级 / 奖励：逐行往下排 ---
            float y = k_entryPadY + topRowHeight + k_lineGap;

            SetTopText(descText, k_entryPadX, k_entryPadX, y, descHeight);
            y += descHeight + k_lineGap;

            SetTopText(metaText, k_entryPadX, k_entryPadX, y, metaHeight);
            y += metaHeight + k_lineGap;

            SetTopText(rewardText, k_entryPadX, k_entryPadX, y, rewardHeight);

            // --- 进入按钮（右上角）---
            DungeonDefinitionSO captured = dungeon;
            RectTransform button = CreateButton("EnterButton", entry, "进入", s_buttonColor,
                () => HandleEnterClicked(captured));
            button.anchorMin = new Vector2(1f, 1f);
            button.anchorMax = new Vector2(1f, 1f);
            button.pivot = new Vector2(1f, 1f);
            button.anchoredPosition = new Vector2(-k_entryPadX, -k_entryPadY);
            button.sizeDelta = new Vector2(k_enterButtonWidth, k_enterButtonHeight);

            m_entries.Add(entry.gameObject);
        }

        /// <summary>没有可展示的秘境时给一行占位提示</summary>
        private void CreatePlaceholder()
        {
            const string tip = "（未配置秘境：请在 DungeonManager 的「秘境注册表」里添加 DungeonDefinitionSO）";

            TextMeshProUGUI placeholder = CreateText("Placeholder", m_content, tip, k_descFontSize, s_descColor, FontStyles.Normal, true);
            float height = placeholder.GetPreferredValues(tip, k_windowWidth - 2f * k_windowPadding, 0f).y;
            SetTopText(placeholder, 0f, 0f, 0f, height);

            m_entries.Add(placeholder.gameObject);
        }

        /// <summary>销毁所有动态生成的条目并清空引用</summary>
        private void ClearEntries()
        {
            for (int i = 0; i < m_entries.Count; i++)
            {
                if (m_entries[i] != null)
                {
                    Destroy(m_entries[i]);
                }
            }

            m_entries.Clear();
        }

        // ====================================================================
        // 按钮回调
        // ====================================================================

        /// <summary>点击「进入」：记录回程场景 → 开始一次秘境 → 加载秘境场景</summary>
        private void HandleEnterClicked(DungeonDefinitionSO dungeon)
        {
            if (dungeon == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(dungeon.SceneName))
            {
                Debug.LogWarning($"[{GetType().Name}] 秘境「{dungeon.DisplayName}」未配置场景名，无法进入", this);
                return;
            }

            // 必须在切场景**之前**记下当前场景名，切过去之后就取不到"上一层"了
            string returnScene = SceneManager.GetActiveScene().name;
            DungeonRunContext.BeginRun(dungeon, returnScene);

            Debug.Log($"[{GetType().Name}] 进入秘境「{dungeon.DisplayName}」，返回场景 {returnScene}，目标场景 {dungeon.SceneName}", this);

            SceneLoader.Load(dungeon.SceneName, dungeon.DisplayName);
        }

        /// <summary>点击「关闭」：关掉本面板</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(m_panelID);
        }

        // ====================================================================
        // 界面搭建（只在 Awake 跑一次，建的是"空框架"；条目在 OnEnter 里重建）
        // ====================================================================

        private void BuildUI()
        {
            // 本面板自身铺满全屏（没有任何 Graphic，不挡射线），真正可见的是正中的窗口
            RectTransform self = (RectTransform)transform;
            self.anchorMin = Vector2.zero;
            self.anchorMax = Vector2.one;
            self.offsetMin = Vector2.zero;
            self.offsetMax = Vector2.zero;

            // 窗口底板
            RectTransform window = CreateRect("Window", self);
            window.anchorMin = new Vector2(0.5f, 0.5f);
            window.anchorMax = new Vector2(0.5f, 0.5f);
            window.pivot = new Vector2(0.5f, 0.5f);
            window.anchoredPosition = Vector2.zero;
            window.sizeDelta = new Vector2(k_windowWidth, k_windowHeight);

            Image windowImage = window.gameObject.AddComponent<Image>();
            windowImage.color = s_windowColor;
            if (m_windowSprite != null)
            {
                windowImage.sprite = m_windowSprite;
                windowImage.color = Color.white;
                windowImage.type = m_windowSprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
            }

            // 标题
            TextMeshProUGUI title = CreateText("Title", window, "秘境", k_titleFontSize, s_titleColor, FontStyles.Bold, false);
            RectTransform titleRect = (RectTransform)title.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(0f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(k_windowPadding, -k_windowPadding);
            titleRect.sizeDelta = new Vector2(360f, 40f);

            // 关闭按钮（右上角）
            RectTransform close = CreateButton("CloseButton", window, "关闭", s_buttonColor, HandleCloseClicked);
            close.anchorMin = new Vector2(1f, 1f);
            close.anchorMax = new Vector2(1f, 1f);
            close.pivot = new Vector2(1f, 1f);
            close.anchoredPosition = new Vector2(-k_windowPadding, -k_windowPadding);
            close.sizeDelta = new Vector2(96f, 40f);

            // 内容区：横向铺满窗口，纵向留出标题区
            m_content = CreateRect("Content", window);
            m_content.anchorMin = new Vector2(0f, 0f);
            m_content.anchorMax = new Vector2(1f, 1f);
            m_content.offsetMin = new Vector2(k_windowPadding, k_windowPadding);
            m_content.offsetMax = new Vector2(-k_windowPadding, -k_headerHeight);
        }

        // ====================================================================
        // 界面搭建工具（只服务本面板，所以放类内不抽公共库）
        // ====================================================================

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>
        /// 创建一段 TMP 文本。
        /// 运行时新建的 TMP 不显式指定字体不会显示，所以这里统一设默认字体
        /// </summary>
        private static TextMeshProUGUI CreateText(
            string name, RectTransform parent, string content, float fontSize,
            Color color, FontStyles style, bool wrap)
        {
            RectTransform rect = CreateRect(name, parent);

            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
            {
                text.font = TMP_Settings.defaultFontAsset;
            }

            text.text = content;
            text.fontSize = fontSize;
            text.color = color;
            text.fontStyle = style;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = wrap;
            text.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// 把一段文本按"距条目上边缘 top、左右各留 left/right"摆放，高度取现算的 height。
        /// 文本横向铺满（anchorMin.x=0 / anchorMax.x=1），纵向顶对齐
        /// </summary>
        private static void SetTopText(TextMeshProUGUI text, float left, float right, float top, float height)
        {
            RectTransform rect = (RectTransform)text.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);

            // 顶边在锚点下方 top 处，底边再往下 height：offsetMax 管顶边、offsetMin 管底边
            rect.offsetMax = new Vector2(-right, -top);
            rect.offsetMin = new Vector2(left, -(top + height));
        }

        /// <summary>创建一个带文字标签的按钮（代码绑定回调）</summary>
        private RectTransform CreateButton(
            string name, RectTransform parent, string label, Color color,
            UnityEngine.Events.UnityAction onClick)
        {
            RectTransform rect = CreateRect(name, parent);

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            if (m_buttonSprite != null)
            {
                image.sprite = m_buttonSprite;
                image.type = m_buttonSprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
            }

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            if (onClick != null)
            {
                button.onClick.AddListener(onClick);
            }

            TextMeshProUGUI text = CreateText("Label", rect, label, 20f, Color.white, FontStyles.Normal, false);
            RectTransform textRect = (RectTransform)text.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            text.alignment = TextAlignmentOptions.Center;

            return rect;
        }

        // ====================================================================
        // 奖励预览拼装
        // ====================================================================

        /// <summary>把固定奖励与随机奖励拼成一行预览文本</summary>
        private static string BuildRewardText(DungeonDefinitionSO dungeon)
        {
            List<string> parts = new();

            List<DungeonDefinitionSO.FixedReward> fixedRewards = dungeon.FixedRewards;
            if (fixedRewards != null)
            {
                for (int i = 0; i < fixedRewards.Count; i++)
                {
                    DungeonDefinitionSO.FixedReward reward = fixedRewards[i];

                    // 没填物品 ID 或数量非正的条目跳过：这是配置笔误，不该显示成"给 0 个"
                    if (reward == null || string.IsNullOrEmpty(reward.ItemId) || reward.Amount <= 0)
                    {
                        continue;
                    }

                    parts.Add($"{ResolveItemName(reward.ItemId)}×{reward.Amount}");
                }
            }

            string fixedPart = parts.Count > 0 ? string.Join("、", parts) : "无";
            string text = $"奖励：{fixedPart}";

            if (dungeon.RandomDropTable != null && dungeon.RandomRolls > 0)
            {
                text += $"　随机奖励×{dungeon.RandomRolls}";
            }

            return text;
        }

        /// <summary>查物品显示名，查不到就用 ID 兜底（保证预览不空白）</summary>
        private static string ResolveItemName(string itemId)
        {
            InventoryManager inventory = InventoryManager.Instance;
            ItemDataSO data = inventory != null ? inventory.FindItem(itemId) : null;
            return data != null ? data.DisplayName : itemId;
        }
    }
}
