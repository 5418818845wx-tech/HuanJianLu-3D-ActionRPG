using MyGame.Audio;
using UnityEngine;

namespace MyGame.UI
{
    /// <summary>
    /// UGUI 面板基类：所有面板（主菜单/暂停菜单/背包 等）的统一抽象
    ///
    /// 与 UI Toolkit 版的差异：
    /// - 显隐用 SetActive 控制，而不是 VisualElement 的 display
    /// - 界面的视觉层级在 prefab / Hierarchy 里搭好（编辑器里直接看得见），代码只负责显隐与逻辑
    /// - 因此本基类不再需要「加载 UXML」这一步，面板的 RectTransform 由 prefab 决定
    ///
    /// 生命周期（由 UGUIManager 调度）：
    /// - OnEnter()   面板从隐藏→显示：可刷新数据、重置状态
    /// - OnExit()    面板从显示→隐藏：可清理状态（物体不销毁，下次显示不用重建）
    /// - OnPause()   面板被新面板压栈时调用：仍可见但不可交互
    /// - OnResume()  上层面板关闭、本面板回到栈顶时调用：恢复交互
    ///
    /// 显隐方式：隐藏时直接 SetActive(false)
    /// - 关闭的面板不渲染、不参与 UI 布局、Update 也不执行，编辑器里也不会和其他面板叠在一起
    /// - 代价：「隐藏期间仍然等待某个条件，条件满足时自己冒出来」这种写法不再可行。
    ///   这类需求应交给常驻对象转发——例如结算面板由战斗 HUD 在战斗结束时主动调用，
    ///   而不是自己开 Update 轮询（轮询代码在被停用的物体上根本不会执行）
    ///
    /// CanvasGroup 依然保留，用于「被上层弹窗压住」时关闭交互（OnPause / OnResume）
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public abstract class UGUIPanel : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("面板配置")]
        [Tooltip("面板唯一标识。子类在 Awake 中设置，或在此手动指定")]
        [SerializeField] protected EUIPanelID m_panelID = EUIPanelID.PauseMenu;

        [Header("行为开关")]
        [Tooltip("是否常驻显示（不参与显示栈，启动时自动显示，如战斗 HUD）")]
        [SerializeField] private bool m_isPersistent = false;

        [Tooltip("是否进入场景后自动显示一次")]
        [SerializeField] private bool m_showOnStart = false;

        [Tooltip("打开本面板时是否暂停游戏（Time.timeScale=0，冻结人物/敌人/刷怪/技能冷却）。" +
                 "小型常驻 HUD 请关掉；普通弹窗保持开启")]
        [SerializeField] private bool m_pauseGameWhenOpen = true;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>CanvasGroup：统一控制显隐与交互开关</summary>
        protected CanvasGroup m_canvasGroup;

        /// <summary>所属管理器引用（由 UGUIManager 在注册时注入）</summary>
        protected UGUIManager m_manager;

        // ====================================================================
        // 只读属性
        // ====================================================================

        /// <summary>面板 ID</summary>
        public EUIPanelID PanelID => m_panelID;

        /// <summary>是否常驻显示</summary>
        public bool IsPersistent => m_isPersistent;

        /// <summary>是否默认进入场景后自动显示</summary>
        public bool ShouldShowOnStart => m_showOnStart;

        /// <summary>打开本面板时是否需要暂停游戏（由 UGUIManager 读取并设置 Time.timeScale）</summary>
        public bool PauseGameWhenOpen => m_pauseGameWhenOpen;

        /// <summary>当前是否可见</summary>
        public bool IsVisible { get; private set; }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected virtual void Awake()
        {
            // RequireComponent 保证本组件一定存在
            m_canvasGroup = GetComponent<CanvasGroup>();
        }

        /// <summary>
        /// 物体销毁时调用（含场景卸载）
        /// 子类可重写以解除事件订阅，避免回调访问已销毁的对象
        /// </summary>
        protected virtual void OnDestroy()
        {
            // 默认空实现，子类按需重写
        }

        // ====================================================================
        // 生命周期方法（子类按需重写）
        // ====================================================================

        /// <summary>
        /// 面板进入显示状态
        /// 默认实现：显示并可交互。子类重写时建议调用 base.OnEnter()
        /// </summary>
        public virtual void OnEnter()
        {
            SetVisible(true);

            // 弹窗开/关音：常驻面板（战斗 HUD）开局就会自动进入，不该出声；
            // 场景启动那一轮初始化也静音（否则进场景会连响好几声）
            if (!m_isPersistent && !UGUIManager.IsBootstrapping)
            {
                AudioManager.Play(ESfx.UI_Panel_Open);
            }

            // 面板刚打开时动态格子（背包/商店/技能）已经建好，重扫一次让新按钮也带上点击音
            UISoundBinder.RescanAll();
        }

        /// <summary>
        /// 面板退出显示状态（隐藏但保留物体与状态）
        /// 默认实现：隐藏并禁用交互。子类重写时建议调用 base.OnExit()
        /// </summary>
        public virtual void OnExit()
        {
            SetVisible(false);

            // 场景启动时 UGUIManager 会把所有未显示的弹窗统一走一遍 OnExit，
            // 那属于初始化而不是玩家关闭，必须静音
            if (!m_isPersistent && !UGUIManager.IsBootstrapping)
            {
                AudioManager.Play(ESfx.UI_Panel_Close);
            }
        }

        /// <summary>
        /// 面板被新面板压栈时调用（仍可见，但失去交互）
        /// 默认实现：关闭交互与射线拦截，让点击穿透到上层
        /// </summary>
        public virtual void OnPause()
        {
            if (m_canvasGroup != null)
            {
                m_canvasGroup.interactable = false;
                m_canvasGroup.blocksRaycasts = false;
            }
        }

        /// <summary>
        /// 上层面板关闭、本面板回到栈顶时调用（恢复交互）
        /// </summary>
        public virtual void OnResume()
        {
            if (m_canvasGroup != null)
            {
                m_canvasGroup.interactable = true;
                m_canvasGroup.blocksRaycasts = true;
            }
        }

        // ====================================================================
        // 内部辅助方法（仅供 UGUIManager 与子类调用）
        // ====================================================================

        /// <summary>由 UGUIManager 在注册面板时注入自身引用</summary>
        internal void InjectManager(UGUIManager manager)
        {
            m_manager = manager;
        }

        /// <summary>
        /// 设置面板显隐
        ///
        /// 显示 = 激活物体 + 打开交互；隐藏 = 停用整个物体。
        /// CanvasGroup 的交互开关同步维护，供 OnPause / OnResume 的压栈逻辑复用。
        /// </summary>
        protected void SetVisible(bool visible)
        {
            IsVisible = visible;

            // 显示前先激活物体：Awake 只在物体激活时执行，子类要靠它拿到组件引用
            if (visible && !gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }

            // 被停用的物体其 Awake 尚未执行过，这里兜底取一次引用
            if (m_canvasGroup == null)
            {
                m_canvasGroup = GetComponent<CanvasGroup>();
            }

            if (m_canvasGroup != null)
            {
                m_canvasGroup.alpha = 1f;
                m_canvasGroup.interactable = visible;
                m_canvasGroup.blocksRaycasts = visible;
            }

            // 隐藏：直接停用物体，不渲染也不消耗 Update
            if (!visible && gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }
    }
}
