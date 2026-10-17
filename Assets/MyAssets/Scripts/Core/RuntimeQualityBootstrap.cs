using UnityEngine;

namespace MyGame.Core
{
    /// <summary>
    /// 运行期「默认画质」兜底。
    ///
    /// 【为什么需要它】
    /// ProjectSettings/QualitySettings.asset 里的默认画质属于**构建配置**，
    /// 不在热更范围内（热更只能换代码/资源/数据，改不了已打包的 PlayerSettings）。
    /// 也就是说：把默认画质从 Ultra 调到 High 这件事，只有"重新打的包"才吃得到，
    /// 已经发出去的老客户端不会变。
    ///
    /// 这里在启动时补一次：只要玩家没主动改过画质，就套用一个性能友好的默认档。
    /// 因为它写在热更程序集（Game.HotUpdate）里，所以能通过热更下发到老客户端——
    /// 这样就等价于"把构建期默认值改了"。
    ///
    /// 【不会覆盖玩家选择】
    /// 设置面板改画质时会写入 PlayerPrefs("settings.qualityLevel")。
    /// 一旦该键存在，本类就不再干预。
    /// </summary>
    internal static class RuntimeQualityBootstrap
    {
        /// <summary>与设置面板（UGUISettingsPanel）共用的 PlayerPrefs 键，保证两边同源</summary>
        private const string k_qualityPrefsKey = "settings.qualityLevel";

        /// <summary>默认画质档名（对应 ProjectSettings 里的画质等级名）</summary>
        private const string k_defaultQualityName = "High";

        /// <summary>
        /// 编辑器下由 Unity 自动调用。
        ///
        /// ⚠️ 真机（HybridCLR）上这个特性不会执行（热更程序集是运行时才加载的）。
        /// 因此热更入口 GameEntry 还会显式调用一次 Initialize()。两处都调、内部保证幂等。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            Initialize();
        }

        /// <summary>显式初始化：套用默认画质（玩家已设置过则不覆盖）。可安全重复调用。</summary>
        public static void Initialize()
        {
            // 玩家改过画质：尊重玩家选择，不覆盖
            if (PlayerPrefs.HasKey(k_qualityPrefsKey))
            {
                return;
            }

            int index = FindQualityIndexByName(k_defaultQualityName);
            if (index < 0)
            {
                // 画质等级被改名/删除：静默跳过，不猜一个档位硬套
                return;
            }

            QualitySettings.SetQualityLevel(index, true);
        }

        /// <summary>按名字查画质等级下标（按名查而不写死下标，避免等级顺序调整后失效）</summary>
        private static int FindQualityIndexByName(string name)
        {
            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == name)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
