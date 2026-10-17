using System.Collections.Generic;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 命中检测控制器：负责在攻击的"命中帧"检测前方范围内的敌人并造成伤害
    /// 工作原理：动画播放到武器挥击的命中帧时，通过动画事件调用 PerformHitDetect()
    /// 此时在该角色前方进行球形检测，找到敌人后调用其 CombatUnit.TakeDamage()
    /// </summary>
    public class HitboxController : MonoBehaviour
    {
        [Header("命中检测设置")]
        [Tooltip("命中检测中心点（建议在角色前方约1米处创建一个空物体作为子物体）")]
        [SerializeField] private Transform m_hitCenter;

        [Tooltip("命中检测半径（球形检测的范围）")]
        [SerializeField] private float m_hitRadius = 1.2f;

        [Tooltip("命中检测层级掩码（应包含敌人所在的Layer）")]
        [SerializeField] private LayerMask m_hitMask = ~0;

        /// <summary>
        /// 当前攻击类型（普攻/重击/技能）
        /// 在每次攻击开始前由 ComboSystem / ChargeAttackSystem / SkillSystem 设置
        /// </summary>
        private EAttackType m_currentAttackType = EAttackType.NormalAttack;

        /// <summary>
        /// 当前伤害倍率：普攻=1.0，重击按蓄力等级递增，技能按配置
        /// 在每次攻击开始前由各战斗子系统设置
        /// </summary>
        private float m_damageMultiplier = 1f;

        /// <summary>攻击者的 CombatUnit（用于读取攻击力）</summary>
        private CombatUnit m_attackerUnit;

        /// <summary>
        /// 本次挥击已命中的目标集合
        /// 用于防止同一敌人被同一次挥击多次命中
        /// 每次攻击开始时通过 ResetHitTargets() 清空
        /// </summary>
        private readonly HashSet<CombatUnit> m_hitTargets = new HashSet<CombatUnit>();

        /// <summary>用于 OverlapSphere 的缓冲数组，避免每次检测都 new</summary>
        private readonly Collider[] m_hitColliders = new Collider[16];

        private void Awake()
        {
            // 自动获取本物体上的 CombatUnit 作为攻击者
            m_attackerUnit = GetComponent<CombatUnit>();
            if (m_attackerUnit == null)
            {
                m_attackerUnit = GetComponentInParent<CombatUnit>();
            }
        }

        /// <summary>
        /// 设置当前攻击类型：在攻击动画开始前调用
        /// </summary>
        /// <param name="attackType">普攻/重击/技能</param>
        public void SetAttackType(EAttackType attackType)
        {
            m_currentAttackType = attackType;
        }

        /// <summary>
        /// 设置当前伤害倍率：在攻击动画开始前调用
        /// 普攻=1.0，重击一阶=2.0，重击二阶=3.5，技能=SkillDataSO 配置值
        /// </summary>
        /// <param name="multiplier">伤害倍率</param>
        public void SetDamageMultiplier(float multiplier)
        {
            m_damageMultiplier = multiplier;
        }

        /// <summary>
        /// 重置已命中目标列表：在每次攻击动画开始时调用
        /// 清空上一段攻击的命中记录，确保本次挥击能重新命中所有敌人
        /// </summary>
        public void ResetHitTargets()
        {
            m_hitTargets.Clear();
        }

        /// <summary>
        /// 执行命中检测：由动画事件在命中帧调用
        /// 在 m_hitCenter 位置进行球形检测，对所有敌人造成伤害
        /// </summary>
        /// <returns>本次实际打到的目标数量（0 = 挥空）。调用方据此决定要不要播命中音效</returns>
        public int PerformHitDetect()
        {
            if (m_hitCenter == null || m_attackerUnit == null)
            {
                return 0;
            }

            // 在命中中心点进行球形检测，返回检测到的碰撞体数量
            int hitCount = Physics.OverlapSphereNonAlloc(
                m_hitCenter.position,
                m_hitRadius,
                m_hitColliders,
                m_hitMask);

            // 实际造成伤害的目标数（挥空时为 0）
            int damagedCount = 0;

            // 遍历所有检测到的碰撞体
            for (int i = 0; i < hitCount; i++)
            {
                Collider col = m_hitColliders[i];
                if (col == null) continue;

                // 尝试获取碰撞体上的 CombatUnit 组件
                CombatUnit targetUnit = col.GetComponentInParent<CombatUnit>();

                // 跳过：无战斗单位、是自己、是友军、已命中过、已死亡
                if (targetUnit == null) continue;
                if (targetUnit == m_attackerUnit) continue;
                if (targetUnit.Team == m_attackerUnit.Team) continue;
                if (m_hitTargets.Contains(targetUnit)) continue;
                if (!targetUnit.IsAlive) continue;

                // 记录已命中，防止本次挥击重复命中
                m_hitTargets.Add(targetUnit);

                // 对目标造成伤害（传入当前伤害倍率，普攻=1.0，重击/技能更高）
                targetUnit.TakeDamage(m_attackerUnit, m_currentAttackType, m_damageMultiplier);
                damagedCount++;
            }

            return damagedCount;
        }

        /// <summary>
        /// 在场景视图中绘制命中范围，方便 Inspector 调试
        /// 仅在 Editor 中显示，不影响发布
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (m_hitCenter == null) return;

            // 用红色线框球体表示命中范围
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(m_hitCenter.position, m_hitRadius);
        }
    }
}
