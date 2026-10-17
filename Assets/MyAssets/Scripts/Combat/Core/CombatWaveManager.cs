using System;
using System.Collections.Generic;
using MyGame.Audio;
using UnityEngine;
using UnityEngine.AI;

namespace MyGame.Combat
{
    /// <summary>战斗结果数据：结算界面展示所需的一次性快照</summary>
    public struct CombatResult
    {
        /// <summary>是否胜利（true=清完所有波次，false=玩家阵亡）</summary>
        public bool IsVictory;

        /// <summary>累计击杀数</summary>
        public int KillCount;

        /// <summary>累计得分</summary>
        public int Score;

        /// <summary>战斗总时长（秒）</summary>
        public float DurationInSeconds;
    }

    /// <summary>
    /// 战斗波次管理器：构成"刷怪 → 击杀 → 计分 → 胜负结算"的完整战斗闭环
    ///
    /// 状态机：
    ///   WaitingFirstWave ──延迟结束──→ Spawning ──刷完──→ Fighting
    ///                                                      │
    ///                     ┌──本波未打完────────────────────┘
    ///                     │
    ///                     └──本波清空──→ BetweenWaves ──间隔结束──→ Spawning（下一波）
    ///                                        │
    ///                                        └──已是最后一波──→ Over（胜利）
    ///   任意时刻玩家阵亡 ──→ Over（失败）
    ///
    /// 设计要点：
    /// 1. 只负责"流程与计数"，不碰伤害计算（那是 CombatUnit 的职责）
    /// 2. 通过事件向外广播状态，UI（HUD/结算面板）订阅即可，二者互不依赖
    /// 3. 刷怪用计时器驱动而非协程：项目为 Unity 2022.3，无 Awaitable；计时器无生命周期陷阱
    /// 4. 生成的敌人用 NavMesh.SamplePosition 吸附到导航网格，避免站不上 NavMesh 导致 AI 失效
    /// </summary>
    public class CombatWaveManager : MonoBehaviour
    {
        /// <summary>波次流程状态</summary>
        private enum EWaveState
        {
            /// <summary>等待第一波开始（给玩家准备时间）</summary>
            WaitingFirstWave,

            /// <summary>正在逐个生成当前波次的敌人</summary>
            Spawning,

            /// <summary>当前波次已全部生成，等待玩家清场</summary>
            Fighting,

            /// <summary>两波之间的间隔</summary>
            BetweenWaves,

            /// <summary>战斗已结束（胜负已分）</summary>
            Over,
        }

        // ====================================================================
        // 刷怪配置
        // ====================================================================

        [Header("刷怪配置")]
        [Tooltip("敌人预制体（必填）。可从场景中已有的敌人物体拖成 prefab 后使用")]
        [SerializeField] private GameObject m_enemyPrefab;

        [Tooltip("刷怪点列表（留空则在管理器自身位置周围刷怪）")]
        [SerializeField] private Transform[] m_spawnPoints;

        [Tooltip("第一波的敌人数")]
        [SerializeField] private int m_baseEnemyCount = 2;

        [Tooltip("每波递增的敌人数（波次越高人越多）")]
        [SerializeField] private int m_enemiesAddedPerWave = 1;

        [Tooltip("总波数。全部清完即判定胜利")]
        [SerializeField] private int m_totalWaves = 3;

        [Tooltip("进入场景后第一波的延迟（秒），给玩家准备时间")]
        [SerializeField] private float m_firstWaveDelay = 1.5f;

        [Tooltip("同一波内逐个刷怪的时间间隔（秒），避免敌人凭空同时出现")]
        [SerializeField] private float m_spawnStagger = 0.6f;

        [Tooltip("两波之间的间隔（秒）")]
        [SerializeField] private float m_waveInterval = 3f;

        [Tooltip("刷怪点周围的随机散布半径（米），避免多个敌人生成在同一点重叠")]
        [SerializeField] private float m_spawnScatterRadius = 1.5f;

        // ====================================================================
        // 计分与玩家配置
        // ====================================================================

        [Header("计分配置")]
        [Tooltip("击杀一个敌人获得的分数")]
        [SerializeField] private int m_scorePerKill = 100;

        [Header("玩家设置")]
        [Tooltip("玩家 Tag，用于监听玩家死亡以判定失败")]
        [SerializeField] private string m_playerTag = "Player";

        [Tooltip("玩家尚未生成时的查找间隔（秒）。网络模式玩家可能延迟生成")]
        [SerializeField] private float m_playerLookupInterval = 0.5f;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前波次（1 起；0 表示还没开始）</summary>
        private int m_currentWave;

        /// <summary>累计得分</summary>
        private int m_score;

        /// <summary>累计击杀数</summary>
        private int m_killCount;

        /// <summary>战斗开始时间（第一波开始时记录）</summary>
        private float m_combatStartTime;

        /// <summary>当前波次待生成的敌人数量</summary>
        private int m_pendingSpawnCount;

        /// <summary>下一次刷怪的时间点（Time.time）</summary>
        private float m_nextSpawnTime;

        /// <summary>下一次状态转换的时间点（Time.time）</summary>
        private float m_nextStateTime;

        /// <summary>当前流程状态</summary>
        private EWaveState m_state = EWaveState.WaitingFirstWave;

        /// <summary>当前存活的敌人（由本管理器生成）</summary>
        private readonly List<EnemyAI> m_aliveEnemies = new();

        /// <summary>最近一次广播过的存活数（用于只在变化时发事件）</summary>
        private int m_lastReportedAliveCount = -1;

        /// <summary>已绑定的玩家战斗单位</summary>
        private CombatUnit m_playerUnit;

        /// <summary>下一次尝试绑定玩家的时间点（Time.unscaledTime）</summary>
        private float m_nextPlayerLookupTime;

        // ====================================================================
        // 对外只读访问
        // ====================================================================

        /// <summary>当前波次（1 起）</summary>
        public int CurrentWave => m_currentWave;

        /// <summary>总波数</summary>
        public int TotalWaves => m_totalWaves;

        /// <summary>当前存活敌人数</summary>
        public int AliveCount => m_aliveEnemies.Count;

        /// <summary>累计得分</summary>
        public int Score => m_score;

        /// <summary>累计击杀数</summary>
        public int KillCount => m_killCount;

        /// <summary>战斗是否已结束</summary>
        public bool IsCombatOver => m_state == EWaveState.Over;

        // ====================================================================
        // 对外事件
        // ====================================================================

        /// <summary>新波次开始：(当前波次, 总波数)</summary>
        public event Action<int, int> WaveStarted;

        /// <summary>存活敌人数变化（仅在数值变化时触发）</summary>
        public event Action<int> AliveCountChanged;

        /// <summary>得分变化</summary>
        public event Action<int> ScoreChanged;

        /// <summary>战斗结束：(结果数据)</summary>
        public event Action<CombatResult> CombatEnded;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Start()
        {
            if (m_enemyPrefab == null)
            {
                Debug.LogError($"[{GetType().Name}] 未配置敌人预制体 m_enemyPrefab，波次系统不会刷怪", this);
                return;
            }

            // 进入等待第一波状态
            m_state = EWaveState.WaitingFirstWave;
            m_nextStateTime = Time.time + m_firstWaveDelay;
        }

        private void Update()
        {
            // 玩家可能晚于本物体生成（网络模式）：按间隔轮询补绑，用于判定失败
            TryBindPlayer();

            switch (m_state)
            {
                case EWaveState.WaitingFirstWave:
                    UpdateWaitingFirstWave();
                    break;

                case EWaveState.Spawning:
                    UpdateSpawning();
                    break;

                case EWaveState.Fighting:
                    UpdateFighting();
                    break;

                case EWaveState.BetweenWaves:
                    UpdateBetweenWaves();
                    break;

                case EWaveState.Over:
                    // 战斗已结束，不再推进流程
                    break;
            }
        }

        private void OnDisable()
        {
            // 解绑玩家事件，防止物体禁用后回调导致空引用
            UnbindPlayer();
        }

        // ====================================================================
        // 状态推进
        // ====================================================================

        /// <summary>等待第一波：延迟结束后开始第 1 波</summary>
        private void UpdateWaitingFirstWave()
        {
            if (Time.time < m_nextStateTime)
            {
                return;
            }

            m_combatStartTime = Time.time;
            StartWave(1);
        }

        /// <summary>刷怪中：按间隔逐个生成，全部生成完转入 Fighting</summary>
        private void UpdateSpawning()
        {
            if (Time.time < m_nextSpawnTime)
            {
                return;
            }

            SpawnOneEnemy();
            m_pendingSpawnCount--;

            if (m_pendingSpawnCount <= 0)
            {
                m_state = EWaveState.Fighting;
                ReportAliveCount();
                return;
            }

            m_nextSpawnTime = Time.time + m_spawnStagger;
        }

        /// <summary>战斗中：清理失效引用；本波清空则进入间隔或直接结算胜利</summary>
        private void UpdateFighting()
        {
            PruneDestroyedEnemies();
            ReportAliveCount();

            // 还有敌人存活，继续等待
            if (m_aliveEnemies.Count > 0)
            {
                return;
            }

            // 最后一波清空 → 胜利
            if (m_currentWave >= m_totalWaves)
            {
                EndCombat(true);
                return;
            }

            // 否则进入波次间隔
            m_state = EWaveState.BetweenWaves;
            m_nextStateTime = Time.time + m_waveInterval;
        }

        /// <summary>波次间隔：到时开始下一波</summary>
        private void UpdateBetweenWaves()
        {
            if (Time.time < m_nextStateTime)
            {
                return;
            }

            StartWave(m_currentWave + 1);
        }

        // ====================================================================
        // 刷怪
        // ====================================================================

        /// <summary>开始指定波次：计算数量、进入刷怪状态并广播</summary>
        private void StartWave(int wave)
        {
            m_currentWave = wave;
            m_pendingSpawnCount = m_baseEnemyCount + (wave - 1) * m_enemiesAddedPerWave;
            m_nextSpawnTime = Time.time;
            m_state = EWaveState.Spawning;

            // 开波即切战斗 BGM：波次之间保持不切，避免短短几秒来回横跳
            AudioManager.PlayMusic(EMusic.Combat);

            WaveStarted?.Invoke(m_currentWave, m_totalWaves);
            ReportAliveCount();
        }

        /// <summary>生成一个敌人：在随机刷怪点附近取合法 NavMesh 位置</summary>
        private void SpawnOneEnemy()
        {
            if (m_enemyPrefab == null)
            {
                return;
            }

            Vector3 basePosition = GetRandomSpawnPosition();

            // 把生成点吸附到导航网格：偏离 NavMesh 会导致 NavMeshAgent 无法寻路
            Vector3 spawnPosition = basePosition;
            NavMeshHit hit;
            if (NavMesh.SamplePosition(basePosition, out hit, 3f, NavMesh.AllAreas))
            {
                spawnPosition = hit.position;
            }

            GameObject instance = Instantiate(m_enemyPrefab, spawnPosition, Quaternion.identity);
            EnemyAI enemy = instance.GetComponent<EnemyAI>();

            if (enemy == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 敌人预制体上没有 EnemyAI 组件，无法统计击杀", instance);
                return;
            }

            m_aliveEnemies.Add(enemy);
            enemy.EnemyDied += HandleEnemyDied;
        }

        /// <summary>取一个生成位置：有刷怪点则随机选一个并散布，否则用管理器自身位置散布</summary>
        private Vector3 GetRandomSpawnPosition()
        {
            Vector3 center = transform.position;

            if (m_spawnPoints != null && m_spawnPoints.Length > 0)
            {
                // 过滤掉空引用后随机选一个
                int index = UnityEngine.Random.Range(0, m_spawnPoints.Length);
                Transform point = m_spawnPoints[index];
                if (point != null)
                {
                    center = point.position;
                }
            }

            // 在水平面上随机散布，避免多个敌人重叠在同一点
            Vector2 offset = UnityEngine.Random.insideUnitCircle * m_spawnScatterRadius;
            return center + new Vector3(offset.x, 0f, offset.y);
        }

        // ====================================================================
        // 击杀与计数
        // ====================================================================

        /// <summary>敌人死亡回调：移除引用、累计击杀与得分</summary>
        private void HandleEnemyDied(EnemyAI enemy)
        {
            if (enemy != null)
            {
                enemy.EnemyDied -= HandleEnemyDied;
                m_aliveEnemies.Remove(enemy);
            }

            m_killCount++;
            m_score += m_scorePerKill;

            ScoreChanged?.Invoke(m_score);
            ReportAliveCount();
        }

        /// <summary>清理被外部销毁的敌人引用（不重复计分，只保持计数准确）</summary>
        private void PruneDestroyedEnemies()
        {
            for (int i = m_aliveEnemies.Count - 1; i >= 0; i--)
            {
                if (m_aliveEnemies[i] == null)
                {
                    m_aliveEnemies.RemoveAt(i);
                }
            }
        }

        /// <summary>仅在存活数变化时广播，避免 UI 每帧刷新</summary>
        private void ReportAliveCount()
        {
            int alive = m_aliveEnemies.Count;
            if (alive == m_lastReportedAliveCount)
            {
                return;
            }

            m_lastReportedAliveCount = alive;
            AliveCountChanged?.Invoke(alive);
        }

        // ====================================================================
        // 玩家绑定与胜负判定
        // ====================================================================

        /// <summary>查找并绑定玩家战斗单位（失败时静默等待下次轮询）</summary>
        private void TryBindPlayer()
        {
            if (m_playerUnit != null || Time.unscaledTime < m_nextPlayerLookupTime)
            {
                return;
            }

            m_nextPlayerLookupTime = Time.unscaledTime + m_playerLookupInterval;

            if (string.IsNullOrEmpty(m_playerTag))
            {
                return;
            }

            GameObject player = GameObject.FindGameObjectWithTag(m_playerTag);
            if (player == null)
            {
                return;
            }

            CombatUnit unit = player.GetComponent<CombatUnit>();
            if (unit == null)
            {
                return;
            }

            m_playerUnit = unit;
            m_playerUnit.Died += HandlePlayerDied;
        }

        /// <summary>解绑玩家事件（可安全重复调用）</summary>
        private void UnbindPlayer()
        {
            if (m_playerUnit == null)
            {
                return;
            }

            m_playerUnit.Died -= HandlePlayerDied;
            m_playerUnit = null;
        }

        /// <summary>玩家阵亡 → 判定失败</summary>
        private void HandlePlayerDied(CombatUnit diedUnit)
        {
            EndCombat(false);
        }

        /// <summary>
        /// 结束战斗并广播结果
        /// 用状态判断保证只结算一次（玩家阵亡与波次清空可能同帧触发）
        /// </summary>
        private void EndCombat(bool isVictory)
        {
            if (m_state == EWaveState.Over)
            {
                return;
            }

            m_state = EWaveState.Over;

            CombatResult result = new CombatResult
            {
                IsVictory = isVictory,
                KillCount = m_killCount,
                Score = m_score,
                DurationInSeconds = Mathf.Max(0f, Time.time - m_combatStartTime),
            };

            Debug.Log($"[{GetType().Name}] 战斗结束：{(isVictory ? "胜利" : "失败")}，" +
                      $"击杀 {result.KillCount}，得分 {result.Score}，" +
                      $"用时 {result.DurationInSeconds:0.0}s", this);

            // 战斗结束：先退回野外 BGM。
            // 若结算面板随即弹出，它会把音乐换成胜负短旋律（后者会接管交叉淡变）；
            // 没有结算面板时也能正确回到探索状态，不会一直循环战斗曲
            AudioManager.PlayMusic(EMusic.Field);

            CombatEnded?.Invoke(result);
        }
    }
}
