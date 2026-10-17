using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 摄像机震屏：命中/受击时让镜头轻微抖动
    ///
    /// 为什么用"叠加位移差"而不是"记录原始位置再偏移"：
    /// 本项目的相机由 ExampleCamera 每帧重新计算位置。
    /// 如果在 Awake 记一次初始位置、之后按"初始位置 + 偏移"写回去，
    /// 就等于每帧把相机拽回出生点，画面会直接崩掉。
    /// 所以这里只做一件事：**把"本帧偏移"与"上帧偏移"的差加到当前位置上**。
    /// 无论相机控制器把镜头摆到哪，抖动都只是叠加在它之上，且不会累积漂移
    ///
    /// 用 Perlin 噪声而不是 Random：噪声在时间上是连续的，
    /// 抖动看起来像"抖"而不是"闪"，Random 会让画面碎成一团噪点
    ///
    /// 挂载位置：主摄像机（与 AudioListener 同一个物体）
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]   // 必须排在相机控制器之后，否则抖动会被它覆盖掉
    public class CameraShake : MonoBehaviour
    {
        /// <summary>全局单例（静态入口）</summary>
        public static CameraShake Instance { get; private set; }

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("抖动频率（越高越细碎）。20~30 比较像撞击")]
        [SerializeField, Range(5f, 60f)] private float m_frequency = 26f;

        [Tooltip("强度上限（米）。超过这个值的请求会被削平，防止重击时镜头晃到晕")]
        [SerializeField, Range(0.02f, 1f)] private float m_maxOffset = 0.3f;

        [Tooltip("是否启用震屏（调试时可关掉）")]
        [SerializeField] private bool m_enabled = true;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>剩余抖动时间（真实秒）</summary>
        private float m_remaining;

        /// <summary>本次抖动的总时长（用于算衰减比例）</summary>
        private float m_duration;

        /// <summary>当前抖动的强度（米）</summary>
        private float m_strength;

        /// <summary>上一帧已经加出去的偏移量，用来计算"本帧该补多少差值"</summary>
        private Vector3 m_appliedOffset;

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
            // 物体禁用/场景卸载时把偏移还回去，避免镜头停在偏移后的位置
            if (m_appliedOffset != Vector3.zero)
            {
                transform.position -= m_appliedOffset;
                m_appliedOffset = Vector3.zero;
            }

            m_remaining = 0f;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void LateUpdate()
        {
            if (m_remaining <= 0f)
            {
                return;
            }

            // 用 unscaledDeltaTime：顿帧期间 timeScale 极低，用缩放时间抖动会变得几乎不动，
            // 而"冻结 + 抖动"同时发生恰恰是最有冲击力的组合
            m_remaining -= Time.unscaledDeltaTime;

            float progress = m_duration > 0f ? Mathf.Clamp01(m_remaining / m_duration) : 0f;
            float amplitude = Mathf.Min(m_strength, m_maxOffset) * progress;   // 线性衰减到 0

            float seed = Time.unscaledTime * m_frequency;
            Vector3 offset = new Vector3(
                (Mathf.PerlinNoise(seed, 0.37f) * 2f - 1f) * amplitude,
                (Mathf.PerlinNoise(0.11f, seed) * 2f - 1f) * amplitude,
                0f);

            // 只补差值：不依赖相机原本在哪，也不会一帧帧累积位移
            transform.position += offset - m_appliedOffset;
            m_appliedOffset = offset;

            if (m_remaining <= 0f)
            {
                // 收尾：把最后一帧的偏移完全还回去
                transform.position -= m_appliedOffset;
                m_appliedOffset = Vector3.zero;
                m_strength = 0f;
            }
        }

        // ====================================================================
        // 对外入口
        // ====================================================================

        /// <summary>
        /// 触发一次震屏。同一时间只保留"更强的那一次"，
        /// 不做强度叠加——多段伤害同时打到会叠成剧烈晃动，非常晕
        /// </summary>
        /// <param name="strength">强度（米，建议 0.05~0.3）</param>
        /// <param name="duration">持续时间（秒，建议 0.1~0.3）</param>
        public static void Shake(float strength, float duration)
        {
            Instance?.ShakeInternal(strength, duration);
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        private void ShakeInternal(float strength, float duration)
        {
            if (!m_enabled || strength <= 0f || duration <= 0f)
            {
                return;
            }

            // 已有抖动在跑：只有"更强"或"刚好已经结束"时才覆盖
            bool running = m_remaining > 0f;
            if (running && strength < m_strength)
            {
                return;
            }

            m_strength = strength;
            m_duration = duration;
            m_remaining = duration;
        }
    }
}
