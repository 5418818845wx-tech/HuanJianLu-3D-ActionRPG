using System.Collections.Generic;
using MyGame.Inventory;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 「获得物品」提示面板（常驻 HUD）：右下角弹出一条条"图标 + 名字 ×数量"，
    /// 几秒后自动淡出，效果对齐原神捡东西时的提示
    ///
    /// 数据来源：ItemGainNotifier.ItemGained 静态事件（谁真正把物品塞进背包，谁上报）
    ///
    /// 为什么是常驻面板而不是弹窗：
    /// 它是战斗中随时会冒出来的轻量提示，绝不能压栈、更不能暂停游戏
    ///
    /// 界面由代码搭建：条数是运行时动态的，在场景里摆不现实
    /// </summary>
    [DisallowMultipleComponent]
    public class UGUIPickupToastPanel : UGUIPanel
    {
        // ====================================================================
        // 布局常量（对齐原神的提示尺寸：小而紧凑）
        // ====================================================================

        private const float k_itemWidth = 250f;
        private const float k_itemHeight = 42f;
        private const float k_itemSpacing = 6f;
        private const float k_iconSize = 30f;

        /// <summary>距底部高度：要避开右下角的波次 HUD（它占底部 28~128）</summary>
        private const float k_bottomOffset = 140f;
        private const float k_rightOffset = 18f;

        // ====================================================================
        // 时序与数量
        // ====================================================================

        /// <summary>一条提示的存活秒数（含最后的淡出时间）</summary>
        private const float k_lifeSeconds = 2.6f;

        /// <summary>末尾淡出的秒数</summary>
        private const float k_fadeSeconds = 0.5f;

        /// <summary>同屏最多几条，超出就把最旧的挤掉</summary>
        private const int k_maxToasts = 5;

        // ====================================================================
        // 配色
        // ====================================================================

        private static readonly Color s_barColor = new(0.06f, 0.07f, 0.09f, 0.72f);
        private static readonly Color s_nameColor = new(0.96f, 0.96f, 0.96f, 1f);
        private static readonly Color s_countColor = new(0.85f, 0.88f, 0.92f, 1f);

        // ====================================================================
        // 美术素材（留空则用纯色占位）
        // ====================================================================

        [Header("美术素材（留空则用纯色占位）")]
        [Tooltip("提示条底板素材（需带九宫格边框）。留空则用半透明纯色条")]
        [SerializeField] private Sprite m_barSprite;

        [Tooltip("图标占位（物品没配 Icon 时用它）。留空则该条只显示名字")]
        [SerializeField] private Sprite m_fallbackIcon;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>一条提示的运行时句柄</summary>
        private class ToastEntry
        {
            public GameObject Root;
            public CanvasGroup Group;
            public float Age;
        }

        private RectTransform m_root;
        private readonly List<ToastEntry> m_toasts = new();

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            m_panelID = EUIPanelID.PickupToast;
            BuildUI();
        }

        private void OnEnable()
        {
            ItemGainNotifier.ItemGained += HandleItemGained;
        }

        private void OnDisable()
        {
            ItemGainNotifier.ItemGained -= HandleItemGained;
        }

        private void Update()
        {
            UpdateToasts();
        }

        // ====================================================================
        // 提示的生成与更新
        // ====================================================================

        /// <summary>有物品入包：生成一条提示（插到最下方，旧的往上顶）</summary>
        private void HandleItemGained(string itemId, int amount)
        {
            if (m_root == null)
            {
                return;
            }

            // 超上限先挤掉最旧的一条，避免刷屏
            while (m_toasts.Count >= k_maxToasts)
            {
                RemoveToast(0);
            }

            m_toasts.Insert(0, CreateToast(itemId, amount));
            RelayoutToasts();
        }

        /// <summary>推进每条提示的寿命，到点淡出并销毁</summary>
        private void UpdateToasts()
        {
            // 倒序遍历：删除元素不影响前面的下标
            for (int i = m_toasts.Count - 1; i >= 0; i--)
            {
                ToastEntry toast = m_toasts[i];
                if (toast.Root == null)
                {
                    m_toasts.RemoveAt(i);
                    continue;
                }

                toast.Age += Time.unscaledDeltaTime;

                float fadeStart = k_lifeSeconds - k_fadeSeconds;
                if (toast.Age >= fadeStart)
                {
                    // 用 unscaledDeltaTime：暂停/慢动作时提示也该正常消失
                    toast.Group.alpha = Mathf.Clamp01((k_lifeSeconds - toast.Age) / k_fadeSeconds);
                }

                if (toast.Age >= k_lifeSeconds)
                {
                    RemoveToast(i);
                }
            }
        }

        /// <summary>按列表顺序重排纵向位置：索引 0 贴底，越往后越往上</summary>
        private void RelayoutToasts()
        {
            for (int i = 0; i < m_toasts.Count; i++)
            {
                if (m_toasts[i].Root == null)
                {
                    continue;
                }

                var rt = (RectTransform)m_toasts[i].Root.transform;
                rt.anchoredPosition = new Vector2(0f, i * (k_itemHeight + k_itemSpacing));
            }
        }

        private void RemoveToast(int index)
        {
            if (index < 0 || index >= m_toasts.Count)
            {
                return;
            }

            if (m_toasts[index].Root != null)
            {
                Destroy(m_toasts[index].Root);
            }

            m_toasts.RemoveAt(index);
            RelayoutToasts();
        }

        // ====================================================================
        // 界面搭建
        // ====================================================================

        private void BuildUI()
        {
            // 本面板铺满全屏（无 Graphic，不挡射线），真正可见的是右下角那一列提示
            RectTransform self = (RectTransform)transform;
            self.anchorMin = Vector2.zero;
            self.anchorMax = Vector2.one;
            self.offsetMin = Vector2.zero;
            self.offsetMax = Vector2.zero;

            m_root = CreateRect("Toasts", self);
            m_root.anchorMin = new Vector2(1f, 0f);
            m_root.anchorMax = new Vector2(1f, 0f);
            m_root.pivot = new Vector2(1f, 0f);
            m_root.anchoredPosition = new Vector2(-k_rightOffset, k_bottomOffset);
            m_root.sizeDelta = new Vector2(k_itemWidth, k_itemHeight);
        }

        /// <summary>生成一条提示（底板 + 图标 + 名字 + ×数量）</summary>
        private ToastEntry CreateToast(string itemId, int amount)
        {
            RectTransform bar = CreateRect("Toast", m_root);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 0f);
            bar.pivot = new Vector2(1f, 0f);
            bar.sizeDelta = new Vector2(k_itemWidth, k_itemHeight);

            var group = bar.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            // 底板
            Image background = bar.gameObject.AddComponent<Image>();
            background.raycastTarget = false;
            if (m_barSprite != null)
            {
                background.sprite = m_barSprite;
                background.type = m_barSprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
                background.color = Color.white;
            }
            else
            {
                background.color = s_barColor;
            }

            // 物品图标（查不到配置就用兜底图；都没有则留空）
            ItemDataSO data = InventoryManager.Instance != null ? InventoryManager.Instance.FindItem(itemId) : null;
            Sprite icon = data != null && data.Icon != null ? data.Icon : m_fallbackIcon;

            if (icon != null)
            {
                RectTransform iconRect = CreateRect("Icon", bar);
                iconRect.anchorMin = new Vector2(0f, 0.5f);
                iconRect.anchorMax = new Vector2(0f, 0.5f);
                iconRect.pivot = new Vector2(0f, 0.5f);
                iconRect.anchoredPosition = new Vector2(10f, 0f);
                iconRect.sizeDelta = new Vector2(k_iconSize, k_iconSize);

                Image iconImage = iconRect.gameObject.AddComponent<Image>();
                iconImage.sprite = icon;
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
            }

            // 名称（左对齐，图标右侧）
            string displayName = data != null ? data.DisplayName : itemId;

            RectTransform nameRect = CreateRect("Name", bar);
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(1f, 1f);
            nameRect.offsetMin = new Vector2(48f, 0f);
            nameRect.offsetMax = new Vector2(-56f, 0f);

            TextMeshProUGUI nameText = nameRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
            {
                nameText.font = TMP_Settings.defaultFontAsset;
            }
            nameText.text = displayName;
            nameText.fontSize = 17f;
            nameText.alignment = TextAlignmentOptions.MidlineLeft;
            nameText.color = s_nameColor;
            nameText.raycastTarget = false;
            nameText.overflowMode = TextOverflowModes.Ellipsis;

            // 数量（右对齐）
            RectTransform countRect = CreateRect("Count", bar);
            countRect.anchorMin = new Vector2(1f, 0f);
            countRect.anchorMax = new Vector2(1f, 1f);
            countRect.pivot = new Vector2(1f, 0.5f);
            countRect.offsetMin = new Vector2(-52f, 0f);
            countRect.offsetMax = new Vector2(-10f, 0f);

            TextMeshProUGUI countText = countRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
            {
                countText.font = TMP_Settings.defaultFontAsset;
            }
            countText.text = $"×{amount}";
            countText.fontSize = 17f;
            countText.alignment = TextAlignmentOptions.MidlineRight;
            countText.color = s_countColor;
            countText.raycastTarget = false;

            return new ToastEntry { Root = bar.gameObject, Group = group, Age = 0f };
        }

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }
    }
}
