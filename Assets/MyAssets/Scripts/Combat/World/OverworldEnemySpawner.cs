using System.Collections;
using MyGame.Dungeon;
using UnityEngine;
using UnityEngine.AI;

namespace MyGame.Combat
{
    /// <summary>
    /// 大世界怪物刷新器：像原神那样，在场景各处放几小群怪。
    ///
    /// 【设计思路】
    /// 1. 为什么不直接把敌人预制体摆进场景？
    ///    摆死的怪只能在编辑器里一个个挪，数量、分布一改就得重新摆。
    ///    这里改成「配置若干刷怪点 + 每个点随机生成 1~3 只」，
    ///    以后调数量/位置只动 Inspector，不用碰场景。
    ///
    /// 2. 为什么玩家路过不会一路打怪？
    ///    怪物沿用 EnemyAI 自己的感知范围（发现 8 米 / 丢失 12 米），
    ///    站在范围外它们只会原地游荡（半径 6 米），不会追着玩家满地图跑。
    ///
    /// 3. 为什么刷怪点之间要拉开距离？
    ///    两点挨太近时，玩家一站过去就会同时拉两群怪的仇恨，
    ///    变成"走两步打一架"。所以三个点摆在出生点周围不同方向、彼此隔开。
    ///
    /// 4. 刷新机制：
    ///    每只怪死掉后单独计时，到点在**原来的刷怪点**重新生成一只，
    ///    保证总量恒定（不会越打越多）；玩家守在刷怪点旁边时会延迟刷，
    ///    避免怪"凭空出现在脸上"。
    ///
    /// 【挂载位置】主世界场景（Demo_Scene_Pines_1）
    /// 【依赖】场景里必须有烘焙好的 NavMesh，否则怪物不会走动
    /// </summary>
    public class OverworldEnemySpawner : MonoBehaviour
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("生成配置")]
        [Tooltip("敌人预制体（拉 Assets/MyAssets/Prefabs/EnemyDummy.prefab 进来）")]
        [SerializeField] private GameObject m_enemyPrefab;

        [Tooltip("刷怪点列表。每个点会生成 m_minPerSpot ~ m_maxPerSpot 只怪")]
        [SerializeField] private Transform[] m_spawnPoints;

        [Tooltip("每个刷怪点最少生成多少只")]
        [SerializeField, Min(0)] private int m_minPerSpot = 1;

        [Tooltip("每个刷怪点最多生成多少只")]
        [SerializeField, Min(1)] private int m_maxPerSpot = 3;

        [Tooltip("同一个刷怪点内，每只怪在这个半径内随机散开（米），避免几只叠在一起")]
        [SerializeField, Min(0f)] private float m_scatterRadius = 3f;

        [Header("刷新（怪被打死之后）")]
        [Tooltip("怪被杀后隔多少秒在原来的刷怪点补生成一只。0 = 不自动刷新（离开场景再回来才重新刷）")]
        [SerializeField, Min(0f)] private float m_respawnSeconds = 60f;

        [Header("安全限制")]
        [Tooltip("生成时至少要离玩家这么远（米），防止怪直接刷在玩家脸上")]
        [SerializeField, Min(0f)] private float m_minDistanceFromPlayer = 15f;

        [Tooltip("把生成点吸附到 NavMesh 上的最大搜索距离（米）。找不到网格就跳过这一次生成")]
        [SerializeField, Min(0.5f)] private float m_navMeshSnapDistance = 6f;

        [Tooltip("玩家 Tag，用于「离玩家太近就不刷」的判断")]
        [SerializeField] private string m_playerTag = "Player";

        [Header("调试")]
        [Tooltip("把每次生成/跳过的原因打到 Console，方便确认怪到底有没有刷出来")]
        [SerializeField] private bool m_logSpawn = true;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>玩家 Transform（懒查找并缓存，玩家可能晚于本物体生成）</summary>
        private Transform m_player;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Start()
        {
            // 秘境（副本）里有自己的波次刷怪系统，这里不参与，避免两套系统互相抢怪
            if (DungeonRunContext.IsInDungeon)
            {
                enabled = false;
                return;
            }

            if (m_enemyPrefab == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 没有配置敌人预制体，不会生成任何怪", this);
                return;
            }

            if (m_spawnPoints == null || m_spawnPoints.Length == 0)
            {
                Debug.LogWarning($"[{GetType().Name}] 没有配置刷怪点，不会生成任何怪", this);
                return;
            }

            // 进入场景先按配置铺一遍怪
            int spawned = 0;
            foreach (Transform point in m_spawnPoints)
            {
                if (point == null)
                {
                    continue;
                }

                spawned += SpawnAtSpot(point);
            }

            if (m_logSpawn)
            {
                Debug.Log($"[{GetType().Name}] 大世界刷怪完成，共生成 {spawned} 只（{m_spawnPoints.Length} 个刷怪点）", this);
            }
        }

        // ====================================================================
        // 生成
        // ====================================================================

        /// <summary>在某个刷怪点按随机数量铺怪，返回实际生成的数量</summary>
        private int SpawnAtSpot(Transform spot)
        {
            // Random.Range(int) 是左闭右开，所以最大值要 +1 才能取到
            int count = Random.Range(m_minPerSpot, m_maxPerSpot + 1);

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                if (SpawnOne(spot))
                {
                    spawned++;
                }
            }

            return spawned;
        }

        /// <summary>
        /// 在刷怪点附近生成一只怪。返回是否成功。
        /// 失败有几种正常原因：附近没有 NavMesh、玩家站得太近、刷怪点被销毁
        /// </summary>
        private bool SpawnOne(Transform spot)
        {
            if (spot == null)
            {
                return false;
            }

            // 在刷怪点周围随机散开，避免几只怪重叠在一起
            Vector2 circle = Random.insideUnitCircle * m_scatterRadius;
            Vector3 desired = spot.position + new Vector3(circle.x, 0f, circle.y);

            // 必须吸附到 NavMesh 上：怪物是靠 NavMeshAgent 走路的，
            // 生成在网格外的话它既不会巡逻也不会追击，只会杵在原地
            if (!NavMesh.SamplePosition(desired, out NavMeshHit hit, m_navMeshSnapDistance, NavMesh.AllAreas))
            {
                if (m_logSpawn)
                {
                    Debug.LogWarning($"[{GetType().Name}] 刷怪点 '{spot.name}' 附近没有 NavMesh，跳过这次生成", this);
                }
                return false;
            }

            // 别刷在玩家脸上
            if (IsPlayerTooClose(hit.position))
            {
                return false;
            }

            GameObject enemy = Instantiate(m_enemyPrefab, hit.position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            HookRespawn(enemy, spot);
            return true;
        }

        /// <summary>订阅这只怪的死亡事件，到点在原刷怪点补一只</summary>
        private void HookRespawn(GameObject enemy, Transform spot)
        {
            if (m_respawnSeconds <= 0f)
            {
                return;
            }

            EnemyAI ai = enemy.GetComponent<EnemyAI>();
            if (ai == null)
            {
                return;
            }

            // 用 lambda 记住"这只怪属于哪个刷怪点"，这样每只怪都能各自计时、各自补位
            ai.EnemyDied += _ => StartCoroutine(RespawnAfterDelay(spot));
        }

        /// <summary>等待刷新间隔，然后在原刷怪点补生成一只</summary>
        private IEnumerator RespawnAfterDelay(Transform spot)
        {
            // WaitForSeconds 会跟着 timeScale 走：打开 UI 暂停时刷新计时也停住，符合直觉
            yield return new WaitForSeconds(m_respawnSeconds);
            if (spot == null)
            {
                yield break;
            }

            // 玩家守在刷怪点旁边时先别刷（会凭空出现在脸上），每隔几秒再看一眼
            while (IsPlayerTooClose(spot.position))
            {
                yield return new WaitForSeconds(5f);
                if (spot == null)
                {
                    yield break;
                }
            }

            SpawnOne(spot);
        }

        // ====================================================================
        // 工具
        // ====================================================================

        /// <summary>给定位置是否离玩家太近（玩家不存在时视为不近，不阻塞生成）</summary>
        private bool IsPlayerTooClose(Vector3 position)
        {
            if (m_minDistanceFromPlayer <= 0f)
            {
                return false;
            }

            if (m_player == null)
            {
                GameObject found = GameObject.FindGameObjectWithTag(m_playerTag);
                if (found != null)
                {
                    m_player = found.transform;
                }
                else
                {
                    // 玩家还没生成：当作不近，不阻塞刷怪
                    return false;
                }
            }

            return Vector3.Distance(position, m_player.position) < m_minDistanceFromPlayer;
        }
    }
}
