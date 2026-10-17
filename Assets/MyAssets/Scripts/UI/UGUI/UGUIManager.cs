using System.Collections.Generic;
using MyGame.KeyBindings;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MyGame.UI
{
    /// <summary>
    /// UGUI 总管理器：单例，统一管理所有面板的注册、显示栈与输入路由
    ///
    /// 核心职责：
    /// 1. 维护面板注册字典（按 EUIPanelID 索引）+ 显示栈（弹窗式面板压栈/弹栈）
    /// 2. 提供对外接口：ShowPanel / HidePanel / TogglePanel / HideAllOverlay
    /// 3. 输入路由：监听 ESC/I/P/K/L/T/B 按键切换对应面板（旧版 Input 兜底）
    ///    注意刻意避开 Q/E/R/F（战斗技能键）与 C（下蹲键），避免同一按键双触发
    /// 4. 鼠标光标管理：有弹窗面板打开时解锁鼠标，全部关闭后锁回
    /// 5. 游戏暂停管理：有「打开时暂停」的弹窗面板时 Time.timeScale=0，全部关闭后恢复
    ///
    /// 生命周期规则：
    /// - 常驻面板（IsPersistent=true，如战斗 HUD）：Start 时自动 OnEnter，不参与显示栈
    /// - 弹窗式面板（IsPersistent=false）：ShowPanel 时压栈，先 OnPause 栈顶，再 OnEnter 新栈顶
    /// - HidePanel 弹栈：OnExit 当前栈顶，OnResume 新栈顶
    ///
    /// 扩展：新增面板只需在 Hierarchy 里搭好界面 + 挂一个继承 UGUIPanel 的脚本，
    /// Awake 时会被自动发现并注册，无需改动本文件
    /// </summary>
    public class UGUIManager : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("面板注册（可空，留空则自动查找场景中所有 UGUIPanel）")]
        [Tooltip("手动指定面板列表。留空时会在 Start 自动查找场景中所有 UGUIPanel")]
        [SerializeField] private List<UGUIPanel> m_manualPanels = new();

        [Header("输入路由（旧版 Input 兜底，方便测试）")]
        [Tooltip("是否启用旧版 Input 输入路由")]
        [SerializeField] private bool m_enableLegacyInput = true;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>面板注册字典：按 EUIPanelID 索引</summary>
        private readonly Dictionary<EUIPanelID, UGUIPanel> m_panels = new();

        /// <summary>显示栈：记录当前打开的弹窗式面板（栈顶为最上层）</summary>
        private readonly Stack<UGUIPanel> m_overlayStack = new();

        // ====================================================================
        // 单例访问
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 UGUIManager）</summary>
        public static UGUIManager Instance { get; private set; }

        /// <summary>
        /// 游戏当前是否因弹窗面板而暂停（静态便捷查询，任意脚本可读）
        ///
        /// 用途：战斗/移动等输入系统在 Update 里读取此值，
        /// 为 true 时屏蔽游戏输入，避免"点 UI 的点击被当成连招点击缓存起来"
        ///
        /// 注意：Time.timeScale=0 只会冻结物理/动画，不会拦截 Input 输入，
        /// 因此输入屏蔽必须由各输入脚本主动判断此标志
        /// </summary>
        public static bool IsGamePaused { get; private set; }

        /// <summary>
        /// 当前是否有 UI 正在占用鼠标光标。
        ///
        /// 两种情况为真：有弹窗面板打开，或玩家正按住快捷栏的"呼出鼠标"键。
        /// 与 IsGamePaused 的区别：本标志**不冻结时间**，只用于：
        /// 1. 维持光标解锁（对抗玩家控制器每帧的重新锁定）
        /// 2. 告诉战斗/移动/交互系统"这会儿别响应鼠标操作"，避免点 UI 时人物跟着挥刀
        /// </summary>
        public static bool WantsCursor { get; private set; }

        /// <summary>
        /// 鼠标此刻是否正悬停在 UI 上（按钮、面板等）。
        ///
        /// 为什么需要它：玩家在 UI 上点击时，同一帧的战斗/交互系统也会读到这次点击，
        /// 结果是"点一下按钮，角色顺手挥了一刀连招"。
        /// WantsCursor 只能覆盖"按住呼出键"这种主动情况，
        /// 覆盖不了"编辑器里按 Esc 释放了光标"这类被动情况，
        /// 所以这里直接用引擎的 UI 射线结果做兜底判断。
        ///
        /// 只在光标未锁定时判断：光标锁定时它被钉在屏幕正中，
        /// 悬停结果没有意义，会误伤正常游玩时的每一次点击
        /// </summary>
        public static bool IsPointerOverUI
        {
            get
            {
                if (UnityEngine.Cursor.lockState == CursorLockMode.Locked)
                {
                    return false;
                }

                return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            }
        }

        /// <summary>
        /// 是否正在做场景启动时的面板初始化（Start 里那一轮）。
        ///
        /// 用途：启动时会依次调用 OnEnter（显示开场面板）与 OnExit（关闭其余弹窗），
        /// 如果照常播音效，进场景就会"啪"地连响好几声开/关音。
        /// 面板据此在启动阶段静音，只有玩家真正点击打开/关闭时才出声
        /// </summary>
        public static bool IsBootstrapping { get; private set; }

        // ====================================================================
        // 对外只读访问
        // ====================================================================

        /// <summary>当前栈顶弹窗面板（无则 null）</summary>
        public UGUIPanel TopOverlay => m_overlayStack.Count > 0 ? m_overlayStack.Peek() : null;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            // 单例设置（场景中只允许一个 UGUIManager）
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 UGUIManager 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            // 仅注册 Inspector 手动配置的面板
            // 此时其他组件的 Awake 可能尚未执行，m_panelID 可能仍是默认值，不能在此自动发现
            RegisterManualPanels();
        }

        private void OnDisable()
        {
            // 同步静态标志，防止场景切换后输入系统仍以为处于暂停/仍在要鼠标
            IsGamePaused = false;
            WantsCursor = false;

            // 兜底恢复时间流速：防止场景切换后 timeScale 仍然卡在 0
            // （Time.timeScale 是全局状态，不会随场景卸载自动重置）
            Time.timeScale = 1f;

            // 场景切换/物体禁用时清空栈与字典，避免悬挂引用
            m_overlayStack.Clear();
            m_panels.Clear();
        }

        private void Start()
        {
            // Start 时所有组件的 Awake 已执行完毕，m_panelID 已正确设置
            RegisterAutoDiscoveredPanels();

            // 这一段是"场景初始化"，不是玩家操作：期间面板不开/关音效，
            // 否则进场景会连响好几声（开场面板的开音 + 其余弹窗的关音）
            IsBootstrapping = true;

            try
            {
                // 常驻面板自动显示，非常驻面板按 m_showOnStart 自动显示
                foreach (KeyValuePair<EUIPanelID, UGUIPanel> pair in m_panels)
                {
                    UGUIPanel panel = pair.Value;

                    if (panel.IsPersistent)
                    {
                        panel.OnEnter();
                    }
                    else if (panel.ShouldShowOnStart)
                    {
                        ShowPanel(pair.Key);
                    }
                    else
                    {
                        // 未显示的弹窗面板：统一走一遍 OnExit，确保物体处于关闭状态
                        panel.OnExit();
                    }
                }
            }
            finally
            {
                // 用 try/finally 保证异常时也能复位，否则会永久静音
                IsBootstrapping = false;
            }

            // 场景加载后统一复位鼠标状态与暂停状态
            // （主菜单面板 showOnStart 时栈非空，会正确进入暂停；游戏场景栈为空则恢复 1）
            UpdateCursorLock();
            UpdateGamePause();
        }

        private void Update()
        {
            // 光标需求与"是否启用旧版输入"无关，必须每帧同步
            SyncCursorDemand();

            if (!m_enableLegacyInput)
            {
                return;
            }

            HandleLegacyInput();
        }

        /// <summary>
        /// 同步"是否有 UI 在占用鼠标"，并据此每帧维持光标解锁。
        ///
        /// 需求来源两种：有弹窗面板打开，或玩家按住快捷栏的呼出键。
        /// 为什么要每帧写：玩家控制器在 Update 里会重新锁定光标，谁先执行不确定，
        /// 只在打开面板那一帧解锁大概率会被它立刻锁回去
        /// </summary>
        private void SyncCursorDemand()
        {
            WantsCursor = m_overlayStack.Count > 0 || UGUIQuickBarPanel.IsCursorRequested;

            if (WantsCursor)
            {
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
        }

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>
        /// 指定面板当前是否可见（常驻面板与弹窗面板都适用）。
        /// 用途：其它系统需要"这会儿玩家是不是正开着某个面板"时，不必去翻显示栈
        /// </summary>
        public bool IsPanelVisible(EUIPanelID panelID)
        {
            return m_panels.TryGetValue(panelID, out UGUIPanel panel) && panel != null && panel.IsVisible;
        }

        /// <summary>
        /// 显示指定面板（弹窗式：压栈；常驻面板：仅 OnEnter，不压栈）
        /// </summary>
        public void ShowPanel(EUIPanelID panelID)
        {
            if (!m_panels.TryGetValue(panelID, out UGUIPanel panel))
            {
                Debug.LogWarning($"[{GetType().Name}] 未注册面板：{panelID}", this);
                return;
            }

            // 常驻面板：直接显示（不压栈，不互相影响）
            if (panel.IsPersistent)
            {
                if (!panel.IsVisible)
                {
                    panel.OnEnter();
                }

                return;
            }

            // 弹窗式：若已在栈顶，幂等返回
            if (m_overlayStack.Count > 0 && m_overlayStack.Peek() == panel)
            {
                return;
            }

            // 暂停当前栈顶（让其失去交互）
            if (m_overlayStack.Count > 0)
            {
                m_overlayStack.Peek().OnPause();
            }

            // 进入新面板并压栈
            panel.OnEnter();
            m_overlayStack.Push(panel);

            // 弹窗打开：解锁鼠标让玩家能点击 UI
            UpdateCursorLock();

            // 弹窗打开：按面板配置暂停游戏（Time.timeScale=0）
            UpdateGamePause();
        }

        /// <summary>
        /// 隐藏指定面板（弹窗式：从栈顶弹出）
        /// 注意：当前实现只允许关闭栈顶面板，与 ESC 行为一致
        /// </summary>
        public void HidePanel(EUIPanelID panelID)
        {
            if (!m_panels.TryGetValue(panelID, out UGUIPanel panel))
            {
                return;
            }

            // 常驻面板：仅隐藏，不操作栈
            if (panel.IsPersistent)
            {
                if (panel.IsVisible)
                {
                    panel.OnExit();
                }

                return;
            }

            // 弹窗式：必须是栈顶才能关闭
            if (m_overlayStack.Count == 0 || m_overlayStack.Peek() != panel)
            {
                Debug.LogWarning($"[{GetType().Name}] 面板 {panelID} 不在栈顶，无法直接关闭", this);
                return;
            }

            m_overlayStack.Pop();
            panel.OnExit();

            // 新栈顶恢复交互
            if (m_overlayStack.Count > 0)
            {
                m_overlayStack.Peek().OnResume();
            }

            // 弹窗栈空了：锁回鼠标
            UpdateCursorLock();

            // 面板关闭：按剩余栈状态恢复或继续暂停
            UpdateGamePause();
        }

        /// <summary>
        /// 切换面板：若栈顶是该面板则关闭，否则打开
        /// </summary>
        public void TogglePanel(EUIPanelID panelID)
        {
            if (!m_panels.TryGetValue(panelID, out UGUIPanel panel))
            {
                return;
            }

            if (panel.IsPersistent)
            {
                if (panel.IsVisible)
                {
                    HidePanel(panelID);
                }
                else
                {
                    ShowPanel(panelID);
                }

                return;
            }

            // 弹窗式：栈顶 == 自己 → 关闭；否则打开
            if (m_overlayStack.Count > 0 && m_overlayStack.Peek() == panel)
            {
                HidePanel(panelID);
            }
            else
            {
                ShowPanel(panelID);
            }
        }

        /// <summary>
        /// 关闭所有弹窗式面板（常驻面板保留）
        /// 用于战斗结算、场景切换等场景
        /// </summary>
        public void HideAllOverlay()
        {
            while (m_overlayStack.Count > 0)
            {
                UGUIPanel panel = m_overlayStack.Pop();
                panel.OnExit();
            }

            // 栈清空，锁回鼠标
            UpdateCursorLock();

            // 所有弹窗关闭：恢复游戏运行
            UpdateGamePause();
        }

        /// <summary>
        /// 获取指定面板实例（外部系统可拿到面板实例调用其公开方法）
        /// </summary>
        public T GetPanel<T>(EUIPanelID panelID) where T : UGUIPanel
        {
            return m_panels.TryGetValue(panelID, out UGUIPanel panel) ? panel as T : null;
        }

        // ====================================================================
        // 面板注册
        // ====================================================================

        /// <summary>
        /// 注册 Inspector 手动配置的面板（在 OnEnable 中调用）
        /// </summary>
        private void RegisterManualPanels()
        {
            if (m_manualPanels == null || m_manualPanels.Count == 0)
            {
                return;
            }

            foreach (UGUIPanel panel in m_manualPanels)
            {
                RegisterPanel(panel);
            }
        }

        /// <summary>
        /// 自动发现并注册场景中所有 UGUIPanel（在 Start 中调用，确保所有 Awake 已执行）
        /// 此时面板的 m_panelID 已被各自的 Awake 正确设置，可安全按 ID 注册
        ///
        /// 注意 FindObjectsInactive.Include：弹窗面板默认是关闭（inactive）的，
        /// 不带这个参数会漏掉全部关闭状态的面板，导致按键打不开
        /// </summary>
        private void RegisterAutoDiscoveredPanels()
        {
            UGUIPanel[] allPanels = FindObjectsByType<UGUIPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (UGUIPanel panel in allPanels)
            {
                if (!m_panels.ContainsKey(panel.PanelID))
                {
                    RegisterPanel(panel);
                }
            }
        }

        /// <summary>
        /// 注册单个面板：注入管理器引用并加入字典
        /// </summary>
        private void RegisterPanel(UGUIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            if (m_panels.ContainsKey(panel.PanelID))
            {
                Debug.LogWarning($"[{GetType().Name}] 面板 ID {panel.PanelID} 重复注册，已忽略后续", this);
                return;
            }

            m_panels[panel.PanelID] = panel;
            panel.InjectManager(this);
        }

        // ====================================================================
        // 鼠标光标管理
        // ====================================================================

        /// <summary>
        /// 根据当前显示栈状态同步鼠标光标
        /// - 栈非空（有弹窗面板）：解锁鼠标 + 显示光标，玩家可点击 UI
        /// - 栈为空：锁回鼠标 + 隐藏光标，恢复视角控制
        ///
        /// 注意：玩家控制器可能在 Update 里重新锁鼠标，
        /// 因此除了这里，Update 中也会在有弹窗时持续解锁
        /// </summary>
        private void UpdateCursorLock()
        {
            bool hasOverlay = m_overlayStack.Count > 0;

            if (hasOverlay)
            {
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
            else
            {
                UnityEngine.Cursor.lockState = CursorLockMode.Locked;
                UnityEngine.Cursor.visible = false;
            }
        }

        // ====================================================================
        // 游戏暂停管理（Time.timeScale）
        // ====================================================================

        /// <summary>
        /// 根据当前弹窗栈同步游戏暂停状态
        ///
        /// 规则：栈中只要有任意一个「打开时暂停游戏」的面板（PauseGameWhenOpen=true），
        /// 就把 Time.timeScale 置为 0；全部关闭后恢复为 1。
        ///
        /// timeScale=0 时自动冻结的系统（本项目均已核对）：
        /// - 人物移动：KinematicCharacterSystem 在 FixedUpdate 里驱动电机，
        ///   timeScale=0 时 Unity 不再调用 FixedUpdate，人物彻底停住
        /// - 物理模拟：FixedUpdate 停止，敌人/物理体都不动
        /// - 敌人 AI：NavMeshAgent 与脚本里的 Time.deltaTime 计时同步冻结
        /// - 刷怪波次：CombatWaveManager 用 Time.time 计时，timeScale=0 时 Time.time 不增长
        /// - 技能冷却/蓄力/切武器：均使用 Time.deltaTime，自动暂停
        /// - 普通 Animator：默认 Normal 更新模式，随 timeScale 暂停
        ///
        /// 不受影响（UI 仍可正常操作）：
        /// - 本管理器的 Update 照常执行，ESC/I/C 等开关面板按键仍然有效
        /// - UGUI 按钮点击/输入基于真实时间，弹窗里的按钮照常点
        ///
        /// 注意：若以后有需要在暂停期间继续播放的 UI 动画，
        /// 应使用 Time.unscaledDeltaTime 驱动，或把 Animator 的 Update Mode 设为 Unscaled Time
        /// </summary>
        private void UpdateGamePause()
        {
            bool shouldPause = false;

            // 遍历弹窗栈：任意一个面板要求暂停，游戏就暂停
            foreach (UGUIPanel panel in m_overlayStack)
            {
                if (panel.PauseGameWhenOpen)
                {
                    shouldPause = true;
                    break;
                }
            }

            Time.timeScale = shouldPause ? 0f : 1f;

            // 同步静态标志：战斗/移动等输入系统每帧读它来决定是否屏蔽输入
            IsGamePaused = shouldPause;
        }

        // ====================================================================
        // 输入路由（旧版 Input 兜底）
        // ====================================================================

        /// <summary>
        /// 输入路由：响应暂停/背包/角色/技能/任务日志等按键切换面板。
        /// 键位来自 KeyBindingManager（可在设置面板自定义），默认 ESC/I/P/K/L
        /// </summary>
        private void HandleLegacyInput()
        {
            // 正在改键时屏蔽面板快捷键：否则"把背包改成 K"会顺手把技能面板打开
            if (KeyBindingManager.IsCapturing)
            {
                return;
            }

            // 面板快捷键统一走 KeyBindingManager，玩家可以在设置面板里改
            if (KeyBindingManager.IsDown(EGameKey.Pause))
            {
                ToggleOverlayOrPause();
            }

            else if (KeyBindingManager.IsDown(EGameKey.Inventory))
            {
                TogglePanel(EUIPanelID.Inventory);
            }

            // 角色属性默认 P：刻意避开 C——C 是下蹲键，同帧会同时下蹲并弹出角色面板
            else if (KeyBindingManager.IsDown(EGameKey.Character))
            {
                TogglePanel(EUIPanelID.Character);
            }

            else if (KeyBindingManager.IsDown(EGameKey.SkillPanel))
            {
                TogglePanel(EUIPanelID.Skill);
            }

            // 任务日志默认 L：刻意避开 Q——Q/E/R/F 是战斗技能键，
            // 同帧会同时触发技能和开面板，表现为"按 Q 想开面板却打出跑攻连招"
            else if (KeyBindingManager.IsDown(EGameKey.QuestLog))
            {
                TogglePanel(EUIPanelID.QuestLog);
            }

            // T：对话（正式流程由 NPC 交互触发，这里给个测试键便于独立预览，不参与改键）
            else if (Input.GetKeyDown(KeyCode.T))
            {
                TogglePanel(EUIPanelID.Dialogue);
            }

            // B：商店（正式流程由商人交互触发，这里给个测试键便于独立预览，不参与改键）
            else if (Input.GetKeyDown(KeyCode.B))
            {
                TogglePanel(EUIPanelID.Shop);
            }
        }

        /// <summary>
        /// ESC 行为：
        /// - 栈顶是主菜单（根界面，无下层可回退）时忽略，避免把主菜单关掉后无法恢复
        /// - 栈顶是其他弹窗面板时关闭栈顶
        /// - 栈为空时打开暂停菜单（若已注册）
        /// </summary>
        private void ToggleOverlayOrPause()
        {
            if (m_overlayStack.Count > 0)
            {
                UGUIPanel top = m_overlayStack.Peek();

                // 主菜单是根界面，ESC 不关闭它
                if (top.PanelID == EUIPanelID.MainMenu)
                {
                    return;
                }

                HidePanel(top.PanelID);
                return;
            }

            // 栈为空：打开暂停菜单（未注册时静默忽略）
            if (m_panels.ContainsKey(EUIPanelID.PauseMenu))
            {
                ShowPanel(EUIPanelID.PauseMenu);
            }
        }
    }
}
