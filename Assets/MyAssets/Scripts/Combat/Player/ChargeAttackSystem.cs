using MyGame.Audio;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 蓄力攻击系统：管理双阶段重击的蓄力与释放
    /// 核心设计：
    /// 1. 长按左键进入蓄力状态，播放蓄力起手动画 → 循环蓄力动画
    /// 2. 蓄力时间达到一阶阈值（武器配置）→ 达成一阶蓄力（弱重击）
    /// 3. 继续蓄力达到二阶阈值（武器配置）→ 达成二阶蓄力（强重击）
    /// 4. 松开左键时根据当前蓄力等级释放对应重击动画并造成伤害
    /// 5. 未达一阶就松开则取消蓄力（回到待机，不造成伤害）
    /// 6. 武器数据驱动：阈值/伤害倍率/Trigger参数都从 WeaponDataSO 读取
    /// </summary>
    public class ChargeAttackSystem : MonoBehaviour
    {
        [Header("组件引用")]
        [Tooltip("角色 Animator 组件")]
        [SerializeField] private Animator m_animator;

        [Tooltip("命中检测控制器")]
        [SerializeField] private HitboxController m_hitbox;

        /// <summary>当前武器数据（由 WeaponSystem 通过 SetWeaponData 设置）</summary>
        private WeaponDataSO m_weaponData;

        /// <summary>是否正在蓄力（长按中）</summary>
        private bool m_isCharging = false;

        /// <summary>是否正在播放重击释放动画</summary>
        private bool m_isReleasing = false;

        /// <summary>已蓄力时间（从开始蓄力到现在累计）</summary>
        private float m_chargeTime = 0f;

        /// <summary>当前蓄力等级</summary>
        private EChargeLevel m_currentChargeLevel = EChargeLevel.None;

        /// <summary>当前重击释放动画的 Trigger 参数名（用于检测释放动画是否播完）</summary>
        private string m_currentReleaseParam = "";

        /// <summary>是否正在蓄力或释放重击（外部查询用）</summary>
        public bool IsActive => m_isCharging || m_isReleasing;

        /// <summary>是否正在蓄力（外部查询用）</summary>
        public bool IsCharging => m_isCharging;

        /// <summary>当前蓄力等级（外部查询用，可用于UI显示蓄力进度）</summary>
        public EChargeLevel CurrentChargeLevel => m_currentChargeLevel;

        /// <summary>蓄力进度（0~1，可用于UI蓄力条）</summary>
        public float ChargeProgress
        {
            get
            {
                if (!m_isCharging || m_weaponData == null) return 0f;
                // 进度 = 当前蓄力时间 / 二阶阈值，上限为1
                return Mathf.Clamp01(m_chargeTime / m_weaponData.ChargeStage2Time);
            }
        }

        /// <summary>当前是否可被取消（命中帧后即可取消，原神式手感）</summary>
        public bool CanBeCanceled => m_isReleasing && m_hasPassedHitFrame;

        /// <summary>当前释放动画是否已过命中帧</summary>
        private bool m_hasPassedHitFrame = false;

        private void Awake()
        {
            // 自动查找组件
            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }
            if (m_hitbox == null)
            {
                m_hitbox = GetComponent<HitboxController>();
            }
        }

        /// <summary>
        /// 设置当前武器数据：由 WeaponSystem 在武器切换时调用
        /// 武器数据驱动蓄力阈值、伤害倍率、Trigger 参数
        /// </summary>
        /// <param name="weaponData">当前武器配置数据</param>
        public void SetWeaponData(WeaponDataSO weaponData)
        {
            m_weaponData = weaponData;
            // 切换武器时取消蓄力，避免残留
            CancelCharge();
        }

        /// <summary>
        /// 开始蓄力：由 PlayerCombatController 在检测到长按超过点击阈值时调用
        /// 进入蓄力状态，播放蓄力起手动画，随后自动进入蓄力循环
        /// </summary>
        public bool StartCharging()
        {
            if (m_weaponData == null)
            {
                Debug.LogWarning("[ChargeAttackSystem] 无武器数据，无法蓄力", this);
                return false;
            }

            if (m_isCharging || m_isReleasing) return false;

            // 防御性清理：清除可能残留的释放 Trigger，避免进入 ChargeLoop 时被旧 Trigger 自动触发（表现为"没松手自己释放"）
            ResetChargeTriggers();

            m_isCharging = true;
            m_chargeTime = 0f;
            m_currentChargeLevel = EChargeLevel.None;

            // 设置 IsCharging 为 true，使 Animator 进入并保持 ChargeLoop 状态
            m_animator.SetBool(m_weaponData.IsChargingParam, true);
            // 触发蓄力起手动画（ChargeStart → ChargeLoop 的过渡由 Animator 配置）
            m_animator.SetTrigger(m_weaponData.ChargeStartParam);

            // 蓄力音：起手一声，随后进入循环蓄能音（循环音由 AudioManager 单独一条源承载）
            AudioManager.Play(ESfx.Charge_Start);
            AudioManager.PlayLoop(ESfx.Charge_Loop);

            Debug.Log("[ChargeAttackSystem] 蓄力开始", this);
            return true;
        }

        /// <summary>
        /// 释放重击：由 PlayerCombatController 在检测到左键松开时调用
        /// 根据当前蓄力等级决定释放哪一阶重击，或取消蓄力
        /// </summary>
        public void ReleaseCharge()
        {
            if (!m_isCharging || m_weaponData == null) return;

            Debug.Log($"[ChargeAttackSystem] 释放重击，等级={m_currentChargeLevel}, 蓄力时间={m_chargeTime:F2}s", this);

            // 防御性清理：清除起手 Trigger，防止从 ChargeStart/ChargeLoop 过渡到释放时被旧 Trigger 干扰
            m_animator.ResetTrigger(m_weaponData.ChargeStartParam);

            // 退出蓄力状态
            m_isCharging = false;
            m_animator.SetBool(m_weaponData.IsChargingParam, false);

            // 根据蓄力等级决定释放行为
            switch (m_currentChargeLevel)
            {
                case EChargeLevel.Level2:
                    // 二阶蓄力：释放强重击
                    TriggerRelease(m_weaponData.ChargeRelease2Param, m_weaponData.ChargeStage2DamageMultiplier);
                    break;

                case EChargeLevel.Level1:
                    // 一阶蓄力：释放弱重击
                    TriggerRelease(m_weaponData.ChargeRelease1Param, m_weaponData.ChargeStage1DamageMultiplier);
                    break;

                default:
                    // 未达一阶：取消蓄力，直接回到待机（无伤害）
                    m_currentChargeLevel = EChargeLevel.None;
                    // 蓄力不够就松手：只停循环音，不出重击释放音
                    AudioManager.StopLoop();
                    break;
            }
        }

        /// <summary>
        /// 触发重击释放动画并配置命中参数
        /// </summary>
        /// <param name="triggerParam">Animator Trigger 参数名</param>
        /// <param name="damageMultiplier">本次重击的伤害倍率</param>
        private void TriggerRelease(string triggerParam, float damageMultiplier)
        {
            m_isReleasing = true;
            m_hasPassedHitFrame = false;
            m_currentReleaseParam = triggerParam;

            // 释放音：先停掉循环蓄能音，再出重击释放音
            AudioManager.StopLoop();
            AudioManager.Play(ESfx.Charge_Release);

            // 配置命中系统：重击类型 + 对应伤害倍率 + 重置命中目标
            if (m_hitbox != null)
            {
                m_hitbox.SetAttackType(EAttackType.ChargeAttack);
                m_hitbox.SetDamageMultiplier(damageMultiplier);
                m_hitbox.ResetHitTargets();
            }

            // 触发对应的释放动画
            if (!string.IsNullOrEmpty(triggerParam))
            {
                m_animator.SetTrigger(triggerParam);
            }
        }

        /// <summary>
        /// 强制取消蓄力/释放：用于被打断场景（如闪避、受击）
        /// 重置所有蓄力状态，不造成伤害
        /// </summary>
        public void CancelCharge()
        {
            if (m_isCharging || m_isReleasing)
            {
                Debug.Log($"[ChargeAttackSystem] 蓄力被取消（外部打断），isCharging={m_isCharging}, isReleasing={m_isReleasing}", this);
            }
            m_isCharging = false;
            m_isReleasing = false;
            m_hasPassedHitFrame = false;
            m_chargeTime = 0f;
            m_currentChargeLevel = EChargeLevel.None;
            m_currentReleaseParam = "";

            // 被打断时也要停掉循环蓄能音，否则会一直响到战斗结束
            AudioManager.StopLoop();

            if (m_weaponData != null && !string.IsNullOrEmpty(m_weaponData.IsChargingParam))
            {
                m_animator.SetBool(m_weaponData.IsChargingParam, false);
            }
        }

        /// <summary>
        /// 清除所有蓄力相关 Trigger，防止残留 Trigger 在下次蓄力时自动触发
        /// 在开始新蓄力前调用，避免旧 Trigger（如上次释放的 ChargeRelease1）挂起导致"没松手自己释放"
        /// </summary>
        private void ResetChargeTriggers()
        {
            if (m_weaponData == null || m_animator == null) return;

            m_animator.ResetTrigger(m_weaponData.ChargeStartParam);
            m_animator.ResetTrigger(m_weaponData.ChargeRelease1Param);
            m_animator.ResetTrigger(m_weaponData.ChargeRelease2Param);
        }

        private void Update()
        {
            // ===== 蓄力阶段：累计蓄力时间，更新蓄力等级 =====
            if (m_isCharging && m_weaponData != null)
            {
                m_chargeTime += Time.deltaTime;

                // 根据累计时间更新蓄力等级（二阶优先判断，确保达到二阶时立即升级）
                if (m_chargeTime >= m_weaponData.ChargeStage2Time)
                {
                    m_currentChargeLevel = EChargeLevel.Level2;
                }
                else if (m_chargeTime >= m_weaponData.ChargeStage1Time)
                {
                    m_currentChargeLevel = EChargeLevel.Level1;
                }
                // 未达阈值则保持 None（调用方松开时会取消）
            }

            // ===== 释放阶段：检测释放动画是否播完 =====
            if (m_isReleasing && m_weaponData != null)
            {
                AnimatorStateInfo stateInfo = m_animator.GetCurrentAnimatorStateInfo(0);

                // 检查是否仍在释放动画状态（通过 Trigger 参数名哈希匹配）
                bool inReleaseState = !string.IsNullOrEmpty(m_currentReleaseParam)
                                   && stateInfo.shortNameHash == Animator.StringToHash(m_currentReleaseParam);

                // 命中帧后允许取消（标志位由 OnChargeHitFrame 事件设置）
                // 取消由 Fighter 调用 CancelCharge() 完成

                if (!inReleaseState || stateInfo.normalizedTime >= 1f)
                {
                    // 释放动画播完或已离开释放状态：结束重击
                    m_isReleasing = false;
                    m_hasPassedHitFrame = false;
                    m_currentChargeLevel = EChargeLevel.None;
                    m_currentReleaseParam = "";
                }
            }
        }

        // ====================================================================
        // 以下方法由动画事件调用（在重击释放动画上添加 Animation Event）
        // ====================================================================

        /// <summary>
        /// 重击命中帧：在重击释放动画的武器挥击到目标的帧调用
        /// 执行命中检测，对范围内敌人造成重击伤害
        /// 同时标记 m_hasPassedHitFrame = true，进入可取消窗口
        /// </summary>
        public void OnChargeHitFrame()
        {
            int damaged = 0;
            if (m_hitbox != null)
            {
                damaged = m_hitbox.PerformHitDetect();
            }

            // 重击命中音：比普攻更闷更重，只有真的打中才响
            if (damaged > 0)
            {
                AudioManager.Play(ESfx.Heavy_Impact);

                // 打击感：重击的顿帧更长、震屏更明显，与普攻拉开层次
                HitStop.Play(0.09f);
                CameraShake.Shake(0.2f, 0.22f);
            }

            // 命中帧已过，允许后续被外部取消（如闪避取消重击后摇）
            m_hasPassedHitFrame = true;
        }
    }
}
