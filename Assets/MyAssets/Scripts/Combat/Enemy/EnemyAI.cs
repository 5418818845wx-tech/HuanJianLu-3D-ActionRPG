using MyGame.Audio;
using UnityEngine;
using UnityEngine.AI;

namespace MyGame.Combat
{
    /// <summary>
    /// 敌人 AI 状态枚举：敌人一生中的四种互斥状态
    /// 巡逻(Patrol) → 发现玩家 → 追击(Chase) → 进入攻击距离 → 攻击(Attack)
    /// ↑                    ↓                    ↓
    /// └──── 玩家跑太远/死亡 ←────────────────────┘
    /// 任何状态下血量归零 → 死亡(Dead)
    /// </summary>
    public enum EEnemyAIState
    {
        /// <summary>巡逻：在巡逻点之间移动，或在出生点附近随机漫步</summary>
        Patrol,

        /// <summary>追击：玩家进入感知范围，沿 NavMesh 路径追赶玩家</summary>
        Chase,

        /// <summary>攻击：进入攻击距离，停止移动，周期性播放攻击动画</summary>
        Attack,

        /// <summary>死亡：停止一切 AI 逻辑，播放死亡动画后延迟销毁</summary>
        Dead,
    }

    /// <summary>
    /// 敌人 AI 大脑：基于 NavMeshAgent 的有限状态机
    ///
    /// 工作原理：
    /// 1. 移动完全交给 NavMeshAgent（自动沿烘焙好的 NavMesh 寻路，绕开障碍物）
    /// 2. Update 中按"感知间隔"检测与玩家的水平距离，驱动状态切换
    /// 3. 攻击复用玩家的 HitboxController：攻击动画命中帧的动画事件调用
    ///    OnHitFrame() → PerformHitDetect()，球形检测打到玩家 CombatUnit 后扣血
    /// 4. 死亡通过轮询 CombatUnit.IsAlive 检测（EnemyCombatUnit 死亡后不隐藏物体）
    ///
    /// 挂载要求：EnemyAI / EnemyCombatUnit / NavMeshAgent / HitboxController
    /// 与 Animator 必须在同一个 GameObject 上（动画事件才能调用到本脚本的方法）
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class EnemyAI : MonoBehaviour
    {
        // ====================================================================
        // 动画参数名常量（敌人 Animator Controller 中必须创建同名参数）
        // 集中定义，避免拼写错误，改素材时只改这里
        // ====================================================================

        /// <summary>Speed (Float)：当前移动速度，驱动 Idle/Run 动画切换</summary>
        private const string k_speedParam = "Speed";

        /// <summary>Attack (Trigger)：触发一次攻击动画</summary>
        private const string k_attackTriggerParam = "Attack";

        /// <summary>IsDead (Trigger)：触发死亡动画</summary>
        private const string k_deathTriggerParam = "IsDead";

        /// <summary>敌人 Animator 中攻击状态的名称（用于检测攻击动画是否播完）</summary>
        private const string k_attackStateName = "Attack";

        /// <summary>未填写敌人种类 ID 时使用的兜底 ID（保证任务系统拿到非空 ID）</summary>
        private const string k_defaultEnemyId = "Enemy_Dummy";

        // ====================================================================
        // 目标设置
        // ====================================================================

        [Header("目标设置")]
        [Tooltip("追击目标（留空则自动按 Tag 查找玩家，一般无需手动指定）")]
        [SerializeField] private Transform m_target;

        [Tooltip("自动查找目标使用的 Tag（玩家预制体需要挂此 Tag）")]
        [SerializeField] private string m_targetTag = "Player";

        // ====================================================================
        // 感知范围
        // ====================================================================

        [Header("感知范围")]
        [Tooltip("发现玩家的距离：玩家进入此范围后从巡逻转为追击（米）")]
        [SerializeField] private float m_detectRange = 8f;

        [Tooltip("丢失目标的距离：玩家拉开到此距离后放弃追击，回到巡逻（米）")]
        [SerializeField] private float m_loseTargetRange = 12f;

        [Tooltip("感知检测间隔（秒）。每隔此时长检测一次玩家距离，避免每帧计算")]
        [SerializeField, Range(0.05f, 1f)] private float m_detectInterval = 0.15f;

        // ====================================================================
        // 攻击设置
        // ====================================================================

        [Header("攻击设置")]
        [Tooltip("攻击距离：与玩家的水平距离小于此值时发动攻击（米）。建议略大于 Hitbox 命中半径")]
        [SerializeField] private float m_attackRange = 2.2f;

        [Tooltip("攻击冷却：一次攻击动画结束后，等待多久才能再次攻击（秒）")]
        [SerializeField] private float m_attackCooldown = 1.6f;

        [Tooltip("攻击瞬间转向玩家的速度（越大转得越快）")]
        [SerializeField, Range(1f, 20f)] private float m_attackTurnSpeed = 12f;

        [Tooltip("敌人攻击的伤害倍率（敌人的攻击力在 EnemyCombatUnit 的 Attack 字段配置）")]
        [SerializeField] private float m_attackDamageMultiplier = 1f;

        // ====================================================================
        // 移动速度
        // ====================================================================

        [Header("移动速度")]
        [Tooltip("巡逻移动速度（米/秒）")]
        [SerializeField] private float m_patrolSpeed = 1.2f;

        [Tooltip("追击移动速度（米/秒）")]
        [SerializeField] private float m_chaseSpeed = 3.5f;

        // ====================================================================
        // 巡逻设置
        // ====================================================================

        [Header("巡逻设置")]
        [Tooltip("巡逻点列表（留空则在出生点附近随机漫步）。可在场景中创建空物体作为巡逻点拖入")]
        [SerializeField] private Transform[] m_patrolPoints;

        [Tooltip("到达巡逻点后停留的时间（秒）")]
        [SerializeField] private float m_patrolWaitTime = 2f;

        [Tooltip("无巡逻点时，随机漫步的半径（米，以出生位置为中心）")]
        [SerializeField] private float m_wanderRadius = 6f;

        // ====================================================================
        // 受击硬直
        // ====================================================================

        [Header("受击硬直")]
        [Tooltip("被命中时短暂停顿的时间（秒，0=无硬直）。攻击动画播放中不会被打断")]
        [SerializeField] private float m_stunDuration = 0.25f;

        // ====================================================================
        // 死亡设置
        // ====================================================================

        [Header("死亡设置")]
        [Tooltip("血量归零后是否立刻消失：不播死亡动画、不留尸体，直接移除（默认开启）")]
        [SerializeField] private bool m_vanishOnDeath = true;

        [Tooltip("【仅当上面关闭时生效】死亡后多少秒销毁物体（0=永不销毁，方便调试时检查尸体）")]
        [SerializeField] private float m_destroyDelay = 3f;

        [Tooltip("【仅当上面关闭时生效】是否播放死亡动画（Animator 中需要有 IsDead Trigger 参数，没有请关闭）")]
        [SerializeField] private bool m_playDeathAnimation = true;

        // ====================================================================
        // 任务系统标识
        // ====================================================================

        [Header("任务标识")]
        [Tooltip("敌人种类 ID：任务系统的击杀目标按此 ID 统计。" +
                 "命名规范「Enemy_ + 种类名」（如 Enemy_Dummy / Enemy_Skeleton）。留空记为 Enemy_Dummy")]
        [SerializeField] private string m_enemyId = "Enemy_Dummy";

        // ====================================================================
        // 掉落配置
        // ====================================================================

        [Header("掉落")]
        [Tooltip("掉落表：这种敌人死亡后掉什么。留空则用 DropSystem 的默认掉落表（也没有就不掉东西）")]
        [SerializeField] private DropTableSO m_dropTable;

        // ====================================================================
        // 组件引用（正常情况自动查找，无需手动拖）
        // ====================================================================

        [Header("组件引用（自动查找，一般无需手动指定）")]
        [Tooltip("NavMesh 寻路代理")]
        [SerializeField] private NavMeshAgent m_agent;

        [Tooltip("敌人 Animator（可在子物体上）")]
        [SerializeField] private Animator m_animator;

        [Tooltip("敌人战斗单位（生命值/攻击力）")]
        [SerializeField] private CombatUnit m_unit;

        [Tooltip("攻击命中检测器（敌人武器的挥砍判定）")]
        [SerializeField] private HitboxController m_hitbox;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前 AI 状态</summary>
        private EEnemyAIState m_state = EEnemyAIState.Patrol;

        /// <summary>当前 AI 状态（只读，外部查询用）</summary>
        public EEnemyAIState State => m_state;

        /// <summary>
        /// 敌人死亡事件：敌人彻底死亡时触发
        /// 未来的掉落物系统、任务计数系统、波次管理器可订阅此事件
        /// </summary>
        public event System.Action<EnemyAI> EnemyDied;

        /// <summary>
        /// 全局击杀事件（静态）：任何敌人死亡时都会触发
        /// 供任务系统等"全局系统"订阅，省去逐个敌人查找订阅的麻烦
        /// 注意：订阅方必须在 OnDisable 中取消订阅，避免场景卸载后回调访问已销毁对象
        /// </summary>
        public static event System.Action<EnemyAI> AnyEnemyDied;

        /// <summary>
        /// 敌人种类 ID（任务系统按此值统计击杀目标；未填写时返回 Enemy_Dummy）
        /// </summary>
        public string EnemyId => string.IsNullOrEmpty(m_enemyId) ? k_defaultEnemyId : m_enemyId;

        /// <summary>
        /// 掉落表（DropSystem 在击杀时读取；未配置返回 null，由 DropSystem 决定是否用默认表）
        /// </summary>
        public DropTableSO DropTable => m_dropTable;

        /// <summary>攻击状态的 Animator 哈希（提前计算，避免每帧字符串转换）</summary>
        private int m_attackStateHash;

        // ====================================================================
        // Animator 参数存在性缓存
        // 关键性能点：m_animator.parameters 每次读取都会**分配一个新数组**，
        // 敌人多时若每帧查询会持续制造垃圾、频繁触发 GC 卡顿。
        // 所以这里在 Awake 一次性查好，运行期只读这几个 bool
        // ====================================================================

        /// <summary>Animator 是否有 Speed(float) 参数</summary>
        private bool m_hasSpeedParam;

        /// <summary>Animator 是否有 Attack(Trigger) 参数</summary>
        private bool m_hasAttackTriggerParam;

        /// <summary>Animator 是否有 IsDead(Trigger) 参数</summary>
        private bool m_hasDeathTriggerParam;

        /// <summary>当前巡逻点索引（-1 表示还未开始巡逻）</summary>
        private int m_patrolIndex = -1;

        /// <summary>下一次感知检测的时间点（Time.time）</summary>
        private float m_nextDetectTime;

        /// <summary>下一次攻击就绪的时间点（Time.time）</summary>
        private float m_nextAttackReadyTime;

        /// <summary>巡逻等待结束的时间点（Time.time）</summary>
        private float m_patrolResumeTime;

        /// <summary>受击硬直结束的时间点（Time.time）</summary>
        private float m_stunUntil;

        /// <summary>攻击动画是否正在播放</summary>
        private bool m_isAttackAnimating;

        /// <summary>本次挥砍是否已真正进入过 Attack 动画状态（用于排除过渡帧的误判）</summary>
        private bool m_attackStateEntered;

        /// <summary>是否已收到本次挥砍的 OnAttackEnd 动画事件回调</summary>
        private bool m_attackEndEventReceived;

        /// <summary>出生位置（随机漫步的中心）</summary>
        private Vector3 m_spawnPosition;

        /// <summary>目标的战斗单位（用于判断玩家是否死亡）</summary>
        private CombatUnit m_targetUnit;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            // 自动查找所需组件，降低 Inspector 配置负担
            if (m_agent == null) m_agent = GetComponent<NavMeshAgent>();
            if (m_animator == null) m_animator = GetComponentInChildren<Animator>();
            if (m_unit == null) m_unit = GetComponent<CombatUnit>();
            if (m_hitbox == null) m_hitbox = GetComponent<HitboxController>();

            // 预计算动画状态哈希
            m_attackStateHash = Animator.StringToHash(k_attackStateName);

            // 一次性缓存 Animator 参数是否存在，运行期不再读 m_animator.parameters（避免每帧数组分配）
            CacheAnimatorParameters();

            // 敌人数量多时打开 Animator 裁剪：镜头看不到的敌人不再无脑更新动画，显著省 CPU。
            // 用 CullUpdateTransforms 而不是 CullCompletely——前者仍会推进状态机与动画事件
            // （攻击命中帧、死亡回调不会丢），只是跳过蒙皮计算，功能与性能兼顾
            if (m_animator != null)
            {
                m_animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }

            // 记录出生位置，作为随机漫步的中心
            m_spawnPosition = transform.position;

            // 初始为巡逻速度
            if (m_agent != null)
            {
                m_agent.speed = m_patrolSpeed;
            }
        }

        private void Start()
        {
            // 查找追击目标：优先手动指定，否则按 Tag 找玩家
            if (m_target == null && !string.IsNullOrEmpty(m_targetTag))
            {
                GameObject player = GameObject.FindGameObjectWithTag(m_targetTag);
                if (player != null)
                {
                    m_target = player.transform;
                }
                else
                {
                    Debug.LogWarning($"[EnemyAI] 场景中未找到 Tag 为 '{m_targetTag}' 的目标，敌人将只会巡逻", this);
                }
            }

            // 缓存目标的 CombatUnit，用于判断玩家是否存活
            if (m_target != null)
            {
                m_targetUnit = m_target.GetComponent<CombatUnit>();
            }

            // 开始第一次巡逻
            MoveToNextPatrolPoint();
        }

        private void OnEnable()
        {
            // 订阅受击事件：被打中时进入短暂硬直
            // （Awake 先于 OnEnable 执行，此时 m_unit 已赋值）
            if (m_unit != null)
            {
                m_unit.HitTaken += HandleHitTaken;
            }
        }

        private void OnDisable()
        {
            // 取消订阅，防止物体禁用后回调导致空引用
            if (m_unit != null)
            {
                m_unit.HitTaken -= HandleHitTaken;
            }
        }

        private void Update()
        {
            // 死亡状态：停止一切 AI 逻辑（尸体动画由 Animator 自己播放）
            if (m_state == EEnemyAIState.Dead)
            {
                return;
            }

            // 死亡检测：EnemyCombatUnit 死亡后不隐藏物体，靠 IsAlive 判断
            if (m_unit != null && !m_unit.IsAlive)
            {
                EnterDead();
                return;
            }

            // 受击硬直：非攻击状态下被命中会短暂停顿（攻击动画不打断，让挥砍动作完整）
            if (m_state != EEnemyAIState.Attack && Time.time < m_stunUntil)
            {
                if (m_agent != null && m_agent.isOnNavMesh)
                {
                    m_agent.isStopped = true;
                }
                SetSpeedParam(0f);
                return;
            }

            // 定时感知：每隔 m_detectInterval 秒检测一次玩家距离并驱动状态切换
            if (Time.time >= m_nextDetectTime)
            {
                m_nextDetectTime = Time.time + m_detectInterval;
                UpdatePerception();
            }

            // 状态机：每个状态有独立的 Update 逻辑
            switch (m_state)
            {
                case EEnemyAIState.Patrol:
                    UpdatePatrol();
                    break;

                case EEnemyAIState.Chase:
                    UpdateChase();
                    break;

                case EEnemyAIState.Attack:
                    UpdateAttack();
                    break;
            }

            // 把 NavMeshAgent 的实际移动速度同步给 Animator，播放走/跑动画
            UpdateAnimatorSpeed();
        }

        // ====================================================================
        // 感知：检测玩家距离，决定状态切换
        // ====================================================================

        /// <summary>
        /// 感知更新：检测目标是否有效、距离多少，据此切换状态
        /// </summary>
        private void UpdatePerception()
        {
            // 目标引用为空时重新按 Tag 查找。
            // 典型场景：敌人随场景加载，但玩家由网络系统在主机/客户端连接后才生成，
            // Start 时找不到玩家。此后每次感知检测（0.15s）都补找一次
            if (m_target == null && !string.IsNullOrEmpty(m_targetTag))
            {
                GameObject found = GameObject.FindGameObjectWithTag(m_targetTag);
                if (found != null)
                {
                    m_target = found.transform;
                    m_targetUnit = found.GetComponent<CombatUnit>();
                }
            }

            // 目标无效（不存在或已死亡）：战斗状态中的敌人回到巡逻
            if (!IsTargetValid())
            {
                if (m_state == EEnemyAIState.Chase || m_state == EEnemyAIState.Attack)
                {
                    ChangeToPatrol();
                }
                return;
            }

            float distance = GetHorizontalDistanceToTarget();

            switch (m_state)
            {
                case EEnemyAIState.Patrol:
                    // 巡逻中：玩家进入发现距离 → 追击
                    if (distance <= m_detectRange)
                    {
                        ChangeToChase();
                    }
                    break;

                case EEnemyAIState.Chase:
                    // 追击中持续刷新玩家位置作为寻路目标（按感知间隔刷新，不每帧重置路径）
                    if (m_agent != null && m_agent.isOnNavMesh)
                    {
                        m_agent.SetDestination(m_target.position);
                    }

                    // 进入攻击距离 → 攻击
                    if (distance <= m_attackRange)
                    {
                        ChangeToAttack();
                    }
                    // 玩家拉开到丢失距离以外 → 放弃追击
                    else if (distance >= m_loseTargetRange)
                    {
                        ChangeToPatrol();
                    }
                    break;

                case EEnemyAIState.Attack:
                    // 攻击中：玩家逃出攻击距离的 1.2 倍（留一点缓冲，防止临界抖动）→ 回到追击
                    if (distance > m_attackRange * 1.2f)
                    {
                        ChangeToChase();
                    }
                    break;
            }
        }

        /// <summary>
        /// 判断追击目标是否有效（存在且存活）
        /// </summary>
        private bool IsTargetValid()
        {
            if (m_target == null)
            {
                return false;
            }

            // 目标有 CombatUnit 但已死亡：视为无效目标
            if (m_targetUnit != null && !m_targetUnit.IsAlive)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 计算与目标的水平距离（忽略高度差，避免跳起来/高低差导致距离误判）
        /// </summary>
        private float GetHorizontalDistanceToTarget()
        {
            Vector3 self = transform.position;
            Vector3 target = m_target.position;

            // 把 Y 分量清零，只比较 XZ 平面距离
            self.y = 0f;
            target.y = 0f;

            return Vector3.Distance(self, target);
        }

        // ====================================================================
        // 状态：巡逻
        // ====================================================================

        /// <summary>
        /// 巡逻状态逻辑：在巡逻点之间移动，到达后等待一会再去下一个点
        /// </summary>
        private void UpdatePatrol()
        {
            if (m_agent == null || !m_agent.isOnNavMesh)
            {
                return;
            }

            // 巡逻等待中：保持站立
            if (Time.time < m_patrolResumeTime)
            {
                m_agent.isStopped = true;
                return;
            }

            m_agent.isStopped = false;

            // 路径计算完毕且已到达当前目的地：停留等待，然后选择下一个巡逻点
            if (!m_agent.pathPending && m_agent.remainingDistance <= m_agent.stoppingDistance)
            {
                // 先进入等待
                m_patrolResumeTime = Time.time + m_patrolWaitTime;

                // 选定下一个目的地（等待期间 agent 被 isStopped 停住，等待结束后开始走）
                MoveToNextPatrolPoint();
            }
        }

        /// <summary>
        /// 选择下一个巡逻目的地
        /// 有巡逻点：按列表顺序循环；无巡逻点：在出生点附近随机选一个可行走位置
        /// </summary>
        private void MoveToNextPatrolPoint()
        {
            if (m_agent == null || !m_agent.isOnNavMesh)
            {
                return;
            }

            m_agent.speed = m_patrolSpeed;

            // 情况 1：配置了巡逻点 → 按顺序循环移动
            if (m_patrolPoints != null && m_patrolPoints.Length > 0)
            {
                m_patrolIndex = (m_patrolIndex + 1) % m_patrolPoints.Length;

                Transform point = m_patrolPoints[m_patrolIndex];
                if (point != null)
                {
                    m_agent.SetDestination(point.position);
                }
                return;
            }

            // 情况 2：没有巡逻点 → 出生点附近随机漫步
            Vector3 randomOffset = Random.insideUnitSphere * m_wanderRadius;
            randomOffset.y = 0f;
            Vector3 targetPos = m_spawnPosition + randomOffset;

            // NavMesh.SamplePosition：把随机点吸附到附近最近的可行走地面上
            // 防止随机点落到墙里/空中导致 SetDestination 失败
            if (NavMesh.SamplePosition(targetPos, out NavMeshHit hit, m_wanderRadius, NavMesh.AllAreas))
            {
                m_agent.SetDestination(hit.position);
            }
        }

        // ====================================================================
        // 状态：追击
        // ====================================================================

        /// <summary>
        /// 追击状态逻辑：保持追击速度跑向玩家
        /// 目的地刷新在 UpdatePerception 中按感知间隔进行（避免每帧重置寻路）
        /// </summary>
        private void UpdateChase()
        {
            if (m_agent == null || !m_agent.isOnNavMesh)
            {
                return;
            }

            m_agent.isStopped = false;
            m_agent.speed = m_chaseSpeed;
        }

        // ====================================================================
        // 状态：攻击
        // ====================================================================

        /// <summary>
        /// 攻击状态逻辑：停下脚步，面朝玩家，攻击冷却结束就挥砍
        /// 攻击动画是否播完通过 Animator 的 normalizedTime 检测（与玩家连招系统同一套做法）
        /// </summary>
        private void UpdateAttack()
        {
            if (m_agent != null && m_agent.isOnNavMesh)
            {
                // 攻击时站住不动
                m_agent.isStopped = true;
            }

            // 攻击动画播放中：平滑转向玩家，等待动画播完
            if (m_isAttackAnimating)
            {
                FaceTargetSmoothly();

                AnimatorStateInfo stateInfo = m_animator.GetCurrentAnimatorStateInfo(0);

                // SetTrigger 后有约 0.05~0.1s 的过渡帧（当前状态仍是 Idle/Run）。
                // 必须先确认真正进入过 Attack 状态，之后才允许判定动画结束，
                // 否则过渡帧的"非 Attack 状态"会被误判为挥砍已结束，导致节奏计算错误
                if (stateInfo.shortNameHash == m_attackStateHash)
                {
                    m_attackStateEntered = true;
                }

                // 挥砍结束条件（三者任一）：
                // 1. 动画剪辑上的 OnAttackEnd 事件已回调（挥砍动作主体完成）
                // 2. 动画进度 >= 99%
                // 3. 已离开 Attack 状态（切回了 Idle/Run）
                bool isAttackAnimFinished =
                    m_attackStateEntered &&
                    (m_attackEndEventReceived ||
                     stateInfo.normalizedTime >= 0.99f ||
                     stateInfo.shortNameHash != m_attackStateHash);

                if (isAttackAnimFinished)
                {
                    m_isAttackAnimating = false;
                    m_nextAttackReadyTime = Time.time + m_attackCooldown;
                }
                return;
            }

            // 不在攻击动画中：冷却结束且目标仍有效 → 发动下一次攻击
            if (Time.time >= m_nextAttackReadyTime && IsTargetValid())
            {
                StartAttack();
            }
        }

        /// <summary>
        /// 发动一次攻击：配置命中判定参数，触发攻击动画 Trigger
        /// 实际伤害在动画命中帧由 OnHitFrame() → PerformHitDetect() 完成
        /// </summary>
        private void StartAttack()
        {
            m_isAttackAnimating = true;

            // 重置本次挥砍的动画跟踪标志
            // （必须等待真正进入 Attack 状态后才允许判定结束，避免过渡帧误判）
            m_attackStateEntered = false;
            m_attackEndEventReceived = false;

            // 攻击前先把身体正面对准玩家，避免砍歪
            FaceTargetSmoothly();

            // 配置本次挥砍的命中判定
            if (m_hitbox != null)
            {
                m_hitbox.SetAttackType(EAttackType.NormalAttack);
                m_hitbox.SetDamageMultiplier(m_attackDamageMultiplier);
                m_hitbox.ResetHitTargets();
            }

            // 触发攻击动画
            if (m_animator != null && m_hasAttackTriggerParam)
            {
                m_animator.SetTrigger(k_attackTriggerParam);
            }
        }

        /// <summary>
        /// 平滑转向玩家（攻击过程中身体持续对准目标）
        /// </summary>
        private void FaceTargetSmoothly()
        {
            if (m_target == null)
            {
                return;
            }

            Vector3 direction = m_target.position - transform.position;
            direction.y = 0f;

            // 方向几乎为零时不转，防止 LookRotation 报错
            if (direction.sqrMagnitude < 0.001f)
            {
                return;
            }

            Quaternion targetRotation = Quaternion.LookRotation(direction.normalized);

            // Slerp 平滑插值，转向速度由 m_attackTurnSpeed 控制
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                m_attackTurnSpeed * Time.deltaTime);
        }

        // ====================================================================
        // 状态切换
        // ====================================================================

        /// <summary>切换到巡逻状态</summary>
        private void ChangeToPatrol()
        {
            m_state = EEnemyAIState.Patrol;
            m_isAttackAnimating = false;

            // m_patrolResumeTime 设为 0：立即开始巡逻，不等待
            m_patrolResumeTime = 0f;

            if (m_agent != null && m_agent.isOnNavMesh)
            {
                m_agent.isStopped = false;
                m_agent.speed = m_patrolSpeed;
            }

            MoveToNextPatrolPoint();
        }

        /// <summary>切换到追击状态</summary>
        private void ChangeToChase()
        {
            m_state = EEnemyAIState.Chase;

            if (m_agent != null && m_agent.isOnNavMesh)
            {
                m_agent.isStopped = false;
                m_agent.speed = m_chaseSpeed;

                // 立刻以玩家当前位置为寻路目标
                if (m_target != null)
                {
                    m_agent.SetDestination(m_target.position);
                }
            }
        }

        /// <summary>切换到攻击状态</summary>
        private void ChangeToAttack()
        {
            m_state = EEnemyAIState.Attack;
            m_isAttackAnimating = false;

            // 进入攻击范围后立刻可以出第一刀（不等冷却）
            m_nextAttackReadyTime = Time.time;

            if (m_agent != null && m_agent.isOnNavMesh)
            {
                m_agent.isStopped = true;
                m_agent.ResetPath();
            }
        }

        /// <summary>进入死亡状态：停止 AI、通知外部、按配置立刻消失或播放死亡表现</summary>
        private void EnterDead()
        {
            m_state = EEnemyAIState.Dead;

            // 停止 NavMesh 寻路并禁用代理，防止尸体继续被推动
            if (m_agent != null)
            {
                if (m_agent.isOnNavMesh)
                {
                    m_agent.isStopped = true;
                    m_agent.ResetPath();
                }
                m_agent.enabled = false;
            }

            // ===== 先通知外部系统，再处理消失 =====
            // 顺序很关键：掉落系统 / 任务计数 / 波次管理都靠下面两个事件，
            // 必须在本物体被销毁之前触发，否则这些系统收不到击杀通知
            EnemyDied?.Invoke(this);

            // 死亡音：敌人不走 CombatUnit.Died（EnemyCombatUnit 重写了死亡流程且不触发该事件），
            // 所以在死亡状态入口这里发声；走 3D 让击杀位置有空间感
            AudioManager.PlayAt(ESfx.Enemy_Death, transform.position);

            // 通知全局系统（任务系统订阅此静态事件统计击杀进度）
            AnyEnemyDied?.Invoke(this);

            // ===== 表现 =====
            // 立刻消失：不播死亡动画、不留尸体。避免"血量已归零却还站着"的观感问题
            if (m_vanishOnDeath)
            {
                Destroy(gameObject);
                return;
            }

            // 播放死亡动画（Animator 没有 IsDead 参数时跳过，避免刷警告）
            if (m_playDeathAnimation && m_animator != null && m_hasDeathTriggerParam)
            {
                // 先清掉可能还挂着的攻击 Trigger：
                // AnyState → Attack 与 AnyState → Death 优先级相同，残留的 Attack 会把死亡动画顶掉
                m_animator.ResetTrigger(k_attackTriggerParam);

                m_animator.SetTrigger(k_deathTriggerParam);
            }

            // 延迟销毁尸体，给玩家看清击败结果
            if (m_destroyDelay > 0f)
            {
                Destroy(gameObject, m_destroyDelay);
            }
        }

        // ====================================================================
        // 受击事件回调
        // ====================================================================

        /// <summary>
        /// 受击事件回调：被命中时进入短暂硬直
        /// 由 CombatUnit.HitTaken 事件触发（每次实际扣血都会调用）
        /// </summary>
        private void HandleHitTaken(CombatUnit attacker, EAttackType attackType, float damage)
        {
            // 死亡状态不再处理；攻击动画中也不打断（让挥砍动作完整播放）
            if (m_state == EEnemyAIState.Dead || m_state == EEnemyAIState.Attack)
            {
                return;
            }

            m_stunUntil = Time.time + m_stunDuration;
        }

        // ====================================================================
        // 动画事件回调
        // 敌人攻击动画片段上添加同名 Animation Event 即可触发
        // （SwordAnimationPack 中已配置好事件的攻击 clip 可直接复用，
        //   动画事件按方法名匹配，会调用到本脚本的同名方法）
        // ====================================================================

        #region 动画事件方法

        /// <summary>
        /// 攻击开始：攻击动画第 1 帧调用
        /// 清空上一次挥砍的命中记录，确保本次攻击能重新命中玩家
        /// </summary>
        public void OnAttackStart()
        {
            if (m_hitbox != null)
            {
                m_hitbox.ResetHitTargets();
            }

            // 敌人挥砍音（带怪物叫，两种变体随机）：走 3D，远处听感更小更远
            AudioManager.PlayVariantAt(ESfx.Enemy_Swing_01, ESfx.Enemy_Swing_02, transform.position);
        }

        /// <summary>
        /// 命中帧：武器挥砍到目标位置的那一帧调用
        /// 执行球形检测，对范围内的玩家造成伤害
        /// </summary>
        public void OnHitFrame()
        {
            int damaged = 0;
            if (m_hitbox != null)
            {
                damaged = m_hitbox.PerformHitDetect();
            }

            // 打中玩家才出命中音（敌人挥空时只有风声）
            if (damaged > 0)
            {
                AudioManager.PlayVariant(ESfx.Hit_Flesh_01, ESfx.Hit_Flesh_02);

                // 玩家挨打：震屏 + 顿帧。这里能确认"打中的就是玩家"，
                // 所以不需要额外做玩家受击的订阅组件
                HitStop.Play(0.06f);
                CameraShake.Shake(0.18f, 0.2f);
            }
        }

        /// <summary>
        /// 攻击结束：攻击动画剪辑尾部的动画事件调用
        /// 标记本次挥砍动画主体已播完，UpdateAttack 据此进入攻击冷却
        /// </summary>
        public void OnAttackEnd()
        {
            m_attackEndEventReceived = true;
        }

        #endregion

        // ====================================================================
        // 工具方法
        // ====================================================================

        /// <summary>
        /// 同步移动动画：把 NavMeshAgent 的实际速度写入 Animator 的 Speed 参数
        /// </summary>
        private void UpdateAnimatorSpeed()
        {
            if (m_animator == null || m_agent == null)
            {
                return;
            }

            // 死亡后速度归零；其他状态用 agent 实际速度（巡逻慢、追击快）
            float speed = m_state == EEnemyAIState.Dead ? 0f : m_agent.velocity.magnitude;
            SetSpeedParam(speed);
        }

        /// <summary>设置 Animator 的 Speed 参数（参数不存在时安全跳过）</summary>
        private void SetSpeedParam(float speed)
        {
            if (m_animator != null && m_hasSpeedParam)
            {
                m_animator.SetFloat(k_speedParam, speed);
            }
        }

        /// <summary>
        /// 一次性检查 Animator 中有哪些参数（Awake 调用一次）
        /// 防止素材的 Animator 缺少参数时 SetTrigger/SetFloat 刷一堆警告，
        /// 同时避免每帧读 m_animator.parameters 带来的数组分配
        /// </summary>
        private void CacheAnimatorParameters()
        {
            if (m_animator == null)
            {
                return;
            }

            int speedHash = Animator.StringToHash(k_speedParam);
            int attackHash = Animator.StringToHash(k_attackTriggerParam);
            int deathHash = Animator.StringToHash(k_deathTriggerParam);

            AnimatorControllerParameter[] parameters = m_animator.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                int hash = parameters[i].nameHash;

                if (hash == speedHash)
                {
                    m_hasSpeedParam = true;
                }
                else if (hash == attackHash)
                {
                    m_hasAttackTriggerParam = true;
                }
                else if (hash == deathHash)
                {
                    m_hasDeathTriggerParam = true;
                }
            }
        }

        /// <summary>
        /// 场景视图中绘制感知范围，方便调参（仅 Editor 下显示）
        /// 黄圈=发现距离，红圈=丢失距离，橙圈=攻击距离
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            // 发现范围（黄色）
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, m_detectRange);

            // 丢失范围（红色）
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, m_loseTargetRange);

            // 攻击范围（橙色）
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(transform.position, m_attackRange);
        }
    }
}
