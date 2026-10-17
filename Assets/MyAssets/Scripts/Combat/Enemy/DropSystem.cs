using System.Collections.Generic;
using MyGame.Audio;
using MyGame.Interaction;
using MyGame.Inventory;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 掉落系统：把"敌人死了"变成"地上多了几件东西"
    ///
    /// 为什么要独立成一个系统，而不是写进 EnemyAI：
    /// - 敌人不需要知道"掉落物长什么样、掉在哪里"（那是表现层的事）
    /// - 掉落规则需要按种类复用（小怪 / 精英 / Boss 各一张表），
    ///   挂在 EnemyAI 上会让"同一张表配给多种敌人"变成逐个拖拽
    ///
    /// 工作方式：
    /// 1. 订阅 EnemyAI.AnyEnemyDied（静态全局事件）——不用逐个敌人订阅
    /// 2. 死亡时取该敌人的掉落表（没配则用本系统的默认表）
    /// 3. 摇奖 → 在尸体附近生成可拾取物（复用 PickupItem，走过去按 G 捡）
    ///
    /// 兜底原则：拾取物预制体没配时，直接把物品塞进背包并打警告。
    /// 宁可"掉落直接进包"也不让玩家打完怪什么都拿不到——
    /// 掉落凭空消失是最难排查的一类体验问题
    ///
    /// 挂载位置：场景中一个常驻物体（与其它管理器同层，例如 SaveSystem 所在物体）
    /// </summary>
    [DisallowMultipleComponent]
    public class DropSystem : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("拾取物")]
        [Tooltip("掉在地上的拾取物预制体（需挂 PickupItem 组件 + Collider）。" +
                 "留空则掉落直接进背包（不推荐，仅作兜底）")]
        [SerializeField] private GameObject m_pickupPrefab;

        [Tooltip("敌人没配掉落表时使用的兜底表（可留空＝这种敌人不掉东西）")]
        [SerializeField] private DropTableSO m_defaultDropTable;

        [Header("生成位置")]
        [Tooltip("相对尸体位置的抬高量（米），让掉落物浮在地面上而不是嵌进地板")]
        [SerializeField] private float m_spawnHeightOffset = 0.4f;

        [Tooltip("掉落物在尸体周围的散开半径（米）。多件物品不会叠在同一点")]
        [SerializeField] private float m_scatterRadius = 0.6f;

        [Header("调试")]
        [Tooltip("每次掉落都打日志，方便验证掉落表配置")]
        [SerializeField] private bool m_logDrops = true;

        // ====================================================================
        // 单例
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 DropSystem）</summary>
        public static DropSystem Instance { get; private set; }

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>
        /// 摇奖结果缓冲区：复用一个 List，避免每次击杀都新建列表产生 GC。
        /// 只在 HandleEnemyDied 内使用，单线程无重入风险
        /// </summary>
        private readonly List<DropResult> m_rollBuffer = new();

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 DropSystem 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            // 静态事件：场景里所有敌人死亡都会走到这里
            EnemyAI.AnyEnemyDied += HandleEnemyDied;
        }

        private void OnDisable()
        {
            // 必须成对解绑：场景卸载后静态事件仍持有本对象，
            // 回调时访问已销毁的物体就会抱"MissingReferenceException"
            EnemyAI.AnyEnemyDied -= HandleEnemyDied;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 击杀回调
        // ====================================================================

        /// <summary>敌人死亡：按掉落表摇奖并在尸体附近生成拾取物</summary>
        private void HandleEnemyDied(EnemyAI enemy)
        {
            if (enemy == null)
            {
                return;
            }

            // 优先用敌人自己的掉落表，没配则退回默认表
            DropTableSO table = enemy.DropTable != null ? enemy.DropTable : m_defaultDropTable;
            if (table == null)
            {
                return;
            }

            m_rollBuffer.Clear();
            table.Roll(m_rollBuffer);

            if (m_rollBuffer.Count == 0)
            {
                return;
            }

            // 尸体位置：敌人死亡后还会停留几秒才销毁，这里取当前坐标即可
            Vector3 origin = enemy.transform.position + Vector3.up * m_spawnHeightOffset;

            for (int i = 0; i < m_rollBuffer.Count; i++)
            {
                SpawnPickup(m_rollBuffer[i], origin, enemy.EnemyId);
            }
        }

        // ====================================================================
        // 生成拾取物
        // ====================================================================

        /// <summary>把一条掉落结果实例化成地上的拾取物</summary>
        private void SpawnPickup(DropResult drop, Vector3 origin, string enemyId)
        {
            string displayName = ResolveDisplayName(drop.ItemId);

            // 兜底：没有拾取物预制体，直接把物品塞进背包。
            // 这样即使美术/场景还没配好，掉落规则本身也能验证
            if (m_pickupPrefab == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 未配置拾取物预制体，掉落「{displayName}」×{drop.Amount} 直接放入背包", this);
                AddToInventoryDirectly(drop);
                return;
            }

            // 在水平面上随机散开，避免多件物品完全重叠
            Vector2 scatter = Random.insideUnitCircle * m_scatterRadius;
            Vector3 position = origin + new Vector3(scatter.x, 0f, scatter.y);

            GameObject instance = Instantiate(m_pickupPrefab, position, Quaternion.identity);

            PickupItem pickup = instance.GetComponent<PickupItem>();
            if (pickup == null)
            {
                // 预制体挂错组件：销毁避免地上留一个捡不了的模型，同时明确报错
                Debug.LogError($"[{GetType().Name}] 拾取物预制体「{m_pickupPrefab.name}」上没有 PickupItem 组件，无法拾取", this);
                Destroy(instance);
                return;
            }

            // respawn: false → 捡一次就销毁，不会像采集物那样再长出来
            pickup.Initialize(drop.ItemId, displayName, drop.Amount, respawn: false);

            // 落地音：让玩家"听见"有东西掉出来了，不用盯着尸体看。走 3D，跟随掉落位置
            AudioManager.PlayAt(ESfx.Drop_Land, position, 0.7f);

            if (m_logDrops)
            {
                Debug.Log($"[{GetType().Name}] 敌人 {enemyId} 掉落「{displayName}」×{drop.Amount}（物品 ID：{drop.ItemId}）", this);
            }
        }

        /// <summary>没有拾取物预制体时的兜底：直接进背包</summary>
        private void AddToInventoryDirectly(DropResult drop)
        {
            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 InventoryManager，掉落「{drop.ItemId}」丢失", this);
                return;
            }

            int added = inventory.AddItem(drop.ItemId, drop.Amount);

            // 上报"玩家获得了物品"，右下角提示面板据此弹出（AddItem 返回实际入包数量）
            ItemGainNotifier.Notify(drop.ItemId, added);
        }

        /// <summary>查物品显示名，查不到就用 ID 兜底（保证拾取提示不空白）</summary>
        private string ResolveDisplayName(string itemId)
        {
            InventoryManager inventory = InventoryManager.Instance;
            ItemDataSO data = inventory != null ? inventory.FindItem(itemId) : null;
            return data != null ? data.DisplayName : itemId;
        }
    }
}
