using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 战斗大脑：统一调度所有战斗子系统的状态机
    /// 核心职责：
    /// 1. 维护 ECombatState 战斗状态（Idle/NormalAttacking/Charging/SkillCasting/WeaponSwitching）
    /// 2. 提供动作取消窗口查询：闪避/受击通过 CanCancelNow 判断是否可打断
    /// 3. 提供 InterruptCombat() 一键取消所有战斗动作（闪避时调用）
    /// 4. 与 ExampleCharacterController 集成：战斗时通知移动减速
    /// </summary>
    public class Fighter : MonoBehaviour
    {
        [Header("子系统引用")]
        [Tooltip("连招系统")]
        [SerializeField] private ComboSystem m_comboSystem;

        [Tooltip("蓄力系统")]
        [SerializeField] private ChargeAttackSystem m_chargeSystem;

        [Tooltip("技能系统")]
        [SerializeField] private SkillSystem m_skillSystem;

        [Tooltip("武器系统")]
        [SerializeField] private WeaponSystem m_weaponSystem;

        [Header("移动减速")]
        [Tooltip("战斗中移动速度倍率（0~1，1=不减速）")]
        [SerializeField, Range(0f, 1f)] private float m_combatMoveSpeedMultiplier = 0.5f;

        [Tooltip("战斗状态变更事件（参数：新状态）")]
        [SerializeField] private bool m_logStateChanges = false;

        /// <summary>当前战斗状态</summary>
        private ECombatState m_currentState = ECombatState.Idle;

        /// <summary>当前战斗状态（只读属性，外部查询用）</summary>
        public ECombatState CurrentState => m_currentState;

        /// <summary>是否处于战斗中（非 Idle 状态）</summary>
        public bool IsInCombat => m_currentState != ECombatState.Idle;

        /// <summary>战斗中移动速度倍率（外部 CharacterController 读取）</summary>
        public float CombatMoveSpeedMultiplier => IsInCombat ? m_combatMoveSpeedMultiplier : 1f;

        /// <summary>
        /// 当前是否可取消战斗动作
        /// 规则：
        /// - Idle 状态：本身无战斗动作，无需取消
        /// - NormalAttacking：命中帧后可取消（原神式手感）
        /// - Charging：随时可取消（松开即释放）
        /// - ChargeReleasing：命中帧后可取消
        /// - SkillCasting：命中帧后可取消
        /// - WeaponSwitching：不可取消（必须等切换完成）
        /// </summary>
        public bool CanCancelNow
        {
            get
            {
                switch (m_currentState)
                {
                    case ECombatState.Idle:
                        return true; // 空闲无动作，等于"可接受新动作"

                    case ECombatState.NormalAttacking:
                        // 普攻命中帧后可取消
                        return m_comboSystem != null && m_comboSystem.CanBeCanceled;

                    case ECombatState.Charging:
                        // 蓄力中随时可取消（松开即释放或被打断）
                        return true;

                    case ECombatState.ChargeReleasing:
                        // 重击释放命中帧后可取消
                        return m_chargeSystem != null && m_chargeSystem.CanBeCanceled;

                    case ECombatState.SkillCasting:
                        // 技能命中帧后可取消
                        return m_skillSystem != null && m_skillSystem.CanBeCanceled;

                    case ECombatState.WeaponSwitching:
                        // 武器切换不可中断，必须等切换完成
                        return false;

                    default:
                        return true;
                }
            }
        }

        private void Awake()
        {
            // 自动查找子系统
            if (m_comboSystem == null) m_comboSystem = GetComponent<ComboSystem>();
            if (m_chargeSystem == null) m_chargeSystem = GetComponent<ChargeAttackSystem>();
            if (m_skillSystem == null) m_skillSystem = GetComponent<SkillSystem>();
            if (m_weaponSystem == null) m_weaponSystem = GetComponent<WeaponSystem>();
        }

        private void Update()
        {
            // 同步状态机：根据各子系统的当前状态推导 Fighter 状态
            SyncState();
        }

        /// <summary>
        /// 同步战斗状态：根据各子系统的当前状态推导 Fighter 状态
        /// 状态优先级：武器切换 > 技能释放 > 蓄力释放 > 蓄力中 > 普攻中 > 空闲
        /// </summary>
        private void SyncState()
        {
            ECombatState newState;

            if (m_weaponSystem != null && m_weaponSystem.IsSwitching)
            {
                newState = ECombatState.WeaponSwitching;
            }
            else if (m_skillSystem != null && m_skillSystem.IsCasting)
            {
                newState = ECombatState.SkillCasting;
            }
            else if (m_chargeSystem != null && m_chargeSystem.IsActive)
            {
                // 区分蓄力中 vs 释放中
                newState = m_chargeSystem.IsCharging
                    ? ECombatState.Charging
                    : ECombatState.ChargeReleasing;
            }
            else if (m_comboSystem != null && m_comboSystem.IsAttacking)
            {
                newState = ECombatState.NormalAttacking;
            }
            else
            {
                newState = ECombatState.Idle;
            }

            // 状态发生变化时记录日志（可选）
            if (newState != m_currentState)
            {
                ECombatState oldState = m_currentState;
                m_currentState = newState;

                if (m_logStateChanges)
                {
                    Debug.Log($"[Fighter] 状态切换：{oldState} → {newState}", this);
                }
            }
        }

        // ==================== 战斗动作入口（由 PlayerCombatController 调用） ====================

        /// <summary>
        /// 尝试开始普攻连招
        /// 检查条件：不在武器切换中、不在技能释放中
        /// </summary>
        /// <returns>true=成功开始；false=被其他动作占用</returns>
        public bool TryStartCombo()
        {
            // 武器切换中不接受普攻输入
            if (m_weaponSystem != null && m_weaponSystem.IsSwitching) return false;
            // 技能释放中不接受普攻输入
            if (m_skillSystem != null && m_skillSystem.IsCasting) return false;
            // 蓄力释放中不接受普攻输入（避免重击未完时插入普攻）
            if (m_chargeSystem != null && m_chargeSystem.IsActive && !m_chargeSystem.IsCharging) return false;

            return m_comboSystem != null && m_comboSystem.TryStartCombo();
        }

        /// <summary>
        /// 尝试开始蓄力
        /// 检查条件：不在武器切换中、不在技能释放中、不在普攻中
        /// </summary>
        /// <returns>true=成功开始；false=被其他动作占用</returns>
        public bool TryStartCharge()
        {
            // 武器切换中不接受蓄力输入
            if (m_weaponSystem != null && m_weaponSystem.IsSwitching) return false;
            // 技能释放中不接受蓄力输入
            if (m_skillSystem != null && m_skillSystem.IsCasting) return false;
            // 普攻中不接受蓄力输入（避免攻击中插入蓄力）
            if (m_comboSystem != null && m_comboSystem.IsAttacking) return false;

            return m_chargeSystem != null && m_chargeSystem.StartCharging();
        }

        /// <summary>
        /// 释放重击：松开左键时调用（仅蓄力中有效）
        /// </summary>
        public void ReleaseCharge()
        {
            if (m_chargeSystem != null && m_chargeSystem.IsCharging)
            {
                m_chargeSystem.ReleaseCharge();
            }
        }

        /// <summary>
        /// 尝试释放技能
        /// 检查条件：不在武器切换中、不在蓄力中、不在普攻中
        /// </summary>
        /// <param name="slot">技能槽位</param>
        /// <returns>true=成功释放；false=被其他动作占用或无法释放</returns>
        public bool TryCastSkill(ESkillSlot slot)
        {
            // 武器切换中不接受技能输入
            if (m_weaponSystem != null && m_weaponSystem.IsSwitching) return false;
            // 蓄力中不接受技能输入
            if (m_chargeSystem != null && m_chargeSystem.IsActive) return false;
            // 普攻中不接受技能输入（除非已过命中帧，可取消）
            if (m_comboSystem != null && m_comboSystem.IsAttacking && !m_comboSystem.CanBeCanceled) return false;

            return m_skillSystem != null && m_skillSystem.TryCastSkill(slot);
        }

        /// <summary>
        /// 尝试开始或衔接跑攻：移动中按攻击键时调用
        /// 检查条件：不在武器切换中、不在蓄力中、不在普攻中
        /// </summary>
        /// <returns>true=成功开始或缓冲；false=被其他动作占用</returns>
        public bool TryRunAttack()
        {
            // 武器切换中不接受输入
            if (m_weaponSystem != null && m_weaponSystem.IsSwitching) return false;
            // 蓄力中不接受输入
            if (m_chargeSystem != null && m_chargeSystem.IsActive) return false;
            // 普攻中不接受输入（除非已过命中帧，可取消）
            if (m_comboSystem != null && m_comboSystem.IsAttacking && !m_comboSystem.CanBeCanceled) return false;

            return m_skillSystem != null && m_skillSystem.TryRunAttack();
        }

        /// <summary>
        /// 尝试切换武器
        /// 检查条件：不在任何战斗动作中
        /// </summary>
        /// <returns>true=开始切换；false=被其他动作占用</returns>
        public bool TrySwitchWeapon()
        {
            // 任何战斗动作中不接受武器切换
            if (m_comboSystem != null && m_comboSystem.IsAttacking) return false;
            if (m_chargeSystem != null && m_chargeSystem.IsActive) return false;
            if (m_skillSystem != null && m_skillSystem.IsCasting) return false;

            return m_weaponSystem != null && m_weaponSystem.TrySwitchWeapon();
        }

        // ==================== UI 暂停时的输入清理 ====================

        /// <summary>
        /// 丢弃所有"等待中"的战斗输入：由 PlayerCombatController 在打开 UI 暂停瞬间调用
        ///
        /// 与 InterruptCombat() 的区别：
        /// - 本方法只清输入缓冲，不打断已经挥出去的攻击/技能（关闭 UI 后当前段正常播完）
        /// - InterruptCombat() 会直接中断所有动作（闪避/受击用）
        ///
        /// 清理内容：
        /// 1. 连招下一段的点击缓冲
        /// 2. 跑攻第2段的衔接缓冲
        /// 3. 进行中的蓄力：安静取消（暂停期间鼠标松开事件收不到，不取消会卡死在蓄力状态）
        /// </summary>
        public void DiscardBufferedInputs()
        {
            if (m_comboSystem != null)
            {
                m_comboSystem.ClearBufferedInput();
            }

            if (m_skillSystem != null)
            {
                m_skillSystem.ClearBufferedInput();
            }

            // 蓄力中直接取消：暂停后玩家的"松鼠标"动作发生在 UI 上，
            // 战斗输入被屏蔽收不到 ReleaseCharge，不取消会一直保持蓄力姿势
            if (m_chargeSystem != null && m_chargeSystem.IsCharging)
            {
                m_chargeSystem.CancelCharge();
            }
        }

        // ==================== 中断与取消 ====================

        /// <summary>
        /// 中断所有战斗动作：由闪避/受击系统调用
        /// 取消连招、蓄力、技能、武器切换，回到 Idle 状态
        /// </summary>
        public void InterruptCombat()
        {
            // 中断普攻连招
            if (m_comboSystem != null)
            {
                m_comboSystem.EndCombo();
            }

            // 取消蓄力/重击释放
            if (m_chargeSystem != null)
            {
                m_chargeSystem.CancelCharge();
            }

            // 取消技能释放
            if (m_skillSystem != null)
            {
                m_skillSystem.CancelCast();
            }

            // 取消武器切换（一般不取消，按需启用）
            // if (m_weaponSystem != null) m_weaponSystem.CancelSwitch();
        }

        /// <summary>
        /// 闪避时调用：先判断是否可取消当前动作，再执行中断
        /// 返回 false 表示闪避被当前动作阻挡（如武器切换中）
        /// </summary>
        /// <returns>true=闪避成功取消；false=无法闪避</returns>
        public bool TryDodgeCancel()
        {
            if (!CanCancelNow) return false;

            InterruptCombat();
            return true;
        }
    }
}
