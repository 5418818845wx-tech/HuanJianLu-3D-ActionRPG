using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 区域触发器：玩家走进某片区域时，向任务系统上报一次「到达」进度
    ///
    /// 为什么需要它：
    /// 任务目标里 Reach（到达）和击杀/收集不一样——后两者有天然的事件源
    /// （敌人死亡、背包数量变化），而"到达"没有任何东西会主动告诉你，
    /// 必须有人负责"发现玩家进来了"并上报。本组件就是那个事件源
    ///
    /// 职责边界（与击杀/收集完全一致）：**只上报，不判断**
    /// 是不是任务目标、算不算进度、有没有完成，全由 QuestManager 决定。
    /// 所以同一个区域能被多个任务共用，触发器上不需要配任何任务信息
    ///
    /// 重复进入重复上报是安全的：QuestManager 内部会拦截已达成的目标，
    /// 玩家来回穿行不会把"到达训练场 1 次"刷成多次
    ///
    /// 挂载要求：
    /// - 本物体必须有 Collider 并勾上 Is Trigger（漏勾会在 Awake 里自动纠正并提示）
    /// - 玩家侧需要 Collider + Rigidbody 才能产生触发（玩家预制体已具备）
    ///
    /// 典型用法：在场景里建一个空物体 → 加 BoxCollider 勾 Is Trigger → 拉大范围 → 挂本组件 → 填区域 ID
    /// </summary>
    [DisallowMultipleComponent]
    public class AreaTrigger : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("区域标识")]
        [Tooltip("区域 ID：任务里的「到达」目标按此值匹配。命名规范 Area_ + 名字（如 Area_TrainingGround）")]
        [SerializeField] private string m_areaId = "Area_Unnamed";

        [Header("触发条件")]
        [Tooltip("视为玩家的 Tag（与玩家预制体一致）")]
        [SerializeField] private string m_playerTag = "Player";

        [Header("调试")]
        [Tooltip("玩家进入时打一条日志，方便确认区域有没有正常工作")]
        [SerializeField] private bool m_logOnReport = true;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>本物体的碰撞体（Awake 缓存）</summary>
        private Collider m_collider;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>区域 ID（内容校验器用它检查任务里的到达目标是否有对应区域）</summary>
        public string AreaId => m_areaId;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            m_collider = GetComponent<Collider>();

            if (m_collider == null)
            {
                // 没有碰撞体就永远收不到 OnTriggerEnter：明确报错而不是静默失效
                Debug.LogError($"[{GetType().Name}]「{name}」上没有 Collider，玩家走进来也不会触发", this);
                enabled = false;
                return;
            }

            // 没勾 Is Trigger 会变成实体阻挡（玩家撞墙而不是"到达"）：自动纠正，避免"配了没反应"
            if (!m_collider.isTrigger)
            {
                Debug.LogWarning($"[{GetType().Name}]「{name}」的 Collider 没有勾 Is Trigger，已自动勾上", this);
                m_collider.isTrigger = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            // 只认玩家；敌人的碰撞体和掉落物不该推进"到达"类任务
            if (!other.CompareTag(m_playerTag))
            {
                return;
            }

            QuestManager questManager = QuestManager.Instance;
            if (questManager == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 QuestManager，区域「{m_areaId}」的上报被丢弃", this);
                return;
            }

            questManager.ReportProgress(EQuestObjectiveType.Reach, m_areaId);

            if (m_logOnReport)
            {
                Debug.Log($"[{GetType().Name}] 玩家到达区域「{m_areaId}」，已上报任务进度", this);
            }
        }

        // ====================================================================
        // 编辑器辅助
        // ====================================================================

        private void OnDrawGizmosSelected()
        {
            // 画个线框方便在场景里看清覆盖范围（只在选中时显示）
            if (m_collider == null)
            {
                return;
            }

            Gizmos.color = new Color(0.2f, 0.9f, 0.6f, 0.6f);

            if (m_collider is BoxCollider box)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else if (m_collider is SphereCollider sphere)
            {
                Gizmos.DrawWireSphere(transform.TransformPoint(sphere.center), sphere.radius);
            }
        }
    }
}
