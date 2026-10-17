using UnityEngine;

namespace MyGame.UI
{
    /// <summary>
    /// 目标帧率设置：把玩家的选择存进 PlayerPrefs，并在每次进游戏时立刻套用。
    ///
    /// 【为什么单独一个常驻脚本，而不是把逻辑全写在设置面板里】
    /// 设置面板是弹窗，平时是 SetActive(false) 的——不打开面板就不会执行它的 Awake，
    /// 那样"上次设的帧率"要等玩家再打开一次设置才生效，重启游戏等于没生效。
    /// 这个脚本挂在 GameRoot 上，每次进游戏第一帧就把存档套上。
    ///
    /// 【一个必须知道的坑】
    /// Application.targetFrameRate 在**垂直同步开着时完全无效**，
    /// 因为此时帧率由显示器刷新率决定（60Hz 屏就是 60，144Hz 屏就是 144）。
    /// 所以想真正限帧，必须同时把 QualitySettings.vSyncCount 设成 0。
    /// </summary>
    public class FrameRateSetting : MonoBehaviour
    {
        /// <summary>PlayerPrefs 键名（设置面板读写的是同一个键）</summary>
        public const string PrefsKey = "settings.targetFrameRate";

        /// <summary>「不限帧」用 -1 表示（与 Unity 的默认值保持一致）</summary>
        public const int Unlimited = -1;

        private void Awake()
        {
            // 没有存档就什么都不做，不去覆盖 Unity 的默认设置
            if (!PlayerPrefs.HasKey(PrefsKey))
            {
                return;
            }

            Apply(PlayerPrefs.GetInt(PrefsKey, Unlimited));
        }

        /// <summary>套用目标帧率（小于等于 0 表示不限帧）</summary>
        public static void Apply(int targetFrameRate)
        {
            if (targetFrameRate <= 0)
            {
                // 不限帧：交回给垂直同步 / 系统默认，帧率跟随显示器刷新率
                QualitySettings.vSyncCount = 1;
                Application.targetFrameRate = Unlimited;
                return;
            }

            // 限帧：必须先关掉垂直同步，否则这个值不起任何作用
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = targetFrameRate;
        }

        /// <summary>读取当前实际生效的目标帧率（-1 = 不限），设置面板回显用</summary>
        public static int Resolve()
        {
            // 垂直同步开着时帧率被刷新率封顶，统一视作"不限"
            if (QualitySettings.vSyncCount > 0)
            {
                return Unlimited;
            }

            return Application.targetFrameRate;
        }
    }
}
