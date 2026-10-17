using System;
using System.Collections.Generic;
using MyGame.Quest;
using UnityEngine;

namespace MyGame.Inventory
{
    /// <summary>
    /// 背包管理器：全局单例，回答"玩家持有什么、有几个"
    ///
    /// 三条红线（与 QuestManager 完全一致）：
    /// 1. **只存动态状态**：物品的配置在 ItemDataSO 里，这里只存 ID → 数量
    /// 2. **唯一入口**：所有增减都走 AddItem / RemoveItem，外部不能直接改内部字典
    /// 3. **自己负责自己的存档**：CaptureSaveData / ApplySaveData 供 SaveManager 调用，
    ///    SaveManager 不认识背包的内部结构
    ///
    /// 为什么由背包上报「收集」类任务进度：
    /// 物品可能来自采集、购买、任务奖励等很多途径，任务系统不该关心来源。
    /// 背包是"玩家手上真的多了这个东西"的唯一真相来源，由它上报最准确，
    /// 也避免每个来源各写一遍上报逻辑（漏一个就静默失效）
    ///
    /// 挂载位置：场景中一个独立的管理器物体（与其他管理器同层）
    /// </summary>
    [DisallowMultipleComponent]
    public class InventoryManager : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("物品目录")]
        [Tooltip("本作全部物品资产的清单。作用有两个：1) 读档时按存档里的 ID 把配置找回来；" +
                 "2) 给 UI 提供稳定的显示顺序。新增物品记得加到这里")]
        [SerializeField] private List<ItemDataSO> m_itemCatalog = new();

        [Tooltip("物品增加时是否自动上报给任务系统（用于推进「收集」类目标）")]
        [SerializeField] private bool m_reportToQuests = true;

        [Header("开局赠送")]
        [Tooltip("进入游戏时白送的物品（金币、初始药水等）。读档会用存档内容整体覆盖，属正常")]
        [SerializeField] private List<InventoryStartingItem> m_startingItems = new();

        // ====================================================================
        // 单例与对外事件
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 InventoryManager）</summary>
        public static InventoryManager Instance { get; private set; }

        /// <summary>背包内容变化（UI 订阅它刷新）</summary>
        public event Action ItemsChanged;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>物品 ID → 持有数量（不变量：只保存大于 0 的项）</summary>
        private readonly Dictionary<string, int> m_counts = new Dictionary<string, int>();

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>物品总数（不同种类数）</summary>
        public int DistinctItemCount => m_counts.Count;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 InventoryManager 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void Start()
        {
            // 放在 Start 而不是 Awake：要等所有管理器的 Awake 跑完，
            // 这样发放时任务系统已经就绪（AddItem 会上报「收集」进度）
            GrantStartingItems();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>持有多少个（没有则 0）</summary>
        public int GetCount(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return 0;
            }

            return m_counts.TryGetValue(itemId, out int count) ? count : 0;
        }

        /// <summary>是否持有至少 amount 个</summary>
        public bool Contains(string itemId, int amount = 1)
        {
            return amount <= 0 || GetCount(itemId) >= amount;
        }

        /// <summary>按 ID 找配置（目录里没有则返回 null）</summary>
        public ItemDataSO FindItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            for (int i = 0; i < m_itemCatalog.Count; i++)
            {
                ItemDataSO data = m_itemCatalog[i];
                if (data != null && data.Id == itemId)
                {
                    return data;
                }
            }

            return null;
        }

        /// <summary>
        /// 把持有中的物品导出给 UI：两个列表一一对应。
        /// 顺序按"物品目录"排，保证界面顺序稳定（要调整显示顺序就调整目录顺序）。
        /// 由调用方传入列表，避免每次刷新都新建集合
        /// </summary>
        public void CopyItemsTo(List<ItemDataSO> items, List<int> counts)
        {
            items.Clear();
            counts.Clear();

            for (int i = 0; i < m_itemCatalog.Count; i++)
            {
                ItemDataSO data = m_itemCatalog[i];
                if (data == null)
                {
                    continue;
                }

                int count = GetCount(data.Id);
                if (count <= 0)
                {
                    continue;
                }

                items.Add(data);
                counts.Add(count);
            }
        }

        // ====================================================================
        // 增减
        // ====================================================================

        /// <summary>
        /// 放入物品，返回**实际放入的数量**（可能因堆叠上限而少于请求量）。
        /// 放入成功会推进对应的「收集」类任务目标
        /// </summary>
        public int AddItem(string itemId, int amount = 1)
        {
            if (string.IsNullOrEmpty(itemId) || amount <= 0)
            {
                return 0;
            }

            int current = GetCount(itemId);
            int capacity = GetCapacity(itemId);
            int added = Mathf.Min(amount, capacity - current);

            if (added <= 0)
            {
                Debug.LogWarning($"[{GetType().Name}] 背包已满，无法放入 {itemId}（上限 {capacity}）", this);
                return 0;
            }

            m_counts[itemId] = current + added;

            ReportToQuests(itemId, added);
            ItemsChanged?.Invoke();

            return added;
        }

        /// <summary>拿走物品，返回**实际拿走的数量**（不足时能拿多少拿多少）</summary>
        public int RemoveItem(string itemId, int amount = 1)
        {
            if (string.IsNullOrEmpty(itemId) || amount <= 0)
            {
                return 0;
            }

            int current = GetCount(itemId);
            int removed = Mathf.Min(amount, current);

            if (removed <= 0)
            {
                return 0;
            }

            if (removed == current)
            {
                // 数量归零就删键，保证"只保存大于 0 的项"这个不变量
                m_counts.Remove(itemId);
            }
            else
            {
                m_counts[itemId] = current - removed;
            }

            ItemsChanged?.Invoke();

            return removed;
        }

        // ====================================================================
        // 存档
        // ====================================================================

        /// <summary>导出背包内容（供 SaveManager 写盘；只导出 ID 与数量）</summary>
        public List<InventorySaveEntry> CaptureSaveData()
        {
            List<InventorySaveEntry> entries = new List<InventorySaveEntry>(m_counts.Count);

            foreach (KeyValuePair<string, int> pair in m_counts)
            {
                entries.Add(new InventorySaveEntry
                {
                    itemId = pair.Key,
                    count = pair.Value,
                });
            }

            return entries;
        }

        /// <summary>用存档数据覆盖背包内容（覆盖而非合并：读档语义就是"回到存档那一刻"）</summary>
        public void ApplySaveData(List<InventorySaveEntry> entries)
        {
            m_counts.Clear();

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    InventorySaveEntry entry = entries[i];

                    if (entry == null || string.IsNullOrEmpty(entry.itemId) || entry.count <= 0)
                    {
                        continue;
                    }

                    if (FindItem(entry.itemId) == null)
                    {
                        // 不静默丢弃：物品目录里没有它，说明改过 ID 或删过资产，读档时要说一声
                        Debug.LogWarning($"[{GetType().Name}] 读档跳过物品 {entry.itemId}：物品目录里没有它", this);
                        continue;
                    }

                    m_counts[entry.itemId] = entry.count;
                }
            }

            Debug.Log($"[{GetType().Name}] 已还原 {m_counts.Count} 种物品", this);

            ItemsChanged?.Invoke();
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>把「开局赠送」清单发到背包里（清单为空则什么也不做）</summary>
        private void GrantStartingItems()
        {
            for (int i = 0; i < m_startingItems.Count; i++)
            {
                InventoryStartingItem entry = m_startingItems[i];
                if (entry == null || entry.Item == null)
                {
                    continue;
                }

                int added = AddItem(entry.Item.Id, entry.Count);
                Debug.Log($"[{GetType().Name}] 开局赠送 {entry.Item.DisplayName} ×{added}", this);
            }
        }

        /// <summary>取堆叠上限（未配置或不限时返回 int.MaxValue）</summary>
        private int GetCapacity(string itemId)
        {
            ItemDataSO data = FindItem(itemId);

            if (data == null || data.MaxStack <= 0)
            {
                return int.MaxValue;
            }

            return data.MaxStack;
        }

        /// <summary>把物品增加上报给任务系统（任务系统是"收集类目标"的裁判）</summary>
        private void ReportToQuests(string itemId, int amount)
        {
            if (!m_reportToQuests || amount <= 0)
            {
                return;
            }

            QuestManager questManager = QuestManager.Instance;
            if (questManager == null)
            {
                // 没有任务系统也要允许背包独立工作，所以只提示不报错
                return;
            }

            questManager.ReportProgress(EQuestObjectiveType.Collect, itemId, amount);
        }
    }
}
