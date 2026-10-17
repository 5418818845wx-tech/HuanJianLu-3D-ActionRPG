using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using KinematicCharacterController;
using KinematicCharacterController.Examples;
using MyGame.UI;

namespace KinematicCharacterController.Examples
{
    /// <summary>
    /// 玩家输入驱动（原 KinematicCharacterController 示例脚本，已被本项目改造）
    ///
    /// 为什么放在 Game.HotUpdate 程序集而不是 KCC 自己的程序集：
    /// 它现在依赖 UGUIManager（UI 暂停/光标策略）与 KeyBindingManager（可自定义键位），
    /// 这两者都是业务热更代码。若留在 KCC 程序集就会形成
    /// 「KCC → 业务」与「业务 → KCC」的循环引用（Unity 不允许程序集循环依赖）。
    /// 因此把它归到业务热更程序集，KCC 程序集保持纯第三方、零业务依赖。
    /// 文件 GUID 已一并迁移，场景/预制体上的引用不受影响。
    /// </summary>
    public class ExamplePlayer : MonoBehaviour
    {
        public ExampleCharacterController Character;
        public ExampleCharacterCamera CharacterCamera;

        private const string MouseXInput = "Mouse X";
        private const string MouseYInput = "Mouse Y";
        private const string MouseScrollInput = "Mouse ScrollWheel";
        private const string HorizontalInput = "Horizontal";
        private const string VerticalInput = "Vertical";

        /// <summary>
        /// 移动输入死区：摇杆/按键的微小残留输入（手柄漂移等）当作 0。
        /// 不加死区的话，这些"看不见的输入"会让角色一直悄悄移动，
        /// 脚步声也就跟着一直响，玩家会误以为附近有敌人
        /// </summary>
        private const float k_moveInputDeadzone = 0.15f;

        private void Start()
        {
            // 设置相机跟随
            if (CharacterCamera != null && Character != null)
            {
                CharacterCamera.SetFollowTransform(Character.CameraFollowPoint);

                // Ignore the character's collider(s) for camera obstruction checks
                CharacterCamera.IgnoredColliders.Clear();
                CharacterCamera.IgnoredColliders.AddRange(Character.GetComponentsInChildren<Collider>());
            }

            // 单机模式:正常锁定鼠标
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void Update()
        {
            // 点击左键重新锁定光标（视角控制需要）；
            // 但 UI 正在用鼠标时不能抢锁：否则点快捷栏面板入口的那一下
            // 会把光标锁回屏幕中心，按钮就再也点不中了（表现为"点一次鼠标光标就没了"）
            if (Input.GetMouseButtonDown(0)
                && !UGUIManager.WantsCursor
                && !UGUIManager.IsPointerOverUI)
            {
                Cursor.lockState = CursorLockMode.Locked;
            }

            HandleCharacterInput();
        }

        private void LateUpdate()
        {
            // Handle rotating the camera along with physics movers
            if (CharacterCamera.RotateWithPhysicsMover && Character.Motor.AttachedRigidbody != null)
            {
                CharacterCamera.PlanarDirection = Character.Motor.AttachedRigidbody.GetComponent<PhysicsMover>().RotationDeltaFromInterpolation * CharacterCamera.PlanarDirection;
                CharacterCamera.PlanarDirection = Vector3.ProjectOnPlane(CharacterCamera.PlanarDirection, Character.Motor.CharacterUp).normalized;
            }

            HandleCameraInput();
        }

        private void HandleCameraInput()
        {
            // Create the look input vector for the camera
            float mouseLookAxisUp = Input.GetAxisRaw(MouseYInput);
            float mouseLookAxisRight = Input.GetAxisRaw(MouseXInput);
            Vector3 lookInputVector = new Vector3(mouseLookAxisRight, mouseLookAxisUp, 0f);

            // Prevent moving the camera while the cursor isn't locked
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                lookInputVector = Vector3.zero;
            }

            // Input for zooming the camera (disabled in WebGL because it can cause problems)
            float scrollInput = -Input.GetAxis(MouseScrollInput);
#if UNITY_WEBGL
        scrollInput = 0f;
#endif

            // Apply inputs to the camera
            CharacterCamera.UpdateWithInput(Time.deltaTime, scrollInput, lookInputVector);

            // Handle toggling zoom level
            if (Input.GetMouseButtonDown(1))
            {
                CharacterCamera.TargetDistance = (CharacterCamera.TargetDistance == 0f) ? CharacterCamera.DefaultDistance : 0f;
            }
        }

        private void HandleCharacterInput()
        {
            PlayerCharacterInputs characterInputs = new PlayerCharacterInputs();

            // ===== UI 弹窗暂停期间：移动/跳跃/闪避输入全部清零 =====
            // 物理虽然被 timeScale=0 冻结，但按键的"按下瞬间"仍会被记录，
            // 若不清零，关闭面板后的第一个物理帧会消费到暂停期间按的空格/Shift，角色突然跳一下
            //
            // WantsCursor：玩家按住快捷栏的呼出键时也一并屏蔽移动——
            // 那会儿玩家在用鼠标点按钮，人物不该跟着走
            if (UGUIManager.IsGamePaused || UGUIManager.WantsCursor)
            {
                characterInputs.MoveAxisForward = 0f;
                characterInputs.MoveAxisRight = 0f;
                characterInputs.CameraRotation = CharacterCamera.Transform.rotation;
                characterInputs.JumpDown = false;
                characterInputs.DodgeDown = false;
                characterInputs.CrouchDown = false;
                characterInputs.CrouchUp = false;
                Character.SetInputs(ref characterInputs);
                return;
            }

            // Build the CharacterInputs struct
            characterInputs.MoveAxisForward = Input.GetAxisRaw(VerticalInput);
            characterInputs.MoveAxisRight = Input.GetAxisRaw(HorizontalInput);

            // 死区过滤：把摇杆漂移这类微小输入当成 0
            Vector2 rawMove = new Vector2(characterInputs.MoveAxisRight, characterInputs.MoveAxisForward);
            if (rawMove.magnitude < k_moveInputDeadzone)
            {
                characterInputs.MoveAxisForward = 0f;
                characterInputs.MoveAxisRight = 0f;
            }

            characterInputs.CameraRotation = CharacterCamera.Transform.rotation;

            // 跳跃 / 闪避 / 下蹲：键位走 KeyBindingManager，玩家可在设置面板自定义
            // （默认仍是 空格 / 左Shift / C，行为与改造前一致）
            KeyCode jumpKey = MyGame.KeyBindings.KeyBindingManager.Get(MyGame.KeyBindings.EGameKey.Jump);
            KeyCode dodgeKey = MyGame.KeyBindings.KeyBindingManager.Get(MyGame.KeyBindings.EGameKey.Dodge);
            KeyCode crouchKey = MyGame.KeyBindings.KeyBindingManager.Get(MyGame.KeyBindings.EGameKey.Crouch);

            characterInputs.JumpDown = Input.GetKeyDown(jumpKey);
            characterInputs.DodgeDown = Input.GetKeyDown(dodgeKey);
            characterInputs.CrouchDown = Input.GetKeyDown(crouchKey);
            characterInputs.CrouchUp = Input.GetKeyUp(crouchKey);

            // Apply inputs to character
            Character.SetInputs(ref characterInputs);
        }
    }
}
