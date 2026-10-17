using MyGame.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 角色属性面板（UGUI 版）：C 键打开的居中弹窗，展示玩家战斗属性
    ///
    /// 职责：
    /// 1. 显示玩家的生命值（数值 + 进度条）、攻击力、防御力
    /// 2. 订阅玩家 CombatUnit 的 DamageTaken/Healed/Died 事件，数值变化时实时刷新
    /// 3. 玩家可能由网络系统延迟生成，因此按间隔轮询补绑（与 CombatHUDController 同一套思路）
    ///
    /// 绑定方式：本面板全是固定控件，全部用 Inspector 拖曳绑定
    /// （「关闭」按钮直接拖 OnClick；数值文本/进度条拖到对应序列化字段）
    ///
    /// 架构说明：
    /// - 面板只负责展示，不做数值计算；所有数据来源于 CombatUnit 与 PlayerLevelSystem 的只读属性
    /// - 等级与经验来自 PlayerLevelSystem，订阅其 ExpChanged / LevelChanged 事件实时刷新
    /// </summary>
    public class UGUICharacterPanel : UGUIPanel
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("数值显示")]
        [Tooltip("生命值文本，如「生命值  850 / 1000」")]
        [SerializeField] private TextMeshProUGUI m_hpText;

        [Tooltip("生命值进度条。Image Type 必须设为 Filled / Horizontal")]
        [SerializeField] private Image m_hpFill;

        [Tooltip("攻击力数值文本")]
        [SerializeField] private TextMeshProUGUI m_attackValueText;

        [Tooltip("防御力数值文本")]
        [SerializeField] private TextMeshProUGUI m_defenseValueText;

        [Header("等级与经验")]
        [Tooltip("等级文本，如「Lv.5」")]
        [SerializeField] private TextMeshProUGUI m_levelText;

        [Tooltip("经验值文本，如「120 / 300」")]
        [SerializeField] private TextMeshProUGUI m_expText;

        [Tooltip("经验条填充图。Image Type 必须设为 Filled / Horizontal")]
        [SerializeField] private Image m_expFill;

        [Header("玩家绑定")]
        [Tooltip("玩家未找到时的重试间隔（秒）。玩家可能由网络系统延迟生成")]
        [SerializeField] private float m_playerLookupInterval = 0.5f;

        // ====================================================================
        // 文案常量
        // ====================================================================

        /// <summary>未绑定玩家时的占位文本（显示 0 会误导玩家，所以用 --）</summary>
        private const string k_placeholder = "--";

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>已绑定的玩家战斗单位（null 表示尚未找到）</summary>
        private CombatUnit m_playerUnit;

        /// <summary>已绑定的玩家等级系统（null 表示尚未找到）</summary>
        private PlayerLevelSystem m_levelSystem;

        /// <summary>下一次尝试绑定玩家的时间点（unscaledTime）</summary>
        private float m_nextPlayerLookupTime;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.Character;
        }

        private void Update()
        {
            // 仅当面板可见时才轮询（隐藏时刷新没有意义，省掉开销）
            if (!IsVisible)
            {
                return;
            }

            // 玩家可能晚于本物体生成（网络模式主机生成玩家）：按间隔轮询补绑
            // 战斗单位与等级系统分开绑：某一项缺失时也要继续尝试，不能因为绑好了战斗单位就放弃等级
            if ((m_playerUnit == null || m_levelSystem == null) && Time.unscaledTime >= m_nextPlayerLookupTime)
            {
                TryBindPlayer();
                RefreshStats();
            }
        }

        protected override void OnDestroy()
        {
            // 物体销毁（含场景卸载）时解绑，避免死对象的回调
            UnbindPlayer();
            base.OnDestroy();
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：绑定玩家并刷新一次数值</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 每次打开都尝试绑定一次玩家（玩家可能在本面板创建之后才生成）
            TryBindPlayer();
            RefreshStats();
        }

        /// <summary>面板退出显示：解绑事件，防止悬挂引用</summary>
        public override void OnExit()
        {
            UnbindPlayer();
            base.OnExit();
        }

        // ====================================================================
        // 玩家绑定
        // ====================================================================

        /// <summary>
        /// 查找并绑定玩家战斗单位与等级系统。
        /// 两者独立处理：其中一个已绑好、另一个还没找到时，仍要能继续绑定另一个
        /// </summary>
        private void TryBindPlayer()
        {
            if (m_playerUnit != null && m_levelSystem != null)
            {
                return;
            }

            m_nextPlayerLookupTime = Time.unscaledTime + m_playerLookupInterval;

            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player == null)
            {
                return;
            }

            if (m_playerUnit == null)
            {
                CombatUnit unit = player.GetComponent<CombatUnit>();
                if (unit != null)
                {
                    m_playerUnit = unit;
                    m_playerUnit.DamageTaken += HandlePlayerDamageTaken;
                    m_playerUnit.Healed += HandlePlayerHealed;
                    m_playerUnit.Died += HandlePlayerDied;
                }
            }

            if (m_levelSystem == null)
            {
                // 用 GetComponentInChildren：等级系统可能挂在玩家的子物体上
                PlayerLevelSystem levelSystem = player.GetComponentInChildren<PlayerLevelSystem>();
                if (levelSystem != null)
                {
                    m_levelSystem = levelSystem;
                    m_levelSystem.ExpChanged += RefreshStats;
                    m_levelSystem.LevelChanged += HandleLevelChanged;
                }
            }
        }

        /// <summary>解绑玩家事件（可安全重复调用）</summary>
        private void UnbindPlayer()
        {
            if (m_playerUnit != null)
            {
                m_playerUnit.DamageTaken -= HandlePlayerDamageTaken;
                m_playerUnit.Healed -= HandlePlayerHealed;
                m_playerUnit.Died -= HandlePlayerDied;
                m_playerUnit = null;
            }

            if (m_levelSystem != null)
            {
                m_levelSystem.ExpChanged -= RefreshStats;
                m_levelSystem.LevelChanged -= HandleLevelChanged;
                m_levelSystem = null;
            }
        }

        /// <summary>玩家受伤事件回调：数值实时刷新</summary>
        private void HandlePlayerDamageTaken(float damage)
        {
            RefreshStats();
        }

        /// <summary>玩家被治疗事件回调：数值实时刷新（喝药回血走这条）</summary>
        private void HandlePlayerHealed(float amount)
        {
            RefreshStats();
        }

        /// <summary>玩家死亡事件回调：刷新为 0 血量</summary>
        private void HandlePlayerDied(CombatUnit diedUnit)
        {
            RefreshStats();
        }

        /// <summary>等级变化事件回调：升级时整体刷新（属性加成也会跟着变）</summary>
        private void HandleLevelChanged(int newLevel)
        {
            Debug.Log($"[{GetType().Name}] 玩家升到 {newLevel} 级，角色面板刷新", this);
            RefreshStats();
        }

        // ====================================================================
        // 数值刷新
        // ====================================================================

        /// <summary>刷新面板全部内容（战斗属性 + 等级经验）</summary>
        private void RefreshStats()
        {
            RefreshCombatStats();
            RefreshLevel();
        }

        /// <summary>
        /// 刷新生命/攻击/防御。
        /// 玩家未绑定时保持占位符（"--"），避免显示误导性的 0
        /// </summary>
        private void RefreshCombatStats()
        {
            if (m_playerUnit == null)
            {
                if (m_hpText != null)
                {
                    m_hpText.text = $"生命值  {k_placeholder} / {k_placeholder}";
                }

                if (m_hpFill != null)
                {
                    m_hpFill.fillAmount = 0f;
                }

                if (m_attackValueText != null)
                {
                    m_attackValueText.text = k_placeholder;
                }

                if (m_defenseValueText != null)
                {
                    m_defenseValueText.text = k_placeholder;
                }

                return;
            }

            // 生命值：数值文本 + 进度条比例
            float maxHp = m_playerUnit.MaxHP;
            float ratio = maxHp > 0f ? m_playerUnit.CurrentHP / maxHp : 0f;

            if (m_hpText != null)
            {
                m_hpText.text = $"生命值  {m_playerUnit.CurrentHP:0} / {maxHp:0}";
            }

            if (m_hpFill != null)
            {
                m_hpFill.fillAmount = Mathf.Clamp01(ratio);
            }

            if (m_attackValueText != null)
            {
                m_attackValueText.text = $"{m_playerUnit.Attack:0}";
            }

            if (m_defenseValueText != null)
            {
                m_defenseValueText.text = $"{m_playerUnit.Defense:0}";
            }
        }

        /// <summary>刷新等级文本与经验条（满级时经验条按满显示）</summary>
        private void RefreshLevel()
        {
            if (m_levelSystem == null)
            {
                if (m_levelText != null)
                {
                    m_levelText.text = k_placeholder;
                }

                if (m_expText != null)
                {
                    m_expText.text = $"{k_placeholder} / {k_placeholder}";
                }

                if (m_expFill != null)
                {
                    m_expFill.fillAmount = 0f;
                }

                return;
            }

            if (m_levelText != null)
            {
                m_levelText.text = $"Lv.{m_levelSystem.Level}";
            }

            if (m_expText != null)
            {
                // 满级后没有"下一级需求"，显示数值会变成 0/0，明确写"已满级"更清楚
                m_expText.text = m_levelSystem.IsMaxLevel
                    ? "已满级"
                    : $"{m_levelSystem.CurrentExp} / {m_levelSystem.ExpRequired}";
            }

            if (m_expFill != null)
            {
                m_expFill.fillAmount = m_levelSystem.ExpProgress01;
            }
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>「关闭」按钮回调（Inspector 拖曳绑定）</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.Character);
        }
    }
}
