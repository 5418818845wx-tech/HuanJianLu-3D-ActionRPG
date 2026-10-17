using System;
using System.Collections.Generic;
using MyGame.KeyBindings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 按键设置面板：逐条列出可改的动作，点右侧按钮后按任意键即可改绑
    ///
    /// 为什么这个面板的界面是"代码搭出来"的，而不是像其它面板那样在 Hierarchy 里摆好：
    /// 它的行数是跟着 EGameKey 的数量走的（当前 15 条），
    /// 而每行结构完全一样（动作名 + 按键按钮）。在场景里手摆 15 行、以后加一个动作再摆一行，
    /// 既容易漏也很难维护；用代码按枚举生成，加动作只需要在枚举里加一项
    ///
    /// 改键流程：
    /// 1. 点某行右侧按钮 → 按钮文字变成「按任意键…」，同时置位 KeyBindingManager.IsCapturing
    /// 2. 玩家按下任意键 → 写入绑定并立即保存（含冲突时与对方交换）
    /// 3. 按 Esc 取消（Esc 在其它场景是"暂停"，在这里专门用作取消，不进绑定）
    ///
    /// 挂载位置：UI 根节点（UICanvas）下的一个空物体，挂本组件即可，界面自己会搭
    /// </summary>
    [DisallowMultipleComponent]
    public class UGUIKeyBindPanel : UGUIPanel
    {
        // ====================================================================
        // 布局常量（仅几何，视觉样式统一用下面的静态颜色常量）
        // ====================================================================

        private const float k_windowWidth = 760f;
        private const float k_windowHeight = 580f;
        private const float k_rowHeight = 48f;
        private const float k_rowSpacing = 4f;
        private const float k_scrollHeight = 360f;

        // ====================================================================
        // 配色（与其它面板保持一致的深色风格）
        // ====================================================================

        private static readonly Color s_backdropColor = new(0f, 0f, 0f, 0.6f);
        // ====================================================================
        // 美术素材（留空则退回下面的纯色占位）
        // ====================================================================

        [Header("美术素材（留空则用纯色占位）")]
        [Tooltip("窗口底板素材（需带九宫格边框）。留空则用纯色")]
        [SerializeField] private Sprite m_windowSprite;

        [Tooltip("按钮底板素材（需带九宫格边框）。留空则用纯色")]
        [SerializeField] private Sprite m_buttonSprite;

        private static readonly Color s_windowColor = new(0.11f, 0.12f, 0.14f, 0.98f);
        private static readonly Color s_buttonColor = new(0.20f, 0.23f, 0.28f, 1f);
        private static readonly Color s_buttonWaitingColor = new(0.24f, 0.42f, 0.30f, 1f);
        private static readonly Color s_textColor = new(0.92f, 0.92f, 0.92f, 1f);
        private static readonly Color s_mutedColor = new(0.65f, 0.65f, 0.65f, 1f);

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>正在等待玩家按键的动作（null = 不在改键状态）</summary>
        private EGameKey? m_waitingKey;

        /// <summary>开始等待的帧号：跳过"点击按钮那一帧"，否则鼠标抬起会被当成按键捕获</summary>
        private int m_waitStartFrame = -1;

        /// <summary>每行的按键按钮（与 EGameKey 顺序一一对应）</summary>
        private readonly List<Button> m_rowButtons = new();

        /// <summary>每行的按键按钮文本</summary>
        private readonly List<TextMeshProUGUI> m_rowButtonLabels = new();

        /// <summary>底部提示文本（显示"按 Esc 取消"之类的当前状态）</summary>
        private TextMeshProUGUI m_hintText;

        /// <summary>KeyCode 全量缓存：改键时要遍历找"哪个键被按下"，每帧 Enum.GetValues 会产生垃圾</summary>
        private static readonly KeyCode[] s_allKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            m_panelID = EUIPanelID.KeyBindings;

            BuildUI();
        }

        private void Update()
        {
            if (!IsVisible || !m_waitingKey.HasValue)
            {
                return;
            }

            // 点击按钮那一帧的鼠标抬起不该被当成"玩家按了鼠标"：跳过一帧
            if (Time.frameCount == m_waitStartFrame)
            {
                return;
            }

            // Esc 用作"取消改键"，不参与绑定（否则容易把暂停键改没了）
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                StopWaiting();
                m_hintText.text = "已取消改键";
                return;
            }

            if (!Input.anyKeyDown)
            {
                return;
            }

            KeyCode captured = FindPressedKey();
            if (captured == KeyCode.None)
            {
                return;
            }

            KeyBindingManager.Set(m_waitingKey.Value, captured);
            m_hintText.text = $"「{KeyBindingManager.GetDisplayName(m_waitingKey.Value)}」已改为 {KeyBindingManager.GetKeyLabel(captured)}";

            StopWaiting();
        }

        private void OnDisable()
        {
            // 面板被禁用时若还在等待按键，必须复位标志：
            // 否则 KeyBindingManager.IsCapturing 会一直为 true，整个游戏的快捷键全部失灵
            if (m_waitingKey.HasValue)
            {
                StopWaiting();
            }
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        public override void OnEnter()
        {
            base.OnEnter();

            StopWaiting();
            m_hintText.text = "★ 这一行是热更下来的新代码 ★";
            RefreshRows();
        }

        public override void OnExit()
        {
            StopWaiting();
            base.OnExit();
        }

        // ====================================================================
        // 界面搭建（代码生成）
        // ====================================================================

        private void BuildUI()
        {
            RectTransform root = (RectTransform)transform;

            // 半透明遮罩：点击关闭面板
            RectTransform backdrop = CreateImage("Backdrop", root, s_backdropColor, Vector2.zero, Vector2.one);
            Button backdropButton = backdrop.gameObject.AddComponent<Button>();
            backdropButton.transition = Selectable.Transition.None;
            backdropButton.onClick.AddListener(HandleCloseClicked);

            // 面板主体
            RectTransform window = CreateImage("Window", root, s_windowColor, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            ApplySprite(window, m_windowSprite);
            window.sizeDelta = new Vector2(k_windowWidth, k_windowHeight);
            window.anchoredPosition = Vector2.zero;

            // 标题
            CreateText("Title", window, "按键设置", 28f, TextAlignmentOptions.Center, s_textColor,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -28f), new Vector2(-40f, 48f), new Vector2(0.5f, 1f));

            // 提示行
            m_hintText = CreateText("Hint", window, "点击右侧按键即可修改，按 Esc 取消", 18f, TextAlignmentOptions.Center, s_mutedColor,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -76f), new Vector2(-40f, 30f), new Vector2(0.5f, 1f));

            // 列表标题（动作 / 按键）
            CreateText("ColumnHeader", window, "动作                                    当前按键", 18f, TextAlignmentOptions.Center, s_mutedColor,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -118f), new Vector2(-80f, 26f), new Vector2(0.5f, 1f));

            BuildScrollList(window);

            // 底部按钮：恢复默认 / 关闭
            CreateButton("ResetButton", window, "恢复默认", new Vector2(-110f, 34f), new Vector2(170f, 44f), HandleResetClicked, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            CreateButton("CloseButton", window, "关闭", new Vector2(110f, 34f), new Vector2(170f, 44f), HandleCloseClicked, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
        }

        /// <summary>搭建可滚动的动作列表（15 条动作一次性生成，超出部分滚动查看）</summary>
        private void BuildScrollList(RectTransform window)
        {
            // 滚动区外壳
            RectTransform scrollRoot = CreateRect("KeyList", window);
            scrollRoot.anchorMin = new Vector2(0f, 1f);
            scrollRoot.anchorMax = new Vector2(1f, 1f);
            scrollRoot.pivot = new Vector2(0.5f, 1f);
            scrollRoot.anchoredPosition = new Vector2(0f, -142f);
            scrollRoot.sizeDelta = new Vector2(-80f, k_scrollHeight);

            ScrollRect scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 25f;

            // 视口：必须有 Mask/Image，否则内容会溢出到面板外
            RectTransform viewport = CreateImage("Viewport", scrollRoot, new Color(0f, 0f, 0f, 0.25f), Vector2.zero, Vector2.one);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            // 内容：纵向排列 + 高度自适应
            RectTransform content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.spacing = k_rowSpacing;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;

            // 按枚举顺序生成每一行
            foreach (EGameKey key in KeyBindingManager.AllKeys)
            {
                BuildRow(content, key);
            }
        }

        /// <summary>生成一行：左侧动作名 + 右侧按键按钮</summary>
        private void BuildRow(RectTransform content, EGameKey key)
        {
            RectTransform row = CreateRect($"Row_{key}", content);

            LayoutElement element = row.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = k_rowHeight;

            // 动作名（左）
            CreateText("Name", row, KeyBindingManager.GetDisplayName(key), 20f, TextAlignmentOptions.Left, s_textColor,
                new Vector2(0f, 0f), new Vector2(0.6f, 1f), new Vector2(20f, 0f), new Vector2(-10f, 0f), new Vector2(0f, 0.5f));

            // 按键按钮（右）
            RectTransform button = CreateButton("KeyButton", row, "-", Vector2.zero, Vector2.zero, null,
                new Vector2(0.6f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            button.offsetMin = new Vector2(4f, 4f);
            button.offsetMax = new Vector2(-20f, -4f);

            Button component = button.GetComponent<Button>();
            component.onClick.AddListener(() => HandleKeyButtonClicked(key));

            m_rowButtons.Add(component);
            m_rowButtonLabels.Add(button.Find("Label").GetComponent<TextMeshProUGUI>());
        }

        // ====================================================================
        // 改键流程
        // ====================================================================

        /// <summary>点击某行的按键按钮：进入"等待按键"状态</summary>
        private void HandleKeyButtonClicked(EGameKey key)
        {
            m_waitingKey = key;
            m_waitStartFrame = Time.frameCount;

            // 置位后 UGUIManager 会暂停处理面板快捷键，避免改键时误开别的面板
            KeyBindingManager.IsCapturing = true;

            m_hintText.text = $"请按下要绑定到「{KeyBindingManager.GetDisplayName(key)}」的按键（Esc 取消）";
            RefreshRows();
        }

        /// <summary>结束等待状态并复位屏蔽标志（可安全重复调用）</summary>
        private void StopWaiting()
        {
            m_waitingKey = null;
            KeyBindingManager.IsCapturing = false;
            RefreshRows();
        }

        /// <summary>找出本帧被按下的键（遍历 KeyCode 全量；只在等待按键时执行）</summary>
        private static KeyCode FindPressedKey()
        {
            for (int i = 0; i < s_allKeyCodes.Length; i++)
            {
                KeyCode code = s_allKeyCodes[i];

                // None 不是有效按键；Esc 上面已作为"取消"处理过
                if (code == KeyCode.None || code == KeyCode.Escape)
                {
                    continue;
                }

                if (Input.GetKeyDown(code))
                {
                    return code;
                }
            }

            return KeyCode.None;
        }

        // ====================================================================
        // 刷新
        // ====================================================================

        /// <summary>刷新所有行的按键显示（等待中的那行显示「按任意键…」）</summary>
        private void RefreshRows()
        {
            for (int i = 0; i < m_rowButtonLabels.Count && i < KeyBindingManager.AllKeys.Count; i++)
            {
                EGameKey key = KeyBindingManager.AllKeys[i];
                bool waiting = m_waitingKey.HasValue && m_waitingKey.Value == key;

                if (m_rowButtonLabels[i] != null)
                {
                    m_rowButtonLabels[i].text = waiting
                        ? "按任意键…"
                        : KeyBindingManager.GetKeyLabel(KeyBindingManager.Get(key));
                }

                if (m_rowButtons[i] != null)
                {
                    Image image = m_rowButtons[i].GetComponent<Image>();
                    if (image != null)
                    {
                        image.color = waiting ? s_buttonWaitingColor : s_buttonColor;
                    }
                }
            }
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>「恢复默认」按钮：把所有动作还原成初始键位</summary>
        public void HandleResetClicked()
        {
            KeyBindingManager.ResetToDefaults();
            m_hintText.text = "已恢复默认键位";
            RefreshRows();
        }

        /// <summary>「关闭」按钮 / 点击遮罩：关闭本面板</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.KeyBindings);
        }

        // ====================================================================
        // 界面搭建工具（只服务本面板，所以放在类内而不是抽公共库）
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
        /// 给刚建好的纯色 Image 套上美术素材。没配素材就保持原样（纯色占位），
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
            // 必须显式指定字体：运行时新建的 TMP 文本不指定字体不会显示
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

        private RectTransform CreateButton(
            string name, RectTransform parent, string label, Vector2 anchoredPosition, Vector2 sizeDelta,
            UnityEngine.Events.UnityAction onClick, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            RectTransform rect = CreateImage(name, parent, s_buttonColor, anchorMin, anchorMax);
            ApplySprite(rect, m_buttonSprite);
            rect.pivot = pivot;

            // 固定尺寸的按钮用 anchoredPosition + sizeDelta；拉伸按钮（列表行）由调用方设置 offset
            if (sizeDelta != Vector2.zero)
            {
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = sizeDelta;
            }

            Button button = rect.gameObject.AddComponent<Button>();
            if (onClick != null)
            {
                button.onClick.AddListener(onClick);
            }

            CreateText("Label", rect, label, 20f, TextAlignmentOptions.Center, s_textColor,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));

            return rect;
        }
    }
}
