using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 快捷按钮栏（常驻 HUD）：右上角一排入口按钮，点击直接打开对应面板（原神主界面那种做法）
    ///
    /// 为什么做成**常驻面板**而不是普通弹窗：
    /// 它要在游玩过程中一直看得见，不能参与显示栈，更不能"打开就暂停游戏"
    ///
    /// 为什么不用 SetActive 隐藏自己：
    /// 常驻面板由 UGUIManager 一直显示着，自己 SetActive(false) 会让 Update 停摆，
    /// 之后再也无法自己恢复（UGUIPanel 注释里专门记过这个坑）。
    /// 所以真正被隐藏的是内部那排按钮（m_row），而不是本面板
    ///
    /// 【重要】关于"按钮点不到"：
    /// 游玩时玩家控制器把鼠标锁在屏幕中心（第三人称视角需要），
    /// 光标被锁定时鼠标位置不会移动，UGUI 按钮根本点不中。
    /// 因此这里加了"按住呼出键（默认左 Alt）临时解锁光标"：
    /// 按住时按钮可点，松开立刻锁回，视角操作不受影响。
    /// 解锁期间 UGUIManager.WantsCursor 为 true，战斗/移动输入会被一并屏蔽，
    /// 避免"点按钮的同时人物挥了一刀"
    ///
    /// 界面由代码搭建：按钮数量跟着配置列表走，摆进场景反而不好维护
    /// </summary>
    [DisallowMultipleComponent]
    public class UGUIQuickBarPanel : UGUIPanel
    {
        // ====================================================================
        // 按钮条目
        // ====================================================================

        /// <summary>一个快捷按钮的配置</summary>
        [Serializable]
        public struct Entry
        {
            [Tooltip("点击后打开/切换的面板")]
            public EUIPanelID Panel;

            [Tooltip("图标（美术素材放这里，留空则显示文字占位）")]
            public Sprite Icon;

            [Tooltip("没有图标时显示的文字（也可作为图标的说明）")]
            public string Label;
        }

        // ====================================================================
        // 布局常量
        // ====================================================================

        // 只有图标没有底板，按钮尺寸按图标留白即可
        private const float k_buttonSize = 56f;
        private const float k_buttonSpacing = 10f;
        private const float k_topOffset = 18f;
        private const float k_rightOffset = 18f;

        // ====================================================================
        // 配色（与其它面板保持一致的深色风格）
        // ====================================================================

        [Header("美术素材（留空则用纯色占位）")]
        [Tooltip("按钮底板素材（需带九宫格边框）。留空则用纯色方块")]
        [SerializeField] private Sprite m_buttonSprite;

        /// <summary>
        /// 按钮底色：全透明。
        /// 原神那种快捷栏只有图标、没有底板，看着更轻。
        /// 透明不影响点击——raycast 只认 Image 的矩形，与 alpha 无关
        /// </summary>
        private static readonly Color s_buttonColor = new(0f, 0f, 0f, 0f);

        /// <summary>图标投影颜色：白色图标压在亮天空上会糊掉，投影保证任何背景下都看得清</summary>
        private static readonly Color s_iconShadowColor = new(0f, 0f, 0f, 0.7f);
        private static readonly Color s_iconColor = new(0.95f, 0.95f, 0.95f, 1f);
        private static readonly Color s_labelColor = new(0.88f, 0.90f, 0.93f, 1f);

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("按钮清单（从右往左依次排布）")]
        [Tooltip("要显示哪些面板的入口按钮。默认给的是玩家最常主动打开的几个面板")]
        [SerializeField]
        private List<Entry> m_entries = new()
        {
            new Entry { Panel = EUIPanelID.Inventory, Label = "背包" },
            new Entry { Panel = EUIPanelID.Character, Label = "角色" },
            new Entry { Panel = EUIPanelID.Skill, Label = "技能" },
            new Entry { Panel = EUIPanelID.QuestLog, Label = "任务" },
            new Entry { Panel = EUIPanelID.Settings, Label = "设置" },
        };

        [Header("呼出鼠标（让按钮能点）")]
        [Tooltip("按住此键临时解锁鼠标光标，松开恢复锁定。开关关掉则只能靠快捷键面板")]
        [SerializeField] private bool m_enableCursorKey = true;

        [Tooltip("呼出鼠标的按键（鼠标被锁定时按钮点不到，需要它来临时解锁）")]
        [SerializeField] private KeyCode m_cursorKey = KeyCode.LeftAlt;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>真正被隐藏/显示的那排按钮（不能隐藏本面板自己）</summary>
        private GameObject m_row;

        /// <summary>上一帧那排按钮是否可见（只在切换帧改 SetActive，避免每帧调用）</summary>
        private bool m_lastRowVisible = true;

        /// <summary>本帧是否有 UI 在要鼠标（供输入系统读取，见 UGUIManager.WantsCursor）</summary>
        public static bool IsCursorRequested { get; private set; }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            m_panelID = EUIPanelID.QuickBar;

            BuildUI();
        }

        private void OnDisable()
        {
            // 面板被停用时必须撤销请求，否则会永久锁着鼠标
            IsCursorRequested = false;
        }

        private void Update()
        {
            SyncRowVisibility();
            UpdateCursorRequest();
        }

        // ====================================================================
        // 每帧同步
        // ====================================================================

        /// <summary>
        /// 有弹窗面板打开时收起按钮栏：此时玩家在看菜单，按钮栏既没用又会挡住内容
        /// </summary>
        private void SyncRowVisibility()
        {
            if (m_row == null)
            {
                return;
            }

            bool visible = m_manager == null || m_manager.TopOverlay == null;

            if (visible != m_lastRowVisible)
            {
                m_lastRowVisible = visible;
                m_row.SetActive(visible);
            }
        }

        /// <summary>
        /// 按住呼出键时解锁光标，让按钮可点；松开立刻锁回
        ///
        /// 必须每帧写一次而不是"按下时写一次"：
        /// 玩家控制器在 Update 里会重新锁定光标，谁先执行不确定，
        /// 只在按下那一帧解锁大概率会被它立刻锁回去（UGUIManager 处理弹窗时也是这么做的）
        /// </summary>
        private void UpdateCursorRequest()
        {
            bool request = m_enableCursorKey && Input.GetKey(m_cursorKey);

            IsCursorRequested = request;

            if (request)
            {
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
        }

        // ====================================================================
        // 按钮回调
        // ====================================================================

        /// <summary>
        /// 点击某个入口按钮：打开对应面板
        ///
        /// 用 TogglePanel 而不是 ShowPanel：同一个按钮再点一次可以关掉面板，
        /// 和原神里"图标点亮/熄灭"的手感一致
        /// </summary>
        private void HandleEntryClicked(EUIPanelID panelID)
        {
            m_manager?.TogglePanel(panelID);
        }

        // ====================================================================
        // 界面搭建
        // ====================================================================

        private void BuildUI()
        {
            // 本面板自身铺满全屏（没有任何 Graphic，所以不会挡住射线），
            // 真正可见的是右上角那排按钮
            RectTransform self = (RectTransform)transform;
            self.anchorMin = Vector2.zero;
            self.anchorMax = Vector2.one;
            self.offsetMin = Vector2.zero;
            self.offsetMax = Vector2.zero;

            RectTransform row = CreateRect("Row", self);
            row.anchorMin = new Vector2(1f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(1f, 1f);
            row.anchoredPosition = new Vector2(-k_rightOffset, -k_topOffset);
            row.sizeDelta = new Vector2(CalcRowWidth(), k_buttonSize);
            m_row = row.gameObject;

            for (int i = 0; i < m_entries.Count; i++)
            {
                CreateEntryButton(m_entries[i], i, row);
            }
        }

        /// <summary>整排按钮需要的宽度</summary>
        private float CalcRowWidth()
        {
            int count = m_entries.Count;
            if (count <= 0)
            {
                return 0f;
            }

            return count * k_buttonSize + (count - 1) * k_buttonSpacing;
        }

        /// <summary>生成第 index 个入口按钮（从右往左排，最右边是列表第一个）</summary>
        private void CreateEntryButton(Entry entry, int index, RectTransform row)
        {
            RectTransform rect = CreateRect($"Entry_{entry.Panel}", row);
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(-index * (k_buttonSize + k_buttonSpacing), 0f);
            rect.sizeDelta = new Vector2(k_buttonSize, k_buttonSize);

            // 按钮底：深色方块，有美术素材后它会成为图标底板
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = s_buttonColor;

            // 配了素材就用素材（九宫格），没配就保持纯色方块
            if (m_buttonSprite != null)
            {
                background.sprite = m_buttonSprite;
                background.color = Color.white;
                background.type = m_buttonSprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
            }

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;

            // 点击音由 UISoundBinder 统一扫描绑定，这里只关心"点了做什么"
            EUIPanelID captured = entry.Panel;
            button.onClick.AddListener(() => HandleEntryClicked(captured));

            // 图标：有素材就显示，没有就退回文字占位，素材到位后真的会自动出现
            RectTransform icon = CreateRect("Icon", rect);
            icon.anchorMin = Vector2.zero;
            icon.anchorMax = Vector2.one;
            icon.offsetMin = new Vector2(8f, 8f);
            icon.offsetMax = new Vector2(-8f, -8f);

            Image iconImage = icon.gameObject.AddComponent<Image>();

            // 图标投影：没有底板兜底时，靠它把白色图标从亮背景里"抬"出来
            var shadow = icon.gameObject.AddComponent<Shadow>();
            shadow.effectColor = s_iconShadowColor;
            shadow.effectDistance = new Vector2(1.5f, -1.5f);

            iconImage.sprite = entry.Icon;
            iconImage.color = s_iconColor;
            iconImage.raycastTarget = false;
            iconImage.enabled = entry.Icon != null;

            // 各图标原图宽高比并不一致（背包 60x64、设置 78x62 …），
            // 不保持比例硬填满正方形容器会被拉伸变形，这里按原比例等比缩放
            iconImage.preserveAspect = true;

            // 文字占位：只在没有图标时显示
            TextMeshProUGUI label = CreateLabel("Label", rect, entry.Label);
            label.gameObject.SetActive(entry.Icon == null);
        }

        // ====================================================================
        // 界面搭建工具
        // ====================================================================

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static TextMeshProUGUI CreateLabel(string name, RectTransform parent, string text)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            // 运行时新建的 TMP 文本必须显式指定字体，否则不显示
            if (TMP_Settings.defaultFontAsset != null)
            {
                label.font = TMP_Settings.defaultFontAsset;
            }

            label.text = text;
            label.fontSize = 15f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = s_labelColor;
            label.raycastTarget = false;
            return label;
        }
    }
}
