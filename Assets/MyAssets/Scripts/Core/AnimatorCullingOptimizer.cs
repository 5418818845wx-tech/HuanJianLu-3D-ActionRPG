using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyGame.Core
{
    /// <summary>
    /// 全局 Animator 裁剪优化：让镜头外的角色不再无脑更新骨骼动画。
    ///
    /// 【为什么需要它】
    /// Unity 的 Animator 默认是 AlwaysAnimate（永远完整更新）。
    /// 场景里 NPC、敌人、装饰性动画物体一多，即便全在镜头外，
    /// 每个 Animator 仍会逐帧计算骨骼 —— 白白吃掉 CPU（人形骨骼尤其明显）。
    /// 打开裁剪后：看不见时跳过骨骼/蒙皮计算，但**保留状态机与动画事件**
    /// （攻击命中帧、死亡回调不会丢），功能与性能兼顾。
    ///
    /// 【为什么用 CullUpdateTransforms 而不是 CullCompletely】
    /// CullCompletely 会连状态机一起停掉，动画事件不再触发，
    /// 会导致怪物的攻击判定、死亡逻辑失灵。所以只用前者。
    ///
    /// 【怎么用】零配置：游戏启动时自动订阅场景加载事件，
    /// 每次场景加载后对场景内所有「带蒙皮网格的」Animator 生效。
    /// 运行时动态生成的敌人由 EnemyAI 在 Awake 里自行设置，效果一致。
    /// </summary>
    internal static class AnimatorCullingOptimizer
    {
        /// <summary>
        /// 编辑器下由 Unity 自动调用。
        ///
        /// ⚠️ 真机（HybridCLR）上这个特性**不会被执行**：热更程序集是运行时才加载的，
        /// 而 Unity 在启动时就扫描完了 RuntimeInitializeOnLoadMethod，扫不到后来加载的程序集。
        /// 所以热更入口 GameEntry 还会显式调用一次 Initialize()。两处都调、内部保证幂等。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            Initialize();
        }

        /// <summary>
        /// 显式初始化：订阅场景加载事件，并立即处理当前已加载的场景。
        /// 可安全重复调用（订阅用「先减后加」，不会重复累积）。
        /// </summary>
        public static void Initialize()
        {
            // 先减后加，避免重复订阅导致多次执行
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;

            // 可能已有场景加载完成（如 BootScene）：先立刻处理一次
            ApplyCullingToCharacterAnimators();
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplyCullingToCharacterAnimators();
        }

        /// <summary>
        /// 把场景里所有「带 SkinnedMeshRenderer 的」Animator 设为裁剪模式。
        ///
        /// 只处理带蒙皮网格的：这类才是骨骼动画的真正开销来源；
        /// 同时避开了没有渲染器的 Animator（如纯 UI 动画），防止它们被误裁掉。
        /// </summary>
        private static void ApplyCullingToCharacterAnimators()
        {
            Animator[] animators = Object.FindObjectsByType<Animator>(FindObjectsSortMode.None);

            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null)
                {
                    continue;
                }

                // 只挑有蒙皮网格的角色动画器
                if (animator.GetComponentInChildren<SkinnedMeshRenderer>() == null)
                {
                    continue;
                }

                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }
        }
    }
}
