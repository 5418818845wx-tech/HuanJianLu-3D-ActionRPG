using MyGame.Combat;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 敌人头顶血条（UGUI 版）：把本敌人的血量以小血条形式显示在头顶
    ///
    /// 与 UI Toolkit 版的关键差异（同样是"世界坐标 → UI"，但手段不同）：
    /// - UI Toolkit 用 RuntimePanelUtils.CameraTransformWorldToPanel 换算面板坐标，再写 style.left/top
    /// - UGUI 在 Screen Space Overlay 画布下，RectTransform.position 本身就是屏幕像素坐标，
    ///   所以只需 WorldToScreenPoint 后直接赋给 position，无需再做坐标转换
    /// - 填充量用 Image.fillAmount（需把 Image 的 Type 设为 Filled / Horizontal）
    ///
    /// 工作原理：
    /// 1. 向 UGUICombatHUDController 申请一个血条实例（克隆血条 prefab 到敌人血条层级）
    /// 2. 订阅 CombatUnit 的伤害/死亡事件，刷新 fillAmount
    /// 3. LateUpdate 中把敌人头顶世界坐标换算成屏幕坐标，驱动血条跟随
    /// 4. 满血时隐藏（受击后才显示），在相机背后或屏幕外时也隐藏
    ///
    /// 挂载要求：与 CombatUnit 同一物体（RequireComponent 保证）
    /// </summary>
    [RequireComponent(typeof(CombatUnit))]
    public class UGUIEnemyHealthBar : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("显示设置")]
        [Tooltip("血条锚点相对物体脚底的垂直偏移（米），需高过头顶模型")]
        [SerializeField] private float m_heightOffset = 2.4f;

        [Tooltip("满血时隐藏血条，受到伤害后才显示")]
        [SerializeField] private bool m_hideWhenFullHP = true;

        /// <summary>屏幕外剔除的像素余量：血条稍微超出屏幕一点仍显示，避免边缘闪烁</summary>
        private const float k_offscreenMarginPixels = 64f;

        // ====================================================================
        // 运行时引用
        // ====================================================================

        /// <summary>本敌人身上的战斗单位</summary>
        private CombatUnit m_unit;

        /// <summary>HUD 控制器（提供血条 prefab 与相机）</summary>
        private UGUICombatHUDController m_hud;

        /// <summary>渲染相机</summary>
        private Camera m_camera;

        /// <summary>血条根节点（负责定位与显隐）</summary>
        private RectTransform m_barRect;

        /// <summary>血条填充图片（fillAmount = 血量比例）</summary>
        private Image m_fillImage;

        /// <summary>当前血量比例（0~1）</summary>
        private float m_hpRatio = 1f;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            // 缓存同物体上的战斗单位
            m_unit = GetComponent<CombatUnit>();
        }

        private void OnEnable()
        {
            // 订阅伤害与死亡事件，驱动血条刷新
            m_unit.DamageTaken += HandleDamageTaken;
            m_unit.Died += HandleDied;
        }

        private void OnDisable()
        {
            // 取消订阅并销毁自己的血条实例
            m_unit.DamageTaken -= HandleDamageTaken;
            m_unit.Died -= HandleDied;
            DetachBar();
        }

        private void Start()
        {
            // 首次尝试解析依赖（HUD 控制器 / 相机 / 血条实例）
            TryResolveDependencies();
        }

        private void LateUpdate()
        {
            // 依赖未就绪（HUD / 相机晚于敌人激活）时逐帧补找
            if (m_barRect == null || m_camera == null)
            {
                TryResolveDependencies();

                if (m_barRect == null || m_camera == null)
                {
                    return;
                }
            }

            // 满血且开启了"满血隐藏"：不显示
            if (m_hideWhenFullHP && m_hpRatio >= 1f)
            {
                HideBar();
                return;
            }

            // 头顶锚点的世界坐标（脚底 + 垂直偏移）
            Vector3 headPosition = transform.position + Vector3.up * m_heightOffset;

            // 一次投影同时完成"屏幕外剔除"与"定位"：
            // WorldToScreenPoint 返回的 z 是相机空间深度（<=0 即在相机背后），
            // x/y 就是屏幕像素坐标，超出屏幕外带余量就直接隐藏。
            // 原来先 WorldToViewportPoint 再 WorldToScreenPoint 会做两遍矩阵变换，这里省掉一遍
            Vector3 screenPoint = m_camera.WorldToScreenPoint(headPosition);

            bool isOffScreen = screenPoint.z <= 0.05f ||
                               screenPoint.x < -k_offscreenMarginPixels ||
                               screenPoint.x > Screen.width + k_offscreenMarginPixels ||
                               screenPoint.y < -k_offscreenMarginPixels ||
                               screenPoint.y > Screen.height + k_offscreenMarginPixels;
            if (isOffScreen)
            {
                HideBar();
                return;
            }

            // Screen Space Overlay 画布下，RectTransform.position 就是屏幕像素坐标，直接赋值即可
            m_barRect.position = screenPoint;

            ShowBar();
        }

        // ====================================================================
        // 依赖解析与显隐
        // ====================================================================

        /// <summary>补齐 HUD 控制器 / 相机 / 血条实例（可重复调用，逐项补齐）</summary>
        private void TryResolveDependencies()
        {
            if (m_hud == null)
            {
                m_hud = FindFirstObjectByType<UGUICombatHUDController>();
            }

            if (m_hud == null)
            {
                return;
            }

            if (m_camera == null)
            {
                m_camera = m_hud.TargetCamera;
            }

            if (m_barRect == null)
            {
                m_barRect = m_hud.CreateEnemyBar(out m_fillImage);

                // 创建后按当前血量初始化一次填充量
                UpdateRatio();
                UpdateFill();
            }
        }

        /// <summary>显示血条</summary>
        private void ShowBar()
        {
            if (m_barRect != null && !m_barRect.gameObject.activeSelf)
            {
                m_barRect.gameObject.SetActive(true);
            }
        }

        /// <summary>隐藏血条</summary>
        private void HideBar()
        {
            if (m_barRect != null && m_barRect.gameObject.activeSelf)
            {
                m_barRect.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 解绑自己的血条实例（敌人隐藏/销毁时调用，避免残留在 HUD 层级里）。
        /// 注意：这里把血条**还给对象池复用**，而不是销毁——下一只敌人可以接着用同一条血条
        /// </summary>
        private void DetachBar()
        {
            if (m_barRect == null)
            {
                return;
            }

            if (m_hud != null)
            {
                m_hud.ReleaseEnemyBar(m_barRect);
            }
            else
            {
                // HUD 已不存在（异常情况）：兜底销毁
                Destroy(m_barRect.gameObject);
            }

            m_barRect = null;
            m_fillImage = null;
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>受伤事件回调：按剩余血量刷新填充量</summary>
        private void HandleDamageTaken(float damage)
        {
            UpdateRatio();
            UpdateFill();
        }

        /// <summary>死亡事件回调：血量清零（血条随敌人物体一起销毁）</summary>
        private void HandleDied(CombatUnit diedUnit)
        {
            m_hpRatio = 0f;
            UpdateFill();
        }

        // ====================================================================
        // 工具方法
        // ====================================================================

        /// <summary>根据当前血量重算比例</summary>
        private void UpdateRatio()
        {
            m_hpRatio = m_unit.MaxHP > 0f ? m_unit.CurrentHP / m_unit.MaxHP : 0f;
        }

        /// <summary>把比例写入图片填充量</summary>
        private void UpdateFill()
        {
            if (m_fillImage != null)
            {
                m_fillImage.fillAmount = m_hpRatio;
            }
        }
    }
}
