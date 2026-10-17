using System;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 战斗属性结构体：定义角色的基础战斗数值
    /// 使用结构体避免引用开销，方便在 Inspector 中配置
    /// </summary>
    [Serializable]
    public struct CombatStats
    {
        [Tooltip("最大生命值")]
        public float MaxHP;

        [Tooltip("攻击力（用于计算造成的伤害）")]
        public float Attack;

        [Tooltip("防御力（用于抵消受到的伤害）")]
        public float Defense;
    }

    /// <summary>
    /// 战斗单位组件：挂载到玩家和敌人身上，提供生命值管理与伤害结算功能
    /// 这是战斗系统的数据核心，所有伤害最终都会通过 TakeDamage 方法生效
    /// </summary>
    public class CombatUnit : MonoBehaviour
    {
        [Header("队伍归属")]
        [Tooltip("当前单位所属队伍，用于命中检测时过滤友军")]
        [SerializeField] private ETeam m_team = ETeam.Player;

        /// <summary>
        /// 是否打印每次受击的伤害日志。
        ///
        /// 性能提示：每次扣血都会拼字符串并写 Console，战斗中（尤其多敌人围殴时）
        /// 这是持续产生垃圾与磁盘 I/O 的热点。默认关闭；排查伤害数值时置为 true 即可。
        /// </summary>
        public static bool VerboseDamageLog = false;

        [Header("基础属性")]
        [Tooltip("角色的基础战斗属性（生命值/攻击力/防御力）")]
        [SerializeField] private CombatStats m_baseStats;

        /// <summary>
        /// 外部加成（等级成长等），叠加在基础属性之上。
        /// 只由 ApplyBonusStats 写入——外部系统不该直接改战斗单位的数值，
        /// 否则"加成了多少"就无从还原（读档、重算都会算错）
        /// </summary>
        private CombatStats m_bonusStats;

        /// <summary>当前生命值（运行时变化，不暴露给Inspector）</summary>
        private float m_currentHP;

        /// <summary>当前生命值（只读属性，外部只能读取不能修改）</summary>
        public float CurrentHP => m_currentHP;

        /// <summary>最大生命值（基础值 + 等级等外部加成）</summary>
        public float MaxHP => m_baseStats.MaxHP + m_bonusStats.MaxHP;

        /// <summary>攻击力（基础值 + 等级等外部加成）</summary>
        public float Attack => m_baseStats.Attack + m_bonusStats.Attack;

        /// <summary>防御力（基础值 + 等级等外部加成）</summary>
        public float Defense => m_baseStats.Defense + m_bonusStats.Defense;

        /// <summary>所属队伍</summary>
        public ETeam Team => m_team;

        /// <summary>是否存活（生命值大于0）</summary>
        public bool IsAlive => m_currentHP > 0f;

        /// <summary>
        /// 受到伤害时触发的事件，参数为最终扣减的血量
        /// UI血条等系统可订阅此事件来刷新显示
        /// </summary>
        public event Action<float> DamageTaken;

        /// <summary>
        /// 被治疗时触发的事件，参数为**实际回复的血量**（满血溢出部分不算）
        /// 与 DamageTaken 成对：只订阅受伤的话，喝药回血后血条不会动
        /// </summary>
        public event Action<float> Healed;

        /// <summary>
        /// 被命中（实际扣血）时触发的事件，携带攻击者与攻击类型
        /// 受击表现（闪白/击退/飘字）订阅此事件，比 DamageTaken 多出"攻击者"信息用于计算击退方向
        /// </summary>
        public event Action<CombatUnit, EAttackType, float> HitTaken;

        /// <summary>单位死亡时触发的事件</summary>
        public event Action<CombatUnit> Died;

        private void Awake()
        {
            // 初始化当前生命值为最大生命值（Awake 阶段加成为 0，与基础值相等）
            m_currentHP = MaxHP;
        }

        /// <summary>
        /// 受到伤害结算方法
        /// 伤害公式：最终伤害 = max(1, 攻击力 * 伤害倍率 - 防御力)
        /// 保证防御力再高也至少受到1点伤害，避免出现无法破防的死循环
        /// 伤害倍率用于区分普攻(1.0)/重击(2.0~3.5)/技能(各技能自定义)的伤害差异
        /// </summary>
        /// <param name="attacker">攻击者的 CombatUnit（用于读取攻击力）</param>
        /// <param name="attackType">攻击类型（普攻/重击/技能），可用于差异化反馈</param>
        /// <param name="damageMultiplier">伤害倍率，普攻=1.0，重击按蓄力等级递增，技能按配置</param>
        public virtual void TakeDamage(CombatUnit attacker, EAttackType attackType, float damageMultiplier = 1f)
        {
            // 已死亡的单位不再接受伤害，防止重复触发死亡逻辑
            if (!IsAlive || attacker == null)
            {
                return;
            }

            // 计算最终伤害：攻击力乘以倍率后减去防御力，最低为1
            // 防御力用 Defense 属性（含等级加成），不能用 m_baseStats.Defense，否则升级白升
            float finalDamage = Mathf.Max(1f, attacker.Attack * damageMultiplier - Defense);

            // 扣减生命值
            m_currentHP = Mathf.Max(0f, m_currentHP - finalDamage);

            // 触发受伤事件，通知UI等系统更新
            DamageTaken?.Invoke(finalDamage);

            // 触发被命中事件，通知受击表现系统（闪白/击退/飘字）
            HitTaken?.Invoke(attacker, attackType, finalDamage);

            // 调试日志：记录伤害结算。
            // 默认关闭——每次扣血都打日志在战斗中（尤其多敌人围殴）开销很大，
            // 会把本该用于帧率的时间花在拼字符串与写 Console 上。需要排查时把开关打开
            if (VerboseDamageLog)
            {
                Debug.Log($"[{gameObject.name}] 受到 {attacker.gameObject.name} 的" +
                          $"{attackType}攻击，伤害 {finalDamage}，剩余血量 {m_currentHP}/{MaxHP}");
            }

            // 血量归零时触发死亡
            if (m_currentHP <= 0f)
            {
                HandleDeath();
            }
        }

        /// <summary>
        /// 死亡处理逻辑：子类可重写以实现不同的死亡表现（如播放死亡动画、掉落物品）
        /// </summary>
        protected virtual void HandleDeath()
        {
            Debug.Log($"[{gameObject.name}] 已死亡");

            // 触发死亡事件，通知敌人管理器等系统
            Died?.Invoke(this);

            // 简单处理：隐藏游戏物体
            // 实际项目中可改为播放死亡动画后延迟销毁
            gameObject.SetActive(false);
        }

        /// <summary>
        /// 治疗方法：恢复生命值（上限不超过最大生命值）
        /// </summary>
        /// <param name="amount">治疗量</param>
        public void Heal(float amount)
        {
            if (!IsAlive || amount <= 0f)
            {
                return;
            }

            float before = m_currentHP;
            m_currentHP = Mathf.Min(MaxHP, m_currentHP + amount);

            // 只广播真正恢复的部分：满血溢出不该让 UI 误以为回了一整瓶的血
            float restored = m_currentHP - before;
            if (restored <= 0f)
            {
                return;
            }

            Healed?.Invoke(restored);

            Debug.Log($"[{gameObject.name}] 回复 {restored} 点生命值，" +
                      $"剩余血量 {m_currentHP}/{MaxHP}");
        }

        /// <summary>
        /// 覆盖外部属性加成（等级成长等），叠加在基础属性之上。
        ///
        /// 语义是"重设"而不是"累加"：传入的是从等级换算出的**总加成**。
        /// 这样读档、改配置、重设等级都能得到同一结果，不会越加越多。
        ///
        /// 生命上限提高时会同步补上等量当前血量——
        /// 否则刚升级的瞬间血条比例会突然从"满"掉到"八成"，看起来像受了暗伤
        /// </summary>
        /// <param name="hp">最大生命值加成</param>
        /// <param name="attack">攻击力加成</param>
        /// <param name="defense">防御力加成</param>
        public void ApplyBonusStats(float hp, float attack, float defense)
        {
            hp = Mathf.Max(0f, hp);
            float hpDelta = hp - m_bonusStats.MaxHP;

            m_bonusStats = new CombatStats
            {
                MaxHP = hp,
                Attack = Mathf.Max(0f, attack),
                Defense = Mathf.Max(0f, defense),
            };

            // 上限提高就补血（差值可能为负，那是加成被调低，不扣血，只夹取上限）
            if (hpDelta > 0f && IsAlive)
            {
                m_currentHP += hpDelta;
            }

            m_currentHP = Mathf.Clamp(m_currentHP, 0f, MaxHP);
        }

        /// <summary>
        /// 回满血（升级奖励、读档恢复用）。
        /// 已死亡的单位不会被"复活"——复活是另一套流程，不该由补血顺手完成
        /// </summary>
        public void FullRestore()
        {
            if (!IsAlive)
            {
                return;
            }

            float before = m_currentHP;
            m_currentHP = MaxHP;

            float restored = m_currentHP - before;
            if (restored <= 0f)
            {
                return;
            }

            // 走 Healed 事件：HUD 与角色面板都订阅了它，不需要为新方法再补一套刷新
            Healed?.Invoke(restored);
        }
    }
}
