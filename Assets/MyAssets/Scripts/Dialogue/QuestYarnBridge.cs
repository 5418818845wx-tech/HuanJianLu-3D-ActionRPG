using MyGame.Quest;
using UnityEngine;
using Yarn.Unity;

namespace MyGame.Dialogue
{
    /// <summary>
    /// 任务系统的 Yarn 桥接：把 QuestManager 的能力暴露成剧本里能用的「命令」和「函数」
    ///
    /// 为什么要这一层：
    /// - 剧本（.yarn）是给策划/文案写的，不该出现 C# 调用
    /// - 剧本里只写 <<accept_quest "Q_xxx">> 这种命令、{quest_title("Q_xxx")} 这种函数，
    ///   真正的任务逻辑仍然全部留在 QuestManager，本类只做转发
    ///
    /// 为什么用 [YarnCommand] / [YarnFunction] 特性而不是运行时注册：
    /// Yarn 是「先编译剧本、再运行」的。编译期必须知道每个函数的签名，否则会报
    /// 「Can't determine the type of the expression ...」。
    /// 特性写在方法上，Yarn 的编译器与运行时都能看到，因此这是唯一可靠的方式。
    /// 代价：函数必须是 static（编译期没有实例），所以任务资产查表放在静态字典里
    ///
    /// 挂载位置：与 DialogueRunner 同一物体（或手动把 Runner 拖到 m_dialogueRunner）
    /// </summary>
    [DisallowMultipleComponent]
    public class QuestYarnBridge : MonoBehaviour
    {
        // 说明：任务资产的目录由 QuestManager 统一持有（QuestManager 的「任务目录」列表），
        // 这里不再自己维护一份——否则"加任务要记得在两个地方登记"，迟早会漏一个。
        // 剧本里的 quest_xxx 函数最终都是查 QuestManager.FindQuestById。

        // ====================================================================
        // 命令：会改变游戏状态，剧本里写 <<accept_quest "Q_xxx">>
        // ====================================================================

        /// <summary>
        /// 接取任务。
        /// 注意：Yarn 的命令只允许返回 void / 协程 / Task，不能返回 bool 让剧本分支；
        /// 剧本要按结果分支时，命令之后再调 quest_available / quest_active 这类函数查询
        /// </summary>
        [YarnCommand("accept_quest")]
        public static void HandleAcceptQuest(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            QuestDataSO quest = FindQuest(questId);

            if (manager == null || quest == null)
            {
                Debug.LogWarning($"[{nameof(QuestYarnBridge)}] 接取任务失败：找不到任务 {questId}");
                return;
            }

            if (!manager.CanAcceptQuest(quest, out string reason))
            {
                Debug.LogWarning($"[{nameof(QuestYarnBridge)}] 任务 {questId} 当前不可接取：{reason}");
                return;
            }

            manager.AcceptQuest(quest);
        }

        /// <summary>交付任务</summary>
        [YarnCommand("turnin_quest")]
        public static void HandleTurnInQuest(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            if (manager == null || !manager.TurnInQuest(questId))
            {
                Debug.LogWarning($"[{nameof(QuestYarnBridge)}] 交付任务失败：{questId}");
            }
        }

        // ====================================================================
        // 函数：只读查询，剧本里写在条件与插值里，如 <<if quest_ready("Q_xxx")>>
        // ====================================================================

        /// <summary>任务是否可接取（未接取 + 前置条件满足）</summary>
        [YarnFunction("quest_available")]
        public static bool IsQuestAvailable(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            QuestDataSO quest = FindQuest(questId);

            if (manager == null || quest == null)
            {
                return false;
            }

            return manager.CanAcceptQuest(quest, out string _);
        }

        /// <summary>任务是否进行中（已接取且未交付完成）</summary>
        [YarnFunction("quest_active")]
        public static bool IsQuestActive(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            return manager != null && manager.IsQuestActive(questId);
        }

        /// <summary>任务目标是否已全部达成、等待交付</summary>
        [YarnFunction("quest_ready")]
        public static bool IsQuestReady(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            if (manager == null)
            {
                return false;
            }

            QuestState state = manager.GetQuestState(questId);
            return state != null && state.Status == EQuestStatus.ReadyToTurnIn;
        }

        /// <summary>任务是否已交付完成</summary>
        [YarnFunction("quest_done")]
        public static bool IsQuestDone(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            return manager != null && manager.IsQuestCompleted(questId);
        }

        /// <summary>任务标题（找不到时返回任务 ID，便于在剧本里看出哪条数据没配上）</summary>
        [YarnFunction("quest_title")]
        public static string GetQuestTitle(string questId)
        {
            QuestDataSO quest = FindQuest(questId);
            return quest != null ? quest.Title : questId;
        }

        /// <summary>任务描述</summary>
        [YarnFunction("quest_desc")]
        public static string GetQuestDescription(string questId)
        {
            QuestDataSO quest = FindQuest(questId);
            return quest != null ? quest.Description : string.Empty;
        }

        /// <summary>奖励摘要（如「金币 ×100」）</summary>
        [YarnFunction("quest_reward")]
        public static string GetQuestRewardSummary(string questId)
        {
            QuestDataSO quest = FindQuest(questId);
            return quest != null ? quest.GetRewardSummary() : string.Empty;
        }

        // ====================================================================
        // 辅助
        // ====================================================================

        /// <summary>按 ID 取任务资产（查 QuestManager 的统一目录，未配置时返回 null）</summary>
        private static QuestDataSO FindQuest(string questId)
        {
            QuestManager manager = QuestManager.Instance;
            return manager != null ? manager.FindQuestById(questId) : null;
        }
    }
}
