using MyGame.Combat;
using UnityEngine;

namespace MyGame.Inventory
{
    /// <summary>
    /// 物品使用系统：唯一负责"把一件物品真正用出去"的规则
    ///
    /// 为什么规则不写在 UI 里：
    /// 面板只回答"玩家点了哪个格子"，而"满血时该不该消耗药水""先回血还是先扣数量"
    /// 属于游戏规则。规则留在系统层后，换成快捷栏、手柄、脚本调用（&lt;&lt;use_item "Item_Potion"&gt;&gt;）
    /// 都能复用同一套判断，也不会出现"三个入口三套规则"的经典 bug
    ///
    /// 与 InventoryManager 的分工：
    /// - InventoryManager 只管"有什么、有几个"
    /// - 本系统管"能不能用、用了之后发生什么"，扣数量也只是调用它的 RemoveItem
    ///
    /// 挂载位置：与 InventoryManager 同一个管理器物体（不要新加物体）
    /// </summary>
    [DisallowMultipleComponent]
    public class ItemUseSystem : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("玩家")]
        [Tooltip("玩家的 Tag（用它与玩家身上的 CombatUnit 对接）")]
        [SerializeField] private string m_playerTag = "Player";

        // ====================================================================
        // 单例
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 ItemUseSystem）</summary>
        public static ItemUseSystem Instance { get; private set; }

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 ItemUseSystem 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 对外方法
        // ====================================================================

        /// <summary>
        /// 尝试使用一件物品。
        /// 失败时**不扣数量**并通过 failReason 说明原因（交给调用方决定怎么提示玩家）
        /// </summary>
        /// <param name="itemId">物品 ID</param>
        /// <param name="failReason">失败原因（成功时为空字符串）</param>
        /// <returns>是否真的用出去了</returns>
        public bool TryUse(string itemId, out string failReason)
        {
            failReason = string.Empty;

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory == null)
            {
                failReason = "场景中没有背包管理器";
                return false;
            }

            if (inventory.GetCount(itemId) <= 0)
            {
                failReason = "背包里没有这件物品";
                return false;
            }

            ItemDataSO data = inventory.FindItem(itemId);
            if (data == null)
            {
                failReason = $"物品目录里找不到 {itemId}";
                return false;
            }

            if (!data.CanUse)
            {
                failReason = $"{data.DisplayName}不能直接使用";
                return false;
            }

            CombatUnit target = FindPlayerUnit();
            if (target == null)
            {
                failReason = $"场景中找不到玩家的战斗单位（Tag = {m_playerTag}）";
                return false;
            }

            if (!IsEffectUseful(data, target))
            {
                failReason = $"生命值已满，{data.DisplayName}现在用不上";
                return false;
            }

            ApplyEffect(data, target);

            // 效果生效之后才扣数量：反过来的话，一旦效果应用失败就白白吞掉玩家的药
            inventory.RemoveItem(itemId, 1);

            Debug.Log($"[{GetType().Name}] 使用 {data.DisplayName}，生命值 " +
                      $"{target.CurrentHP}/{target.MaxHP}", target);

            return true;
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>找玩家身上的战斗单位（使用是玩家主动点击，不是每帧调用，不必缓存）</summary>
        private CombatUnit FindPlayerUnit()
        {
            if (string.IsNullOrEmpty(m_playerTag))
            {
                return null;
            }

            GameObject player = GameObject.FindGameObjectWithTag(m_playerTag);
            return player != null ? player.GetComponent<CombatUnit>() : null;
        }

        /// <summary>
        /// 效果此刻是否"用得上"。
        /// 集中在这里判断的好处：以后加自动喝药、快捷栏喝药，都自动继承这条保护，
        /// 不会出现某个入口满血也照扣药水的情况
        /// </summary>
        private static bool IsEffectUseful(ItemDataSO data, CombatUnit target)
        {
            if (!target.IsAlive)
            {
                return false;
            }

            switch (data.UseType)
            {
                case EItemUseType.Heal:
                    return target.CurrentHP < target.MaxHP;

                default:
                    return true;
            }
        }

        /// <summary>把效果作用到目标身上（新增效果类型时同步扩这里）</summary>
        private static void ApplyEffect(ItemDataSO data, CombatUnit target)
        {
            switch (data.UseType)
            {
                case EItemUseType.Heal:
                    target.Heal(data.UseAmount);
                    break;
            }
        }
    }
}
