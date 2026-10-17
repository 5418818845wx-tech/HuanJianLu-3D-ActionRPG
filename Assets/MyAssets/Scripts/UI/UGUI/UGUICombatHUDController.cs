using MyGame.Combat;
using MyGame.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 战斗 HUD（UGUI 版）：玩家血条 + 敌人头顶血条宿主 + 战斗状态区
    ///
    /// 职责：
    /// 1. 绑定玩家 CombatUnit，订阅 DamageTaken/Healed/Died 刷新玩家血条与数值文本
    /// 2. 作为敌人头顶血条的宿主：按 prefab 克隆血条实例（UGUIEnemyHealthBar 调用）
    /// 3. 订阅 CombatWaveManager 的状态事件，刷新波次 / 剩余敌人 / 得分
    /// 4. 战斗结束时转发给结算面板：面板关闭时物体是停用的（Update 不执行），
    ///    所以「战斗结束 → 弹结算」这件事必须由常驻的 HUD 来做
    ///
    /// 为什么是"常驻面板"（IsPersistent = true）：
    /// HUD 在战斗中始终可见，不参与弹窗显示栈，也不会被暂停菜单顶掉
    ///
    /// UGUI 与 UI Toolkit 的差异：
    /// - 血条填充用 Image.fillAmount（Image 需设为 Filled/Horizontal），而不是写 style.width
    /// - 数值刷新依然是"事件驱动 + 文本赋值"，这一层没有区别
    /// </summary>
    public class UGUICombatHUDController : UGUIPanel
    {
        /// <summary>敌人血条 prefab 中填充图片所在子物体的名字</summary>
        private const string k_fillChildName = "Fill";

        /// <summary>战斗状态区子节点的名字（Inspector 没接线时的兜底查找用）</summary>
        private const string k_statusChildName = "CombatStatus";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("玩家 HUD")]
        [Tooltip("玩家血条填充图片（Image Type 必须设为 Filled / Horizontal）")]
        [SerializeField] private Image m_playerHpFill;

        [Tooltip("玩家血量数值文本")]
        [SerializeField] private TextMeshProUGUI m_playerHpText;

        [Header("战斗状态区")]
        [Tooltip("战斗状态区的根节点（波次/剩余/得分那一整块）。\n" +
                 "只有波次玩法里才有意义（场景中存在 CombatWaveManager，也就是副本/试炼场）；\n" +
                 "大世界里没有波次，留着会一直显示「准备中 / 剩余 0 / 得分 0」，所以按有无波次管理器自动显隐")]
        [SerializeField] private GameObject m_statusRoot;

        [Tooltip("波次文本（如「第 1 / 3 波」）")]
        [SerializeField] private TextMeshProUGUI m_statusWave;

        [Tooltip("剩余敌人文本")]
        [SerializeField] private TextMeshProUGUI m_statusEnemies;

        [Tooltip("得分文本")]
        [SerializeField] private TextMeshProUGUI m_statusScore;

        [Header("敌人血条")]
        [Tooltip("敌人血条宿主层级（克隆的血条都挂到它下面）")]
        [SerializeField] private RectTransform m_enemyBarLayer;

        [Tooltip("敌人血条 prefab（根节点一个背景图，子物体 Fill 为填充图）")]
        [SerializeField] private GameObject m_enemyBarPrefab;

        [Header("玩家绑定")]
        [Tooltip("玩家未找到时的重试间隔（秒）。玩家可能由网络系统延迟生成")]
        [SerializeField] private float m_playerLookupInterval = 0.5f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>已绑定的玩家战斗单位（null 表示尚未找到）</summary>
        private CombatUnit m_playerUnit;

        /// <summary>下一次尝试绑定玩家的时间点（unscaledTime）</summary>
        private float m_nextPlayerLookupTime;

        /// <summary>已绑定的波次管理器（null 表示尚未找到）</summary>
        private CombatWaveManager m_waveManager;

        /// <summary>是否已订阅波次事件，避免重复订阅</summary>
        private bool m_isWaveSubscribed;

        /// <summary>敌人血条对象池（按需创建；敌人反复进出场时复用血条实例，避免频繁 Instantiate/Destroy）</summary>
        private GameObjectPool m_enemyBarPool;

        // ====================================================================
        // 对外只读访问
        // ====================================================================

        /// <summary>渲染相机（世界坐标换算用），敌人血条会读取它</summary>
        public Camera TargetCamera { get; private set; }

        /// <summary>玩家血量数值文本（外部调试用）</summary>
        public TextMeshProUGUI PlayerHpText => m_playerHpText;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.CombatHUD;

            // 兜底：Inspector 里没接线就按名字找一次（这个子节点就叫 CombatStatus）
            if (m_statusRoot == null)
            {
                Transform status = transform.Find(k_statusChildName);
                if (status != null)
                {
                    m_statusRoot = status.gameObject;
                }
            }

            // 先按"没有波次"处理，等 Update 里真找到波次管理器再显示，避免大世界开局闪一下
            RefreshStatusVisibility();
        }

        private void Update()
        {
            // 仅当面板可见时才执行轮询（节省开销）
            if (!IsVisible)
            {
                return;
            }

            // 相机可能被禁用/销毁（例如场景切换），丢失时重找
            if (TargetCamera == null)
            {
                TargetCamera = Camera.main;
            }

            // 玩家可能晚于本物体生成（网络模式主机生成玩家）：按间隔轮询补绑
            if (m_playerUnit == null && Time.unscaledTime >= m_nextPlayerLookupTime)
            {
                TryBindPlayer();
            }

            // 波次管理器可能晚于本物体初始化：未绑定时持续尝试订阅其状态事件
            if (m_waveManager == null || !m_isWaveSubscribed)
            {
                TryBindWaveManager();
            }

            // 找到/丢失波次管理器都要跟着调整战斗状态区的显隐
            RefreshStatusVisibility();
        }

        /// <summary>
        /// 按"当前场景里有没有波次玩法"决定战斗状态区的显隐。
        /// 大世界（没有 CombatWaveManager）里波次/剩余/得分都是无意义的数据，直接藏起来，
        /// 否则玩家会在大世界看到「准备中 / 剩余 0 / 得分 0」这种副本才有的东西
        /// </summary>
        private void RefreshStatusVisibility()
        {
            if (m_statusRoot == null)
            {
                return;
            }

            bool visible = m_waveManager != null;
            if (m_statusRoot.activeSelf != visible)
            {
                m_statusRoot.SetActive(visible);
            }
        }

        /// <summary>
        /// 面板进入显示：绑定玩家与波次管理器，并先刷新一次显示
        /// </summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 首次缓存相机
            if (TargetCamera == null)
            {
                TargetCamera = Camera.main;
            }

            TryBindPlayer();
            TryBindWaveManager();
            RefreshStatusVisibility();
            RefreshPlayerHud();
        }

        /// <summary>
        /// 面板退出显示：解绑玩家事件防止悬挂引用
        /// 注意：常驻面板通常不会被隐藏，这里保留实现以便场景切换时统一调用
        /// </summary>
        public override void OnExit()
        {
            UnbindPlayer();
            base.OnExit();
        }

        /// <summary>物体销毁（含场景卸载）时解除波次事件订阅</summary>
        protected override void OnDestroy()
        {
            UnbindWaveManager();

            // 宿主销毁时清掉闲置的血条实例（正在使用的会随场景一起销毁）
            if (m_enemyBarPool != null)
            {
                m_enemyBarPool.Clear();
                m_enemyBarPool = null;
            }

            base.OnDestroy();
        }

        // ====================================================================
        // 敌人血条宿主接口
        // ====================================================================

        /// <summary>
        /// 克隆一个敌人血条实例到血条层级
        /// 由 UGUIEnemyHealthBar 在依赖解析时调用
        /// </summary>
        /// <param name="fillImage">输出：血条填充图片（对其设置 fillAmount 表示血量）</param>
        /// <returns>血条根节点（调用方负责定位与显隐），依赖缺失时返回 null</returns>
        public RectTransform CreateEnemyBar(out Image fillImage)
        {
            fillImage = null;

            if (m_enemyBarLayer == null || m_enemyBarPrefab == null)
            {
                return null;
            }

            // 血条会随敌人反复创建/销毁：用对象池复用实例，避免持续 Instantiate/Destroy 引发 GC 卡顿
            if (m_enemyBarPool == null)
            {
                m_enemyBarPool = new GameObjectPool(
                    m_enemyBarPrefab,
                    parent: m_enemyBarLayer,
                    defaultCapacity: 8,
                    maxSize: 64);
            }

            GameObject instance = m_enemyBarPool.GetFromPool();
            RectTransform barRect = instance.GetComponent<RectTransform>();

            // 填充图片是血条根节点下的子物体 Fill
            Transform fillTransform = instance.transform.Find(k_fillChildName);
            if (fillTransform != null)
            {
                fillImage = fillTransform.GetComponent<Image>();
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] 敌人血条 prefab 下未找到名为 '{k_fillChildName}' 的子物体，血条不会显示血量变化", instance);
            }

            // 初始隐藏：满血或依赖未就绪时不显示（显隐由 UGUIEnemyHealthBar 控制）
            instance.SetActive(false);

            return barRect;
        }

        /// <summary>
        /// 归还一个敌人血条实例到对象池（由 UGUIEnemyHealthBar 在敌人隐藏/销毁时调用）。
        /// 注意：归还而不是销毁，这样下一个敌人可以直接复用同一条血条
        /// </summary>
        public void ReleaseEnemyBar(RectTransform barRect)
        {
            if (barRect == null)
            {
                return;
            }

            if (m_enemyBarPool != null)
            {
                m_enemyBarPool.ReturnToPool(barRect.gameObject);
            }
            else
            {
                // 池未建立（异常情况）：兜底销毁，不留残留
                Destroy(barRect.gameObject);
            }
        }

        // ====================================================================
        // 玩家绑定与刷新
        // ====================================================================

        /// <summary>查找并绑定玩家战斗单位（失败时静默等待下次轮询）</summary>
        private void TryBindPlayer()
        {
            m_nextPlayerLookupTime = Time.unscaledTime + m_playerLookupInterval;

            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player == null)
            {
                return;
            }

            CombatUnit unit = player.GetComponent<CombatUnit>();
            if (unit == null)
            {
                return;
            }

            m_playerUnit = unit;
            m_playerUnit.DamageTaken += HandlePlayerDamageTaken;
            m_playerUnit.Healed += HandlePlayerHealed;
            m_playerUnit.Died += HandlePlayerDied;

            // 绑定后立即刷新一次，显示当前血量
            RefreshPlayerHud();
        }

        /// <summary>解绑玩家事件（可安全重复调用）</summary>
        private void UnbindPlayer()
        {
            if (m_playerUnit == null)
            {
                return;
            }

            m_playerUnit.DamageTaken -= HandlePlayerDamageTaken;
            m_playerUnit.Healed -= HandlePlayerHealed;
            m_playerUnit.Died -= HandlePlayerDied;
            m_playerUnit = null;
        }

        /// <summary>玩家受伤事件回调：刷新血条</summary>
        private void HandlePlayerDamageTaken(float damage)
        {
            RefreshPlayerHud();
        }

        /// <summary>玩家被治疗事件回调：刷新血条（喝药回血走这条）</summary>
        private void HandlePlayerHealed(float amount)
        {
            RefreshPlayerHud();
        }

        /// <summary>玩家死亡事件回调：血条清零</summary>
        private void HandlePlayerDied(CombatUnit diedUnit)
        {
            RefreshPlayerHud();
        }

        /// <summary>按当前血量刷新玩家血条填充量与数值文本</summary>
        private void RefreshPlayerHud()
        {
            if (m_playerUnit == null)
            {
                return;
            }

            float ratio = m_playerUnit.MaxHP > 0f ? m_playerUnit.CurrentHP / m_playerUnit.MaxHP : 0f;

            if (m_playerHpFill != null)
            {
                m_playerHpFill.fillAmount = ratio;
            }

            if (m_playerHpText != null)
            {
                m_playerHpText.text = $"{m_playerUnit.CurrentHP:0} / {m_playerUnit.MaxHP:0}";
            }
        }

        // ====================================================================
        // 战斗状态区（波次 / 剩余敌人 / 得分）
        // ====================================================================

        /// <summary>查找并订阅波次管理器的状态事件（失败时静默等待下次轮询）</summary>
        private void TryBindWaveManager()
        {
            m_waveManager = FindFirstObjectByType<CombatWaveManager>();
            if (m_waveManager == null)
            {
                return;
            }

            m_waveManager.WaveStarted += HandleWaveStarted;
            m_waveManager.AliveCountChanged += HandleAliveCountChanged;
            m_waveManager.ScoreChanged += HandleScoreChanged;
            m_waveManager.CombatEnded += HandleCombatEnded;
            m_isWaveSubscribed = true;

            // 绑定后立即同步一次当前状态：若订阅时机晚于第一波开始，不会错过已广播的事件
            RefreshWaveStatus(
                m_waveManager.CurrentWave,
                m_waveManager.TotalWaves,
                m_waveManager.AliveCount,
                m_waveManager.Score);
        }

        /// <summary>解除波次事件订阅（可安全重复调用）</summary>
        private void UnbindWaveManager()
        {
            if (m_waveManager != null && m_isWaveSubscribed)
            {
                m_waveManager.WaveStarted -= HandleWaveStarted;
                m_waveManager.AliveCountChanged -= HandleAliveCountChanged;
                m_waveManager.ScoreChanged -= HandleScoreChanged;
                m_waveManager.CombatEnded -= HandleCombatEnded;
            }

            m_isWaveSubscribed = false;
        }

        /// <summary>新波次开始：刷新波次文本并同步当前剩余/得分</summary>
        private void HandleWaveStarted(int wave, int total)
        {
            int alive = m_waveManager != null ? m_waveManager.AliveCount : 0;
            int score = m_waveManager != null ? m_waveManager.Score : 0;
            RefreshWaveStatus(wave, total, alive, score);
        }

        /// <summary>存活敌人数变化：只刷新剩余文本</summary>
        private void HandleAliveCountChanged(int alive)
        {
            if (m_statusEnemies != null)
            {
                m_statusEnemies.text = $"剩余 {alive}";
            }
        }

        /// <summary>得分变化：只刷新得分文本</summary>
        private void HandleScoreChanged(int score)
        {
            if (m_statusScore != null)
            {
                m_statusScore.text = $"得分 {score}";
            }
        }

        /// <summary>
        /// 战斗结束：把结果转交给结算面板弹出
        ///
        /// 为什么由 HUD 转发而不是结算面板自己订阅：
        /// 结算面板在战斗结束前是关闭（物体停用）的，它的 Update 与自身订阅都不会生效；
        /// HUD 是常驻面板，始终在跑，所以由它担任这个「转发者」
        /// </summary>
        private void HandleCombatEnded(CombatResult result)
        {
            UGUIResultPanel resultPanel = m_manager != null
                ? m_manager.GetPanel<UGUIResultPanel>(EUIPanelID.Result)
                : null;

            if (resultPanel == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 未找到结算面板，无法显示战斗结果", this);
                return;
            }

            resultPanel.ShowResult(result);
        }

        /// <summary>统一刷新状态区三个文本</summary>
        private void RefreshWaveStatus(int wave, int total, int alive, int score)
        {
            if (m_statusWave != null)
            {
                m_statusWave.text = wave > 0 ? $"第 {wave} / {total} 波" : "准备中";
            }

            HandleAliveCountChanged(alive);
            HandleScoreChanged(score);
        }
    }
}
