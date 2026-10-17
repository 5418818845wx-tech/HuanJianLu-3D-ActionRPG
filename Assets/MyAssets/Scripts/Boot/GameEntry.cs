using System.Collections;
using MyGame.Boot;
using MyGame.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyGame.HotUpdate
{
    /// <summary>
    /// 热更程序集的启动入口
    ///
    /// 启动壳（AOT）扫描到它之后会调用 StartGame，把「接力棒」交出来。
    /// 从这里开始的代码，全部可以被热更替换。
    ///
    /// 它本身不是 MonoBehaviour（不能直接开协程），所以：
    /// 创建一个 GameObject 挂一个热更自己的 MonoBehaviour 去跑启动协程——
    /// 这一步顺带验证了"热更程序集里的 MonoBehaviour 能被正常 AddComponent"，
    /// 这正是热更能不能落地的关键能力之一。
    /// </summary>
    public class GameEntry : IHotUpdateEntry
    {
        /// <summary>主场景的定位地址（同时用作 YooAsset 的 location 与 Build Settings 里的场景名）</summary>
        public const string MainSceneLocation = "MainScene";

        /// <summary>配置表名</summary>
        public const string ConfigTableName = "game_config";

        /// <summary>全局配置（其它系统可从 HotUpdateRuntime.Config 读取）</summary>
        public static GameConfig Config { get; private set; }

        public void StartGame(IHotUpdateContext context)
        {
            GameObject host = new GameObject("[HotUpdateEntry]");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<GameEntryRunner>().Initialize(context);
        }

        /// <summary>由启动协程写入配置，供其它系统读取</summary>
        internal static void SetConfig(GameConfig config)
        {
            Config = config;
        }
    }

    /// <summary>启动协程宿主（热更程序集里的 MonoBehaviour）</summary>
    public class GameEntryRunner : MonoBehaviour
    {
        private IHotUpdateContext m_context;

        public void Initialize(IHotUpdateContext context)
        {
            m_context = context;
        }

        private void Start()
        {
            StartCoroutine(BootRoutine());
        }

        private IEnumerator BootRoutine()
        {
            // 先把服务挂到全局入口：静态工具类（如 SceneLoader）要靠它加载资源包里的东西
            HotUpdateRuntime.Initialize(m_context);

            // 性能基线：必须在这里**显式**调用。
            // 原因：真机上热更程序集是运行时才 Assembly.Load 进来的，
            // 其上的 [RuntimeInitializeOnLoadMethod] 不会被 Unity 扫描执行，
            // 所以不能只靠特性，要在热更入口主动触发一次。
            RuntimeQualityBootstrap.Initialize();
            AnimatorCullingOptimizer.Initialize();
            URPPerformanceTuner.Initialize();

            LogBootSummary();

            // ---------- 数据通道：读热更数据表 ----------
            GameConfig config = LoadConfig();
            if (config != null)
            {
                // 数据表的一个真实用途：控制帧率（改表即可生效，无需重编代码）
                if (config.targetFrameRate > 0)
                {
                    Application.targetFrameRate = config.targetFrameRate;
                }
                GameEntry.SetConfig(config);
                Debug.Log($"[HotUpdateEntry] 配置已生效：{config.gameTitle}｜金币 {config.startingGold}｜" +
                          $"经验倍率 {config.expRate}｜目标帧率 {config.targetFrameRate}");
            }

            // ---------- 资源通道：加载主场景 ----------
            yield return LoadMainSceneRoutine();
        }

        private void LogBootSummary()
        {
            Debug.Log(
                "[HotUpdateEntry] === 热更链路汇总 ===\n" +
                $"  包体版本：{m_context.AppVersion}\n" +
                $"  代码版本：{m_context.CodeVersion}\n" +
                $"  资源版本：{m_context.ResourceVersion}\n" +
                $"  数据版本：{m_context.DataVersion}\n" +
                $"  本次启动是否更新：{(m_context.DidUpdate ? "是" : "否")}\n" +
                $"  可写目录：{m_context.WritableRoot}\n" +
                $"  数据表目录下共有：{string.Join("、", m_context.DataTables.ListTables())}");
        }

        private GameConfig LoadConfig()
        {
            GameConfig config = m_context.DataTables?.LoadTable<GameConfig>(GameEntry.ConfigTableName);
            if (config == null)
            {
                // 数据表缺失不该让游戏起不来：退回代码里的默认值，并明确提示
                Debug.LogWarning($"[HotUpdateEntry] 没读到数据表 {GameEntry.ConfigTableName}，使用代码内默认配置");
                return new GameConfig();
            }
            return config;
        }

        private IEnumerator LoadMainSceneRoutine()
        {
            // 优先从资源包加载：这样"关卡布局/摆放改动"也算热更内容
            if (m_context.Assets != null && m_context.Assets.Exists(GameEntry.MainSceneLocation))
            {
                bool done = false;
                bool success = false;
                string error = null;

                yield return m_context.Assets.LoadSceneAsync(GameEntry.MainSceneLocation, null, (ok, message) =>
                {
                    success = ok;
                    error = message;
                    done = true;
                });

                if (done && success)
                {
                    yield break;
                }

                Debug.LogWarning($"[HotUpdateEntry] 从资源包加载主场景失败（{error}），改用 Build Settings 里的同名场景");
            }

            // 兜底：场景不进资源包时，直接按 Build Settings 加载（编辑器里最常用）
            if (Application.CanStreamedLevelBeLoaded(GameEntry.MainSceneLocation))
            {
                SceneManager.LoadScene(GameEntry.MainSceneLocation);
            }
            else
            {
                Debug.LogError($"[HotUpdateEntry] 场景 {GameEntry.MainSceneLocation} 既不在资源包里，也不在 Build Settings 中，无法进入游戏");
            }
        }
    }
}
