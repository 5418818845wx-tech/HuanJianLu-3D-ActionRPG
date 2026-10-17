using TMPro;
using UnityEngine;

namespace MyGame.UI
{
    /// <summary>
    /// 屏幕帧率显示：把实时 FPS 写到一小行文本上。
    ///
    /// 【为什么不是每帧刷新文本】
    /// 每帧改一次 TMP 文本都会触发一次网格重建，本身就要花时间，
    /// 还会干扰我们正在观察的帧率（观察行为影响观察对象）。
    /// 所以按固定间隔（默认 0.25 秒）刷新一次，显示这段区间的平均值：更省、更稳、更好读。
    ///
    /// 【为什么用 unscaledDeltaTime】
    /// 打开 UI 面板时游戏是 timeScale = 0 暂停的，用 deltaTime 会算出错误的帧率；
    /// 而且"暂停时帧率还有多少"本身就是个有意义的数字。
    ///
    /// 挂载位置：UI Canvas 角落的一行小文本
    /// </summary>
    public class FpsCounter : MonoBehaviour
    {
        [Header("显示")]
        [Tooltip("用来显示帧率的文本。留空则自动取同一个物体上的 TextMeshProUGUI")]
        [SerializeField] private TextMeshProUGUI m_label;

        [Tooltip("刷新间隔（秒）。刷得太频繁会自己影响帧率，0.25 秒足够跟手")]
        [SerializeField, Range(0.1f, 1f)] private float m_refreshInterval = 0.25f;

        [Tooltip("文本格式，{0} 会被替换成整数帧率")]
        [SerializeField] private string m_format = "FPS {0}";

        [Tooltip("单帧超过这个秒数就当作「异常帧」（场景加载、编辑器暂停）不计入统计。\n" +
                 "不排除的话，一次 4 秒的加载卡顿会让读数直接变成 0，看起来像坏了")]
        [SerializeField, Range(0.1f, 2f)] private float m_maxFrameSeconds = 0.5f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>本次统计区间内累计的真实时间</summary>
        private float m_elapsed;

        /// <summary>本次统计区间内累计的帧数</summary>
        private int m_frames;

        private void Awake()
        {
            if (m_label == null)
            {
                m_label = GetComponent<TextMeshProUGUI>();
            }
        }

        private void Update()
        {
            // 用 unscaledDeltaTime：打开 UI 暂停（timeScale = 0）时帧率仍然有意义
            float deltaTime = Time.unscaledDeltaTime;

            // 超长帧（场景加载、编辑器断点/暂停）不是"游戏真实帧"，计进去会让读数掉到 0。
            // 直接丢掉并重新开始统计，读数就只反映游戏运行中的帧率
            if (deltaTime > m_maxFrameSeconds)
            {
                m_elapsed = 0f;
                m_frames = 0;
                return;
            }

            m_elapsed += deltaTime;
            m_frames++;

            if (m_elapsed < m_refreshInterval)
            {
                return;
            }

            float fps = m_frames / Mathf.Max(0.0001f, m_elapsed);
            m_elapsed = 0f;
            m_frames = 0;

            if (m_label != null)
            {
                m_label.text = string.Format(m_format, Mathf.RoundToInt(fps));
            }
        }
    }
}
