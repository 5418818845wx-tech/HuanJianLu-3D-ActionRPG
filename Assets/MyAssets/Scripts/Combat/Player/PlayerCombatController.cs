using UnityEngine;
using KinematicCharacterController.Examples;
using MyGame.KeyBindings;
using MyGame.UI;

namespace MyGame.Combat
{
    /// <summary>
    /// 玩家战斗控制器：输入入口与路由分发
    /// 核心职责：
    /// 1. 读取所有战斗相关输入：鼠标左键（普攻/蓄力）、Q/E/R/F（技能）、Tab（武器切换）
    /// 2. 将输入路由给 Fighter（战斗大脑）统一调度，避免子系统间状态冲突
    /// 3. 区分"短按（普攻）"与"长按（蓄力）"：通过点击阈值判定
    /// 4. 与 ExampleCharacterController 集成：闪避时通知 Fighter 取消战斗动作
    ///
    /// 输入判定逻辑：
    /// - 按下左键瞬间：记录按下时间，标记"可能蓄力"
    /// - 按住超过 m_tapThreshold 秒：判定为蓄力，调用 Fighter.TryStartCharge()
    /// - 在阈值内松开：判定为点击，调用 Fighter.TryStartCombo()
    /// - 蓄力中松开：调用 Fighter.ReleaseCharge() 释放重击
    /// </summary>
    public class PlayerCombatController : MonoBehaviour
    {
        [Header("战斗大脑引用")]
        [Tooltip("战斗大脑（统一调度所有战斗动作）")]
        [SerializeField] private Fighter m_fighter;

        [Header("角色移动控制器")]
        [Tooltip("角色移动控制器（用于判断是否移动中以触发跑攻）")]
        [SerializeField] private ExampleCharacterController m_characterController;

        [Header("输入判定设置")]
        [Tooltip("区分点击与蓄力的时间阈值（秒）。按住超过此时间则进入蓄力")]
        [SerializeField] private float m_tapThreshold = 0.35f;

        /// <summary>左键按下的时间点（Time.time）</summary>
        private float m_mouseDownTime = 0f;

        /// <summary>左键当前是否处于按住状态</summary>
        private bool m_isMouseHeld = false;

        /// <summary>是否已触发蓄力（用于区分"松开时是点击还是释放重击"）</summary>
        private bool m_chargeTriggered = false;

        /// <summary>
        /// 上一帧是否处于 UI 暂停状态（用于检测"刚进入暂停"的上升沿）
        /// 只在进入暂停的那一帧做一次输入残留清理，暂停期间每帧直接跳过输入处理
        /// </summary>
        private bool m_wasPausedLastFrame = false;

        private void Awake()
        {
            // 自动查找 Fighter
            if (m_fighter == null)
            {
                m_fighter = GetComponent<Fighter>();
            }

            // 自动查找角色移动控制器
            if (m_characterController == null)
            {
                m_characterController = GetComponent<ExampleCharacterController>();
            }
        }

        private void Update()
        {
            // 战斗大脑未配置则不处理输入
            if (m_fighter == null) return;

            // ===== UI 弹窗暂停期间：屏蔽全部战斗输入 =====
            // Time.timeScale=0 只会冻结物理和动画，Input 输入检测仍然每帧执行，
            // 玩家在 UI 上的点击如果不拦截，会被连招缓冲记录，关闭 UI 后变成自动挥刀
            //
            // WantsCursor：玩家按住快捷栏的呼出键时也一并屏蔽——
            // 那会儿鼠标是用来点面板入口按钮的，不该被当成攻击点击
            // IsPointerOverUI：兜底。鼠标只要悬停在 UI 上（快捷栏按钮、面板等），
            // 这次点击就属于 UI，不能算攻击——否则"点一下按钮，角色顺手挥一刀"
            if (UGUIManager.IsGamePaused || UGUIManager.WantsCursor || UGUIManager.IsPointerOverUI)
            {
                // 只在"刚进入暂停"的那一帧清理一次：丢弃按住状态、蓄力状态和输入缓冲
                // 避免关闭 UI 瞬间的鼠标抬起事件被误判成一次攻击点击
                if (!m_wasPausedLastFrame)
                {
                    m_isMouseHeld = false;
                    m_chargeTriggered = false;
                    m_fighter.DiscardBufferedInputs();
                    m_wasPausedLastFrame = true;
                }

                // 暂停期间不读取任何战斗输入（攻击/技能/切武器全部忽略）
                return;
            }

            m_wasPausedLastFrame = false;

            HandleAttackInput();
            HandleSkillInput();
            HandleWeaponSwitchInput();
        }

        /// <summary>
        /// 每帧同步「移动锁」：蓄力 / 重击释放期间角色必须定身（与原神一致）
        ///
        /// 放在 LateUpdate：
        /// Fighter 在 Update 里根据各子系统推导本帧状态，LateUpdate 时状态已确定，
        /// 写入的移动锁会在下一帧的移动输入处理中生效（相差 1 帧，感知不到）。
        /// </summary>
        private void LateUpdate()
        {
            if (m_characterController == null || m_fighter == null) return;

            ECombatState state = m_fighter.CurrentState;

            // 所有攻击类动作期间锁住移动（连招 / 蓄力 / 重击释放 / 技能包括跑攻），
            // 避免"边打边滑步"。武器切换不锁，那只是换武器的过场动作
            m_characterController.MovementLocked =
                state == ECombatState.NormalAttacking ||
                state == ECombatState.Charging ||
                state == ECombatState.ChargeReleasing ||
                state == ECombatState.SkillCasting;
        }

        // ====================================================================
        // 攻击输入处理：鼠标左键（普攻 / 蓄力）
        // ====================================================================

        /// <summary>
        /// 处理攻击输入：检测攻击键的按下、按住、松开
        /// 键位走 KeyBindingManager（默认鼠标左键），所以玩家可以自己改
        /// </summary>
        private void HandleAttackInput()
        {
            // ===== 1. 检测攻击键按下（本帧） =====
            if (KeyBindingManager.IsDown(EGameKey.Attack))
            {
                m_mouseDownTime = Time.time;
                m_isMouseHeld = true;
                m_chargeTriggered = false;
            }

            // ===== 2. 检测左键按住中（判断是否超过阈值进入蓄力） =====
            // 连招/跑攻进行中时跳过蓄力检测，避免吞掉连招点击
            if (m_isMouseHeld && !m_chargeTriggered)
            {
                ECombatState state = m_fighter.CurrentState;
                bool inCombatAction = state == ECombatState.NormalAttacking
                                   || state == ECombatState.SkillCasting;

                if (!inCombatAction)
                {
                    float holdDuration = Time.time - m_mouseDownTime;

                    // 按住时间超过阈值：判定为蓄力
                    if (holdDuration >= m_tapThreshold)
                    {
                        // 尝试开始蓄力（Fighter 会检查当前状态是否允许）
                        if (m_fighter.TryStartCharge())
                        {
                            m_chargeTriggered = true;
                        }
                        // 失败时不设 m_chargeTriggered，下一帧继续尝试
                    }
                }
            }

            // ===== 3. 检测攻击键松开（本帧） =====
            if (KeyBindingManager.IsUp(EGameKey.Attack) && m_isMouseHeld)
            {
                m_isMouseHeld = false;

                if (m_chargeTriggered)
                {
                    // 已触发蓄力：松开即释放重击
                    // 注意：如果当时因状态不允许未能真正进入蓄力，ReleaseCharge 内部会安全处理
                    m_fighter.ReleaseCharge();
                    m_chargeTriggered = false;
                }
                else
                {
                    // 未触发蓄力：本次是短按点击
                    // 普攻连招进行中 → 继续普攻衔接（不因移动切换到跑攻）
                    // 跑攻进行中 → 继续跑攻衔接（不因停下来切换到普攻）
                    // 空闲且移动中 → 跑攻（两段衔接）
                    // 空闲且静止 → 普攻连招第1段
                    ECombatState combatState = m_fighter.CurrentState;
                    if (combatState == ECombatState.NormalAttacking)
                    {
                        m_fighter.TryStartCombo();
                    }
                    else if (combatState == ECombatState.SkillCasting)
                    {
                        m_fighter.TryRunAttack();
                    }
                    else if (m_characterController != null && m_characterController.IsMoving)
                    {
                        m_fighter.TryRunAttack();
                    }
                    else
                    {
                        m_fighter.TryStartCombo();
                    }
                }
            }
        }

        // ====================================================================
        // 技能输入处理：Q / E / R / F
        // ====================================================================

        /// <summary>
        /// 处理技能输入：检测 Q/E/R/F 按键按下，路由给 Fighter 尝试释放技能
        /// </summary>
        private void HandleSkillInput()
        {
            // 四个技能槽各对应一个可自定义的键位（默认 Q/E/R/F）
            if (KeyBindingManager.IsDown(EGameKey.Skill_Q))
            {
                m_fighter.TryCastSkill(ESkillSlot.Slot_Q);
                return;
            }

            if (KeyBindingManager.IsDown(EGameKey.Skill_E))
            {
                m_fighter.TryCastSkill(ESkillSlot.Slot_E);
                return;
            }

            if (KeyBindingManager.IsDown(EGameKey.Skill_R))
            {
                m_fighter.TryCastSkill(ESkillSlot.Slot_R);
                return;
            }

            if (KeyBindingManager.IsDown(EGameKey.Skill_F))
            {
                m_fighter.TryCastSkill(ESkillSlot.Slot_F);
                return;
            }
        }

        // ====================================================================
        // 武器切换输入处理：Tab
        // ====================================================================

        /// <summary>
        /// 处理武器切换输入：检测 Tab 按键按下，路由给 Fighter 尝试切换武器
        /// </summary>
        private void HandleWeaponSwitchInput()
        {
            if (KeyBindingManager.IsDown(EGameKey.SwitchWeapon))
            {
                m_fighter.TrySwitchWeapon();
            }
        }

        // ====================================================================
        // 外部接口：供 ExampleCharacterController 调用
        // ====================================================================

        /// <summary>
        /// 闪避时调用：由 ExampleCharacterController 在按下闪避键（Left Shift）时调用
        /// 通过 Fighter 判断当前动作是否可被闪避取消，再执行中断
        /// </summary>
        /// <returns>true=闪避成功取消战斗动作；false=当前动作无法被取消（如武器切换中）</returns>
        public bool OnDodgeRequested()
        {
            if (m_fighter == null) return true;
            return m_fighter.TryDodgeCancel();
        }

        /// <summary>
        /// 获取当前战斗状态（外部查询用，如 UI 显示、移动控制器减速判断）
        /// </summary>
        public ECombatState CurrentCombatState => m_fighter != null ? m_fighter.CurrentState : ECombatState.Idle;

        /// <summary>
        /// 获取战斗中移动速度倍率（外部 CharacterController 在战斗时调用）
        /// </summary>
        public float CombatMoveSpeedMultiplier => m_fighter != null ? m_fighter.CombatMoveSpeedMultiplier : 1f;
    }
}
