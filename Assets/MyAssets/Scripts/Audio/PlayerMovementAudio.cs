using KinematicCharacterController;
using KinematicCharacterController.Examples;
using MyGame.UI;
using UnityEngine;

namespace MyGame.Audio
{
    /// <summary>
    /// 玩家移动音效：脚步 / 起跳 / 落地 / 闪避
    ///
    /// 为什么用轮询而不是动画事件：
    /// 走跑动画是混合树（Blend Tree），没有可挂事件的固定剪辑；
    /// 而且脚步节奏应当跟随实际速度，轮询速度来定间隔反而更自然。
    ///
    /// 状态来源：
    /// - 是否站在地面 → KinematicCharacterMotor.GroundingStatus.IsStableOnGround
    /// - 是否在移动   → ExampleCharacterController.IsMoving（与动画状态同源，不会画面在跑却没声音）
    /// - 是否在闪避   → Animator 的 IsDodging 参数
    ///
    /// 挂载位置：玩家预制体
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerMovementAudio : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("脚步节奏")]
        [Tooltip("慢走时两次脚步声的间隔（秒）")]
        [SerializeField, Range(0.2f, 1.2f)] private float m_walkStepInterval = 0.55f;

        [Tooltip("全速奔跑时两次脚步声的间隔（秒）")]
        [SerializeField, Range(0.15f, 0.8f)] private float m_runStepInterval = 0.32f;

        [Tooltip("视为全速奔跑的速度（米/秒），用于在走与跑之间插值")]
        [SerializeField, Range(1f, 12f)] private float m_runSpeed = 5f;

        [Header("音量")]
        [Tooltip("脚步音量缩放（脚步是高频音，适度调低才不吵）")]
        [SerializeField, Range(0f, 1f)] private float m_footstepVolumeScale = 0.55f;

        // ====================================================================
        // 组件引用
        // ====================================================================

        private KinematicCharacterMotor m_motor;
        private ExampleCharacterController m_controller;
        private Animator m_animator;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>下一次允许播脚步的时间点</summary>
        private float m_nextStepTime;

        /// <summary>上一帧是否站在地面（用于检测起跳/落地的瞬间）</summary>
        private bool m_wasGrounded = true;

        /// <summary>上一帧是否在闪避</summary>
        private bool m_wasDodging;

        /// <summary>Animator 中闪避参数名</summary>
        private static readonly int s_isDodgingHash = Animator.StringToHash("IsDodging");

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            m_motor = GetComponent<KinematicCharacterMotor>();
            m_controller = GetComponent<ExampleCharacterController>();
            m_animator = GetComponentInChildren<Animator>();

            m_wasGrounded = IsGrounded();
        }

        private void Update()
        {
            // 面板打开时游戏暂停：此时不播脚步，避免"角色站着不动却一直响"
            if (UGUIManager.IsGamePaused)
            {
                // 暂停等于"人停下了"，同样要把正在响的那一声掐掉
                AudioManager.StopFootsteps();
                return;
            }

            UpdateJumpAndLand();
            UpdateDodge();
            UpdateFootsteps();
        }

        // ====================================================================
        // 起跳 / 落地
        // ====================================================================

        private void UpdateJumpAndLand()
        {
            bool grounded = IsGrounded();

            if (grounded && !m_wasGrounded)
            {
                AudioManager.Play(ESfx.Land, 0.8f);
            }
            else if (!grounded && m_wasGrounded)
            {
                AudioManager.Play(ESfx.Jump, 0.6f);
            }

            m_wasGrounded = grounded;
        }

        // ====================================================================
        // 闪避
        // ====================================================================

        private void UpdateDodge()
        {
            bool dodging = m_animator != null && m_animator.GetBool(s_isDodgingHash);

            // 只在"进入闪避"的那一刻播一次，而不是整个闪避期间每帧都播
            if (dodging && !m_wasDodging)
            {
                AudioManager.Play(ESfx.Dodge);
            }

            m_wasDodging = dodging;
        }

        // ====================================================================
        // 脚步
        // ====================================================================

        /// <summary>
        /// 判定"真的在走路"的最低水平速度（米/秒）
        /// </summary>
        private const float k_minLocomotionSpeed = 0.15f;

        private void UpdateFootsteps()
        {
            // 注意这里同时要求"有移动输入"(IsMoving) 和"真的有位移"(IsLocomoting)。
            // 只看输入会误判：手柄摇杆漂移、或角色被墙/地形卡住时，输入不为 0 但人根本没动，
            // 就会表现为"站着不动却一直响脚步"——所以必须再确认实际速度
            if (!IsGrounded() || !IsMoving() || !IsLocomoting() || m_wasDodging)
            {
                // 停下时清掉计时，下次起步立刻有第一声脚步，而不是等半个间隔
                m_nextStepTime = 0f;
                // 并且立刻掐断还在响的那一声脚步：
                // 脚步样本长度不可控（本项目 Footstep_02/03 长达 21~22 秒），
                // 不掐的话人会站住了脚步还在响，听起来像附近有敌人
                AudioManager.StopFootsteps();
                return;
            }

            if (Time.time < m_nextStepTime)
            {
                return;
            }

            AudioManager.PlayFootstepAt(transform.position, m_footstepVolumeScale);

            // 越快间隔越短：在"慢走"与"全速跑"之间插值
            float speed = m_motor != null ? m_motor.Velocity.magnitude : 0f;
            float t = Mathf.InverseLerp(0.5f, m_runSpeed, speed);
            m_nextStepTime = Time.time + Mathf.Lerp(m_walkStepInterval, m_runStepInterval, t);
        }

        // ====================================================================
        // 状态查询
        // ====================================================================

        private bool IsGrounded()
        {
            return m_motor == null || m_motor.GroundingStatus.IsStableOnGround;
        }

        private bool IsMoving()
        {
            if (m_controller != null)
            {
                return m_controller.IsMoving;
            }

            // 没有 ExampleCharacterController 时退回按速度判断（例如换成别的控制器）
            return m_motor != null && m_motor.Velocity.magnitude > 0.3f;
        }

        /// <summary>
        /// 角色是否真的产生了水平位移。
        /// 只看输入（IsMoving）是不够的：摇杆漂移或角色被卡住时输入不为 0 但人没动，
        /// 会让人"站着不动却听到脚步"，所以这里以真实水平速度为准（忽略竖直方向的落地/重力速度）
        /// </summary>
        private bool IsLocomoting()
        {
            // 拿不到 motor 时不额外限制，退回只按输入判断（保持原行为）
            if (m_motor == null)
            {
                return true;
            }

            Vector3 velocity = m_motor.Velocity;
            velocity.y = 0f;
            return velocity.magnitude > k_minLocomotionSpeed;
        }
    }
}
