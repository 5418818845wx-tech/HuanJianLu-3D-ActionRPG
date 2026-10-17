using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 武器数据 ScriptableObject：定义一把武器的所有配置
    /// 设计原则：每把武器有独立的连招链、技能集、动画参数前缀
    /// 武器切换时，WeaponSystem 会将当前武器数据广播给各战斗子系统
    /// 创建方式：在 Project 窗口右键 → Create → 游戏数据 → 武器（Weapon）
    /// </summary>
    [CreateAssetMenu(fileName = "WeaponData", menuName = "游戏数据/武器（Weapon）", order = 1)]
    public class WeaponDataSO : ScriptableObject
    {
        [Header("基础信息")]
        [Tooltip("武器名称（仅用于 Inspector 识别）")]
        [SerializeField] private string m_weaponName = "New Weapon";

        [Tooltip("武器图标（用于 UI 显示，可选）")]
        [SerializeField] private Sprite m_icon;

        [Header("普攻连招配置")]
        [Tooltip("连招段数（建议固定为 4，可按需调整）")]
        [SerializeField] private int m_comboCount = 4;

        [Tooltip("每段普攻的伤害倍率（按段位递增，数组长度应等于 ComboCount）")]
        [SerializeField] private float[] m_comboDamageMultipliers = { 1f, 1.1f, 1.2f, 1.5f };

        [Tooltip("Animator Trigger 参数名（按段位，长度应等于 ComboCount）")]
        [SerializeField] private string[] m_comboTriggerParams = { "Attack1", "Attack2", "Attack3", "Attack4" };

        [Tooltip("命中帧动画事件触发的 normalizedTime 阈值（0~1，超过此值才允许动作取消）")]
        [SerializeField] private float[] m_comboCancelWindowStarts = { 0.5f, 0.5f, 0.5f, 0.6f };

        [Header("蓄力攻击配置")]
        [Tooltip("一阶蓄力所需时间（秒）")]
        [SerializeField] private float m_chargeStage1Time = 0.5f;

        [Tooltip("二阶蓄力所需时间（秒，应大于一阶）")]
        [SerializeField] private float m_chargeStage2Time = 1.5f;

        [Tooltip("一阶重击伤害倍率")]
        [SerializeField] private float m_chargeStage1DamageMultiplier = 2f;

        [Tooltip("二阶重击伤害倍率（应大于一阶）")]
        [SerializeField] private float m_chargeStage2DamageMultiplier = 3.5f;

        [Tooltip("蓄力起手 Animator Trigger 参数名")]
        [SerializeField] private string m_chargeStartParam = "ChargeStart";

        [Tooltip("蓄力循环 Animator Bool 参数名")]
        [SerializeField] private string m_isChargingParam = "IsCharging";

        [Tooltip("一阶重击释放 Animator Trigger 参数名")]
        [SerializeField] private string m_chargeRelease1Param = "ChargeRelease1";

        [Tooltip("二阶重击释放 Animator Trigger 参数名")]
        [SerializeField] private string m_chargeRelease2Param = "ChargeRelease2";

        [Header("技能集配置")]
        [Tooltip("Q/E/R/F 四个槽位的技能数据（可空，空则该槽位无技能）")]
        [SerializeField] private SkillDataSO[] m_skills = new SkillDataSO[4];

        [Header("切换动画")]
        [Tooltip("切换到本武器时播放的 Animator Trigger 参数名")]
        [SerializeField] private string m_switchInParam = "WeaponSwitchIn";

        // ==================== 只读属性（外部访问用） ====================

        /// <summary>武器名称</summary>
        public string WeaponName => m_weaponName;

        /// <summary>武器图标</summary>
        public Sprite Icon => m_icon;

        /// <summary>连招段数</summary>
        public int ComboCount => m_comboCount;

        /// <summary>每段普攻伤害倍率</summary>
        public float[] ComboDamageMultipliers => m_comboDamageMultipliers;

        /// <summary>每段普攻 Trigger 参数名</summary>
        public string[] ComboTriggerParams => m_comboTriggerParams;

        /// <summary>每段普攻可取消起点（normalizedTime 阈值）</summary>
        public float[] ComboCancelWindowStarts => m_comboCancelWindowStarts;

        /// <summary>一阶蓄力时间</summary>
        public float ChargeStage1Time => m_chargeStage1Time;

        /// <summary>二阶蓄力时间</summary>
        public float ChargeStage2Time => m_chargeStage2Time;

        /// <summary>一阶重击伤害倍率</summary>
        public float ChargeStage1DamageMultiplier => m_chargeStage1DamageMultiplier;

        /// <summary>二阶重击伤害倍率</summary>
        public float ChargeStage2DamageMultiplier => m_chargeStage2DamageMultiplier;

        /// <summary>蓄力起手 Trigger 参数名</summary>
        public string ChargeStartParam => m_chargeStartParam;

        /// <summary>蓄力循环 Bool 参数名</summary>
        public string IsChargingParam => m_isChargingParam;

        /// <summary>一阶重击释放 Trigger 参数名</summary>
        public string ChargeRelease1Param => m_chargeRelease1Param;

        /// <summary>二阶重击释放 Trigger 参数名</summary>
        public string ChargeRelease2Param => m_chargeRelease2Param;

        /// <summary>四槽位技能集</summary>
        public SkillDataSO[] Skills => m_skills;

        /// <summary>切换到本武器时播放的 Trigger 参数名</summary>
        public string SwitchInParam => m_switchInParam;

        /// <summary>
        /// 安全获取指定槽位的技能数据
        /// </summary>
        /// <param name="slot">技能槽位</param>
        /// <returns>对应技能数据，无则返回 null</returns>
        public SkillDataSO GetSkill(ESkillSlot slot)
        {
            int idx = (int)slot;
            if (m_skills == null || idx < 0 || idx >= m_skills.Length)
            {
                return null;
            }
            return m_skills[idx];
        }
    }
}
