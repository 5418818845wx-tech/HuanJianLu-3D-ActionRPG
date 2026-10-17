using System.Collections.Generic;
using System.IO;
using MyGame.Combat;
using MyGame.Interaction;
using MyGame.Quest;
using UnityEditor;
using UnityEngine;
using Yarn.Unity;

namespace MyGame.EditorTools
{
    /// <summary>
    /// 内容校验器（编辑器专用工具）
    ///
    /// 为什么需要它：
    /// 任务、NPC、剧本之间靠「字符串 ID」互相引用（如 Npc_Herbalist）。
    /// 字符串写错不会编译报错，只会让任务永远完不成，而且没有任何提示。
    /// 本工具把这类「静默失败」变成「点一下菜单就能查出来」。
    ///
    /// 检查项：
    /// 1. 任务 ID 重复 / 为空
    /// 2. 场景 NPC ID 重复 / 为空
    /// 3. 任务的「对话」目标引用的 NPC，在场景里是否存在（悬空引用）
    /// 4. 任务的「击杀」目标引用的敌人 ID，是否有敌人预制体在用（悬空引用）
    /// 5. 任务的「收集」目标引用的物品 ID，是否有采集物提供（悬空引用）
    /// 6. 任务的「到达」目标引用的区域 ID，场景里是否有区域触发器（悬空引用）
    /// 7. 剧本健康：每个 NPC 在 Yarn 工程里有没有同名节点、.yarn 文件有没有被写入重复 BOM
    ///
    /// 注意第 4 项不做「重复检查」：多个敌人预制体共用同一个敌人 ID 是合法的
    /// （同一种敌人的不同外观/数值），而 NPC 撞 ID 会导致任务进度算错，所以两者规则不同。
    /// </summary>
    public static class ContentValidator
    {
        private const string k_menuPath = "Tools/内容校验/检查任务与 NPC";

        // ID 为空时统一用它当字典键，方便把"空 ID"当成一类问题报出来
        private const string k_emptyIdLabel = "(空)";

        [MenuItem(k_menuPath)]
        public static void Validate()
        {
            List<QuestDataSO> quests = LoadAllQuests();
            List<QuestGiver> givers = FindAllQuestGivers();

            // 第一步：把所有索引都建好（数据准备）
            Dictionary<string, List<string>> questIdIndex = BuildQuestIdIndex(quests);
            Dictionary<string, List<string>> npcIdIndex = BuildNpcIdIndex(givers);
            Dictionary<string, List<string>> enemyIdIndex = BuildEnemyIdIndex();
            Dictionary<string, List<string>> itemIdIndex = BuildItemIdIndex();
            Dictionary<string, List<string>> areaIdIndex = BuildAreaIdIndex();

            Debug.Log($"[内容校验] 开始：任务 {quests.Count} 个，场景 NPC {givers.Count} 个，"
                + $"敌人 ID {enemyIdIndex.Count} 个，物品 ID {itemIdIndex.Count} 个，区域 ID {areaIdIndex.Count} 个");

            // 第二步：逐项检查，把问题数累加起来（新增检查项就加在这一段里）
            int issueCount = 0;
            issueCount += ReportIdProblems("任务 ID", questIdIndex);
            issueCount += ReportIdProblems("NPC ID", npcIdIndex);
            issueCount += ReportDanglingNpcTargets(quests, npcIdIndex);
            issueCount += ReportDanglingEnemyTargets(quests, enemyIdIndex);
            issueCount += ReportDanglingCollectTargets(quests, itemIdIndex);
            issueCount += ReportDanglingReachTargets(quests, areaIdIndex);
            issueCount += ReportYarnProblems(givers);

            // 第三步：最后统一给结论
            if (issueCount == 0)
            {
                Debug.Log("[内容校验] 通过，未发现问题");
                return;
            }

            Debug.LogWarning($"[内容校验] 共发现 {issueCount} 个问题，详见上方日志");
        }

        // ====================================================================
        // 收集：先把要检查的东西全捞出来
        // ====================================================================

        /// <summary>扫全项目的任务资产</summary>
        private static List<QuestDataSO> LoadAllQuests()
        {
            List<QuestDataSO> quests = new();

            // "t:类型名" 能匹配任意类型，自定义 ScriptableObject 也适用
            string[] guids = AssetDatabase.FindAssets("t:QuestDataSO");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                QuestDataSO quest = AssetDatabase.LoadAssetAtPath<QuestDataSO>(path);

                if (quest == null)
                {
                    continue;
                }

                quests.Add(quest);
            }

            return quests;
        }

        /// <summary>扫当前场景里的 NPC（含被禁用的，避免隐藏 NPC 被误判成不存在）</summary>
        private static List<QuestGiver> FindAllQuestGivers()
        {
            QuestGiver[] found = Object.FindObjectsByType<QuestGiver>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            return new List<QuestGiver>(found);
        }

        // ====================================================================
        // 建索引：ID -> 出处（出处用来告诉你"是哪个文件/哪个物体"）
        // ====================================================================

        private static Dictionary<string, List<string>> BuildQuestIdIndex(List<QuestDataSO> quests)
        {
            Dictionary<string, List<string>> index = new();

            foreach (QuestDataSO quest in quests)
            {
                AddToIndex(index, quest.Id, AssetDatabase.GetAssetPath(quest));
            }

            return index;
        }

        private static Dictionary<string, List<string>> BuildNpcIdIndex(List<QuestGiver> givers)
        {
            Dictionary<string, List<string>> index = new();

            foreach (QuestGiver giver in givers)
            {
                AddToIndex(index, giver.NpcId, giver.gameObject.name);
            }

            return index;
        }

        /// <summary>
        /// 扫全项目的预制体，建立 敌人 ID -> 预制体路径 的索引
        /// 敌人和 NPC 不同：NPC 是场景里的物体，敌人是运行时从预制体刷出来的，
        /// 所以编辑器里查敌人必须扫资产，而不是扫场景
        /// </summary>
        private static Dictionary<string, List<string>> BuildEnemyIdIndex()
        {
            Dictionary<string, List<string>> index = new();

            // "t:Prefab" 扫出全项目所有预制体（含插件包里的，多扫一些无妨）
            string[] guids = AssetDatabase.FindAssets("t:Prefab");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (prefab == null)
                {
                    continue;
                }

                // 用复数版：一个预制体里可能挂着多个敌人（比如一队小怪）
                // 参数 true = 连被禁用的子物体也算
                EnemyAI[] enemies = prefab.GetComponentsInChildren<EnemyAI>(true);

                foreach (EnemyAI enemy in enemies)
                {
                    AddToIndex(index, enemy.EnemyId, path);
                }
            }

            return index;
        }

        /// <summary>
        /// 收集「物品 ID」索引：采集物既可能摆在场景里，也可能是预制体（运行时掉落），所以两边都扫
        /// </summary>
        private static Dictionary<string, List<string>> BuildItemIdIndex()
        {
            Dictionary<string, List<string>> index = new();

            // 场景里摆好的采集物
            PickupItem[] sceneItems = Object.FindObjectsByType<PickupItem>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (PickupItem item in sceneItems)
            {
                AddToIndex(index, item.ItemId, item.gameObject.name);
            }

            // 预制体里的采集物
            string[] guids = AssetDatabase.FindAssets("t:Prefab");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (prefab == null)
                {
                    continue;
                }

                PickupItem[] prefabItems = prefab.GetComponentsInChildren<PickupItem>(true);

                foreach (PickupItem item in prefabItems)
                {
                    AddToIndex(index, item.ItemId, path);
                }
            }

            return index;
        }

        /// <summary>
        /// 收集「区域 ID」索引：区域触发器是摆在场景里的物体（和 NPC 一样），所以扫场景
        /// </summary>
        private static Dictionary<string, List<string>> BuildAreaIdIndex()
        {
            Dictionary<string, List<string>> index = new();

            AreaTrigger[] areas = Object.FindObjectsByType<AreaTrigger>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (AreaTrigger area in areas)
            {
                AddToIndex(index, area.AreaId, area.gameObject.name);
            }

            return index;
        }

        private static void AddToIndex(Dictionary<string, List<string>> index, string id, string source)
        {
            string key = string.IsNullOrEmpty(id) ? k_emptyIdLabel : id;

            if (!index.TryGetValue(key, out List<string> sources))
            {
                sources = new List<string>();
                index.Add(key, sources);
            }

            sources.Add(source);
        }

        // ====================================================================
        // 检查
        // ====================================================================

        /// <summary>检查 ID 为空与重复</summary>
        private static int ReportIdProblems(string label, Dictionary<string, List<string>> index)
        {
            int issues = 0;

            foreach (KeyValuePair<string, List<string>> pair in index)
            {
                if (pair.Key == k_emptyIdLabel)
                {
                    Debug.LogError($"[内容校验] {label} 为空：{string.Join("、", pair.Value)}");
                    issues++;
                    continue;
                }

                if (pair.Value.Count < 2)
                {
                    continue;
                }

                Debug.LogError($"[内容校验] {label} 重复「{pair.Key}」：{string.Join("、", pair.Value)}");
                issues++;
            }

            return issues;
        }

        /// <summary>检查任务里的「对话」目标是否指向了场景中不存在的 NPC</summary>
        private static int ReportDanglingNpcTargets(
            List<QuestDataSO> quests,
            Dictionary<string, List<string>> npcIdIndex)
        {
            int issues = 0;

            foreach (QuestDataSO quest in quests)
            {
                foreach (QuestObjectiveData objective in quest.Objectives)
                {
                    // 只检查对话类目标：击杀/收集/到达的目标 ID 属于另外几套（敌人/物品/区域）
                    if (objective.Type != EQuestObjectiveType.Talk)
                    {
                        continue;
                    }

                    // 留空表示"与任意 NPC 对话"，是合法配置，不算问题
                    if (string.IsNullOrEmpty(objective.TargetId))
                    {
                        continue;
                    }

                    if (npcIdIndex.ContainsKey(objective.TargetId))
                    {
                        continue;
                    }

                    Debug.LogError(
                        $"[内容校验] 任务「{quest.Id}」的对话目标引用了场景中不存在的 NPC「{objective.TargetId}」" +
                        $"（资产：{AssetDatabase.GetAssetPath(quest)}）");
                    issues++;
                }
            }

            return issues;
        }

        /// <summary>检查任务里的「击杀」目标是否指向了没有敌人使用的 ID</summary>
        private static int ReportDanglingEnemyTargets(
            List<QuestDataSO> quests,
            Dictionary<string, List<string>> enemyIdIndex)
        {
            int issues = 0;

            foreach (QuestDataSO quest in quests)
            {
                foreach (QuestObjectiveData objective in quest.Objectives)
                {
                    // 与 NPC 版唯一的区别：这里看击杀类目标
                    if (objective.Type != EQuestObjectiveType.Kill)
                    {
                        continue;
                    }

                    // 留空表示"击杀任意敌人"，是合法配置
                    if (string.IsNullOrEmpty(objective.TargetId))
                    {
                        continue;
                    }

                    if (enemyIdIndex.ContainsKey(objective.TargetId))
                    {
                        continue;
                    }

                    Debug.LogError(
                        $"[内容校验] 任务「{quest.Id}」的击杀目标引用了没有敌人使用的 ID「{objective.TargetId}」" +
                        $"（资产：{AssetDatabase.GetAssetPath(quest)}）");
                    issues++;
                }
            }

            return issues;
        }

        /// <summary>检查任务里的「收集」目标是否指向了没有采集物提供的物品 ID</summary>
        private static int ReportDanglingCollectTargets(
            List<QuestDataSO> quests,
            Dictionary<string, List<string>> itemIdIndex)
        {
            int issues = 0;

            foreach (QuestDataSO quest in quests)
            {
                foreach (QuestObjectiveData objective in quest.Objectives)
                {
                    // 与 NPC / 敌人版同理，只是换成收集类目标
                    if (objective.Type != EQuestObjectiveType.Collect)
                    {
                        continue;
                    }

                    // 留空表示"收集任意物品"，是合法配置
                    if (string.IsNullOrEmpty(objective.TargetId))
                    {
                        continue;
                    }

                    if (itemIdIndex.ContainsKey(objective.TargetId))
                    {
                        continue;
                    }

                    Debug.LogError(
                        $"[内容校验] 任务「{quest.Id}」的收集目标引用了没有任何采集物提供的物品 ID「{objective.TargetId}」" +
                        $"（资产：{AssetDatabase.GetAssetPath(quest)}）");
                    issues++;
                }
            }

            return issues;
        }

        /// <summary>
        /// 检查任务里的「到达」目标是否指向了场景中不存在的区域触发器
        ///
        /// 这一项特别值得查：到达类目标没有任何报错渠道——
        /// 区域 ID 写错时，玩家走到指定地点什么都不会发生，
        /// 既不报错也没有提示，看起来就像"任务卡住了"
        /// </summary>
        private static int ReportDanglingReachTargets(
            List<QuestDataSO> quests,
            Dictionary<string, List<string>> areaIdIndex)
        {
            int issues = 0;

            foreach (QuestDataSO quest in quests)
            {
                foreach (QuestObjectiveData objective in quest.Objectives)
                {
                    // 与 NPC / 敌人 / 物品版同理，只是换成到达类目标
                    if (objective.Type != EQuestObjectiveType.Reach)
                    {
                        continue;
                    }

                    // 留空表示"到达任意区域"，是合法配置
                    if (string.IsNullOrEmpty(objective.TargetId))
                    {
                        continue;
                    }

                    if (areaIdIndex.ContainsKey(objective.TargetId))
                    {
                        continue;
                    }

                    Debug.LogError(
                        $"[内容校验] 任务「{quest.Id}」的到达目标引用了场景中不存在的区域「{objective.TargetId}」" +
                        $"（资产：{AssetDatabase.GetAssetPath(quest)}）——玩家走到那里不会有任何反应");
                    issues++;
                }
            }

            return issues;
        }

        /// <summary>
        /// 检查剧本（Yarn）是否健康：NPC 有没有同名节点 + .yarn 文件编码有没有被写坏。
        ///
        /// 为什么这项值得单独查：
        /// QuestGiver 是拿 NpcId 当节点名去启动对话的。**节点不存在时不会有任何报错**——
        /// 按 G 就是没反应，玩家和开发都会以为是交互坏了。
        /// 而"整个剧本编译失败"（比如文件开头被编辑器写入了重复 BOM）会让编译结果变成 0 个节点，
        /// 于是所有 NPC 一起哑掉；Yarn 的报错又只在导入时打印一次，进 Play 后控制台被清空就再也看不到了
        /// </summary>
        private static int ReportYarnProblems(List<QuestGiver> givers)
        {
            int issues = ReportYarnFileEncodingProblems();

            YarnProject project = LoadYarnProject();
            if (project == null)
            {
                Debug.LogError("[内容校验] 项目里找不到 .yarnproject，所有对话都无法启动");
                return issues + 1;
            }

            if (project.Program == null)
            {
                Debug.LogError($"[内容校验] 剧本工程「{project.name}」没有编译结果，所有对话都无法启动");
                return issues + 1;
            }

            HashSet<string> nodeNames = new(project.Program.Nodes.Keys);
            Debug.Log($"[内容校验] 剧本工程「{project.name}」编译出 {nodeNames.Count} 个节点");

            // 一个节点都没有：几乎一定是剧本没编译过，此时逐个 NPC 报错只会刷屏
            if (nodeNames.Count == 0)
            {
                Debug.LogError("[内容校验] 剧本编译出 0 个节点，请检查 Console 里有没有“Error compiling …”的剧本报错");
                return issues + 1;
            }

            foreach (QuestGiver giver in givers)
            {
                if (string.IsNullOrEmpty(giver.NpcId))
                {
                    continue;
                }

                if (nodeNames.Contains(giver.NpcId))
                {
                    continue;
                }

                Debug.LogError(
                    $"[内容校验] NPC「{giver.gameObject.name}」的 ID 是 {giver.NpcId}，" +
                    "但剧本里没有同名节点——按 G 交互不会有任何反应");
                issues++;
            }

            return issues;
        }

        /// <summary>
        /// 检查 .yarn 文件开头有没有重复的 BOM。
        ///
        /// 重复 BOM（EF BB BF EF BB BF）是外部编辑器反复"另存为 UTF-8"造成的，
        /// 肉眼完全看不出来，但会让 Yarn 的词法器解析错位，报出
        /// "extraneous input 'title' expecting HEADER_DELIMITER" 这种指不到真正原因的错
        /// </summary>
        private static int ReportYarnFileEncodingProblems()
        {
            int issues = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:TextAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".yarn"))
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (IOException exception)
                {
                    Debug.LogError($"[内容校验] 读不了剧本文件 {path}：{exception.Message}");
                    issues++;
                    continue;
                }

                int bomCount = 0;
                while (bomCount * 3 + 2 < bytes.Length
                       && bytes[bomCount * 3] == 0xEF
                       && bytes[bomCount * 3 + 1] == 0xBB
                       && bytes[bomCount * 3 + 2] == 0xBF)
                {
                    bomCount++;
                }

                if (bomCount > 1)
                {
                    Debug.LogError($"[内容校验] 剧本 {path} 开头有 {bomCount} 个 BOM（重复了），" +
                        "会导致整个剧本编译失败、所有对话没反应；用「UTF-8 无 BOM」重新保存该文件即可");
                    issues++;
                }
            }

            return issues;
        }

        /// <summary>取项目里唯一的剧本工程（找不到返回 null）</summary>
        private static YarnProject LoadYarnProject()
        {
            string[] guids = AssetDatabase.FindAssets("t:YarnProject");

            foreach (string guid in guids)
            {
                YarnProject project = AssetDatabase.LoadAssetAtPath<YarnProject>(AssetDatabase.GUIDToAssetPath(guid));
                if (project != null)
                {
                    return project;
                }
            }

            return null;
        }
    }
}
