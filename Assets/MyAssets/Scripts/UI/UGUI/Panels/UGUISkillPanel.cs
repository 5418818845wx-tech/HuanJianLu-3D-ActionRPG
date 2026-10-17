using System.Collections.Generic;
using MyGame.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 技能面板（UGUI 版）：K 键打开的居中弹窗，展示 4 个技能槽（Q/E/R/F）与能量条
    ///
    /// 职责：
    /// 1. 按槽位生成 4 张技能卡，显示技能名 + 等级、冷却剩余时间、冷却进度条
    /// 2. 点选卡片切换升级目标，配合「升级」按钮花金币提升技能等级
    /// 3. 显示能量条（F 槽大招消耗的能量）
    /// 4. 面板可见期间逐帧刷新冷却状态（冷却值本身是逐帧递减的）
    ///
    /// 绑定方式：
    /// - 4 张技能卡是**运行时按槽位生成的** → 只能代码绑定
    ///   （卡片上的按钮也是运行时补挂的，用于点选）
    /// - 「升级」按钮的回调也是代码绑定（OnEnter 加、OnExit 解），
    ///   只需在 Inspector 里把按钮引用拖进来，不用配 OnClick
    /// - 「关闭」按钮是固定的 → Inspector 拖曳绑定
    ///
    /// 数据来源：
    /// - 玩家身上的 SkillSystem：GetSkill / GetCooldownRemaining / GetCooldownProgress / 能量
    /// - 场景里的 SkillUpgradeSystem：GetLevel / GetUpgradeCost / TryUpgrade
    ///
    /// 架构说明：面板只读取与转发，不自己算数值；升级是否成立由 SkillUpgradeSystem 判断
    /// </summary>
    public class UGUISkillPanel : UGUIPanel
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("能量条")]
        [Tooltip("能量文本，如「能量  60 / 100」")]
        [SerializeField] private TextMeshProUGUI m_energyText;

        [Tooltip("能量进度条。Image Type 必须设为 Filled / Horizontal")]
        [SerializeField] private Image m_energyFill;

        [Header("技能卡")]
        [Tooltip("技能卡容器（建议挂 HorizontalLayoutGroup 自动排列）")]
        [SerializeField] private RectTransform m_cardContainer;

        [Tooltip("技能卡 prefab。根节点需有 Image（背景），子物体名必须是 Key / Name / Status / CooldownFill")]
        [SerializeField] private GameObject m_cardPrefab;

        [Tooltip("技能就绪时的卡片背景色")]
        [SerializeField] private Color m_cardNormalColor = new Color(0.18f, 0.20f, 0.24f, 1f);

        [Tooltip("冷却中的卡片背景色（压暗以提示不可用）")]
        [SerializeField] private Color m_cardCoolingColor = new Color(0.10f, 0.11f, 0.13f, 1f);

        [Tooltip("被选中卡片的背景色（点选卡片后显示）")]
        [SerializeField] private Color m_cardSelectedColor = new Color(0.16f, 0.34f, 0.24f, 1f);

        [Header("技能升级")]
        [Tooltip("「升级」按钮（引用在代码里绑定回调，不用拖 OnClick）")]
        [SerializeField] private Button m_upgradeButton;

        [Tooltip("升级信息文本，如「技能A   Lv.1 → Lv.2   消耗 金币 ×300」")]
        [SerializeField] private TextMeshProUGUI m_upgradeInfoText;

        [Header("玩家绑定")]
        [Tooltip("玩家未找到时的重试间隔（秒）。玩家可能由网络系统延迟生成")]
        [SerializeField] private float m_playerLookupInterval = 0.5f;

        // ====================================================================
        // 技能卡子物体名常量（与 prefab 层级一一对应）
        // ====================================================================

        private const string k_keyChildName = "Key";
        private const string k_nameChildName = "Name";
        private const string k_statusChildName = "Status";
        private const string k_cooldownFillChildName = "CooldownFill";
        private const string k_iconChildName = "Icon";

        // ====================================================================
        // 文案常量
        // ====================================================================

        /// <summary>槽位为空时的技能名占位</summary>
        private const string k_unknownSkillName = "未装备";

        /// <summary>技能就绪时显示的状态文本</summary>
        private const string k_readyText = "就绪";

        /// <summary>技能槽数量（Q/E/R/F 共 4 个）</summary>
        private const int k_slotCount = 4;

        /// <summary>冷却判定阈值（秒）：小于此值视为就绪，避免显示 "0.0s"</summary>
        private const float k_readyThreshold = 0.05f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>4 张技能卡的根背景图（按槽位索引，用于切换"冷却中"配色）</summary>
        private readonly List<Image> m_cardBackgrounds = new();

        /// <summary>每张卡的技能图标（技能没配图标时隐藏）</summary>
        private readonly List<Image> m_cardIcons = new();

        /// <summary>4 张技能卡的名称文本（按槽位索引）</summary>
        private readonly List<TextMeshProUGUI> m_cardNames = new();

        /// <summary>4 张技能卡的状态文本（按槽位索引）</summary>
        private readonly List<TextMeshProUGUI> m_cardStatuses = new();

        /// <summary>4 张技能卡的冷却进度填充条（按槽位索引）</summary>
        private readonly List<Image> m_cardCooldownFills = new();

        /// <summary>4 张技能卡的按钮（运行时补挂，用于点选要升级的技能）</summary>
        private readonly List<Button> m_cardButtons = new();

        /// <summary>当前选中的技能槽（0~3），默认 Q</summary>
        private int m_selectedSlot;

        /// <summary>升级失败原因（显示在升级信息行里，成功后清空）</summary>
        private string m_upgradeHint;

        /// <summary>已绑定的玩家技能系统（null 表示尚未找到）</summary>
        private SkillSystem m_skillSystem;

        /// <summary>下一次尝试绑定玩家的时间点（unscaledTime）</summary>
        private float m_nextPlayerLookupTime;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.Skill;
        }

        private void Update()
        {
            // 仅当面板可见时才刷新（冷却显示逐帧变化，需要在可见期间更新）
            if (!IsVisible)
            {
                return;
            }

            // 玩家可能晚于本物体生成：按间隔轮询补绑
            if (m_skillSystem == null && Time.unscaledTime >= m_nextPlayerLookupTime)
            {
                TryBindPlayer();
            }

            RefreshAll();
        }

        protected override void OnDestroy()
        {
            m_skillSystem = null;
            base.OnDestroy();
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：生成技能卡 → 绑定玩家 → 首次刷新</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 每次打开都回到"选中 Q 技能、无失败提示"的干净状态
            m_selectedSlot = 0;
            m_upgradeHint = null;

            BuildCards();
            BindUpgradeButton();
            TryBindPlayer();
            RefreshAll();
        }

        /// <summary>面板退出显示：销毁技能卡，释放引用</summary>
        public override void OnExit()
        {
            ClearCards();
            UnbindUpgradeButton();
            m_skillSystem = null;

            base.OnExit();
        }

        /// <summary>
        /// 绑定「升级」按钮。
        /// 用代码绑定而不是 Inspector 拖 OnClick：面板每次打开都会执行 OnEnter，
        /// 拖曳绑定是持久化的，配合代码解绑可以保证加/解对称、不会累积监听
        /// </summary>
        private void BindUpgradeButton()
        {
            if (m_upgradeButton == null)
            {
                return;
            }

            m_upgradeButton.onClick.RemoveListener(HandleUpgradeClicked);
            m_upgradeButton.onClick.AddListener(HandleUpgradeClicked);
        }

        /// <summary>解绑「升级」按钮（可安全重复调用）</summary>
        private void UnbindUpgradeButton()
        {
            if (m_upgradeButton != null)
            {
                m_upgradeButton.onClick.RemoveListener(HandleUpgradeClicked);
            }
        }

        // ====================================================================
        // 技能卡生成
        // ====================================================================

        /// <summary>
        /// 按槽位生成 4 张技能卡，并缓存每张卡内部需要逐帧改写的控件引用
        /// 卡片结构固定、内容后续刷新，因此只在进入面板时构建一次
        /// </summary>
        private void BuildCards()
        {
            ClearCards();

            if (m_cardContainer == null || m_cardPrefab == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 技能卡容器或 prefab 未配置，技能列表无法显示", this);
                return;
            }

            for (int i = 0; i < k_slotCount; i++)
            {
                GameObject card = Instantiate(m_cardPrefab, m_cardContainer);

                Image background = card.GetComponent<Image>();

                // 槽位按键标签（Q/E/R/F）
                TextMeshProUGUI keyText = FindChildText(card.transform, k_keyChildName);
                if (keyText != null)
                {
                    keyText.text = GetSlotKeyName(i);
                }

                TextMeshProUGUI nameText = FindChildText(card.transform, k_nameChildName);
                if (nameText != null)
                {
                    nameText.text = k_unknownSkillName;
                }

                TextMeshProUGUI statusText = FindChildText(card.transform, k_statusChildName);
                if (statusText != null)
                {
                    statusText.text = k_readyText;
                }

                Image cooldownFill = FindChildImage(card.transform, k_cooldownFillChildName);
                if (cooldownFill != null)
                {
                    cooldownFill.fillAmount = 1f;
                }

                // 技能图标：具体 sprite 由 RefreshCards 按当前槽位的技能填
                Image iconImage = FindChildImage(card.transform, k_iconChildName);

                // 卡片补挂按钮：点一下选中这个技能（选中的技能才能升级）
                Button cardButton = card.GetComponent<Button>();
                if (cardButton == null)
                {
                    cardButton = card.AddComponent<Button>();
                }

                // 索引必须用局部变量捕获：闭包共享循环变量的话，每张卡都会选中最后一张
                int capturedSlot = i;
                cardButton.onClick.AddListener(() => HandleCardClicked(capturedSlot));

                m_cardBackgrounds.Add(background);
                m_cardIcons.Add(iconImage);
                m_cardNames.Add(nameText);
                m_cardStatuses.Add(statusText);
                m_cardCooldownFills.Add(cooldownFill);
                m_cardButtons.Add(cardButton);
            }
        }

        /// <summary>
        /// 销毁已生成的技能卡并清空引用列表
        /// 卡片随物体一起销毁，其上的引用不会累积
        /// </summary>
        private void ClearCards()
        {
            int i = 0;
            while (i < m_cardBackgrounds.Count)
            {
                if (m_cardBackgrounds[i] != null)
                {
                    Destroy(m_cardBackgrounds[i].gameObject);
                }
                i = i + 1;
            }

            m_cardBackgrounds.Clear();
            m_cardIcons.Clear();
            m_cardNames.Clear();
            m_cardStatuses.Clear();
            m_cardCooldownFills.Clear();
            m_cardButtons.Clear();
        }

        /// <summary>按名字查找子物体上的文本组件（找不到时返回 null，不报错）</summary>
        private TextMeshProUGUI FindChildText(Transform root, string childName)
        {
            Transform child = root.Find(childName);
            return child != null ? child.GetComponent<TextMeshProUGUI>() : null;
        }

        /// <summary>按名字查找子物体上的图片组件（找不到时返回 null，不报错）</summary>
        private Image FindChildImage(Transform root, string childName)
        {
            Transform child = root.Find(childName);
            return child != null ? child.GetComponent<Image>() : null;
        }

        /// <summary>把槽位索引转成按键名（0→Q，1→E，2→R，3→F）</summary>
        private string GetSlotKeyName(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: return "Q";
                case 1: return "E";
                case 2: return "R";
                case 3: return "F";
                default: return "?";
            }
        }

        // ====================================================================
        // 玩家绑定
        // ====================================================================

        /// <summary>查找并绑定玩家的 SkillSystem（已绑定或找不到时静默返回）</summary>
        private void TryBindPlayer()
        {
            if (m_skillSystem != null)
            {
                return;
            }

            m_nextPlayerLookupTime = Time.unscaledTime + m_playerLookupInterval;

            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player == null)
            {
                return;
            }

            // SkillSystem 可能挂在玩家本体或子物体上，统一用 GetComponentInChildren 查找
            m_skillSystem = player.GetComponentInChildren<SkillSystem>();
        }

        // ====================================================================
        // 刷新
        // ====================================================================

        /// <summary>刷新能量条、4 张技能卡与升级信息行</summary>
        private void RefreshAll()
        {
            RefreshEnergy();
            RefreshCards();
            RefreshUpgradeInfo();
        }

        /// <summary>刷新能量条：未绑定时显示 0，绑定时显示真实能量</summary>
        private void RefreshEnergy()
        {
            float current = m_skillSystem != null ? m_skillSystem.CurrentEnergy : 0f;
            float max = m_skillSystem != null ? m_skillSystem.MaxEnergy : 0f;
            float progress = m_skillSystem != null ? m_skillSystem.EnergyProgress : 0f;

            if (m_energyText != null)
            {
                m_energyText.text = $"能量  {current:0} / {max:0}";
            }

            if (m_energyFill != null)
            {
                m_energyFill.fillAmount = Mathf.Clamp01(progress);
            }
        }

        /// <summary>刷新 4 张技能卡的名称、冷却状态与冷却进度</summary>
        private void RefreshCards()
        {
            for (int i = 0; i < m_cardBackgrounds.Count; i++)
            {
                SkillDataSO skill = null;
                float remaining = 0f;
                float progress = 0f;

                if (m_skillSystem != null)
                {
                    ESkillSlot slot = (ESkillSlot)i;
                    skill = m_skillSystem.GetSkill(slot);
                    remaining = m_skillSystem.GetCooldownRemaining(slot);
                    progress = m_skillSystem.GetCooldownProgress(slot);
                }

                bool isReady = remaining <= k_readyThreshold;

                if (m_cardNames[i] != null)
                {
                    // 等级直接拼在名字后面：卡片宽度有限，不值得为等级再开一行
                    m_cardNames[i].text = skill != null
                        ? $"{skill.SkillName} Lv.{GetSkillLevel(skill)}"
                        : k_unknownSkillName;
                }

                // 技能图标：技能配了图标才显示（没配就隐藏，避免留一个空白方块）
                if (i < m_cardIcons.Count && m_cardIcons[i] != null)
                {
                    m_cardIcons[i].sprite = skill != null ? skill.Icon : null;
                    m_cardIcons[i].enabled = m_cardIcons[i].sprite != null;
                }

                if (m_cardStatuses[i] != null)
                {
                    m_cardStatuses[i].text = isReady ? k_readyText : $"{remaining:0.0}s";
                }

                if (m_cardCooldownFills[i] != null)
                {
                    m_cardCooldownFills[i].fillAmount = Mathf.Clamp01(progress);
                }

                // 冷却中整张卡压暗，一眼看出哪个技能不可用。
                // 选中态优先：选中表示"我正在看这张卡"，不该被冷却色盖掉
                if (m_cardBackgrounds[i] != null)
                {
                    m_cardBackgrounds[i].color = i == m_selectedSlot
                        ? m_cardSelectedColor
                        : (isReady ? m_cardNormalColor : m_cardCoolingColor);
                }
            }
        }

        /// <summary>取技能当前等级（场景里没有升级系统时按 1 级显示）</summary>
        private static int GetSkillLevel(SkillDataSO skill)
        {
            SkillUpgradeSystem upgrades = SkillUpgradeSystem.Instance;
            return upgrades != null ? upgrades.GetLevel(skill.Id) : 1;
        }

        /// <summary>取当前选中的技能配置（未绑定玩家或该槽为空时返回 null）</summary>
        private SkillDataSO GetSelectedSkill()
        {
            return m_skillSystem != null
                ? m_skillSystem.GetSkill((ESkillSlot)m_selectedSlot)
                : null;
        }

        /// <summary>
        /// 刷新升级信息行：显示选中的技能、当前→下一等级、所需金币。
        /// 有失败提示时优先显示提示——玩家刚点完升级，最想知道的是为什么没成功
        /// </summary>
        private void RefreshUpgradeInfo()
        {
            if (m_upgradeInfoText == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(m_upgradeHint))
            {
                m_upgradeInfoText.text = m_upgradeHint;
                return;
            }

            SkillDataSO skill = GetSelectedSkill();
            if (skill == null)
            {
                m_upgradeInfoText.text = "点选一张技能卡进行升级";
                return;
            }

            SkillUpgradeSystem upgrades = SkillUpgradeSystem.Instance;
            if (upgrades == null)
            {
                m_upgradeInfoText.text = "本场景没有技能升级系统";
                return;
            }

            int level = upgrades.GetLevel(skill.Id);

            if (upgrades.IsMaxLevel(skill.Id))
            {
                m_upgradeInfoText.text = $"{skill.SkillName}   Lv.{level}   已满级";
                return;
            }

            m_upgradeInfoText.text =
                $"{skill.SkillName}   Lv.{level} → Lv.{level + 1}   消耗 金币 ×{upgrades.GetUpgradeCost(skill.Id)}";
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>点选技能卡（卡片按钮的运行时回调）：切换升级目标</summary>
        private void HandleCardClicked(int slot)
        {
            m_selectedSlot = Mathf.Clamp(slot, 0, k_slotCount - 1);

            // 换目标时清掉上一次的失败提示，否则"金币不足"会显示在另一个技能上，张冠李戴
            m_upgradeHint = null;

            RefreshUpgradeInfo();
            RefreshCards();
        }

        /// <summary>「升级」按钮回调：花金币把选中技能升一级</summary>
        public void HandleUpgradeClicked()
        {
            SkillDataSO skill = GetSelectedSkill();
            if (skill == null)
            {
                m_upgradeHint = "没有可升级的技能（该槽位未装备）";
                RefreshUpgradeInfo();
                return;
            }

            SkillUpgradeSystem upgrades = SkillUpgradeSystem.Instance;
            if (upgrades == null)
            {
                m_upgradeHint = "本场景没有技能升级系统";
                RefreshUpgradeInfo();
                return;
            }

            // 能不能升、够不够钱由系统层判断，面板只负责转发与显示结果
            m_upgradeHint = upgrades.TryUpgrade(skill.Id, out string failReason) ? null : failReason;

            RefreshUpgradeInfo();
            RefreshCards();
        }

        /// <summary>「关闭」按钮回调（Inspector 拖曳绑定）</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.Skill);
        }
    }
}
