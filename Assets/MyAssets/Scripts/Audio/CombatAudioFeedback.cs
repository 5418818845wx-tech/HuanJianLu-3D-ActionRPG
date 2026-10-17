using MyGame.Combat;
using UnityEngine;

namespace MyGame.Audio
{
    /// <summary>
    /// 角色受击/死亡音效：订阅 CombatUnit 的事件，"谁被打"决定放哪一套音
    ///
    /// 为什么做成独立组件而不是写进 CombatUnit：
    /// CombatUnit 是纯战斗数据（血量/伤害结算），不该认识音频。
    /// 需要声音的角色挂上本组件即可，不需要声音的角色（木桩、召唤物）挂都不挂。
    ///
    /// 队伍自动区分：Player 队 → 玩家受击/死亡音；其余（Enemy）→ 敌人受击/死亡音
    ///
    /// 挂载位置：Player 预制体、敌人预制体（与 CombatUnit 同一物体）
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CombatUnit))]
    public class CombatAudioFeedback : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("同一角色的两次受击音最小间隔（秒）。防止多段伤害瞬间刷出叠音")]
        [SerializeField, Range(0f, 0.5f)] private float m_hurtSoundMinInterval = 0.08f;

        [Tooltip("受击音音量缩放（敌人离得近时容易盖过玩家，可适当调低）")]
        [SerializeField, Range(0f, 1f)] private float m_hurtVolumeScale = 0.9f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>本角色的战斗单位</summary>
        private CombatUnit m_unit;

        /// <summary>下一次允许播受击音的时间点（Time.time）</summary>
        private float m_nextHurtSoundTime;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            m_unit = GetComponent<CombatUnit>();
        }

        private void OnEnable()
        {
            if (m_unit == null)
            {
                return;
            }

            m_unit.HitTaken += HandleHitTaken;
            m_unit.Died += HandleDied;
        }

        private void OnDisable()
        {
            if (m_unit == null)
            {
                return;
            }

            m_unit.HitTaken -= HandleHitTaken;
            m_unit.Died -= HandleDied;
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>被命中：播放受击音（带最小间隔，避免连击时叠成噪音）</summary>
        private void HandleHitTaken(CombatUnit attacker, EAttackType attackType, float damage)
        {
            if (Time.time < m_nextHurtSoundTime)
            {
                return;
            }

            m_nextHurtSoundTime = Time.time + m_hurtSoundMinInterval;

            if (IsPlayer())
            {
                // 玩家自己的痛感是"贴耳"的，用 2D，音量不随距离变化
                AudioManager.PlayVariant(ESfx.Player_Hurt_01, ESfx.Player_Hurt_02, m_hurtVolumeScale);
            }
            else
            {
                // 敌人的惨叫来自世界里某个位置，用 3D：离得远就小声
                AudioManager.PlayVariantAt(
                    ESfx.Enemy_Hurt_01, ESfx.Enemy_Hurt_02, transform.position, m_hurtVolumeScale);
            }
        }

        /// <summary>死亡：玩家走这里；敌人在 EnemyAI 的死亡流程里发声（EnemyCombatUnit 不触发 Died）</summary>
        private void HandleDied(CombatUnit diedUnit)
        {
            if (IsPlayer())
            {
                AudioManager.Play(ESfx.Player_Death);
            }
            else
            {
                AudioManager.PlayAt(ESfx.Enemy_Death, transform.position);
            }
        }

        /// <summary>本角色是否属于玩家阵营</summary>
        private bool IsPlayer()
        {
            return m_unit != null && m_unit.Team == ETeam.Player;
        }
    }
}
