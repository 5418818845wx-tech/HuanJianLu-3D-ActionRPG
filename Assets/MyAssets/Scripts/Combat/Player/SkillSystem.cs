using MyGame.Audio;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 技能系统：管理4个主动技能槽（Q/E/R/F）的释放与冷却
    /// 核心设计：
    /// 1. 每个槽位对应一个 SkillDataSO 配置（由当前武器提供）
    /// 2. 释放技能时：检查CD → 配置命中参数 → 触发动画 → 播放特效
    /// 3. 技能动画通过 Trigger 参数驱动，命中帧由 OnSkillHitFrame 事件触发
    /// 4. 大招（F槽位）需要能量积累，普通技能只需 CD
    /// 5. 武器数据驱动：切换武器时刷新所有技能槽位配置
    /// </summary>
    public class SkillSystem : MonoBehaviour
    {
        [Header("组件引用")]
        [Tooltip("角色 Animator 组件")]
        [SerializeField] private Animator m_animator;

        [Tooltip("命中检测控制器")]
        [SerializeField] private HitboxController m_hitbox;

        [Tooltip("特效挂载根节点（无则用角色自身 Transform）")]
        [SerializeField] private Transform m_vfxRoot;

        /// <summary>当前正在播放的技能特效实例（技能结束时统一停止并销毁）</summary>
        private GameObject m_activeVfx;

        /// <summary>
        /// 本次技能释放是否已经生成过特效。
        /// 一次释放里可能有多个命中帧事件（比如跑攻第2段是双砍），
        /// 靠这个标记保证"一次释放只生成一次特效"，不会重复冒出来。
        /// </summary>
        private bool m_vfxSpawnedThisCast;

        [Header("能量配置")]
        [Tooltip("当前能量值（F槽大招消耗，可通过普攻/受击积累）")]
        [SerializeField] private float m_currentEnergy = 0f;

        [Tooltip("能量最大值")]
        [SerializeField] private float m_maxEnergy = 100f;

        [Tooltip("普攻命中积累能量值")]
        [SerializeField] private float m_energyGainOnHit = 5f;

        /// <summary>4个技能槽位的当前技能数据（按 ESkillSlot 索引）</summary>
        private readonly SkillDataSO[] m_skillSlots = new SkillDataSO[4];

        /// <summary>4个技能槽位的 CD 剩余时间（按 ESkillSlot 索引）</summary>
        private readonly float[] m_cooldownRemaining = new float[4];

        /// <summary>当前正在播放的技能 Trigger 参数名（用于检测动画是否播完）</summary>
        private string m_currentSkillParam = "";

        /// <summary>是否正在播放技能动画</summary>
        private bool m_isCasting = false;

        /// <summary>当前技能动画是否已过命中帧（用于动作取消）</summary>
        private bool m_hasPassedHitFrame = false;

        /// <summary>
        /// 本次技能是否已经真正进入过对应的 Animator 状态。
        /// 释放当帧 Animator 还没切换到技能状态，此时"不在技能状态"是正常现象，
        /// 不能凭它判定技能已结束（否则技能刚触发就会被判结束、特效被立刻销毁）
        /// </summary>
        private bool m_hasEnteredSkillState = false;

        /// <summary>当前正在播放的技能槽位</summary>
        private ESkillSlot m_currentSlot = ESkillSlot.Slot_Q;

        // ===== 跑攻连招专用状态 =====
        // 跑攻是特殊技能：移动中按攻击键触发，有两段衔接（Run_Attack_01 → Run_Attack_02）
        // 不走 SkillDataSO，用内部常量配置，避免占用 E 槽位

        /// <summary>跑攻1的 Animator Trigger 参数名</summary>
        private const string k_runAttack1Param = "Skill1";

        /// <summary>跑攻2的 Animator Trigger 参数名</summary>
        private const string k_runAttack2Param = "Skill2";

        /// <summary>跑攻1伤害倍率</summary>
        private const float k_runAttack1Damage = 1.5f;

        /// <summary>跑攻2伤害倍率（比第一段略高）</summary>
        private const float k_runAttack2Damage = 1.8f;

        /// <summary>跑攻 CD（秒，两段共享一次 CD）</summary>
        private const float k_runAttackCooldown = 2f;

        /// <summary>是否正在跑攻中（第1段或第2段）</summary>
        private bool m_isRunAttacking = false;

        /// <summary>跑攻当前段数（1 或 2）</summary>
        private int m_runAttackStage = 0;

        /// <summary>跑攻输入缓冲：跑攻1播放期间再次按攻击键时置 true，播完时衔接跑攻2</summary>
        private bool m_runAttackBuffered = false;

        /// <summary>是否正在释放技能（外部查询用）</summary>
        public bool IsCasting => m_isCasting;

        /// <summary>当前是否可被取消（命中帧后即可取消）</summary>
        public bool CanBeCanceled => m_isCasting && m_hasPassedHitFrame;

        /// <summary>当前能量值（外部查询，用于 UI 显示）</summary>
        public float CurrentEnergy => m_currentEnergy;

        /// <summary>最大能量值</summary>
        public float MaxEnergy => m_maxEnergy;

        /// <summary>能量进度（0~1，用于 UI 大招图标）</summary>
        public float EnergyProgress => Mathf.Clamp01(m_currentEnergy / m_maxEnergy);

        private void Awake()
        {
            // 自动查找组件
            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }
            if (m_hitbox == null)
            {
                m_hitbox = GetComponent<HitboxController>();
            }
            if (m_vfxRoot == null)
            {
                m_vfxRoot = transform;
            }
        }

        /// <summary>
        /// 设置当前武器数据：由 WeaponSystem 在武器切换时调用
        /// 从武器数据中读取4个槽位的技能配置并刷新本地缓存
        /// </summary>
        /// <param name="weaponData">当前武器配置数据</param>
        public void SetWeaponData(WeaponDataSO weaponData)
        {
            if (weaponData == null) return;

            // 切换武器时取消当前技能
            CancelCast();

            // 从武器配置读取4个槽位的技能数据
            SkillDataSO[] skills = weaponData.Skills;
            for (int i = 0; i < 4; i++)
            {
                m_skillSlots[i] = (skills != null && i < skills.Length) ? skills[i] : null;
            }
        }

        /// <summary>
        /// 尝试释放指定槽位的技能：由 PlayerCombatController 在按下 Q/E/R/F 时调用
        /// 检查：槽位有技能 + CD 已就绪 + 能量满足（仅大招）+ 当前未在释放其他动作
        /// </summary>
        /// <param name="slot">技能槽位</param>
        /// <returns>true=成功释放；false=无法释放</returns>
        public bool TryCastSkill(ESkillSlot slot)
        {
            int idx = (int)slot;
            if (idx < 0 || idx >= m_skillSlots.Length) return false;

            SkillDataSO skill = m_skillSlots[idx];
            if (skill == null)
            {
                // 该槽位无技能，忽略
                return false;
            }

            // 检查 CD 是否就绪
            if (m_cooldownRemaining[idx] > 0f)
            {
                return false;
            }

            // 检查能量（大招需要能量积累）
            if (skill.RequiresEnergy && m_currentEnergy < skill.EnergyCost)
            {
                return false;
            }

            // 通过所有检查，开始释放技能
            StartCast(slot, skill);
            return true;
        }

        // ====================================================================
        // 跑攻连招：移动中按攻击键触发，两段衔接
        // ====================================================================

        /// <summary>
        /// 尝试开始或衔接跑攻：由 Fighter.TryRunAttack() 调用
        /// - 未在跑攻：开始第1段（Run_Attack_01）
        /// - 正在跑攻第1段：缓冲输入，播完时衔接第2段
        /// - 正在跑攻第2段：丢弃输入（最多两段）
        /// </summary>
        /// <returns>true=成功开始或缓冲；false=CD未就绪</returns>
        public bool TryRunAttack()
        {
            // 正在跑攻第1段：缓冲输入，等播完衔接第2段
            if (m_isRunAttacking && m_runAttackStage == 1)
            {
                m_runAttackBuffered = true;
                return true;
            }

            // 正在跑攻第2段：丢弃输入
            if (m_isRunAttacking && m_runAttackStage == 2)
            {
                return true;
            }

            // 检查跑攻 CD（用 Q 槽位的 CD）
            if (m_cooldownRemaining[(int)ESkillSlot.Slot_Q] > 0f)
            {
                return false;
            }

            // 开始第1段跑攻
            StartRunAttack(1);
            return true;
        }

        /// <summary>
        /// 开始指定段数的跑攻动画
        /// </summary>
        /// <param name="stage">段数（1 或 2）</param>
        private void StartRunAttack(int stage)
        {
            m_isCasting = true;
            m_isRunAttacking = true;
            m_runAttackStage = stage;
            m_hasPassedHitFrame = false;
            m_runAttackBuffered = false;

            // 根据段数设置参数
            string triggerParam = stage == 1 ? k_runAttack1Param : k_runAttack2Param;
            float damageMultiplier = stage == 1 ? k_runAttack1Damage : k_runAttack2Damage;
            m_currentSkillParam = triggerParam;
            m_hasEnteredSkillState = false;

            // 第1段开始时设置 CD
            if (stage == 1)
            {
                m_cooldownRemaining[(int)ESkillSlot.Slot_Q] = k_runAttackCooldown;
                // 只有第1段重置特效标记：第1、2段属于同一次跑攻，只出一个特效
                m_vfxSpawnedThisCast = false;
            }

            // 配置命中系统
            if (m_hitbox != null)
            {
                m_hitbox.SetAttackType(EAttackType.SkillAttack);
                m_hitbox.SetDamageMultiplier(damageMultiplier);
                m_hitbox.ResetHitTargets();
            }

            // 触发动画
            m_animator.SetTrigger(triggerParam);
        }

        /// <summary>
        /// 开始释放技能：配置命中参数、消耗能量、触发动画、播放特效
        /// </summary>
        private void StartCast(ESkillSlot slot, SkillDataSO skill)
        {
            m_isCasting = true;
            m_hasPassedHitFrame = false;
            m_vfxSpawnedThisCast = false;
            m_currentSlot = slot;
            m_currentSkillParam = skill.AnimTriggerParam;
            m_hasEnteredSkillState = false;

            // 消耗能量（仅大招需要）
            if (skill.RequiresEnergy)
            {
                m_currentEnergy = Mathf.Max(0f, m_currentEnergy - skill.EnergyCost);
            }

            // 设置 CD（按槽位索引）：走升级系统换算出"当前等级的最终冷却"
            m_cooldownRemaining[(int)slot] = ResolveCooldown(skill);

            // 配置命中系统：技能类型 + 伤害倍率 + 重置命中目标
            if (m_hitbox != null)
            {
                m_hitbox.SetAttackType(EAttackType.SkillAttack);
                m_hitbox.SetDamageMultiplier(ResolveDamageMultiplier(skill));
                m_hitbox.ResetHitTargets();
            }

            // 触发技能动画
            if (!string.IsNullOrEmpty(skill.AnimTriggerParam))
            {
                m_animator.SetTrigger(skill.AnimTriggerParam);
            }

            // 注意：特效不在这里生成。
            // 改由动画事件 OnSkillHitFrame 在"命中帧"生成，让刀光与"刀挥到位"同步，
            // 而不是按下按键的瞬间就冒出来（那样会比挥砍早 0.3 秒左右）。
            // 这里只清理上一次可能残留的特效。
            StopActiveVfx();
        }

        /// <summary>
        /// 生成技能特效：根据配置的骨骼路径找到挂载点，无则用 m_vfxRoot
        /// </summary>
        private void SpawnSkillVfx(SkillDataSO skill)
        {
            Transform attachPoint = m_vfxRoot;
            if (!string.IsNullOrEmpty(skill.VfxBonePath))
            {
                // 通过路径查找骨骼子物体（如 "Root/Hips/Spine/RightHand"）
                Transform bone = m_vfxRoot.Find(skill.VfxBonePath);
                if (bone != null)
                {
                    attachPoint = bone;
                }
                else
                {
                    Debug.LogWarning($"[SkillSystem] 未找到骨骼路径: {skill.VfxBonePath}", this);
                }
            }

            // 实例化特效预制体，挂载到对应骨骼
            GameObject vfx = Instantiate(skill.VfxPrefab, attachPoint.position, attachPoint.rotation);
            vfx.transform.SetParent(attachPoint);

            // 缩放：素材本身是按大尺寸做的，需要按技能配置缩小到角色比例
            float scale = skill.VfxScale;
            vfx.transform.localScale = Vector3.one * scale;

            // 位置偏移：把特效从刀柄挪到刀身（否则会糊在握把上）
            vfx.transform.localPosition = skill.VfxLocalOffset;

            // 旋转偏移：把刀光弧转到本动作的挥砍平面上
            vfx.transform.localRotation = Quaternion.Euler(skill.VfxLocalEuler);

            // 注意：这里【不再】做任何粒子系统配置。
            // 开关循环、播放速度都已经烤进特效预制体资源本身 —— 因为热更代码一旦引用
            // ParticleSystem.MainModule 就会让解释器抛异常（原因见上方注释块）。

            // 记录当前特效：技能动画播完或被取消时由 StopActiveVfx() 统一停止并销毁
            // 注意：这些特效预制体内部的发射器全是 looping，放任不管会一直播下去，
            // 所以不能用固定时间延迟销毁，必须跟着技能动画的结束时机来停
            m_activeVfx = vfx;

            // 兜底销毁：万一技能状态检测异常导致 StopActiveVfx() 没被调用，防止特效永久残留
            Destroy(vfx, 8f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // 这里原来有一个 ConfigureVfxParticles()，用来按 SkillDataSO.VfxPlaySpeed
        // 调整特效内所有粒子系统的 simulationSpeed。
        //
        // 它已被【删除】，原因：调整播放速度必须访问 ParticleSystem.MainModule，
        // 而该类型不在玩家包的 AOT 保留清单里，解释器加载本方法时就会抛
        //   ExecutionEngineException: ReadTypeFromResolutionScope TYPEREF fail.
        //   type:UnityEngine.ParticleSystem.MainModule
        // 从而把整条特效生成流程打断。
        //
        // 替代方案：播放速度直接烤进特效预制体资源本身，不再靠运行时设置：
        //   · Prefabs/VFX/Q_RunAttack_VFX.prefab        （Hovl 层 ×2，Red_clue 层 ×1.4）
        //   · Prefabs/VFX/RedClue Slashfx/.../Slash_fx_03.prefab （×1.4）
        //
        // ⚠️ 副作用：SkillDataSO 上的「Vfx Play Speed」字段暂时失效（死字段）。
        // 想让这个字段重新生效，必须重新生成 link.xml（补上 UnityEngine.ParticleSystemModule）
        // 并重新打玩家包，把 ParticleSystem.MainModule 保留进 AOT。
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 停止当前技能特效：技能动画播完 / 技能被打断时调用
        /// 先停止所有粒子发射，留一小段余韵后销毁整个特效物体，
        /// 避免出现"动画播完了特效还在"的情况。
        ///
        /// ⚠️ 这里【绝对不能】访问 systems[i].main（= ParticleSystem.MainModule）！
        /// 本方法属于热更程序集（运行时解释执行），而 ParticleSystem.MainModule 这个类型
        /// 没有被打进玩家包的 AOT 元数据（不在 Assets/HybridCLR/Generate/link.xml 保留清单里）。
        /// 只要方法体里出现这个类型，解释器加载本方法时就会抛：
        ///   ExecutionEngineException: ReadTypeFromResolutionScope TYPEREF fail.
        ///   type:UnityEngine.ParticleSystem.MainModule
        ///
        /// 最坑的是本方法会被 SetWeaponData → CancelCast 在【游戏启动装配武器时】调到，
        /// 一炸就中断武器/技能槽初始化，表现成"按键没反应、技能放不出来"，
        /// 看起来完全不像特效问题。
        ///
        /// 「余韵时长」以前是遍历粒子读 startLifetime 算最大值，现在直接用固定值。
        /// </summary>
        private void StopActiveVfx()
        {
            if (m_activeVfx == null)
            {
                return;
            }

            // ⚠️ 这里故意【不】遍历粒子系统、也不调用 ParticleSystem.Stop()。
            //
            // 本工程的热更程序集没有保留 UnityEngine.ParticleSystemModule 的 AOT 元数据
            // （Assets/HybridCLR/Generate/link.xml 里根本没有这个程序集）。
            // 热更代码只要引用 ParticleSystem / ParticleSystem.MainModule 相关类型，
            // 解释器在加载本方法时就会抛：
            //   ExecutionEngineException: ReadTypeFromResolutionScope TYPEREF fail.
            //
            // 而本方法会被 SetWeaponData → CancelCast 在【游戏启动装配武器时】调到，
            // 一炸就中断技能槽初始化 —— 表现成"按键没反应、技能放不出来"，
            // 而不是看起来像特效问题。
            //
            // 所以这里对粒子零依赖：特效素材已经在预制体里关掉了循环（只爆一次），
            // 直接延迟销毁即可，不需要先 Stop 再销毁。
            Destroy(m_activeVfx, 0.5f);
            m_activeVfx = null;
        }

        /// <summary>
        /// 仅清空跑攻第2段的衔接缓冲，不打断当前正在播放的技能/跑攻动画
        ///
        /// 使用场景：打开 UI 暂停游戏时调用，
        /// 防止玩家在 UI 上的点击被缓存成"跑攻第2段"，关闭 UI 后自动打出
        /// </summary>
        public void ClearBufferedInput()
        {
            m_runAttackBuffered = false;
        }

        /// <summary>
        /// 强制取消技能释放：用于被打断场景（如闪避、受击）
        /// 重置技能状态，不退 CD（已经消耗）
        /// </summary>
        public void CancelCast()
        {
            StopActiveVfx();
            m_isCasting = false;
            m_hasPassedHitFrame = false;
            m_currentSkillParam = "";
            m_isRunAttacking = false;
            m_runAttackStage = 0;
            m_runAttackBuffered = false;
        }

        /// <summary>
        /// 普攻命中时积累能量：由 ComboSystem / ChargeAttackSystem 在命中后调用
        /// </summary>
        public void AddEnergyOnHit()
        {
            m_currentEnergy = Mathf.Min(m_maxEnergy, m_currentEnergy + m_energyGainOnHit);
        }

        /// <summary>
        /// 受击时积累能量（受击越多，大招越快可用）
        /// </summary>
        public void AddEnergyOnDamaged(float amount)
        {
            m_currentEnergy = Mathf.Min(m_maxEnergy, m_currentEnergy + amount);
        }

        /// <summary>
        /// 获取指定槽位的 CD 剩余时间（用于 UI 显示）
        /// </summary>
        public float GetCooldownRemaining(ESkillSlot slot)
        {
            int idx = (int)slot;
            if (idx < 0 || idx >= m_cooldownRemaining.Length) return 0f;
            return m_cooldownRemaining[idx];
        }

        /// <summary>
        /// 获取指定槽位的技能配置（用于 UI 显示技能名/冷却/伤害倍率等）
        /// 槽位为空时返回 null，调用方需自行判空
        /// </summary>
        public SkillDataSO GetSkill(ESkillSlot slot)
        {
            int idx = (int)slot;
            if (idx < 0 || idx >= m_skillSlots.Length) return null;
            return m_skillSlots[idx];
        }

        /// <summary>
        /// 获取指定槽位的 CD 进度（0~1，1表示就绪）
        /// </summary>
        public float GetCooldownProgress(ESkillSlot slot)
        {
            int idx = (int)slot;
            if (idx < 0 || idx >= m_skillSlots.Length || m_skillSlots[idx] == null) return 0f;

            float cd = ResolveCooldown(m_skillSlots[idx]);
            if (cd <= 0f) return 1f;

            return Mathf.Clamp01(1f - m_cooldownRemaining[idx] / cd);
        }

        // ====================================================================
        // 技能等级的数值换算
        // ====================================================================

        /// <summary>
        /// 该技能当前等级的最终冷却。
        /// 没接升级系统时（例如只放了个技能系统的测试场景）直接用配置值，不会报错
        /// </summary>
        private static float ResolveCooldown(SkillDataSO skill)
        {
            if (skill == null)
            {
                return 0f;
            }

            SkillUpgradeSystem upgrades = SkillUpgradeSystem.Instance;
            return upgrades != null
                ? upgrades.GetCooldown(skill.Id, skill.Cooldown)
                : skill.Cooldown;
        }

        /// <summary>该技能当前等级的最终伤害倍率</summary>
        private static float ResolveDamageMultiplier(SkillDataSO skill)
        {
            if (skill == null)
            {
                return 1f;
            }

            SkillUpgradeSystem upgrades = SkillUpgradeSystem.Instance;
            return upgrades != null
                ? upgrades.GetDamageMultiplier(skill.Id, skill.DamageMultiplier)
                : skill.DamageMultiplier;
        }

        private void Update()
        {
            // ===== CD 计时：每帧递减所有槽位的 CD =====
            for (int i = 0; i < m_cooldownRemaining.Length; i++)
            {
                if (m_cooldownRemaining[i] > 0f)
                {
                    m_cooldownRemaining[i] = Mathf.Max(0f, m_cooldownRemaining[i] - Time.deltaTime);
                }
            }

            // ===== 释放阶段：检测技能动画是否播完 =====
            if (m_isCasting && !string.IsNullOrEmpty(m_currentSkillParam))
            {
                AnimatorStateInfo stateInfo = m_animator.GetCurrentAnimatorStateInfo(0);

                // 通过 Trigger 参数名哈希匹配当前状态
                bool inSkillState = stateInfo.shortNameHash == Animator.StringToHash(m_currentSkillParam);

                // 先记录"确实进入过技能状态"
                if (inSkillState)
                {
                    m_hasEnteredSkillState = true;
                }

                // 在技能状态里 -> 看动画是否播完；
                // 不在技能状态 -> 只有"进去过又出来了"才算结束，否则是释放当帧的正常现象
                bool skillFinished = inSkillState
                    ? stateInfo.normalizedTime >= 1f
                    : m_hasEnteredSkillState;

                if (skillFinished)
                {
                    // 跑攻第1段播完：检查是否有缓冲输入，有则衔接第2段
                    if (m_isRunAttacking && m_runAttackStage == 1 && m_runAttackBuffered)
                    {
                        StartRunAttack(2);
                        return;
                    }

                    // 技能动画播完或已离开技能状态：结束释放
                    StopActiveVfx();
                    m_isCasting = false;
                    m_hasPassedHitFrame = false;
                    m_currentSkillParam = "";
                    m_isRunAttacking = false;
                    m_runAttackStage = 0;
                    m_runAttackBuffered = false;
                }
            }
        }

        // ====================================================================
        // 以下方法由动画事件调用（在技能动画上添加 Animation Event）
        // ====================================================================

        /// <summary>
        /// 技能命中帧：在技能动画的命中帧调用
        /// 执行命中检测，对范围内敌人造成技能伤害
        /// 同时标记 m_hasPassedHitFrame = true，进入可取消窗口
        /// </summary>
        public void OnSkillHitFrame()
        {
            // 刀光在"命中帧"生成：让特效与"刀挥到位"同步
            // （放在最前面，保证命中判定和特效在同一帧触发）
            if (m_isCasting && !m_vfxSpawnedThisCast)
            {
                SkillDataSO vfxSkill = GetSkill(m_currentSlot);
                if (vfxSkill != null && vfxSkill.VfxPrefab != null)
                {
                    m_vfxSpawnedThisCast = true;
                    SpawnSkillVfx(vfxSkill);
                }
            }

            int damaged = 0;
            if (m_hitbox != null)
            {
                damaged = m_hitbox.PerformHitDetect();
            }

            // 命中音：弹反槽（F）用弹反成功音，其余用对应技能的命中音
            if (damaged > 0)
            {
                AudioManager.Play(ResolveSkillHitSfx());

                // 打击感：技能命中介于普攻与重击之间
                HitStop.Play(0.07f);
                CameraShake.Shake(0.13f, 0.18f);
            }

            // 命中帧已过，允许后续被外部取消
            m_hasPassedHitFrame = true;
        }

        /// <summary>
        /// 技能释放开始：在技能动画第1帧调用
        /// 重置命中目标列表
        /// </summary>
        public void OnSkillStart()
        {
            if (m_hitbox != null)
            {
                m_hitbox.ResetHitTargets();
            }

            AudioManager.Play(ResolveSkillCastSfx());
        }

        /// <summary>技能槽位（0~3）对应的起手音</summary>
        private ESfx ResolveSkillCastSfx()
        {
            switch (Mathf.Clamp((int)m_currentSlot, 0, 3))
            {
                case 1: return ESfx.Skill_Cast_02;
                case 2: return ESfx.Skill_Cast_03;
                case 3: return ESfx.Skill_Cast_04;
                default: return ESfx.Skill_Cast_01;
            }
        }

        /// <summary>技能槽位（0~3）对应的命中音；弹反槽（F）用弹反成功音</summary>
        private ESfx ResolveSkillHitSfx()
        {
            switch (Mathf.Clamp((int)m_currentSlot, 0, 3))
            {
                case 1: return ESfx.Skill_Hit_02;
                case 2: return ESfx.Skill_Hit_03;
                case 3: return ESfx.Parry_Success;
                default: return ESfx.Skill_Hit_01;
            }
        }
    }
}
