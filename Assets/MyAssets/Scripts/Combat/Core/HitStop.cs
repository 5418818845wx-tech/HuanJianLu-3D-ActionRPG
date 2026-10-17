using MyGame.UI;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 顿帧（Hit Stop）：命中瞬间把时间流速压到极低，持续极短的真实时间后再恢复
    ///
    /// 为什么它能提升打击感：
    /// 现实里重物相撞会有一瞬间的"凝滞"。游戏里把这 40~80 毫秒的时间几乎冻住，
    /// 动画、物理、位移全部变慢，玩家会感到"这一下很重"，是纯视觉加不出来的效果
    ///
    /// 关键实现细节（踩过坑的点）：
    /// 1. **必须用真实时间计时**：时间流速已被压到 0.05，
    ///    普通 WaitForSeconds 会等上 20 倍时长，顿帧变成"卡死"
    /// 2. **恢复时不能直接写 1**：项目里打开 UI 面板会把 timeScale 设为 0，
    ///    顿帧期间玩家可能正好打开面板——若恢复成 1，面板打开着游戏却动起来了。
    ///    所以恢复前重新问一次 UGUIManager.IsGamePaused
    /// 3. 面板已打开时直接跳过：本来就在暂停，再压时间没有意义
    ///
    /// 挂载位置：场景中一个常驻物体（与其它管理器同层）
    /// </summary>
    [DisallowMultipleComponent]
    public class HitStop : MonoBehaviour
    {
        /// <summary>全局单例（静态入口需要它来找协程宿主）</summary>
        public static HitStop Instance { get; private set; }

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("顿帧期间的时间流速。0.05 = 慢到近乎静止；调大则顿帧更轻微")]
        [SerializeField, Range(0.01f, 0.5f)] private float m_timeScale = 0.05f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前正在跑的顿帧协程（重复触发时先停掉旧的）</summary>
        private Coroutine m_routine;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnDisable()
        {
            // 物体被禁用/场景卸载时兜底复位：否则时间流速会永远卡在 0.05，
            // 表现为"整个游戏像慢动作"，而且很难查
            if (m_routine != null)
            {
                StopCoroutine(m_routine);
                m_routine = null;
            }

            RestoreTimeScale();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 对外入口
        // ====================================================================

        /// <summary>
        /// 播放一次顿帧。
        /// 同一次命中可能同时触发多个系统的弹顿帧，这里用"后一次覆盖前一次"，
        /// 不做时长叠加——叠加会让连击变得黏糊
        /// </summary>
        /// <param name="realSeconds">持续的真实秒数（0.04~0.09 比较舒服）</param>
        public static void Play(float realSeconds)
        {
            Instance?.PlayInternal(realSeconds);
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        private void PlayInternal(float realSeconds)
        {
            if (realSeconds <= 0f)
            {
                return;
            }

            // 面板打开时游戏已经暂停：此时压时间没有意义，还会打乱暂停状态
            if (UGUIManager.IsGamePaused)
            {
                return;
            }

            if (m_routine != null)
            {
                StopCoroutine(m_routine);
            }

            m_routine = StartCoroutine(Run(realSeconds));
        }

        private System.Collections.IEnumerator Run(float realSeconds)
        {
            Time.timeScale = m_timeScale;

            // 必须用 Realtime 等待：timeScale 已被压到 0.05，
            // 普通 WaitForSeconds(0.06) 实际要等 1.2 秒，顿帧会变成明显的卡顿
            yield return new WaitForSecondsRealtime(realSeconds);

            RestoreTimeScale();
            m_routine = null;
        }

        /// <summary>按当前暂停状态恢复到正确的时间流速</summary>
        private void RestoreTimeScale()
        {
            Time.timeScale = UGUIManager.IsGamePaused ? 0f : 1f;
        }
    }
}
