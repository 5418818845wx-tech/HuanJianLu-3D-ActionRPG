using System;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 武器系统：管理双武器切换
    /// 核心设计：
    /// 1. 维护两把武器的 WeaponDataSO 配置（武器1/武器2）
    /// 2. 按 Tab 切换武器：播放切换动画 → 切换数据 → 通知各子系统刷新配置
    /// 3. 切换动画期间锁定其他战斗输入（防止切换中触发攻击产生异常）
    /// 4. 切换完成后通过 OnWeaponSwitched 事件通知订阅者（如 UI 显示）
    /// </summary>
    public class WeaponSystem : MonoBehaviour
    {
        [Header("武器配置")]
        [Tooltip("武器1配置（主武器，默认装备）")]
        [SerializeField] private WeaponDataSO m_weapon1;

        [Tooltip("武器2配置（副武器，按 Tab 切换）")]
        [SerializeField] private WeaponDataSO m_weapon2;

        [Header("组件引用")]
        [Tooltip("角色 Animator 组件")]
        [SerializeField] private Animator m_animator;

        [Tooltip("连招系统（切换时刷新武器数据）")]
        [SerializeField] private ComboSystem m_comboSystem;

        [Tooltip("蓄力系统（切换时刷新武器数据）")]
        [SerializeField] private ChargeAttackSystem m_chargeSystem;

        [Tooltip("技能系统（切换时刷新武器数据）")]
        [SerializeField] private SkillSystem m_skillSystem;

        [Header("切换设置")]
        [Tooltip("切换动画播放时长（秒，期间锁定战斗输入）")]
        [SerializeField] private float m_switchDuration = 0.5f;

        /// <summary>当前武器索引（0=武器1，1=武器2）</summary>
        private int m_currentWeaponIndex = 0;

        /// <summary>是否正在切换武器（期间锁定其他战斗输入）</summary>
        private bool m_isSwitching = false;

        /// <summary>切换动画已过的时间（用于判断切换是否完成）</summary>
        private float m_switchTimeElapsed = 0f;

        /// <summary>切换完成回调的目标时间（用于判断动画是否播完）</summary>
        private float m_switchTargetTime = 0f;

        /// <summary>武器槽位数量（主手 / 副手）</summary>
        public const int k_slotCount = 2;

        /// <summary>当前武器数据（只读属性）</summary>
        public WeaponDataSO CurrentWeapon => (m_currentWeaponIndex == 0) ? m_weapon1 : m_weapon2;

        /// <summary>当前武器索引（只读属性）</summary>
        public EWeaponIndex CurrentWeaponIndex => (EWeaponIndex)m_currentWeaponIndex;

        /// <summary>是否正在切换武器（只读属性，外部用于锁定输入判断）</summary>
        public bool IsSwitching => m_isSwitching;

        /// <summary>武器切换完成事件（参数：新武器索引）</summary>
        public event Action<EWeaponIndex> WeaponSwitched;

        private void Awake()
        {
            // 自动查找组件
            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }
            if (m_comboSystem == null)
            {
                m_comboSystem = GetComponent<ComboSystem>();
            }
            if (m_chargeSystem == null)
            {
                m_chargeSystem = GetComponent<ChargeAttackSystem>();
            }
            if (m_skillSystem == null)
            {
                m_skillSystem = GetComponent<SkillSystem>();
            }
        }

        private void Start()
        {
            // 启动时初始化武器1数据，通知各子系统
            // 注意：在 Start 而非 Awake 中分发，确保各子系统的 Awake 已完成组件缓存
            ApplyWeaponData(CurrentWeapon);
        }

        /// <summary>
        /// 尝试切换武器：由 PlayerCombatController 在按下 Tab 时调用
        /// 切换条件：目标槽位有武器 + 当前未在切换
        /// </summary>
        /// <returns>true=开始切换；false=无法切换</returns>
        public bool TrySwitchWeapon()
        {
            // 正在切换中，忽略输入
            if (m_isSwitching) return false;

            // 目标槽位空着就不切：切过去会变成"赤手空拳"，连招与技能配置全为空
            int targetIndex = (m_currentWeaponIndex == 0) ? 1 : 0;
            if (GetSlotWeapon(targetIndex) == null) return false;

            // 开始切换：进入锁定状态
            m_isSwitching = true;
            m_switchTimeElapsed = 0f;
            m_switchTargetTime = m_switchDuration;

            // 切换武器索引（0↔1）
            m_currentWeaponIndex = targetIndex;
            WeaponDataSO newWeapon = CurrentWeapon;

            // 播放切换动画（如果有配置 Trigger 参数）
            if (newWeapon != null && !string.IsNullOrEmpty(newWeapon.SwitchInParam))
            {
                m_animator.SetTrigger(newWeapon.SwitchInParam);
            }

            return true;
        }

        private void Update()
        {
            // ===== 切换中：累计时间，到达目标时间后完成切换 =====
            if (m_isSwitching)
            {
                m_switchTimeElapsed += Time.deltaTime;

                if (m_switchTimeElapsed >= m_switchTargetTime)
                {
                    // 切换动画播完：应用新武器数据，通知各子系统刷新
                    CompleteSwitch();
                }
            }
        }

        /// <summary>
        /// 完成武器切换：应用新武器数据到各战斗子系统
        /// </summary>
        private void CompleteSwitch()
        {
            m_isSwitching = false;
            m_switchTimeElapsed = 0f;

            WeaponDataSO newWeapon = CurrentWeapon;
            ApplyWeaponData(newWeapon);

            // 通知订阅者（如 UI 武器图标刷新）
            WeaponSwitched?.Invoke(CurrentWeaponIndex);

            Debug.Log($"[WeaponSystem] 切换到武器：{newWeapon?.WeaponName ?? "无"}", this);
        }

        /// <summary>
        /// 读取某个槽位的武器（槽位越界返回 null）
        /// </summary>
        public WeaponDataSO GetSlotWeapon(int slotIndex)
        {
            if (slotIndex == 0) return m_weapon1;
            if (slotIndex == 1) return m_weapon2;
            return null;
        }

        /// <summary>
        /// 写入某个槽位的武器：由装备系统在换装 / 读档时调用。
        /// 换的正好是手上这把时立刻生效，不必等下一次切换
        /// </summary>
        /// <returns>槽位是否合法</returns>
        public bool SetSlotWeapon(int slotIndex, WeaponDataSO weaponData)
        {
            if (slotIndex != 0 && slotIndex != 1) return false;

            if (slotIndex == 0)
            {
                m_weapon1 = weaponData;
            }
            else
            {
                m_weapon2 = weaponData;
            }

            if (slotIndex == m_currentWeaponIndex)
            {
                if (weaponData != null)
                {
                    ApplyWeaponData(weaponData);
                }
                else
                {
                    // 手上这把被拿掉了（读档时物品已不在背包里）：先换到另一只手，
                    // 否则会出现"槽位里有武器、手里却没有"，连招与技能配置全空
                    SwitchToFallbackSlot();
                }
            }

            return true;
        }

        /// <summary>
        /// 直接切到某只手：不走切换动画、不锁输入，供"状态修正"使用
        /// （读档还原、手中武器失效时兜底——这两种都不是玩家主动换武器）
        /// </summary>
        /// <returns>该槽位是否有武器可拿</returns>
        public bool ForceSelectSlot(int slotIndex)
        {
            WeaponDataSO weapon = GetSlotWeapon(slotIndex);
            if (weapon == null)
            {
                return false;
            }

            m_isSwitching = false;
            m_currentWeaponIndex = slotIndex;
            ApplyWeaponData(weapon);

            WeaponSwitched?.Invoke(CurrentWeaponIndex);
            return true;
        }

        /// <summary>当前槽位空了：换到另一只手上还有的武器（都没有则保持空手）</summary>
        private void SwitchToFallbackSlot()
        {
            int otherIndex = (m_currentWeaponIndex == 0) ? 1 : 0;

            if (!ForceSelectSlot(otherIndex))
            {
                Debug.LogWarning("[WeaponSystem] 两个槽位都没有武器，角色处于空手状态", this);
                return;
            }

            Debug.Log($"[WeaponSystem] 手中武器失效，自动改用：{CurrentWeapon.WeaponName}", this);
        }

        /// <summary>
        /// 将武器数据应用到各战斗子系统
        /// 在 Start 初始化和切换完成时调用
        /// </summary>
        /// <param name="weaponData">要应用的武器数据</param>
        private void ApplyWeaponData(WeaponDataSO weaponData)
        {
            if (weaponData == null)
            {
                Debug.LogWarning("[WeaponSystem] 武器数据为空", this);
                return;
            }

            // 通知各子系统刷新武器配置
            if (m_comboSystem != null)
            {
                m_comboSystem.SetWeaponData(weaponData);
            }
            if (m_chargeSystem != null)
            {
                m_chargeSystem.SetWeaponData(weaponData);
            }
            if (m_skillSystem != null)
            {
                m_skillSystem.SetWeaponData(weaponData);
            }
        }

        /// <summary>
        /// 强制取消切换：用于被打断场景（如受击）
        /// </summary>
        public void CancelSwitch()
        {
            m_isSwitching = false;
            m_switchTimeElapsed = 0f;
        }
    }
}
