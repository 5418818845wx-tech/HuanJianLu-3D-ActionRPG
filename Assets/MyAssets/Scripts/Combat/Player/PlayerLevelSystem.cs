using System;
using MyGame.Audio;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 玩家等级系统：经验累积、按曲线升级、把等级换算成战斗属性加成
    ///
    /// 三条职责，互相不越界：
    /// 1. **只算等级与经验**：杀怪/交任务只是"给经验"，不关心经验从哪来
    /// 2. **把等级翻译成属性**：自己不认识 CombatUnit 的内部结构，
    ///    只调 ApplyBonusStats 把"该加多少"告诉它
    /// 3. **自己负责自己的存档**：Level / CurrentExp 直接给 SaveManager 读，
    ///    读档时用 ApplySaveData 一次性覆盖
    ///
    /// 为什么加成走"绝对值重算"而不是"每级累加"：
    /// 读档、重设等级、改配置后重算，都会走同一条路径 ApplyLevelBonuses()。
    /// 如果做成"升级时 +20 血"，读档到 5 级就没法还原成"5 级的正确加成"，
    /// 只能靠乘法凑数，配置一改就全错
    ///
    /// 挂载位置：玩家身上（与 CombatUnit 同一物体或其子物体）
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerLevelSystem : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("等级曲线")]
        [Tooltip("等级上限。到达后不再累积经验")]
        [SerializeField, Min(1)] private int m_maxLevel = 30;

        [Tooltip("从 1 级升到 2 级需要的经验（后续每级按成长系数递增）")]
        [SerializeField, Min(1)] private int m_baseExpRequirement = 100;

        [Tooltip("每级经验需求的成长系数。1.25 表示每级需求是上一级的 1.25 倍")]
        [SerializeField, Min(1f)] private float m_expGrowth = 1.25f;

        [Header("每级成长（叠加在 CombatUnit 基础属性上）")]
        [Tooltip("每升 1 级增加的最大生命值")]
        [SerializeField] private float m_hpPerLevel = 20f;

        [Tooltip("每升 1 级增加的攻击力")]
        [SerializeField] private float m_attackPerLevel = 2f;

        [Tooltip("每升 1 级增加的防御力")]
        [SerializeField] private float m_defensePerLevel = 1f;

        [Tooltip("升级时是否回满血（默认开：升级是正反馈，不该还留着一半血）")]
        [SerializeField] private bool m_refillHpOnLevelUp = true;

        [Header("组件引用（留空自动在自身与子物体中查找）")]
        [Tooltip("玩家的战斗单位（属性加成的接收方）")]
        [SerializeField] private CombatUnit m_unit;

        // ====================================================================
        // 单例与事件
        // ====================================================================

        /// <summary>全局单例（玩家只有一个，UI 与任务奖励都按它取数据）</summary>
        public static PlayerLevelSystem Instance { get; private set; }

        /// <summary>经验值变化（含升级导致的扣除）。HUD / 角色面板订阅它刷新经验条</summary>
        public event Action ExpChanged;

        /// <summary>等级变化，参数为新等级。UI 订阅它播放升级表现</summary>
        public event Action<int> LevelChanged;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前等级（开局恒为 1 级）</summary>
        private int m_level = 1;

        /// <summary>当前等级内已累积的经验（升级时扣除对应需求）</summary>
        private int m_currentExp;

        // ====================================================================
        // 只读访问（UI 与存档使用）
        // ====================================================================

        /// <summary>当前等级</summary>
        public int Level => m_level;

        /// <summary>等级上限</summary>
        public int MaxLevel => m_maxLevel;

        /// <summary>当前等级内已累积的经验</summary>
        public int CurrentExp => m_currentExp;

        /// <summary>升到下一级需要的经验（满级返回 0）</summary>
        public int ExpRequired => GetExpRequiredForLevel(m_level);

        /// <summary>经验条比例（0~1）。满级恒为 1，避免 UI 显示成空条</summary>
        public float ExpProgress01
        {
            get
            {
                if (IsMaxLevel)
                {
                    return 1f;
                }

                int required = ExpRequired;
                return required > 0 ? Mathf.Clamp01((float)m_currentExp / required) : 0f;
            }
        }

        /// <summary>是否已满级</summary>
        public bool IsMaxLevel => m_level >= m_maxLevel;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 PlayerLevelSystem 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;

            // 自动补引用：等级系统与 CombatUnit 通常挂在同一物体上
            if (m_unit == null)
            {
                m_unit = GetComponent<CombatUnit>();
            }

            if (m_unit == null)
            {
                m_unit = GetComponentInChildren<CombatUnit>();
            }
        }

        private void Start()
        {
            // 放在 Start：CombatUnit.Awake 已经把血量初始化成满血，
            // 此时再叠加等级加成，血量增量才能被正确补上（1 级加成为 0，这里主要是幂等保护）
            ApplyLevelBonuses(healFull: false);

            // 通知一次，让已经在监听的面板拿到初始值
            ExpChanged?.Invoke();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 经验与升级
        // ====================================================================

        /// <summary>
        /// 增加经验，可能连升多级。
        /// 用 while 而不是 if：一次交任务给的经验可能跨过好几级的需求
        /// </summary>
        /// <param name="amount">获得的经验值（小于等于 0 直接忽略）</param>
        public void AddExp(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            if (IsMaxLevel)
            {
                // 满级后经验不再有意义，避免经验条一直涨
                return;
            }

            m_currentExp += amount;
            bool leveledUp = false;

            // 循环升级：只要剩余经验够下一级需求就继续升
            while (!IsMaxLevel)
            {
                int required = GetExpRequiredForLevel(m_level);
                if (required <= 0 || m_currentExp < required)
                {
                    break;
                }

                m_currentExp -= required;
                m_level++;
                leveledUp = true;

                Debug.Log($"[{GetType().Name}] 升级！当前等级 {m_level}", this);

                // 升级音：连升多级时每级都会响一次，符合"叮叮叮"的经验反馈
                AudioManager.Play(ESfx.LevelUp);

                LevelChanged?.Invoke(m_level);
            }

            if (IsMaxLevel)
            {
                // 满级后剩余经验无意义，清掉防止经验条显示成"快升级了"
                m_currentExp = 0;
            }

            if (leveledUp)
            {
                ApplyLevelBonuses(healFull: m_refillHpOnLevelUp);
            }

            ExpChanged?.Invoke();
        }

        /// <summary>
        /// 从 <paramref name="level"/> 级升到下一级需要的经验。
        /// 公式：基础需求 × 成长系数^(level-1)，例如 100 × 1.25^(n-1)
        ///
        /// 公开给 UI 用：面板要显示"120 / 300"，不能自己抄一份公式，
        /// 否则改了曲线两边就对不上了
        /// </summary>
        public int GetExpRequiredForLevel(int level)
        {
            if (level >= m_maxLevel)
            {
                return 0;
            }

            float raw = m_baseExpRequirement * Mathf.Pow(m_expGrowth, level - 1);

            // 至少 1 点：防止配置成 0 导致"经验够了却升不了级"的死循环
            return Mathf.Max(1, Mathf.RoundToInt(raw));
        }

        // ====================================================================
        // 属性加成
        // ====================================================================

        /// <summary>
        /// 按当前等级重算属性加成并写入 CombatUnit。
        /// 幂等：无论调用多少次、从几级调到几级，结果都只取决于 m_level
        /// </summary>
        /// <param name="healFull">是否顺带回满血（升级时为 true，读档/初始化时为 false）</param>
        private void ApplyLevelBonuses(bool healFull)
        {
            if (m_unit == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 没有找到 CombatUnit，等级加成无法生效", this);
                return;
            }

            // 1 级是基准等级，加成从"升过的级数"算起
            int gainedLevels = m_level - 1;

            m_unit.ApplyBonusStats(
                gainedLevels * m_hpPerLevel,
                gainedLevels * m_attackPerLevel,
                gainedLevels * m_defensePerLevel);

            if (healFull)
            {
                m_unit.FullRestore();
            }
        }

        // ====================================================================
        // 存档
        // ====================================================================

        /// <summary>用存档覆盖等级与经验，并立刻重算属性（读档专用）</summary>
        /// <param name="level">存档中的等级</param>
        /// <param name="exp">存档中当前等级内的经验</param>
        public void ApplySaveData(int level, int exp)
        {
            m_level = Mathf.Clamp(level, 1, m_maxLevel);
            m_currentExp = Mathf.Max(0, exp);

            // 读档不存血量（见 SaveManager 说明），统一按满血恢复，
            // 避免"读档后血量比例很怪"这种说不出原因的问题
            ApplyLevelBonuses(healFull: true);

            ExpChanged?.Invoke();
            LevelChanged?.Invoke(m_level);
        }
    }
}
