using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MyGame.Audio
{
    /// <summary>
    /// UI 音效绑定器：给一个 Canvas 下所有按钮自动挂上「点击音 / 悬停音」
    ///
    /// 为什么用自动扫描而不是逐个按钮拖事件：
    /// 场景里有几十个按钮（含各面板），逐个绑定既费时又容易漏；
    /// 而"所有按钮的点击音都一样"本来就是正确行为，没有逐个定制的必要。
    ///
    /// 关于动态生成的按钮：
    /// 背包格子、商店行、技能卡都是运行时从预制体实例化的，
    /// 启动时扫描不到。因此本组件提供 Rescan()，
    /// 由 UGUIPanel.OnEnter 在每次面板打开时调用一次（此时格子已建好）
    ///
    /// 挂载位置：UI 根节点（UICanvas）
    /// </summary>
    [DisallowMultipleComponent]
    public class UISoundBinder : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("是否绑定按钮点击音")]
        [SerializeField] private bool m_bindClick = true;

        [Tooltip("是否绑定鼠标悬停音（悬停音很密集，建议保持较低音量）")]
        [SerializeField] private bool m_bindHover = true;

        [Tooltip("悬停音音量缩放")]
        [SerializeField, Range(0f, 1f)] private float m_hoverVolumeScale = 0.4f;

        // ====================================================================
        // 静态便捷入口
        // ====================================================================

        /// <summary>
        /// 让场景中所有绑定器重新扫描一次按钮。
        /// 面板打开（动态格子刚建好）时调用最合适
        /// </summary>
        public static void RescanAll()
        {
            foreach (UISoundBinder binder in FindObjectsByType<UISoundBinder>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                binder.Rescan();
            }
        }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Start()
        {
            Rescan();
        }

        // ====================================================================
        // 扫描与绑定
        // ====================================================================

        /// <summary>扫描自身及子物体下的全部按钮并绑定（可重复调用，不会重复绑定）</summary>
        public void Rescan()
        {
            foreach (Button button in GetComponentsInChildren<Button>(true))
            {
                if (m_bindClick)
                {
                    // 先移除再添加：重复 Rescan 时不会累积多个监听器导致一次点击播两遍
                    button.onClick.RemoveListener(HandleClick);
                    button.onClick.AddListener(HandleClick);
                }

                if (m_bindHover)
                {
                    BindHover(button.gameObject);
                }
            }
        }

        /// <summary>给按钮挂上"鼠标进入"事件（同一物体只挂一次）</summary>
        private void BindHover(GameObject target)
        {
            EventTrigger trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = target.AddComponent<EventTrigger>();
            }

            // 已经有 PointerEnter 条目就不再添加，避免重复播两遍
            foreach (EventTrigger.Entry entry in trigger.triggers)
            {
                if (entry.eventID == EventTriggerType.PointerEnter)
                {
                    return;
                }
            }

            EventTrigger.Entry hoverEntry = new EventTrigger.Entry
            {
                eventID = EventTriggerType.PointerEnter,
            };
            hoverEntry.callback.AddListener(HandlePointerEnter);
            trigger.triggers.Add(hoverEntry);
        }

        // ====================================================================
        // 回调
        // ====================================================================

        private void HandleClick()
        {
            AudioManager.Play(ESfx.UI_Click);
        }

        private void HandlePointerEnter(BaseEventData eventData)
        {
            AudioManager.Play(ESfx.UI_Hover, m_hoverVolumeScale);
        }
    }
}
