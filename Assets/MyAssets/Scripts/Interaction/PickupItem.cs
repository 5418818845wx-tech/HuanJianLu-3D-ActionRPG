using MyGame.Audio;
using MyGame.Inventory;
using UnityEngine;

namespace MyGame.Interaction
{
    /// <summary>
    /// 可采集物：按交互键采集 → 放进背包 → 采完暂时消失，可选过一段时间重新长出来
    ///
    /// 职责边界（很重要）：
    /// - 本类只做"把物品放进背包"和"自己的显隐"两件事
    /// - 任务里的「收集」目标由**背包**统一上报（见 InventoryManager.ReportToQuests）：
    ///   物品可能来自采集、购买、任务奖励，任务系统不该关心来源，
    ///   否则每个来源各写一遍上报逻辑，漏一个就静默失效
    ///
    /// 所以新增一类采集物、或者改成从商店买，任务侧一行都不用改
    ///
    /// 为什么"采完"不是 SetActive(false)：
    /// 物体一旦失活，它自己身上的协程会被**暂停**（不是停止），重生计时永远不会走完。
    /// 所以这里只关掉渲染与碰撞，物体本身保持激活，计时才能继续
    ///
    /// 挂载位置：采集物本体（需要 Collider 供交互检测）
    /// </summary>
    [DisallowMultipleComponent]
    public class PickupItem : MonoBehaviour, IInteractable
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("物品标识")]
        [Tooltip("物品 ID：与 ItemDataSO.Id 一致，命名规范 Item_ + 名字（如 Item_Herb）")]
        [SerializeField] private string m_itemId = "Item_Herb";

        [Tooltip("显示名：只用于交互提示文字，外观由采集物自身的模型决定")]
        [SerializeField] private string m_displayName = "止血草";

        [Tooltip("采集一次提供几个")]
        [SerializeField, Min(1)] private int m_amount = 1;

        [Header("重生")]
        [Tooltip("采集后过多少秒重新长出来。填 0 表示采完就永久消失")]
        [SerializeField, Min(0f)] private float m_respawnSeconds = 30f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>本体的碰撞体（采集期间关掉，交互检测就找不到它了）</summary>
        private Collider m_collider;

        /// <summary>自身与所有子物体的渲染器（采集期间统一隐藏）</summary>
        private Renderer[] m_renderers;

        /// <summary>是否已被采走（等待重生中）</summary>
        private bool m_isHarvested;

        /// <summary>重生计时协程句柄（物体禁用时统一停掉）</summary>
        private Coroutine m_respawnRoutine;

        /// <summary>
        /// 是否是"捡一次就永久消失"的一次性掉落物。
        /// 由 Initialize 按 respawn 参数设置：地上的战斗掉落物为 true，可再生的采集物为 false
        /// </summary>
        private bool m_destroyWhenHarvested;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>物品 ID（内容校验器用它检查任务里的收集目标是否有对应采集物）</summary>
        public string ItemId => m_itemId;

        // ====================================================================
        // 运行时配置（战斗掉落物专用）
        // ====================================================================

        /// <summary>
        /// 运行时配置本拾取物，供掉落系统实例化预制体后立刻调用。
        ///
        /// 为什么需要它：掉落什么物品、掉几个是运行时摇出来的，
        /// 预制体上不可能预先填好。同时地上的战斗掉落物是"捡一次就消失"的，
        /// 与采集物"过一段时间重新长出来"是两种语义，所以顺带关掉重生
        /// </summary>
        /// <param name="itemId">物品 ID（与 ItemDataSO.Id 一致）</param>
        /// <param name="displayName">交互提示里显示的名字（留空则退化为物品 ID）</param>
        /// <param name="amount">一次拾取的数量</param>
        /// <param name="respawn">是否重生（战斗掉落物传 false）</param>
        public void Initialize(string itemId, string displayName, int amount, bool respawn)
        {
            m_itemId = itemId;
            m_displayName = string.IsNullOrEmpty(displayName) ? itemId : displayName;
            m_amount = Mathf.Max(1, amount);
            m_respawnSeconds = respawn ? m_respawnSeconds : 0f;

            // 不重生 = 一次性掉落物：捡完销毁，不留空壳
            m_destroyWhenHarvested = !respawn;
        }

        // ====================================================================
        // IInteractable 实现
        // ====================================================================

        /// <summary>交互提示文本（交互提示条会显示为「[G] 采集 止血草」）</summary>
        public string InteractionPrompt => $"采集 {m_displayName}";

        /// <summary>采走之后到重生之前都不再可交互</summary>
        public bool CanInteract => isActiveAndEnabled && !m_isHarvested;

        /// <summary>采集：放进背包 → 自己进入"被采走"状态，到点再长出来</summary>
        public void Interact(GameObject interactor)
        {
            if (m_isHarvested)
            {
                return;
            }

            InventoryManager inventory = InventoryManager.Instance;

            if (inventory == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 InventoryManager，采集的物品会丢失", this);
            }
            else
            {
                int added = inventory.AddItem(m_itemId, m_amount);
                Debug.Log($"[{GetType().Name}] 采集「{m_displayName}」×{added}（物品 ID：{m_itemId}）", this);

                // 上报"玩家获得了物品"：右下角获得提示
                MyGame.Inventory.ItemGainNotifier.Notify(m_itemId, added);
            }

            SetHarvested(true);

            // 采集物拔草声 / 地上掉落物的拾取声：两者是不同的东西，用不同音效
            AudioManager.Play(m_destroyWhenHarvested ? ESfx.Pickup_Item : ESfx.Harvest);

            // 一次性掉落物：捡完直接销毁，不在地上留一个捡不了的空壳
            if (m_destroyWhenHarvested)
            {
                Destroy(gameObject);
            }
        }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            // 缓存一次即可：采集只改 enabled，不动层级结构
            m_collider = GetComponent<Collider>();

            // 包含未激活的子物体，避免模型挂在临时隐藏的子节点上时漏掉
            m_renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void OnDisable()
        {
            // 物体被整体禁用时协程会暂停而不是结束，显式停掉免得复活后残留
            StopRespawnRoutine();
        }

        // ====================================================================
        // 显隐与重生
        // ====================================================================

        /// <summary>切换"已采走 / 已长好"两种状态</summary>
        private void SetHarvested(bool harvested)
        {
            m_isHarvested = harvested;

            // 关碰撞体：OverlapSphere 找不到它，交互提示自然消失
            if (m_collider != null)
            {
                m_collider.enabled = !harvested;
            }

            for (int i = 0; i < m_renderers.Length; i++)
            {
                if (m_renderers[i] != null)
                {
                    m_renderers[i].enabled = !harvested;
                }
            }

            if (harvested && m_respawnSeconds > 0f)
            {
                StopRespawnRoutine();
                m_respawnRoutine = StartCoroutine(RespawnCo());
            }
        }

        /// <summary>等待重生时间后把自己重新长出来</summary>
        private System.Collections.IEnumerator RespawnCo()
        {
            yield return new WaitForSeconds(m_respawnSeconds);

            // 等待期间物体可能被销毁或禁用，醒来后必须重新确认
            if (this == null || !isActiveAndEnabled)
            {
                yield break;
            }

            m_respawnRoutine = null;
            SetHarvested(false);

            Debug.Log($"[{GetType().Name}]「{m_displayName}」已重新长出", this);
        }

        /// <summary>停掉重生计时（可安全重复调用）</summary>
        private void StopRespawnRoutine()
        {
            if (m_respawnRoutine != null)
            {
                StopCoroutine(m_respawnRoutine);
                m_respawnRoutine = null;
            }
        }
    }
}
