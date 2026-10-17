using System.Collections.Generic;

namespace MyGame.UI
{
    /// <summary>任务类型：主线 / 支线</summary>
    public enum EQuestType
    {
        /// <summary>主线任务</summary>
        Main,

        /// <summary>支线任务</summary>
        Side,
    }

    /// <summary>
    /// 单条任务目标：文本 + 是否完成
    /// </summary>
    public class QuestObjective
    {
        /// <summary>目标描述文本</summary>
        public string Text { get; }

        /// <summary>是否已完成</summary>
        public bool IsDone { get; set; }

        public QuestObjective(string text, bool isDone)
        {
            Text = text;
            IsDone = isDone;
        }
    }

    /// <summary>
    /// 任务数据（运行时视图模型）
    ///
    /// 为什么单独放一个文件：
    /// 任务数据会被任务系统、任务追踪 HUD、任务日志面板、存档系统共用，
    /// 不应挂在某一个 UI 脚本里。
    ///
    /// 说明：当前为纯 C# 数据类，只承载 UI 展示所需字段。
    /// 后续接入真实任务系统时，可替换为 ScriptableObject 配置 + 任务系统运行时状态。
    /// </summary>
    public class QuestData
    {
        /// <summary>任务名</summary>
        public string Name { get; }

        /// <summary>任务描述</summary>
        public string Description { get; }

        /// <summary>任务类型（主线/支线）</summary>
        public EQuestType Type { get; }

        /// <summary>任务目标列表</summary>
        public List<QuestObjective> Objectives { get; } = new();

        /// <summary>奖励描述文本</summary>
        public string Reward { get; }

        /// <summary>是否已完成</summary>
        public bool IsCompleted { get; set; }

        public QuestData(string name, string description, EQuestType type, string reward)
        {
            Name = name;
            Description = description;
            Type = type;
            Reward = reward;
        }
    }
}
