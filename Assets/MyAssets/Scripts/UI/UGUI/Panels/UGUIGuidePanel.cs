using MyGame.Guide;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 新手引导 HUD：屏幕上方的一条提示，显示当前步骤、说明、进度，并提供「跳过」
    ///
    /// 两个关键设计：
    /// 1. **常驻面板**（IsPersistent + 不暂停游戏）：引导期间玩家要能正常走动、打怪，
    ///    如果它是普通弹窗，打开就会把游戏暂停，引导根本没法完成
    /// 2. **不用 SetActive 隐藏自己**：本面板会被 UGUIManager 当作常驻面板一直显示，
    ///    自己 SetActive(false) 会让 Update 停止执行，之后再也无法自己显示回来
    ///    （UGUIPanel 的注释里专门提过这个坑）。所以隐藏的是内部那张"卡片"子物体
    ///
    /// 数据来源：GuideSystem 的只读接口（IsRunning / CurrentStep / ProgressText）
    ///
    /// 界面由代码搭建：它只是几行文字加两个按钮，为它单独在场景里摆一层 UI 不划算
    /// </summary>
    [DisallowMultipleComponent]
    public class UGUIGuidePanel : UGUIPanel
    {
        // ====================================================================
        // 布局常量
        // ====================================================================

        private const float k_cardWidth = 720f;
        private const float k_cardHeight = 116f;

        /// <summary>
        /// 卡片距屏幕顶部的距离。
        ///
        /// 刻意不贴顶：右上角的快捷按钮栏占着顶部 18~74 像素，
        /// 而卡片是居中且很宽（窄分辨率下会一直伸到右边），贴顶就会和按钮栏重叠、
        /// 甚至把卡片右下角的「跳过引导」按钮压在下面点不到。
        /// 让到 96 之后两者在竖直方向彻底错开，任何分辨率都不打架
        /// </summary>
        private const float k_cardTopOffset = 96f;

        // ====================================================================
        // 配色
        // ====================================================================

        [Header("美术素材（留空则用纯色占位）")]
        [Tooltip("卡片底板素材（需带九宫格边框）。留空则用纯色")]
        [SerializeField] private Sprite m_cardSprite;

        [Tooltip("按钮底板素材（需带九宫格边框）。留空则用纯色")]
        [SerializeField] private Sprite m_buttonSprite;

        private static readonly Color s_cardColor = new(0.09f, 0.10f, 0.13f, 0.92f);
        private static readonly Color s_accentColor = new(0.35f, 0.78f, 0.55f, 1f);
        private static readonly Color s_textColor = new(0.95f, 0.95f, 0.95f, 1f);
        private static readonly Color s_mutedColor = new(0.72f, 0.74f, 0.78f, 1f);
        private static readonly Color s_buttonColor = new(0.20f, 0.23f, 0.28f, 1f);

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>卡片根节点（真正被隐藏/显示的是它，而不是本面板）</summary>
        private GameObject m_card;

        /// <summary>步骤序号文本，如「3 / 9」</summary>
        private TextMeshProUGUI m_stepText;

        /// <summary>步骤标题</summary>
        private TextMeshProUGUI m_titleText;

        /// <summary>步骤说明</summary>
        private TextMeshProUGUI m_descText;

        /// <summary>进度文本（击杀数 / 移动距离）</summary>
        private TextMeshProUGUI m_progressText;

        /// <summary>「完成本步」按钮（仅 Manual 条件的步骤显示）</summary>
        private Button m_manualButton;

        /// <summary>上一帧是否在引导（用于只在状态切换时改显隐，避免每帧 SetActive）</summary>
        private bool m_lastRunning;

        /// <summary>上一帧显示的步骤索引（变化时才重建文本，避免每帧拼字符串）</summary>
        private int m_lastStepIndex = -1;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            m_panelID = EUIPanelID.Guide;

            BuildUI();

            // 初始先隐藏卡片：等 GuideSystem 确认这一局要不要播
            m_card.SetActive(false);
        }

        private void Update()
        {
            SyncWithGuideSystem();
        }

        // ====================================================================
        // 与引导系统同步
        // ====================================================================

        private void SyncWithGuideSystem()
        {
            GuideSystem guide = GuideSystem.Instance;
            bool running = guide != null && guide.IsRunning;

            // 显隐只在状态切换那一帧改：SetActive 每帧调用会有额外开销
            if (running != m_lastRunning)
            {
                m_lastRunning = running;
                m_card.SetActive(running);

                if (running)
                {
                    // 刚播起来时强制刷新一次文本
                    m_lastStepIndex = -1;
                }
            }

            if (!running)
            {
                return;
            }

            int index = guide.StepIndex;

            // 只有换了步骤才重建文本；进度文本每帧都可能变（击杀 1/3 → 2/3）
            if (index != m_lastStepIndex)
            {
                m_lastStepIndex = index;
                ApplyStep(guide);
            }

            string progress = guide.ProgressText;
            if (m_progressText != null)
            {
                m_progressText.text = progress;
            }

            // 「完成本步」只对"手动确认"的步骤显示
            bool manual = guide.CurrentStep.Condition == EGuideCondition.Manual;
            if (m_manualButton != null && m_manualButton.gameObject.activeSelf != manual)
            {
                m_manualButton.gameObject.SetActive(manual);
            }
        }

        /// <summary>把当前步骤的文字填进卡片</summary>
        private void ApplyStep(GuideSystem guide)
        {
            GuideStep step = guide.CurrentStep;

            if (m_stepText != null)
            {
                m_stepText.text = $"第 {guide.StepIndex + 1} / {guide.StepCount} 步";
            }

            if (m_titleText != null)
            {
                m_titleText.text = step.Title;
            }

            if (m_descText != null)
            {
                m_descText.text = step.Description;
            }
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>「跳过引导」按钮</summary>
        public void HandleSkipClicked()
        {
            GuideSystem.Instance?.Skip();
        }

        /// <summary>「完成本步」按钮（仅 Manual 步骤可见）</summary>
        public void HandleManualConfirmClicked()
        {
            GuideSystem.Instance?.ConfirmManualStep();
        }

        // ====================================================================
        // 界面搭建
        // ====================================================================

        private void BuildUI()
        {
            RectTransform root = (RectTransform)transform;

            // 卡片：屏幕上方居中，不铺满全屏，避免挡住视线
            RectTransform card = CreateImage("Card", root, s_cardColor, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
            ApplySprite(card, m_cardSprite);
            card.pivot = new Vector2(0.5f, 1f);
            card.anchoredPosition = new Vector2(0f, -k_cardTopOffset);
            card.sizeDelta = new Vector2(k_cardWidth, k_cardHeight);
            m_card = card.gameObject;

            // 左侧竖条：让提示条一眼能和其它 HUD 区分开
            RectTransform accent = CreateImage("Accent", card, s_accentColor, new Vector2(0f, 0f), new Vector2(0f, 1f));
            accent.pivot = new Vector2(0f, 0.5f);
            accent.sizeDelta = new Vector2(5f, 0f);
            accent.anchoredPosition = Vector2.zero;

            // 步骤序号（左上角小字）
            m_stepText = CreateText("StepText", card, "第 1 / 1 步", 16f, TextAlignmentOptions.Left, s_accentColor,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -12f), new Vector2(200f, 22f), new Vector2(0f, 1f));

            // 标题
            m_titleText = CreateText("Title", card, "标题", 22f, TextAlignmentOptions.Left, s_textColor,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -34f), new Vector2(420f, 30f), new Vector2(0f, 1f));

            // 说明
            m_descText = CreateText("Desc", card, "说明", 17f, TextAlignmentOptions.TopLeft, s_mutedColor,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -66f), new Vector2(500f, 42f), new Vector2(0f, 1f));

            // 进度（标题右侧）
            m_progressText = CreateText("Progress", card, "", 18f, TextAlignmentOptions.Right, s_accentColor,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -34f), new Vector2(200f, 28f), new Vector2(1f, 1f));

            // 「完成本步」（仅 Manual 步骤显示）
            m_manualButton = CreateButton("ManualButton", card, "完成本步", HandleManualConfirmClicked, 150f, 36f, 178f);
            m_manualButton.gameObject.SetActive(false);

            // 「跳过引导」
            Button skip = CreateButton("SkipButton", card, "跳过引导", HandleSkipClicked, 132f, 36f, 24f);
            skip.name = "SkipButton";
        }

        // ====================================================================
        // 界面搭建工具
        // ====================================================================

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static RectTransform CreateImage(string name, RectTransform parent, Color color, Vector2 anchorMin, Vector2 anchorMax)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return rect;
        }

        /// <summary>
        /// 给刚建好的纯色 Image 套上美术素材。没配素材就保持纯色占位，
        /// 所以美术素材可以随时补，不必改代码
        /// </summary>
        private static void ApplySprite(RectTransform rect, Sprite sprite)
        {
            if (rect == null || sprite == null)
            {
                return;
            }

            Image image = rect.GetComponent<Image>();
            if (image == null)
            {
                return;
            }

            image.sprite = sprite;
            image.color = Color.white;
            // 素材带了九宫格边框就用 Sliced（拉伸不变形），否则只能 Simple
            image.type = sprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
        }

        private static TextMeshProUGUI CreateText(
            string name, RectTransform parent, string text, float fontSize, TextAlignmentOptions alignment, Color color,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 sizeDelta, Vector2 pivot)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;

            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            // 运行时新建的 TMP 文本必须显式指定字体，否则不显示
            if (TMP_Settings.defaultFontAsset != null)
            {
                label.font = TMP_Settings.defaultFontAsset;
            }

            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>在卡片右上角生成一个固定尺寸的按钮（从右往左排列，rightOffset 是距右边距离）</summary>
        private Button CreateButton(string name, RectTransform parent, string label, UnityEngine.Events.UnityAction onClick, float width, float height, float rightOffset)
        {
            RectTransform rect = CreateImage(name, parent, s_buttonColor, new Vector2(1f, 1f), new Vector2(1f, 1f));
            ApplySprite(rect, m_buttonSprite);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-rightOffset, -66f);
            rect.sizeDelta = new Vector2(width, height);

            Button button = rect.gameObject.AddComponent<Button>();
            button.onClick.AddListener(onClick);

            CreateText("Label", rect, label, 17f, TextAlignmentOptions.Center, s_textColor,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));

            return button;
        }
    }
}
