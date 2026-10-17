using UnityEngine;
using UnityEngine.Pool;

namespace MyGame.Core
{
    /// <summary>
    /// 预制体对象池：对 Unity 内置 <see cref="ObjectPool{T}"/> 的一层薄封装。
    ///
    /// 【为什么要用对象池】
    /// 频繁 Instantiate / Destroy 会做两件很贵的事：
    /// 1. 分配内存 → 垃圾回收（GC）频繁触发，表现为每隔一会儿卡一下（掉帧尖峰）
    /// 2. 新建 GameObject 要重新建 Transform / 组件 / 网格，销毁又要反着来一遍
    /// 对象池的做法是"用的时候拿出来、不用的时候藏起来"，把这两步都省掉。
    ///
    /// 【为什么用 UnityEngine.Pool.ObjectPool 而不是自己写一个】
    /// Unity 2021.1 起内置了对象池，处理好了容量上限、重复入池检查等细节，
    /// 自己写容易在这些边界上出问题。本类只负责"预制体 → 世界物体"这层胶水。
    ///
    /// 【典型用法】
    /// <code>
    /// // 1. 初始化一次（例如 Awake 里），预热 8 个
    /// m_pool = new GameObjectPool(m_prefab, parent: transform, defaultCapacity: 8, maxSize: 64, prewarmCount: 8);
    ///
    /// // 2. 取：从池里拿一个并摆到指定位置
    /// GameObject go = m_pool.GetFromPool(position, rotation);
    ///
    /// // 3. 还：用完不要 Destroy，而是还给池子
    /// m_pool.ReturnToPool(go);
    /// </code>
    ///
    /// 【使用约束（很重要）】
    /// - 还回池子的物体不要再 Destroy，否则池里会留下已销毁的引用
    /// - 复用的物体必须自己把状态（血量/动画/计时器）重置干净，见 ReturnToPool 的说明
    /// - 状态很复杂、很难重置的物体不适合池化
    /// </summary>
    public class GameObjectPool
    {
        /// <summary>被池化的预制体（池内物体都由它实例化而来）</summary>
        private readonly GameObject m_prefab;

        /// <summary>池内物体的父节点（可为空）。还池时会重新挂回这里，避免散落在场景里</summary>
        private readonly Transform m_parent;

        /// <summary>Unity 内置对象池本体</summary>
        private readonly ObjectPool<GameObject> m_pool;

        /// <summary>是否已经销毁（销毁后再取会报错，用这个标记做保护）</summary>
        private bool m_disposed;

        /// <summary>池内物体的模板名字（用于给实例命名，方便在层级里辨认）</summary>
        private readonly string m_instanceName;

        /// <summary>
        /// 创建一个预制体对象池。
        /// </summary>
        /// <param name="prefab">要池化的预制体（不能为空）</param>
        /// <param name="parent">池内物体的父节点，留空则挂在场景根下</param>
        /// <param name="defaultCapacity">默认容量：池内部列表的初始大小，避免用着用着扩容</param>
        /// <param name="maxSize">上限：超过这个数量再归还就直接销毁，防止无限增长占内存</param>
        /// <param name="prewarmCount">预热数量：初始化时先建好这么多藏起来，避免第一次用到时卡一下</param>
        public GameObjectPool(
            GameObject prefab,
            Transform parent = null,
            int defaultCapacity = 8,
            int maxSize = 128,
            int prewarmCount = 0)
        {
            if (prefab == null)
            {
                Debug.LogError($"[{nameof(GameObjectPool)}] 预制体为空，对象池不可用");
                return;
            }

            m_prefab = prefab;
            m_parent = parent;
            m_instanceName = $"{prefab.name}(Pooled)";

            // UnityEngine.Pool.ObjectPool 的四个回调：
            // createFunc      → 池里没货时如何造一个新的
            // actionOnGet     → 取出去时做什么（这里：激活）
            // actionOnRelease → 还回来时做什么（这里：隐藏 + 归位）
            // actionOnDestroy → 真正销毁时（超出上限、或池被清空）做什么
            m_pool = new ObjectPool<GameObject>(
                createFunc: CreateInstance,
                actionOnGet: HandleGet,
                actionOnRelease: HandleRelease,
                actionOnDestroy: HandleDestroy,
                collectionCheck: false,
                defaultCapacity: Mathf.Max(0, defaultCapacity),
                maxSize: Mathf.Max(1, maxSize));

            // 预热：直接"造好就归还"，让它们先躺在池子里
            for (int i = 0; i < prewarmCount; i++)
            {
                m_pool.Release(CreateInstance());
            }
        }

        /// <summary>池中当前闲置（可复用）的物体数量</summary>
        public int CountInactive => m_pool != null ? m_pool.CountInactive : 0;

        /// <summary>池中当前已被取走（正在使用）的物体数量</summary>
        public int CountActive => m_pool != null ? m_pool.CountActive : 0;

        // ====================================================================
        // 取 / 还
        // ====================================================================

        /// <summary>从池中取一个物体（不改变位置，适合 UI 这类自己定位的物体）</summary>
        public GameObject GetFromPool()
        {
            if (!IsUsable)
            {
                return null;
            }

            return m_pool.Get();
        }

        /// <summary>从池中取一个物体并摆到指定位置与朝向（适合世界里的特效/敌人/掉落物）</summary>
        /// <param name="position">世界坐标</param>
        /// <param name="rotation">世界朝向</param>
        public GameObject GetFromPool(Vector3 position, Quaternion rotation)
        {
            GameObject instance = GetFromPool();
            if (instance == null)
            {
                return null;
            }

            instance.transform.SetPositionAndRotation(position, rotation);
            return instance;
        }

        /// <summary>
        /// 把物体还给池子。
        ///
        /// 【注意】调用方要保证物体状态已重置干净（血条填满、计时归零、动画回 Idle 等），
        /// 否则下次取出来会带着上次的残留状态，出现"刚生成的怪一出来就是残血"这类怪现象。
        /// </summary>
        public void ReturnToPool(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (!IsUsable)
            {
                // 池已销毁：直接销毁物体，避免留在场景里
                Object.Destroy(instance);
                return;
            }

            m_pool.Release(instance);
        }

        /// <summary>销毁池中所有闲置物体（正在使用的物体不受影响）。场景切换/宿主销毁时调用</summary>
        public void Clear()
        {
            if (m_pool == null)
            {
                return;
            }

            m_pool.Clear();
            m_disposed = true;
        }

        // ====================================================================
        // ObjectPool 内部回调
        // ====================================================================

        /// <summary>池里没货时造一个新的：实例化后先隐藏，避免"取之前"就露在场景里</summary>
        private GameObject CreateInstance()
        {
            // 第三个参数 instantiateInWorldSpace = false：保持预制体自身的本地坐标，
            // UI 与 3D 物体都适用（世界位置由 GetFromPool 显式赋值）
            GameObject instance = Object.Instantiate(m_prefab, m_parent, false);
            instance.name = m_instanceName;
            instance.SetActive(false);
            return instance;
        }

        /// <summary>取出去：激活物体</summary>
        private void HandleGet(GameObject instance)
        {
            instance.SetActive(true);
        }

        /// <summary>还回来：隐藏并挂回池父节点，等待下次复用</summary>
        private void HandleRelease(GameObject instance)
        {
            instance.SetActive(false);

            if (m_parent != null && instance.transform.parent != m_parent)
            {
                instance.transform.SetParent(m_parent, false);
            }
        }

        /// <summary>真正销毁（超出上限被淘汰，或池被 Clear）</summary>
        private void HandleDestroy(GameObject instance)
        {
            if (instance != null)
            {
                Object.Destroy(instance);
            }
        }

        /// <summary>池是否可用（预制体有效、尚未销毁）</summary>
        private bool IsUsable => m_pool != null && !m_disposed && m_prefab != null;
    }
}
