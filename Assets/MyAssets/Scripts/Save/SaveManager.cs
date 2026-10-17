using System;
using System.Collections.Generic;
using System.IO;
using KinematicCharacterController;
using MyGame.Combat;
using MyGame.Equipment;
using MyGame.Guide;
using MyGame.Inventory;
using MyGame.Quest;
using UnityEngine;

namespace MyGame.Save
{
    /// <summary>
    /// 存档管理器：把「游戏进度」收集成 JSON 落盘，再读回来还原
    ///
    /// 设计要点：
    /// 1. **各系统自己负责自己的存读**：本类不认识 QuestState 的内部结构，
    ///    只调用 QuestManager.CaptureSaveData() / ApplySaveData()。
    ///    以后加背包、技能，只要对应系统各加一对方法，这里加两行调用即可
    /// 2. **只存动态状态**：配置（任务表、道具表）永远从资产读，存档里只有 ID 和进度
    /// 3. **文件放在 Application.persistentDataPath**：不同平台都安全可写（Windows 下在
    ///    C:\Users\<用户>\AppData\LocalLow\<公司名>\<产品名>\）
    /// 4. **所有 IO 都 try-catch**：磁盘/权限问题属于"我们控制不了的外部失败"，不能让它把游戏搞崩
    ///
    /// 目前存的内容：任务进度、背包、装备、玩家位置朝向、等级与经验、技能等级、引导进度、存档时间戳
    /// 还没存：血量、已解锁技能、场景名（多场景后才需要）
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(k_executionOrder)]
    public class SaveManager : MonoBehaviour
    {
        /// <summary>
        /// 执行顺序排在其它系统之后（默认是 0）。
        ///
        /// 为什么必须靠后：「继续游戏」是在 Start 里读档的，
        /// 而 InventoryManager 也是在 Start 里发"开局赠送"（1000 金币 + 初始武器）。
        /// 两者顺序不确定——一旦读档先跑，赠送后跑，玩家就会拿到"存档内容 + 一份白送"的叠加结果。
        /// 明确让存档系统最后跑，把这个不确定性消掉
        /// </summary>
        private const int k_executionOrder = 100;

        /// <summary>存档里开始带装备数据的版本号（比它老的存档要跳过装备还原）</summary>
        private const int k_equipmentSaveVersion = 2;

        /// <summary>存档里开始带等级/经验数据的版本号（比它老的存档保持 1 级）</summary>
        private const int k_levelSaveVersion = 3;

        /// <summary>存档里开始带技能等级数据的版本号（比它老的存档所有技能按 1 级）</summary>
        private const int k_skillSaveVersion = 4;

        /// <summary>存档里开始带引导进度数据的版本号（比它老的存档视为引导已完成）</summary>
        private const int k_guideSaveVersion = 5;

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("测试快捷键（旧版 Input 兜底，与 UGUIManager 的按键路由保持一致）")]
        [Tooltip("打开后可按 F5 存档 / F9 读档，方便在编辑器里验证；正式发行应关掉")]
        [SerializeField] private bool m_enableHotkeys = true;

        [Tooltip("存档快捷键")]
        [SerializeField] private KeyCode m_saveKey = KeyCode.F5;

        [Tooltip("读档快捷键")]
        [SerializeField] private KeyCode m_loadKey = KeyCode.F9;

        [Header("玩家")]
        [Tooltip("玩家的 Tag（用于存取位置）")]
        [SerializeField] private string m_playerTag = "Player";

        // ====================================================================
        // 单例与对外事件
        // ====================================================================

        /// <summary>全局单例（场景中应只有一个 SaveManager）</summary>
        public static SaveManager Instance { get; private set; }

        /// <summary>存档完成（存档界面可订阅它弹"保存成功"提示）</summary>
        public event Action GameSaved;

        /// <summary>读档完成（UI 可订阅它整体刷新一遍）</summary>
        public event Action GameLoaded;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>存档文件的完整路径（与主菜单共用 SaveStorage，保证两边看的是同一个文件）</summary>
        public string SaveFilePath => SaveStorage.FilePath;

        /// <summary>是否已有存档（"继续游戏"按钮据此灰显）</summary>
        public bool HasSaveFile => SaveStorage.Exists;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 SaveManager 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void Start()
        {
            BindQuestAutoSave();

            // 主菜单点「继续游戏」时留下了读档意图，在这里兑现。
            // 放在 Start 且执行顺序靠后（见 k_executionOrder）：此时各系统的 Awake / Start 都已完成，
            // 读档不会被"开局赠送"之类的初始化覆盖或叠加
            if (!GameStartIntent.ConsumeLoadSaveRequest())
            {
                return;
            }

            if (!LoadGame())
            {
                Debug.LogWarning($"[{GetType().Name}] 收到读档请求但读档失败，本次以新游戏开始", this);
            }
        }

        private void OnDestroy()
        {
            UnbindQuestAutoSave();

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 自动存档
        // ====================================================================

        /// <summary>
        /// 订阅"任务完成"做自动存档。
        ///
        /// 为什么要自动存：玩家（以及试玩的人）不会记得手动按保存，
        /// 而"打完一个任务，进度却没了"是最伤体验的一类丢失。
        /// 任务完成是个天然的里程碑，且此时奖励已经发完（QuestManager 先 GrantRewards 再抛事件），
        /// 所以存下来的状态是完整的
        /// </summary>
        private void BindQuestAutoSave()
        {
            QuestManager questManager = QuestManager.Instance;
            if (questManager == null)
            {
                return;
            }

            questManager.QuestCompleted += HandleQuestCompleted;
        }

        /// <summary>解除自动存档订阅（可安全重复调用）</summary>
        private void UnbindQuestAutoSave()
        {
            QuestManager questManager = QuestManager.Instance;
            if (questManager == null)
            {
                return;
            }

            questManager.QuestCompleted -= HandleQuestCompleted;
        }

        /// <summary>任务完成：自动存一次档</summary>
        private void HandleQuestCompleted(QuestState state)
        {
            if (!SaveGame())
            {
                return;
            }

            Debug.Log($"[{GetType().Name}] 任务「{state.Data.Title}」完成，已自动存档", this);
        }

        private void Update()
        {
            if (!m_enableHotkeys)
            {
                return;
            }

            if (Input.GetKeyDown(m_saveKey))
            {
                SaveGame();
            }
            else if (Input.GetKeyDown(m_loadKey))
            {
                LoadGame();
            }
        }

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>存档：收集当前进度 → 写 JSON。成功返回 true</summary>
        public bool SaveGame()
        {
            try
            {
                GameSaveData data = CollectSaveData();
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(SaveFilePath, json);

                Debug.Log($"[{GetType().Name}] 存档成功（{data.quests.Count} 条任务）→ {SaveFilePath}", this);
                GameSaved?.Invoke();
                return true;
            }
            catch (IOException ioException)
            {
                // 磁盘满、文件被占用等
                Debug.LogError($"[{GetType().Name}] 存档失败（IO）：{ioException}", this);
            }
            catch (UnauthorizedAccessException accessException)
            {
                // 无写权限
                Debug.LogError($"[{GetType().Name}] 存档失败（无权限）：{accessException}", this);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[{GetType().Name}] 存档失败（未知）：{exception}", this);
            }

            return false;
        }

        /// <summary>读档：读 JSON → 还原各系统状态。成功返回 true</summary>
        public bool LoadGame()
        {
            if (!HasSaveFile)
            {
                Debug.LogWarning($"[{GetType().Name}] 没有找到存档文件：{SaveFilePath}", this);
                return false;
            }

            try
            {
                string json = File.ReadAllText(SaveFilePath);
                GameSaveData data = JsonUtility.FromJson<GameSaveData>(json);

                if (data == null)
                {
                    Debug.LogError($"[{GetType().Name}] 读档失败：存档内容为空或格式不对", this);
                    return false;
                }

                if (data.version > GameSaveData.CurrentVersion)
                {
                    // 老程序读新存档：结构可能不认识，明确拒绝比读出一堆错数据安全
                    Debug.LogError($"[{GetType().Name}] 读档失败：存档版本 {data.version} 高于本程序支持的版本 " +
                                   $"{GameSaveData.CurrentVersion}", this);
                    return false;
                }

                ApplySaveData(data);

                Debug.Log($"[{GetType().Name}] 读档成功（存档时间 {data.savedAtUtc}）", this);
                GameLoaded?.Invoke();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[{GetType().Name}] 读档失败：{exception}", this);
                return false;
            }
        }

        /// <summary>删档（做"新游戏"时用）</summary>
        public bool DeleteSaveFile()
        {
            if (!HasSaveFile)
            {
                return false;
            }

            try
            {
                File.Delete(SaveFilePath);
                Debug.Log($"[{GetType().Name}] 已删除存档：{SaveFilePath}", this);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[{GetType().Name}] 删档失败：{exception}", this);
                return false;
            }
        }

        // ====================================================================
        // 收集与还原
        // ====================================================================

        /// <summary>把所有需要持久化的东西收集成一份存档数据</summary>
        private GameSaveData CollectSaveData()
        {
            GameSaveData data = new GameSaveData();
            data.savedAtUtc = DateTime.UtcNow.ToString("o");

            // 任务：交给 QuestManager 自己导出，本类不碰它的内部结构
            QuestManager questManager = QuestManager.Instance;
            data.quests = questManager != null
                ? questManager.CaptureSaveData()
                : new List<QuestSaveEntry>();

            // 背包：同理，交给 InventoryManager 自己导出
            InventoryManager inventory = InventoryManager.Instance;
            data.inventory = inventory != null
                ? inventory.CaptureSaveData()
                : new List<InventorySaveEntry>();

            // 装备：注意必须排在背包之后——还原时装备要靠背包来判断"这件武器还在不在身上"
            EquipmentSystem equipment = FindEquipmentSystem();
            data.equipment = equipment != null
                ? equipment.CaptureSaveData()
                : new EquipmentSaveData();

            // 玩家：存位置与朝向
            GameObject player = FindPlayer();
            if (player != null)
            {
                data.playerPosition = player.transform.position;
                data.playerYaw = player.transform.eulerAngles.y;
            }

            // 等级/经验：交给 PlayerLevelSystem 提供，本类只搬运两个数值
            PlayerLevelSystem levelSystem = FindPlayerLevelSystem();
            if (levelSystem != null)
            {
                data.playerLevel = levelSystem.Level;
                data.playerExp = levelSystem.CurrentExp;
            }
            else
            {
                // 没找到就写"新号"数据，避免把默认值 0 级这种非法状态存进去
                data.playerLevel = 1;
                data.playerExp = 0;
            }

            // 技能等级：交给 SkillUpgradeSystem 自己导出
            SkillUpgradeSystem skillUpgrades = SkillUpgradeSystem.Instance;
            data.skillLevels = skillUpgrades != null
                ? skillUpgrades.CaptureSaveData()
                : new List<SkillLevelEntry>();

            // 引导进度：交给 GuideSystem 自己导出
            GuideSystem guide = GuideSystem.Instance;
            if (guide != null)
            {
                guide.CaptureSaveData(out int guideStep, out bool guideSkipped);
                data.guideStepIndex = guideStep;
                data.guideSkipped = guideSkipped;
            }

            return data;
        }

        /// <summary>把存档数据分发给各系统还原</summary>
        private void ApplySaveData(GameSaveData data)
        {
            QuestManager questManager = QuestManager.Instance;
            if (questManager != null)
            {
                questManager.ApplySaveData(data.quests);
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 QuestManager，任务进度未还原", this);
            }

            InventoryManager inventory = InventoryManager.Instance;
            if (inventory != null)
            {
                inventory.ApplySaveData(data.inventory);
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 InventoryManager，背包未还原", this);
            }

            // 装备：必须在背包还原之后，否则会误判"这件武器不在背包里"而清空槽位
            EquipmentSystem equipment = FindEquipmentSystem();
            if (equipment != null && data.version >= k_equipmentSaveVersion && data.equipment != null)
            {
                equipment.ApplySaveData(data.equipment);
            }
            else if (equipment != null)
            {
                // 老存档里没有这项数据。**不能拿空数据去覆盖**——那等于把玩家手上的武器扒光，
                // 而玩家根本没做过这个操作。保留当前（开局）装备更符合直觉
                Debug.LogWarning($"[{GetType().Name}] 存档 v{data.version} 没有装备数据，保留当前装备", this);
            }

            // 等级/经验：v3 起才有这项。老存档没有等级数据，保持 1 级即可
            // （这里仍然显式判版本，避免"字段默认值"和"版本语义"两套规则混在一起，
            //   以后再加等级相关字段时才不会读出一堆错数据）
            PlayerLevelSystem levelSystem = FindPlayerLevelSystem();
            if (levelSystem != null && data.version >= k_levelSaveVersion)
            {
                levelSystem.ApplySaveData(data.playerLevel, data.playerExp);
            }
            else if (levelSystem != null)
            {
                Debug.LogWarning($"[{GetType().Name}] 存档 v{data.version} 没有等级数据，按 1 级开始", this);
            }

            // 技能等级：v4 起才有这项。老存档跳过，保持所有技能 1 级
            SkillUpgradeSystem skillUpgrades = SkillUpgradeSystem.Instance;
            if (skillUpgrades != null && data.version >= k_skillSaveVersion)
            {
                skillUpgrades.ApplySaveData(data.skillLevels);
            }
            else if (skillUpgrades != null)
            {
                Debug.LogWarning($"[{GetType().Name}] 存档 v{data.version} 没有技能等级数据，所有技能按 1 级", this);
            }

            // 引导进度：v5 起才有这项。
            // 更老的存档视为"引导已完成"——否则老玩家一读档就被塞一段新手引导，
            // 那是很糟糕的体验（引导是给第一次玩的人看的）
            GuideSystem guide = GuideSystem.Instance;
            if (guide != null && data.version >= k_guideSaveVersion)
            {
                guide.ApplySaveData(data.guideStepIndex, data.guideSkipped);
            }
            else if (guide != null)
            {
                guide.MarkAsCompleted();
                Debug.Log($"[{GetType().Name}] 存档 v{data.version} 没有引导数据，按已完成处理", this);
            }

            TeleportPlayer(data.playerPosition, data.playerYaw);
        }

        /// <summary>找等级系统。它挂在玩家身上，所以按玩家 Tag 找，找不到再全场搜一次</summary>
        private PlayerLevelSystem FindPlayerLevelSystem()
        {
            if (PlayerLevelSystem.Instance != null)
            {
                return PlayerLevelSystem.Instance;
            }

            GameObject player = FindPlayer();
            return player != null ? player.GetComponentInChildren<PlayerLevelSystem>() : null;
        }

        /// <summary>找装备系统。它挂在玩家身上，所以按玩家 Tag 找，找不到再全场搜一次</summary>
        private EquipmentSystem FindEquipmentSystem()
        {
            if (EquipmentSystem.Instance != null)
            {
                return EquipmentSystem.Instance;
            }

            GameObject player = FindPlayer();
            return player != null ? player.GetComponentInChildren<EquipmentSystem>() : null;
        }

        /// <summary>
        /// 把玩家放回存档位置。
        /// 必须走 KinematicCharacterMotor.SetPositionAndRotation，直接改 transform 会和
        /// 角色控制器的内部状态不同步（表现为穿地、抖动）
        /// </summary>
        private void TeleportPlayer(Vector3 position, float yaw)
        {
            GameObject player = FindPlayer();
            if (player == null)
            {
                return;
            }

            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            KinematicCharacterMotor motor = player.GetComponent<KinematicCharacterMotor>();

            if (motor != null)
            {
                motor.SetPositionAndRotation(position, rotation);
            }
            else
            {
                player.transform.SetPositionAndRotation(position, rotation);
            }
        }

        /// <summary>取玩家物体（按 Tag 查找）</summary>
        private GameObject FindPlayer()
        {
            if (string.IsNullOrEmpty(m_playerTag))
            {
                return null;
            }

            return GameObject.FindGameObjectWithTag(m_playerTag);
        }
    }
}
