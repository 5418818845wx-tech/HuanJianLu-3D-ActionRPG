using System;
using MyGame.KeyBindings;
using MyGame.UI;
using UnityEngine;

namespace MyGame.Interaction
{
    /// <summary>
    /// 交互检测器：挂在玩家身上，负责"找出最近的可交互物 + 显示提示 + 响应按键"
    ///
    /// 工作流程：
    /// 1. 按 m_detectInterval 间隔用 OverlapSphere 找出范围内最近的可交互物（非每帧，省性能）
    /// 2. 目标变化时广播 InteractTargetChanged，交互提示 UI 订阅后显示/隐藏提示条
    /// 3. 玩家按下交互键（默认 E）时调用目标的 Interact()
    ///
    /// 挂载位置：玩家预制体（与 CombatUnit 同一物体即可）
    /// 注意：物理检测需要目标身上有 Collider，QuestGiver 所在的 NPC 需要有碰撞体
    /// </summary>
    [DisallowMultipleComponent]
    public class InteractionDetector : MonoBehaviour
    {
        /// <summary>单次检测最多收集的碰撞体数量（超出部分本帧忽略，下一帧会补上）</summary>
        private const int k_maxOverlapResults = 16;

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("检测设置")]
        [Tooltip("交互检测半径（米），建议 2.5~3.5")]
        [SerializeField] private float m_detectRadius = 3f;

        [Tooltip("检测间隔（秒）。按间隔扫描而非每帧，避免 OverlapSphere 每帧开销")]
        [SerializeField, Range(0.05f, 1f)] private float m_detectInterval = 0.15f;

        [Tooltip("参与检测的层（默认全部层，可按需只勾选 NPC/交互物层）")]
        [SerializeField] private LayerMask m_interactableLayers = ~0;

        [Header("交互按键")]
        // 交互按键已统一由 KeyBindingManager 管理（默认 G，可在设置面板改键），这里不再放字段

        [Header("UI 屏蔽")]
        [Tooltip("有弹窗面板（背包/任务日志等）打开时是否禁用交互，避免按键与 UI 操作冲突")]
        [SerializeField] private bool m_blockedByOverlay = true;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>复用的碰撞体缓存数组（避免每次检测都产生垃圾）</summary>
        private readonly Collider[] m_overlapResults = new Collider[k_maxOverlapResults];

        /// <summary>当前锁定（最近）的可交互物，无则 null</summary>
        private IInteractable m_currentTarget;

        /// <summary>下一次检测的时间点（Time.unscaledTime）</summary>
        private float m_nextDetectTime;

        // ====================================================================
        // 对外只读访问与事件
        // ====================================================================

        /// <summary>当前可交互目标（无则 null）</summary>
        public IInteractable CurrentTarget => m_currentTarget;

        /// <summary>交互目标变化（参数可能为 null，表示离开/目标失效）</summary>
        public event Action<IInteractable> InteractTargetChanged;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Update()
        {
            // 按间隔扫描最近的可交互物
            if (Time.unscaledTime >= m_nextDetectTime)
            {
                m_nextDetectTime = Time.unscaledTime + m_detectInterval;
                DetectNearestTarget();
            }

            // 目标可能已被销毁（敌人 NPC 死亡、物体卸载）：及时清空，避免访问已销毁对象
            if (!IsTargetAlive(m_currentTarget))
            {
                SetTarget(null);
                return;
            }

            if (m_currentTarget == null || IsBlockedByOverlay())
            {
                return;
            }

            // 按键触发交互（键位由 KeyBindingManager 提供，玩家可自定义）
            if (KeyBindingManager.IsDown(EGameKey.Interact))
            {
                m_currentTarget.Interact(gameObject);
            }
        }

        private void OnDisable()
        {
            // 物体禁用时清空目标，让提示条同步消失
            SetTarget(null);
        }

        // ====================================================================
        // 检测
        // ====================================================================

        /// <summary>在半径内找出最近且可交互的目标</summary>
        private void DetectNearestTarget()
        {
            int count = Physics.OverlapSphereNonAlloc(
                transform.position,
                m_detectRadius,
                m_overlapResults,
                m_interactableLayers,
                QueryTriggerInteraction.Collide);

            IInteractable nearest = null;
            float nearestSqrDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                Collider hit = m_overlapResults[i];
                if (hit == null)
                {
                    continue;
                }

                // 接口可能挂在碰撞体所在物体的父级上（如子物体碰撞体 + 父物体逻辑）
                IInteractable candidate = hit.GetComponentInParent<IInteractable>();
                if (candidate == null || !candidate.CanInteract)
                {
                    continue;
                }

                float sqrDistance = (hit.transform.position - transform.position).sqrMagnitude;
                if (sqrDistance >= nearestSqrDistance)
                {
                    continue;
                }

                nearestSqrDistance = sqrDistance;
                nearest = candidate;
            }

            SetTarget(nearest);
        }

        /// <summary>更新当前目标，仅在真正变化时广播事件</summary>
        private void SetTarget(IInteractable target)
        {
            if (ReferenceEquals(m_currentTarget, target))
            {
                return;
            }

            m_currentTarget = target;
            InteractTargetChanged?.Invoke(target);
        }

        // ====================================================================
        // 辅助判断
        // ====================================================================

        /// <summary>
        /// 目标是否有效：MonoBehaviour 被销毁后引用不为 null 但已失效，
        /// 必须用 Unity 重载的 == 判断，否则会访问到已销毁对象
        /// </summary>
        private static bool IsTargetAlive(IInteractable target)
        {
            if (target == null)
            {
                return false;
            }

            return target is not UnityEngine.Object unityObject || unityObject != null;
        }

        /// <summary>当前是否有弹窗面板打开（打开时禁止交互）</summary>
        private bool IsBlockedByOverlay()
        {
            // 直接读取 UGUI 管理器的静态暂停标志：
            // 弹窗面板打开时 Time.timeScale=0 且 IsGamePaused=true，交互一并屏蔽
            //
            // WantsCursor：玩家按住快捷栏的呼出键时也在用鼠标点 UI，交互一并屏蔽
            if (!m_blockedByOverlay)
            {
                return false;
            }

            return UGUIManager.IsGamePaused || UGUIManager.WantsCursor;
        }
    }
}
