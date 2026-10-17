using MyGame.Interaction;
using MyGame.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyGame.Dungeon
{
    /// <summary>
    /// 秘境入口实体：大世界里那个"走过去按键就能进副本"的传送点
    ///
    /// 职责边界（与 PickupItem 的分工思路一致）：
    /// - 本类只回答"提示什么、能不能交互、交互时打开哪个面板"
    /// - 谁被检测、按哪个键由 InteractionDetector 决定
    /// - 真正"进入秘境"的动作发生在**面板**里（玩家要先看清秘境信息再确认），
    ///   所以这里只负责把面板叫出来，并把"玩家点的是哪个秘境"透传过去
    ///
    /// 怎么把"点的是哪个入口"告诉面板：
    /// 用静态字段 PendingDungeon 当一次性的桥。面板打开时读走并置 null，
    /// 取完即清，避免下次从别处（如快捷键）打开面板时还挂着上一次的入口
    ///
    /// 挂载位置：入口本体（需要 Collider 供交互检测）
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonPortal : MonoBehaviour, IInteractable
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("秘境配置")]
        [Tooltip("本入口指向的秘境。必填，为空时该入口不可交互")]
        [SerializeField] private DungeonDefinitionSO m_dungeon;

        [Tooltip("交互检测半径提示（米）。仅用于 Gizmos 可视化与说明，填 0 表示由检测器统一决定")]
        [SerializeField, Min(0f)] private float m_promptRadius = 0f;

        [Tooltip("入口的可见模型根节点（可空）。交互时隐藏/禁用，防止加载场景前被重复触发")]
        [SerializeField] private GameObject m_visualRoot;

        [Tooltip("是否直接进入秘境。勾选=按一下交互键就切场景（推荐）；" +
                 "取消勾选=先弹出秘境面板，玩家点「进入」才切场景")]
        [SerializeField] private bool m_enterDirectly = true;

        // ====================================================================
        // 静态桥：告诉面板"这次是从哪个入口进来的"
        // ====================================================================

        /// <summary>
        /// 待展示的秘境：入口点击时写入，面板打开时读走并清空。
        /// null 表示从其它途径（如快捷键）打开面板，面板不需要高亮任何一条
        /// </summary>
        public static DungeonDefinitionSO PendingDungeon { get; set; }

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>是否已触发进入（加载场景前挡住重复点击）</summary>
        private bool m_isEntering;

        // ====================================================================
        // IInteractable 实现
        // ====================================================================

        /// <summary>交互提示文本：带上秘境名更直观（如「进入秘境「松林试炼场」」）</summary>
        public string InteractionPrompt =>
            m_dungeon != null && !string.IsNullOrEmpty(m_dungeon.DisplayName)
                ? $"进入秘境「{m_dungeon.DisplayName}」"
                : "进入秘境";

        /// <summary>没配秘境、正在进入、或自身未启用时都不可交互</summary>
        public bool CanInteract => isActiveAndEnabled && !m_isEntering && m_dungeon != null;

        /// <summary>
        /// 交互：按一下交互键。
        ///
        /// 两种模式（由 m_enterDirectly 决定）：
        /// - 直入模式（默认）：立刻记下回程场景并切进秘境场景，中途只隔一层加载界面。
        ///   入口实体本身已经写清"进入秘境「某某」"，再让玩家点一次「进入」属于多余的确认
        /// - 面板模式：只打开秘境面板，玩家在面板里点「进入」才切场景
        ///   （适合以后一个入口挂多个秘境、需要玩家挑的时候）
        ///
        /// 不管哪种模式，面板都仍然能从快捷栏的「秘境」打开查看信息，功能不丢
        /// </summary>
        public void Interact(GameObject interactor)
        {
            if (m_dungeon == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 未配置秘境 m_dungeon，无法进入", this);
                return;
            }

            if (m_isEntering)
            {
                return;
            }

            // 场景名没配的话直入会失败，退化回面板模式（面板里有提示，玩家看得见原因）
            if (m_enterDirectly && !string.IsNullOrEmpty(m_dungeon.SceneName))
            {
                EnterDirectly();
                return;
            }

            OpenPanel();
        }

        /// <summary>直接进入秘境：记下回程场景 → 开始一次秘境 → 异步加载秘境场景</summary>
        private void EnterDirectly()
        {
            // 挡住重复交互：交互到"场景真正切过去"之间还有几帧加载时间，
            // 期间玩家再按一次交互键不该触发第二次加载
            m_isEntering = true;
            HideVisual();

            // 必须在切场景**之前**记下当前场景名，切过去之后就取不到"上一层"了
            string returnScene = SceneManager.GetActiveScene().name;
            DungeonRunContext.BeginRun(m_dungeon, returnScene);

            Debug.Log($"[{GetType().Name}] 直接进入秘境「{m_dungeon.DisplayName}」，返回场景 {returnScene}，目标场景 {m_dungeon.SceneName}", this);

            SceneLoader.Load(m_dungeon.SceneName, m_dungeon.DisplayName);
        }

        /// <summary>打开秘境面板：把"玩家点的是哪个入口"透传给面板，由面板决定进不进</summary>
        private void OpenPanel()
        {
            UGUIManager uiManager = UGUIManager.Instance;
            if (uiManager == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 UGUIManager，无法打开秘境面板", this);
                return;
            }

            m_isEntering = true;
            HideVisual();

            // 先记下要展示哪个秘境，再打开面板——面板会在 OnEnter 里读走并清空
            PendingDungeon = m_dungeon;
            uiManager.ShowPanel(EUIPanelID.Dungeon);
        }

        /// <summary>隐藏入口外观（可空），避免加载期间还能看见它、防止被重复触发</summary>
        private void HideVisual()
        {
            if (m_visualRoot != null)
            {
                m_visualRoot.SetActive(false);
            }
        }

        // ====================================================================
        // 场景可视化
        // ====================================================================

        /// <summary>
        /// 选中时画一个黄色线框球，方便在场景里找到入口位置。
        /// 若额外填了 promptRadius，再画一个更大的圈表示交互半径提示
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, 1.5f);

            if (m_promptRadius > 0f)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.35f);
                Gizmos.DrawWireSphere(transform.position, m_promptRadius);
            }
        }
    }
}
