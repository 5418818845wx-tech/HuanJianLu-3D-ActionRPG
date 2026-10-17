using MyGame.Audio;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 连招系统：管理固定段数普攻连招的播放与衔接
    /// 核心设计：
    /// 1. Update 驱动，通过读取 Animator 的 normalizedTime 检测动画进度，无需手动计时
    /// 2. 每段动画期间仅缓冲1次点击（多余点击丢弃），动画完整播完后检查缓冲决定是否接下一段
    /// 3. 武器数据驱动：段数/伤害倍率/Trigger参数/取消窗口都从 WeaponDataSO 读取
    /// 4. 动作取消：暴露 CanBeCanceled() 给 Fighter，命中帧后可被闪避取消
    /// </summary>
    public class ComboSystem : MonoBehaviour
    {
        [Header("组件引用")]
        [Tooltip("角色 Animator 组件")]
        [SerializeField] private Animator m_animator;

        [Tooltip("命中检测控制器（用于攻击命中帧调用）")]
        [SerializeField] private HitboxController m_hitbox;

        [Header("连招手感设置")]
        [Tooltip("当前段动画播放到此进度后才接受下一次输入缓冲（0~1）。值越大每段越清晰，值越小越流畅")]
        [SerializeField] private float m_minProgressBeforeBuffer = 0.4f;

        /// <summary>输入缓冲器：管理连招中的点击缓冲（每段1次）</summary>
        private readonly InputBuffer m_inputBuffer = new InputBuffer();

        /// <summary>当前武器数据（由 WeaponSystem 通过 SetWeaponData 设置）</summary>
        private WeaponDataSO m_weaponData;

        /// <summary>
        /// 当前武器连招各段 Trigger 参数名的哈希（切换武器时预计算一次）。
        /// 攻击中每帧都要判断"当前 Animator 状态是不是某个攻击段"，
        /// 预存哈希可以避免每帧对字符串做 Animator.StringToHash
        /// </summary>
        private int[] m_triggerHashes = System.Array.Empty<int>();

        /// <summary>当前连招段数（0=未攻击，1~N=第几段普攻，N=武器连招段数）</summary>
        private int m_currentComboIndex = 0;

        /// <summary>是否正在播放攻击动画</summary>
        private bool m_isAttacking = false;

        /// <summary>当前段动画是否已过命中帧（用于动作取消窗口判断）</summary>
        private bool m_hasPassedHitFrame = false;

        /// <summary>当前段是否已过最短播放进度，可以接受下一次输入缓冲</summary>
        private bool m_canBufferInput = false;

        /// <summary>
        /// 本段使用的挥空音。**在本段开始时确定**，之后不再重新解析。
        ///
        /// 为什么不在动画事件里现算：动画事件触发时若 Animator 还没真正切进攻击状态
        /// （过渡被其它状态挡住，会晚个零点几秒），Update 会先判定"不在攻击状态"而
        /// 把段号重置成 0，导致这一段退回用第 1 段的风声。开局就定下来最稳。
        /// </summary>
        private ESfx m_segmentSwingSfx = ESfx.Swing_Light_01;

        /// <summary>
        /// 起手宽限（秒）。
        ///
        /// 从 SetTrigger 到 Animator 真正切进攻击状态之间会差 1 帧左右；如果过渡被别的
        /// 状态挡住（例如起手瞬间还在空中、IsGrounded 不满足），会拖得更久。这段窗口里
        /// Update 会看到"当前不在攻击状态"，但那是假象——不能据此判定连招被打断，
        /// 否则连招会在起手当帧被自己结束掉：段号被清 0、缓冲窗口失效，而且**没被消费的
        /// Trigger 会留到下一次回到待机时才生效**，表现就是"没按鼠标却自己又挥一刀"。
        /// </summary>
        private const float k_startGraceSeconds = 0.3f;

        /// <summary>本段起手的时间点（Time.time），配合起手宽限使用</summary>
        private float m_segmentStartTime;

        /// <summary>当前正在攻击（只读属性，供外部查询）</summary>
        public bool IsAttacking => m_isAttacking;

        /// <summary>当前是否可被取消（命中帧后即可取消，原神式手感）</summary>
        public bool CanBeCanceled => m_isAttacking && m_hasPassedHitFrame;

        /// <summary>当前连招段数（只读，0 表示未攻击）</summary>
        public int CurrentComboIndex => m_currentComboIndex;

        private void Awake()
        {
            // 自动查找 Animator 组件
            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }

            // 自动查找 HitboxController
            if (m_hitbox == null)
            {
                m_hitbox = GetComponent<HitboxController>();
            }
        }

        /// <summary>
        /// 设置当前武器数据：由 WeaponSystem 在武器切换时调用
        /// 武器数据驱动连招段数、Trigger参数名、伤害倍率、取消窗口起点
        /// </summary>
        /// <param name="weaponData">当前武器配置数据</param>
        public void SetWeaponData(WeaponDataSO weaponData)
        {
            m_weaponData = weaponData;

            // 预计算各段 Trigger 的参数哈希，供攻击中每帧的状态判断使用
            m_triggerHashes = BuildTriggerHashes(weaponData);

            // 切换武器时重置连招状态，避免残留旧武器连招进度
            EndCombo();
        }

        /// <summary>把武器配置里的连招 Trigger 参数名预先转成哈希（空名字记为 0，判断时跳过）</summary>
        private static int[] BuildTriggerHashes(WeaponDataSO weaponData)
        {
            string[] triggers = weaponData != null ? weaponData.ComboTriggerParams : null;
            if (triggers == null || triggers.Length == 0)
            {
                return System.Array.Empty<int>();
            }

            int[] hashes = new int[triggers.Length];
            for (int i = 0; i < triggers.Length; i++)
            {
                hashes[i] = string.IsNullOrEmpty(triggers[i]) ? 0 : Animator.StringToHash(triggers[i]);
            }

            return hashes;
        }

        /// <summary>
        /// 尝试开始或衔接连招：由 PlayerCombatController 在检测到短按时调用
        /// - 如果当前空闲：开始第1段普攻
        /// - 如果正在攻击：缓冲本次输入（每段仅缓冲1次，多余丢弃）
        /// </summary>
        /// <returns>true=成功开始或缓冲连招；false=无法开始（如无武器数据）</returns>
        public bool TryStartCombo()
        {
            // 武器数据未配置则无法攻击
            if (m_weaponData == null)
            {
                Debug.LogWarning("[ComboSystem] 无武器数据，无法开始连招", this);
                return false;
            }

            // 空闲状态：开始第1段普攻
            if (!m_isAttacking)
            {
                StartComboSegment(1);
                return true;
            }

            // 攻击中：缓冲输入（InputBuffer 内部保证每段仅缓冲1次）
            // 但必须等当前段播放到 m_minProgressBeforeBuffer 后才接受，避免快速点击跳过中间段
            if (!m_canBufferInput)
            {
                // 当前段还没播放到最短进度，丢弃本次点击
                return true;
            }
            m_inputBuffer.BufferAttack();
            return true;
        }

        /// <summary>
        /// 开始指定段数的连招动画
        /// 设置当前段数、触发对应 Animator 参数、通知命中系统重置
        /// </summary>
        /// <param name="comboIndex">连招段数（1~ComboCount）</param>
        private void StartComboSegment(int comboIndex)
        {
            m_currentComboIndex = comboIndex;
            // 本段的风声在这里一次性定下来（原因见字段注释）
            m_segmentSwingSfx = ResolveSwingSfx();
            m_isAttacking = true;
            m_segmentStartTime = Time.time;
            m_hasPassedHitFrame = false;
            // 新段开始时禁止缓冲输入，必须播放到 m_minProgressBeforeBuffer 后才接受
            m_canBufferInput = false;

            // 清空输入缓冲，准备接收本段动画期间的点击
            m_inputBuffer.ClearBuffer();

            // 通知命中系统：本次攻击为普攻，并按段位设置伤害倍率
            if (m_hitbox != null && m_weaponData.ComboDamageMultipliers != null
                && comboIndex - 1 < m_weaponData.ComboDamageMultipliers.Length)
            {
                m_hitbox.SetAttackType(EAttackType.NormalAttack);
                m_hitbox.SetDamageMultiplier(m_weaponData.ComboDamageMultipliers[comboIndex - 1]);
                m_hitbox.ResetHitTargets();
            }

            // 触发对应段的 Animator 参数（段数 - 1 = 数组索引）
            string[] triggers = m_weaponData.ComboTriggerParams;
            if (triggers != null && comboIndex - 1 < triggers.Length
                && !string.IsNullOrEmpty(triggers[comboIndex - 1]))
            {
                // 先把历史遗留的 Trigger 清干净，再设本段的：
                // 没被消费的 Trigger 会一直挂着，等下一次回到待机时才突然生效，
                // 表现就是"没人按键，角色自己又挥了一刀"
                ResetComboTriggers();
                m_animator.SetTrigger(triggers[comboIndex - 1]);
            }
            else
            {
                Debug.LogWarning($"[ComboSystem] 第 {comboIndex} 段 Trigger 参数缺失", this);
            }
        }

        private void Update()
        {
            // 仅在攻击中时检测动画进度
            if (!m_isAttacking) return;

            // 检查当前是否仍处于某个攻击状态
            AnimatorStateInfo stateInfo = m_animator.GetCurrentAnimatorStateInfo(0);
            if (!IsAttackState(stateInfo.shortNameHash))
            {
                // 起手当帧 Animator 还没切进攻击状态，这是正常过渡不是被打断：
                // 宽限期内先不动，等它切过去（否则连招会在起手当帧被自己结束掉，
                // 并残留一个没被消费的 Trigger，之后"没人按键自己又挥一刀"）
                if (Time.time - m_segmentStartTime < k_startGraceSeconds)
                {
                    return;
                }

                // 不在攻击状态（可能已被其他系统打断），重置连招
                EndCombo();
                return;
            }

            // 动作取消窗口检测：当前段已过命中帧后即可被外部取消
            // （取消由 Fighter 调用 EndCombo() 完成，这里只更新标志）
            UpdateCancelWindow(stateInfo);

            // 更新输入缓冲许可：当前段播放到最短进度后才开始接受下一次输入
            if (!m_canBufferInput && stateInfo.normalizedTime >= m_minProgressBeforeBuffer)
            {
                m_canBufferInput = true;
            }

            // 检查动画是否接近播完（0.9，提前于 Animator 的 exitTime=0.95 衔接，避免竞争）
            if (stateInfo.normalizedTime >= 0.9f)
            {
                // 动画接近播完：检查是否有缓冲的输入
                if (m_inputBuffer.ConsumeBuffer() && m_currentComboIndex < m_weaponData.ComboCount)
                {
                    // 有缓冲输入且未到最后一段：衔接下一段普攻
                    StartComboSegment(m_currentComboIndex + 1);
                }
                else if (stateInfo.normalizedTime >= 1f)
                {
                    // 无缓冲输入或已到最后一段且动画完全播完：结束连招，回到待机
                    EndCombo();
                }
            }
        }

        /// <summary>
        /// 更新动作取消窗口标志：通过 normalizedTime 检测是否已过命中帧
        /// 命中帧后置 m_hasPassedHitFrame = true，允许外部取消
        /// </summary>
        private void UpdateCancelWindow(AnimatorStateInfo stateInfo)
        {
            if (m_hasPassedHitFrame) return;

            // 读取当前段的取消窗口起点（normalizedTime 阈值）
            float cancelStart = 1f;
            float[] cancels = m_weaponData.ComboCancelWindowStarts;
            if (cancels != null && m_currentComboIndex - 1 < cancels.Length)
            {
                cancelStart = cancels[m_currentComboIndex - 1];
            }

            // 进度超过阈值：标记为可取消
            if (stateInfo.normalizedTime >= cancelStart)
            {
                m_hasPassedHitFrame = true;
            }
        }

        /// <summary>
        /// 判断当前 Animator 状态是否为攻击状态
        /// 通过对比状态名哈希与预存的各段 Trigger 参数哈希
        /// </summary>
        /// <param name="stateHash">当前状态的 shortNameHash</param>
        /// <returns>是否为攻击状态</returns>
        private bool IsAttackState(int stateHash)
        {
            // 直接用 SetWeaponData 时预计算好的哈希，攻击中每帧零字符串开销
            for (int i = 0; i < m_triggerHashes.Length; i++)
            {
                if (m_triggerHashes[i] != 0 && stateHash == m_triggerHashes[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 仅清空待衔接的点击缓冲，不打断当前正在播放的连招段
        ///
        /// 使用场景：打开 UI 暂停游戏时调用。
        /// 玩家在 UI 上的点击不应被缓存成"下一段连招"，
        /// 但暂停前已经挥出去的当前段攻击应保留，关闭 UI 后正常播完
        /// </summary>
        public void ClearBufferedInput()
        {
            m_inputBuffer.ClearBuffer();
        }

        /// <summary>
        /// 结束连招：重置所有状态，回到待机
        /// 可由内部（动画播完无缓冲）或外部（如闪避打断）调用
        /// </summary>
        public void EndCombo()
        {
            m_isAttacking = false;
            m_currentComboIndex = 0;
            m_hasPassedHitFrame = false;
            m_canBufferInput = false;
            m_inputBuffer.ClearBuffer();

            // 收尾时清掉可能没被消费的攻击 Trigger：
            // 挂着的 Trigger 会在角色回到待机后突然生效，变成"没人按键却自己挥一刀"
            ResetComboTriggers();
        }

        /// <summary>
        /// 清空全部连招 Trigger（防御性清理）。
        ///
        /// Animator 的 Trigger 是"设了就一直挂着、直到被某条过渡消费掉"的语义。
        /// 只要有一次 SetTrigger 没赶上能消费它的过渡（例如起手瞬间不满足 IsGrounded），
        /// 它就会潜伏着，等角色回到待机、过渡重新可用时才突然触发——玩家看到的就是
        /// "我明明没按鼠标，角色又自己挥了一刀"。
        /// </summary>
        private void ResetComboTriggers()
        {
            if (m_animator == null || m_weaponData == null)
            {
                return;
            }

            string[] triggers = m_weaponData.ComboTriggerParams;
            if (triggers == null)
            {
                return;
            }

            for (int i = 0; i < triggers.Length; i++)
            {
                if (!string.IsNullOrEmpty(triggers[i]))
                {
                    m_animator.ResetTrigger(triggers[i]);
                }
            }
        }

        // ====================================================================
        // 以下方法由动画事件调用（在攻击动画片段上添加 Animation Event）
        // 事件名必须与这些方法名完全一致
        // ====================================================================

        /// <summary>
        /// 攻击开始：在每段攻击动画的第1帧调用
        /// 重置命中目标列表，确保本次挥击能重新命中所有敌人
        /// </summary>
        public void OnAttackStart()
        {
            if (m_hitbox != null)
            {
                m_hitbox.ResetHitTargets();
            }

            // 挥空声：起手就响，与"命中声"分两个时机，打空时只有风声
            AudioManager.Play(m_segmentSwingSfx);
        }

        /// <summary>
        /// 同一段动画里的"额外挥击"：由动画事件在第二次挥砍前调用
        ///
        /// 有些连招段是双段挥砍（如 Combo_Attack_01_02 在 0.217s、0.5s 各命中一次），
        /// 但整段只有一个 OnAttackStart，第二次挥击原本是无声的——听起来就是
        /// "挥了两下只响一声"。这个事件专门给额外那一次挥击补上风声。
        /// </summary>
        public void OnAttackSwing()
        {
            AudioManager.Play(m_segmentSwingSfx);
        }

        /// <summary>
        /// 命中帧：在攻击动画的武器挥击到目标的帧调用
        /// 执行实际的命中检测，对范围内的敌人造成伤害
        /// 同时标记 m_hasPassedHitFrame = true，进入可取消窗口
        /// </summary>
        public void OnHitFrame()
        {
            int damaged = 0;
            if (m_hitbox != null)
            {
                damaged = m_hitbox.PerformHitDetect();
            }

            // 命中帧只做结算与打击感，**不再额外出一声打击音**。
            //
            // 原因：命中判定是 1.2 米半径的球形（含身后），玩家"对着空气砍"时也可能
            // 扫到侧面/背后的敌人，于是挥空音之外又叠一声打击音——听起来就是
            // "对着空气砍，有时一声有时两声"。命中反馈改为交给受击方自己发声
            // （敌人受击音是 3D 的，能听出方位，不会和玩家自己的挥砍混在一起）
            if (damaged > 0)
            {
                // 打击感：极短顿帧 + 轻微震屏，让普攻也有"打到了"的分量
                HitStop.Play(0.05f);
                CameraShake.Shake(0.07f, 0.12f);
            }

            // 命中帧已过，允许后续被外部取消（如闪避取消后摇）
            m_hasPassedHitFrame = true;
        }

        /// <summary>当前连招段位对应的挥空音（段位越靠后，挥砍听感越重）</summary>
        private ESfx ResolveSwingSfx()
        {
            int index = Mathf.Clamp(m_currentComboIndex, 1, 4);
            switch (index)
            {
                case 2: return ESfx.Swing_Light_02;
                case 3: return ESfx.Swing_Light_03;
                case 4: return ESfx.Swing_Light_04;
                default: return ESfx.Swing_Light_01;
            }
        }

        /// <summary>
        /// 攻击结束：在攻击动画的最后一帧调用
        /// 注意：实际的连招衔接由 Update 中的 normalizedTime 检测处理
        /// 此方法仅用于补充逻辑（如清理特效），目前为空
        /// </summary>
        public void OnAttackEnd()
        {
            // 连招衔接逻辑已在 Update 中通过 normalizedTime 处理
            // 此处保留方法以匹配动画事件，可按需扩展
        }
    }
}
