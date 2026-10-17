using System.Collections;
using MyGame.Boot;
using MyGame.Combat;
using MyGame.HotUpdate;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 场景加载器：全项目所有切场景统一走这里，负责「异步加载 + 加载界面」
    ///
    /// 界面本体是一个 prefab（HotUpdateSources/BundledAssets/LoadingScreen.prefab）：
    /// 视觉层级全在 prefab 里搭好、在编辑器里直接看得见，本脚本只负责
    /// 「显示 / 隐藏 / 填文字 / 推进度」——与项目里其它 UGUIPanel 的分工保持一致。
    /// 想改外观就直接双击那个 prefab，不用碰这个文件。
    ///
    /// ⚠️ 这个 prefab 为什么不能放 Resources：
    /// 它身上挂着本脚本，而本脚本在**热更程序集**里。Resources 资源是打包时就固化进
    /// 包体的，那时热更程序集已被排除在包体之外，Unity 找不到这个类型，会把它序列化成
    /// missing script —— 表现就是运行时加载界面根本建不出来。
    /// 所以改成走资源包加载：资源包里的引用在运行时才解析，那时热更 DLL 已经加载了。
    ///
    /// 为什么必须挂在 DontDestroyOnLoad 上，而不能像别的面板那样摆在场景里：
    /// 加载界面要在「切场景的那一瞬间」显示，而那一刻新旧两个场景都不存在；
    /// 摆在场景里的话，它会跟着旧场景一起被销毁，自己也就没了。
    /// 所以由本脚本在运行时把 prefab 实例化出来，并挪到 DontDestroyOnLoad 下。
    ///
    /// 为什么必须收口到这里：
    /// 原来各处直接调 SceneManager.LoadScene，那是**同步**加载——主线程会一直卡在
    /// 卸载旧场景 + 加载新场景上，期间画面完全冻结，玩家只看到"卡了很久"。
    /// 换成 LoadSceneAsync 后主线程不再被独占，我们才能插一个加载界面进去。
    ///
    /// 用法（替换掉原来的 SceneManager.LoadScene）：
    ///   SceneLoader.Load("Demo_Scene_Puddle", "松林试炼场");   // 场景名 + 显示名
    ///   SceneLoader.Load("MainScene");                        // 显示名自动取
    ///   SceneLoader.ReloadCurrent();                          // 重新加载当前场景
    /// </summary>
    public class SceneLoader : MonoBehaviour
    {
        // ====================================================================
        // 常量
        // ====================================================================

        /// <summary>加载界面 prefab 在资源包里的定位地址（也是文件名）</summary>
        private const string k_prefabResourcePath = "LoadingScreen";

#if UNITY_EDITOR
        /// <summary>编辑器里直接用工程路径取（不走启动壳时没有资源服务）</summary>
        private const string k_prefabEditorAssetPath = "Assets/MyAssets/HotUpdateSources/BundledAssets/LoadingScreen.prefab";
#endif

        /// <summary>
        /// 资源加载阶段进度条最多涨到这里（剩下的留给"场景实例化"那一段）。
        ///
        /// Unity 的 AsyncOperation.progress 有个特点：资源读完就直接卡在 0.9 不动了，
        /// 最后那 0.1 要等场景真正激活才走完。所以 0~0.9 这段才是真正"在加载"的部分
        /// </summary>
        private const float k_dataPhaseMax = 0.9f;

        /// <summary>
        /// 等待场景切换完成期间，进度条最多涨到这里。
        ///
        /// 刻意**不给 1.0**：进度条只有在场景真的切好之后才会显示 100%，
        /// 这样玩家永远不会看到"已经 100% 了却还停在加载界面"的状态
        /// </summary>
        private const float k_readyPhaseMax = 0.99f;

        /// <summary>
        /// 算进度用的单帧时间上限（秒）。
        ///
        /// 加载大场景时单帧可能卡到几百毫秒，直接拿 Time.unscaledDeltaTime 去推进度的话，
        /// 一帧就能把条推到底——玩家看到的就是"进度条瞬间满了"。
        /// 封顶之后，即使卡顿，进度条也是一帧走一小格，看起来才是平滑在涨
        /// </summary>
        private const float k_maxProgressStepSeconds = 0.05f;

        // ====================================================================
        // 序列化字段：界面引用（prefab 里已经接好，正常不用动）
        // ====================================================================

        [Header("界面引用（prefab 里已接好）")]
        [Tooltip("整块加载界面的根节点。显隐靠它，注意别把它自己设成 inactive 之外的处理")]
        [SerializeField] private GameObject m_visualRoot;

        [Tooltip("顶部目标名（这次要去的秘境名 / 场景名）")]
        [SerializeField] private TextMeshProUGUI m_titleText;

        [Tooltip("顶部副标题")]
        [SerializeField] private TextMeshProUGUI m_subtitleText;

        [Tooltip("左下角玩家名 + 等级")]
        [SerializeField] private TextMeshProUGUI m_playerText;

        [Tooltip("底部小提示")]
        [SerializeField] private TextMeshProUGUI m_tipText;

        [Tooltip("进度条填充段。进度靠改它的 anchorMax.x 表现，不要改成写死宽度")]
        [SerializeField] private RectTransform m_barFill;

        [Tooltip("百分比文字")]
        [SerializeField] private TextMeshProUGUI m_progressText;

        // ====================================================================
        // 序列化字段：文案与节奏（直接在 prefab 上改就行）
        // ====================================================================

        [Header("文案")]
        [Tooltip("左下角显示的玩家名")]
        [SerializeField] private string m_playerName = "旅行者";

        [Tooltip("底部轮播的小提示。留空则不显示提示行")]
        [SerializeField, TextArea(2, 4)] private string[] m_tips = new string[0];

        [Tooltip("副标题固定文案")]
        [SerializeField] private string m_subtitle = "加载中，请稍候";

        [Header("节奏（秒，都用真实时间，不受 timeScale 影响）")]
        [Tooltip("加载界面最短显示时长。场景很小、秒开时也必须留够这么久，" +
                 "否则会一闪而过、玩家根本看不到加载界面")]
        [SerializeField, Min(0f)] private float m_minShowSeconds = 0.6f;

        [Tooltip("进度条追赶真实进度的平滑时间。越小越跟手，越大越平滑")]
        [SerializeField, Min(0.05f)] private float m_progressSmoothSeconds = 0.35f;

        [Tooltip("进度条补满 100% 的收尾时长。填 0 也会有一个很短的最小值，避免瞬间跳满")]
        [SerializeField, Min(0f)] private float m_progressFinishSeconds = 0.15f;

        [Tooltip("新场景激活后再多等一会儿才撤掉加载界面。一般保持 0：等进度条补满的这段时间，" +
                 "新场景已经在背后渲染好了")]
        [SerializeField, Min(0f)] private float m_postLoadDelay = 0f;

        [Tooltip("小提示轮播间隔")]
        [SerializeField, Min(0.1f)] private float m_tipInterval = 3f;

        // ====================================================================
        // 单例与运行时状态
        // ====================================================================

        /// <summary>常驻单例（跨场景存活）</summary>
        private static SceneLoader s_instance;

        /// <summary>当前**显示**出来的进度（0~1）。它平滑地追向真实进度，而不是直接等于真实进度</summary>
        private float m_displayedProgress;

        /// <summary>是否正在加载（防重复调用）</summary>
        private bool m_isLoading;

        /// <summary>本次加载是否已失败（失败清理由加载子流程完成，主流程据此提前收工）</summary>
        private bool m_loadFailed;

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>
        /// 异步加载指定场景并显示加载界面。
        ///
        /// 场景名必须**不带路径**，且已在 Build Settings 里注册。
        /// displayName 是加载界面上那句大标题（如秘境名）；不传则按场景名自动取一个
        /// </summary>
        public static void Load(string sceneName, string displayName = null)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                Debug.LogError($"[{nameof(SceneLoader)}] 场景名为空，无法加载");
                return;
            }

            SceneLoader loader = EnsureInstance();
            if (loader == null)
            {
                // 连加载界面都建不出来（prefab 丢了）也不能让流程卡死：
                // 退化成直接同步加载，至少能进游戏
                Debug.LogError($"[{nameof(SceneLoader)}] 加载界面不可用，改为直接加载场景 {sceneName}");
                SceneManager.LoadScene(sceneName);
                return;
            }

            loader.BeginLoad(sceneName, ResolveDisplayName(sceneName, displayName));
        }

        /// <summary>重新加载当前场景（结算面板的「重试」用）</summary>
        public static void ReloadCurrent()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            Load(activeScene.name);
        }

        /// <summary>
        /// 决定加载界面上显示的名字：调用方给了就用调用方的，否则查内置对照表，
        /// 再查不到就直接用场景名（至少不会显示空白）
        /// </summary>
        private static string ResolveDisplayName(string sceneName, string displayName)
        {
            if (!string.IsNullOrEmpty(displayName))
            {
                return displayName;
            }

            switch (sceneName)
            {
                case "MainScene":
                    return "主菜单";
                case "Demo_Scene_Pines_1":
                    return "松林秘境";
                case "Demo_Scene_Puddle":
                    return "松林试炼场";
                default:
                    return sceneName;
            }
        }

        // ====================================================================
        // 单例
        // ====================================================================

        /// <summary>取常驻单例；没有就从 Resources 实例化一份 prefab 并挪到 DontDestroyOnLoad 下</summary>
        private static SceneLoader EnsureInstance()
        {
            if (s_instance != null)
            {
                return s_instance;
            }

            GameObject prefab = LoadLoadingScreenPrefab();
            if (prefab == null)
            {
                Debug.LogError($"[{nameof(SceneLoader)}] 找不到加载界面 prefab（{k_prefabResourcePath}），加载界面无法显示");
                return null;
            }

            GameObject instance = Instantiate(prefab);
            instance.name = prefab.name;

            // 挂到 DontDestroyOnLoad 上，这样它能在"两个场景都不存在"的空档期持续显示
            DontDestroyOnLoad(instance);

            s_instance = instance.GetComponent<SceneLoader>();
            if (s_instance == null)
            {
                Debug.LogError($"[{nameof(SceneLoader)}] prefab 上没有挂 {nameof(SceneLoader)} 脚本", instance);
                return null;
            }

            s_instance.SetVisible(false);
            return s_instance;
        }

        /// <summary>
        /// 取加载界面 prefab。
        ///
        /// 优先走资源包：热更程序集里的脚本挂在 prefab 上，内置 Resources 在打包阶段
        /// 解析不了热更类型（详见类注释里的说明），所以必须从资源包加载。
        /// 保留 Resources 兜底只是为了"万一没进资源包"时还能退化，正常情况下走不到。
        /// </summary>
        private static GameObject LoadLoadingScreenPrefab()
        {
            IAssetService assets = HotUpdateRuntime.Assets;
            if (assets != null && assets.Exists(k_prefabResourcePath))
            {
                GameObject fromBundle = assets.LoadAsset<GameObject>(k_prefabResourcePath);
                if (fromBundle != null)
                {
                    return fromBundle;
                }
            }

#if UNITY_EDITOR
            // 编辑器里常常是"直接打开某个场景按播放"（没走启动壳），此时没有资源服务；
            // 直接按工程路径取，保证编辑器里随时能迭代加载界面
            GameObject fromAssetDatabase = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(k_prefabEditorAssetPath);
            if (fromAssetDatabase != null)
            {
                return fromAssetDatabase;
            }
#endif

            return Resources.Load<GameObject>(k_prefabResourcePath);
        }

        private void Awake()
        {
            // 场景里万一被人手动摆了一份（不该发生），保留第一个
            if (s_instance != null && s_instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在加载界面实例，重复实例将被销毁", this);
                Destroy(gameObject);
                return;
            }

            s_instance = this;
        }

        private void OnDestroy()
        {
            if (s_instance == this)
            {
                s_instance = null;
            }
        }

        // ====================================================================
        // 加载流程
        // ====================================================================

        /// <summary>开始一次加载（已在加载中则忽略，避免两次调用打架）</summary>
        private void BeginLoad(string sceneName, string displayName)
        {
            if (m_isLoading)
            {
                Debug.LogWarning($"[{GetType().Name}] 上一次加载尚未结束，忽略对场景 {sceneName} 的重复请求", this);
                return;
            }

            StartCoroutine(LoadRoutine(sceneName, displayName));
        }

        private IEnumerator LoadRoutine(string sceneName, string displayName)
        {
            m_isLoading = true;
            m_loadFailed = false;
            m_displayedProgress = 0f;

            ApplyStaticTexts(displayName);

            // 冻结旧场景：加载界面盖住一切的时候，玩家不该还在原地走动
            // （也顺便把弹窗暂停留下的 timeScale=0 统一复位，免得干扰协程节奏）
            Time.timeScale = 0f;

            SetProgress(0f);
            SetVisible(true);

            // 等一帧，确保加载界面这一帧就已经画到屏幕上，再做耗时的卸载
            yield return null;

            float startTime = Time.unscaledTime;

            // 场景来源优先资源包：带业务脚本的场景**必须**走资源包
            // （包体里的资源在打包阶段解析不了热更类型，会整场景变 missing script），
            // 只有资源包里没有的才退回 Build Settings 的内置场景
            IAssetService assets = HotUpdateRuntime.Assets;
            if (assets != null && assets.Exists(sceneName))
            {
                yield return LoadSceneFromBundleRoutine(assets, sceneName, startTime);
            }
            else
            {
                yield return LoadSceneFromBuildSettingsRoutine(sceneName, startTime);
            }

            if (m_loadFailed)
            {
                // 失败清理由子流程完成，这里直接收工
                yield break;
            }

            // === 阶段 3：场景已经切好了，把最后一点点补满，补满就立刻撤界面 ===
            // 关键是「100% 只在真正就绪之后才出现」，而且一出现就马上进游戏，
            // 不会出现"满了还停在加载界面"的情况
            float finishSpeed = 1f / Mathf.Max(0.05f, m_progressFinishSeconds);

            while (m_displayedProgress < 1f)
            {
                float step = Mathf.Min(Time.unscaledDeltaTime, k_maxProgressStepSeconds);
                m_displayedProgress = Mathf.MoveTowards(m_displayedProgress, 1f, finishSpeed * step);
                SetProgress(m_displayedProgress);
                yield return null;
            }

            SetProgress(1f);

            yield return new WaitForEndOfFrame();

            if (m_postLoadDelay > 0f)
            {
                yield return new WaitForSecondsRealtime(m_postLoadDelay);
            }

            SetVisible(false);
            m_isLoading = false;
        }

        // ====================================================================
        // 两条场景加载来源（只负责"阶段 1 读资源 + 阶段 2 等就绪"，收尾部分共用）
        // ====================================================================

        /// <summary>
        /// 从资源包加载场景 —— 热更场景（带业务脚本的场景）走这条路。
        ///
        /// 进度用资源服务回报的真实进度驱动，节奏与内置场景那条保持一致：
        /// 资源阶段最多涨到 k_dataPhaseMax，剩下的 0.9~0.99 留给"等场景真正就绪"。
        /// </summary>
        private IEnumerator LoadSceneFromBundleRoutine(IAssetService assets, string sceneName, float startTime)
        {
            float realProgress = 0f;
            bool finished = false;
            bool success = false;
            string error = null;

            // 资源服务自己是个普通对象（不是 MonoBehaviour），所以由本组件替它跑协程
            StartCoroutine(assets.LoadSceneAsync(sceneName,
                progress => realProgress = progress,
                (ok, message) =>
                {
                    finished = true;
                    success = ok;
                    error = message;
                }));

            float nextTipTime = Time.unscaledTime + m_tipInterval;

            // === 阶段 1：读资源 ===
            while (!finished && realProgress < 0.9f)
            {
                ChaseProgress(realProgress / 0.9f * k_dataPhaseMax);
                nextTipTime = RefreshTipIfDue(nextTipTime);
                yield return null;
            }

            // === 阶段 2：等场景就绪 + 等够"最短显示时长" ===
            Time.timeScale = 1f;

            while (!finished || Time.unscaledTime - startTime < m_minShowSeconds)
            {
                ChaseProgress(k_readyPhaseMax);
                nextTipTime = RefreshTipIfDue(nextTipTime);
                yield return null;
            }

            if (!success)
            {
                Debug.LogError($"[{GetType().Name}] 从资源包加载场景 {sceneName} 失败：{error}", this);
                Time.timeScale = 1f;
                SetVisible(false);
                m_isLoading = false;
                m_loadFailed = true;
            }
        }

        /// <summary>
        /// 从 Build Settings 加载内置场景（兜底路径）。
        /// 注意：内置场景里如果挂了热更脚本，运行时会是 missing script —— 所以这条路
        /// 只适合纯内置场景；带业务脚本的场景必须进资源包。
        /// </summary>
        private IEnumerator LoadSceneFromBuildSettingsRoutine(string sceneName, float startTime)
        {
            AsyncOperation operation = SceneManager.LoadSceneAsync(sceneName);
            if (operation == null)
            {
                Debug.LogError($"[{GetType().Name}] 无法加载场景 {sceneName}，请确认它已加入 Build Settings", this);
                Time.timeScale = 1f;
                SetVisible(false);
                m_isLoading = false;
                m_loadFailed = true;
                yield break;
            }

            float nextTipTime = Time.unscaledTime + m_tipInterval;

            // 抓住"什么时候真正切过去"这个时机。
            // 不这样做的话，进度条会在资源读完的瞬间就冲到 100%，然后干等场景切换
            operation.allowSceneActivation = false;

            // === 阶段 1：读资源。真实进度 0~0.9 映射到进度条的 0~0.9 ===
            while (operation.progress < 0.9f)
            {
                ChaseProgress(operation.progress / 0.9f * k_dataPhaseMax);
                nextTipTime = RefreshTipIfDue(nextTipTime);
                yield return null;
            }

            // === 阶段 2：放行激活；进度条继续往 0.99 爬，同时等够"最短显示时长" ===
            // 先把时间流速复位成 1（旧场景在这一刻即将被卸载）；
            // 新场景的 UGUIManager 起来后会按它自己的弹窗栈再校正一次
            Time.timeScale = 1f;
            operation.allowSceneActivation = true;

            // 两个条件都要满足才往下走：
            //   - 场景真的切好了（isDone）
            //   - 加载界面已经显示够 m_minShowSeconds
            // 这样"秒开的场景"也不会闪一下就过去，玩家每次都看得见加载界面
            while (!operation.isDone || Time.unscaledTime - startTime < m_minShowSeconds)
            {
                ChaseProgress(k_readyPhaseMax);
                nextTipTime = RefreshTipIfDue(nextTipTime);
                yield return null;
            }
        }

        /// <summary>
        /// 让进度条平滑地追向目标进度。
        ///
        /// 为什么不能直接把真实进度赋给进度条：
        /// 1. Unity 的 progress 在资源读完后会**卡在 0.9 不动**，直接赋值就是"冲到底然后不动"
        /// 2. 它的增长本身就是一跳一跳的，直接赋值会看到进度条一帧跳一大截
        ///
        /// 这里用"限速追赶"：基础速度保证慢加载时也看得见在涨，
        /// 同时保证最多 m_progressSmoothSeconds 秒就能追上目标，不会落后太多
        /// </summary>
        private void ChaseProgress(float target)
        {
            float safeSeconds = Mathf.Max(0.05f, m_progressSmoothSeconds);

            float speed = Mathf.Max(0.35f, (target - m_displayedProgress) / safeSeconds);

            // 单帧时间封顶，避免加载卡顿时进度条一帧跳到底
            float step = Mathf.Min(Time.unscaledDeltaTime, k_maxProgressStepSeconds);

            m_displayedProgress = Mathf.MoveTowards(m_displayedProgress, target, speed * step);
            SetProgress(m_displayedProgress);
        }

        /// <summary>到点就换一条小提示（用真实时间计时，不受 timeScale=0 影响）</summary>
        private float RefreshTipIfDue(float nextTipTime)
        {
            if (Time.unscaledTime < nextTipTime)
            {
                return nextTipTime;
            }

            ApplyTip();
            return Time.unscaledTime + m_tipInterval;
        }

        /// <summary>把本次加载的固定文案填进界面（目标名 / 玩家名 / 首条提示）</summary>
        private void ApplyStaticTexts(string displayName)
        {
            if (m_titleText != null)
            {
                m_titleText.text = displayName;
            }

            if (m_subtitleText != null)
            {
                m_subtitleText.text = m_subtitle;
            }

            if (m_playerText != null)
            {
                m_playerText.text = $"Lv.{ResolvePlayerLevel()}   {m_playerName}";
            }

            ApplyTip();
        }

        /// <summary>换一条随机小提示</summary>
        private void ApplyTip()
        {
            if (m_tipText == null)
            {
                return;
            }

            if (m_tips == null || m_tips.Length == 0)
            {
                m_tipText.text = string.Empty;
                return;
            }

            string tip = m_tips[Random.Range(0, m_tips.Length)];
            m_tipText.text = string.IsNullOrEmpty(tip) ? string.Empty : $"小提示：{tip}";
        }

        /// <summary>读玩家等级（玩家可能还没生成，取不到就按 1 级显示）</summary>
        private static int ResolvePlayerLevel()
        {
            PlayerLevelSystem levelSystem = FindFirstObjectByType<PlayerLevelSystem>();
            return levelSystem != null ? levelSystem.Level : 1;
        }

        // ====================================================================
        // 界面辅助
        // ====================================================================

        /// <summary>显隐整块加载界面</summary>
        private void SetVisible(bool visible)
        {
            if (m_visualRoot != null)
            {
                m_visualRoot.SetActive(visible);
            }
        }

        /// <summary>设置进度（0~1），同时刷新进度条长度与百分比文字</summary>
        private void SetProgress(float normalized)
        {
            float value = Mathf.Clamp01(normalized);

            // 进度条长度用锚点表示：anchorMax.x 就是"占底槽的百分比"。
            // 底槽是左右拉伸的，所以这里不用关心画布实际多少像素，任何分辨率都正确
            if (m_barFill != null)
            {
                m_barFill.anchorMax = new Vector2(value, 1f);
            }

            if (m_progressText != null)
            {
                m_progressText.text = Mathf.RoundToInt(value * 100f) + "%";
            }
        }
    }
}
