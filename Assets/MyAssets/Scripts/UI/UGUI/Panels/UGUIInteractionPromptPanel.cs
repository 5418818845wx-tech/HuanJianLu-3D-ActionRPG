using MyGame.Interaction;
using TMPro;
using UnityEngine;

namespace MyGame.UI
{
    /// <summary>
    /// 交互提示面板（UGUI 版）：屏幕下方居中的一小条提示，如"[G] 与 药师阿禾 交谈"
    ///
    /// 架构定位：本面板是纯 UI，只订阅玩家身上 InteractionDetector 的
    /// InteractTargetChanged 事件，不做任何检测逻辑（检测在 InteractionDetector 里）
    ///
    /// 显示规则：
    /// - 有可交互目标且没有弹窗面板暂停游戏时显示，否则隐藏
    /// - 隐藏的只是 Window 子物体，面板根物体保持激活
    ///   （玩家是运行时生成的，根物体要靠 Update 轮询补绑检测器）
    ///
    /// 交互键显示：按键字符由 m_keyLabel 序列化（默认 "G"），
    /// 若以后在 InteractionDetector 里改键，这里也要同步修改
    /// </summary>
    public class UGUIInteractionPromptPanel : UGUIPanel
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("层级引用")]
        [Tooltip("提示条本体（无目标/弹窗暂停时隐藏此物体，根面板保持激活）")]
        [SerializeField] private GameObject m_window;

        [Tooltip("按键方块里的字符文本（如 G）")]
        [SerializeField] private TextMeshProUGUI m_keyText;

        [Tooltip("交互提示文本（如 与 药师阿禾 交谈）")]
        [SerializeField] private TextMeshProUGUI m_promptText;

        [Header("显示设置")]
        [Tooltip("按键方块显示的字符，需与 InteractionDetector 的交互键保持一致")]
        [SerializeField] private string m_keyLabel = "G";

        [Header("玩家查找")]
        [Tooltip("玩家 Tag（用于找到玩家身上的 InteractionDetector）")]
        [SerializeField] private string m_playerTag = "Player";

        [Tooltip("玩家尚未生成时的重试间隔（秒，用真实时间，暂停期间也能补绑）")]
        [SerializeField] private float m_detectorLookupInterval = 0.5f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>已绑定的交互检测器（null 表示玩家还没生成）</summary>
        private InteractionDetector m_detector;

        /// <summary>下一次尝试查找检测器的时间点（Time.unscaledTime）</summary>
        private float m_nextLookupTime;

        /// <summary>检测器当前锁定的交互目标（可能为 null）</summary>
        private IInteractable m_currentTarget;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（硬编码防止 Inspector 漏配）
            m_panelID = EUIPanelID.InteractionPrompt;

            // 按键字符在编辑期就固定下来
            if (m_keyText != null)
            {
                m_keyText.text = m_keyLabel;
            }
        }

        private void Update()
        {
            // 常驻面板在 OnEnter 后始终可见；不可见时不做轮询
            if (!IsVisible)
            {
                return;
            }

            // 玩家可能晚于 UI 创建（网络/刷怪场景）：按真实时间间隔轮询补绑
            if (m_detector == null && Time.unscaledTime >= m_nextLookupTime)
            {
                m_nextLookupTime = Time.unscaledTime + m_detectorLookupInterval;
                TryBindDetector();
            }

            // 每帧同步显隐：覆盖"弹窗打开暂停"和"目标失效但事件还没广播"两种情况
            RefreshVisibility();
        }

        protected override void OnDestroy()
        {
            UnbindDetector();
            base.OnDestroy();
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：默认隐藏提示条，然后尝试绑定检测器</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            if (m_keyText != null)
            {
                m_keyText.text = m_keyLabel;
            }

            SetWindowVisible(false);
            TryBindDetector();
        }

        /// <summary>面板退出显示：解绑事件（常驻面板一般不会走到这里）</summary>
        public override void OnExit()
        {
            UnbindDetector();
            base.OnExit();
        }

        // ====================================================================
        // 绑定与事件
        // ====================================================================

        /// <summary>找到玩家身上的交互检测器并订阅其目标变化事件</summary>
        private void TryBindDetector()
        {
            if (m_detector != null || string.IsNullOrEmpty(m_playerTag))
            {
                return;
            }

            GameObject player = GameObject.FindGameObjectWithTag(m_playerTag);
            if (player == null)
            {
                return;
            }

            InteractionDetector detector = player.GetComponentInChildren<InteractionDetector>();
            if (detector == null)
            {
                return;
            }

            m_detector = detector;
            m_detector.InteractTargetChanged += HandleInteractTargetChanged;

            // 订阅后立即同步一次当前目标：避免订阅时机晚于检测器首次广播
            HandleInteractTargetChanged(m_detector.CurrentTarget);
        }

        /// <summary>解除检测器订阅（可安全重复调用）</summary>
        private void UnbindDetector()
        {
            if (m_detector == null)
            {
                return;
            }

            m_detector.InteractTargetChanged -= HandleInteractTargetChanged;
            m_detector = null;
            m_currentTarget = null;
        }

        /// <summary>交互目标变化：更新提示文本并缓存目标（显隐由 Update 统一刷新）</summary>
        private void HandleInteractTargetChanged(IInteractable target)
        {
            m_currentTarget = target;

            if (target != null && target.CanInteract && m_promptText != null)
            {
                m_promptText.text = target.InteractionPrompt;
            }
        }

        // ====================================================================
        // 显隐
        // ====================================================================

        /// <summary>
        /// 按"目标有效 && 游戏未被弹窗暂停"刷新提示条显隐
        ///
        /// 暂停期间（背包/对话等弹窗打开）强制隐藏：
        /// 检测器的交互键本身也会被暂停屏蔽，提示条不应继续挂在屏幕上
        /// </summary>
        private void RefreshVisibility()
        {
            bool shouldShow = m_currentTarget != null
                           && m_currentTarget.CanInteract
                           && !UGUIManager.IsGamePaused;

            SetWindowVisible(shouldShow);
        }

        /// <summary>控制提示条本体显隐</summary>
        private void SetWindowVisible(bool visible)
        {
            if (m_window != null && m_window.activeSelf != visible)
            {
                m_window.SetActive(visible);
            }
        }
    }
}
