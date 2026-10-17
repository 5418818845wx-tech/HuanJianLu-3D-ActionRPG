using UnityEngine;

namespace MyGame.Quest
{
    /// <summary>
    /// 任务运行时状态：一条"已接取任务"的实例
    ///
    /// 与 QuestDataSO 的分工（架构关键）：
    /// - QuestDataSO：静态配置，多个存档/多次游戏共用同一份，运行时只读
    /// - QuestState ：动态进度，每次接取都会新建一个实例，游戏结束后丢弃
    ///
    /// 状态流转由 QuestManager 驱动，外部只读，避免多处代码各改一半导致状态不一致
    /// </summary>
    public class QuestState
    {
        /// <summary>每个目标当前的累计进度（下标与 QuestDataSO.Objectives 一一对应）</summary>
        private readonly int[] m_objectiveProgress;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>任务配置资产</summary>
        public QuestDataSO Data { get; }

        /// <summary>任务 ID</summary>
        public string Id => Data != null ? Data.Id : string.Empty;

        /// <summary>当前状态</summary>
        public EQuestStatus Status { get; private set; } = EQuestStatus.InProgress;

        /// <summary>目标数量</summary>
        public int ObjectiveCount => m_objectiveProgress.Length;

        // ====================================================================
        // 构造
        // ====================================================================

        /// <summary>
        /// 构造一个"进行中"的任务实例，进度全部归零
        /// </summary>
        public QuestState(QuestDataSO data)
        {
            Data = data;

            // 目标数量以配置为准；Data 为空时退化为空数组，后续所有查询都会安全返回
            int count = data != null && data.Objectives != null ? data.Objectives.Count : 0;
            m_objectiveProgress = new int[count];
        }

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>取指定目标已累计的进度（越界返回 0）</summary>
        public int GetProgress(int index)
        {
            bool isValid = index >= 0 && index < m_objectiveProgress.Length;
            return isValid ? m_objectiveProgress[index] : 0;
        }

        /// <summary>取指定目标的需求数量（越界返回 0）</summary>
        public int GetRequiredAmount(int index)
        {
            bool isValid = index >= 0 && index < m_objectiveProgress.Length;
            return isValid ? Data.Objectives[index].RequiredAmount : 0;
        }

        /// <summary>指定目标是否已达成</summary>
        public bool IsObjectiveDone(int index)
        {
            bool isValid = index >= 0 && index < m_objectiveProgress.Length;
            return isValid && m_objectiveProgress[index] >= GetRequiredAmount(index);
        }

        /// <summary>是否所有目标都已达成</summary>
        public bool IsAllObjectivesDone
        {
            get
            {
                // 无目标的任务视为直接达成（便于做纯剧情类任务）
                if (m_objectiveProgress.Length == 0)
                {
                    return true;
                }

                for (int i = 0; i < m_objectiveProgress.Length; i++)
                {
                    if (!IsObjectiveDone(i))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        // ====================================================================
        // 状态变更（仅供 QuestManager 调用）
        // ====================================================================

        /// <summary>
        /// 给指定目标累加进度（自动裁剪到需求上限，避免进度溢出）
        /// </summary>
        /// <param name="index">目标下标</param>
        /// <param name="amount">累加数量（必须为正）</param>
        /// <returns>本次累加是否让该目标"刚好达成"（用于只在该时点抛事件）</returns>
        public bool TryAddProgress(int index, int amount)
        {
            bool isValid = index >= 0 && index < m_objectiveProgress.Length;

            if (!isValid || amount <= 0 || IsObjectiveDone(index))
            {
                return false;
            }

            int required = GetRequiredAmount(index);
            m_objectiveProgress[index] = Mathf.Min(required, m_objectiveProgress[index] + amount);

            return m_objectiveProgress[index] >= required;
        }

        /// <summary>标记为"待交付"（所有目标已达成）</summary>
        public void MarkReadyToTurnIn()
        {
            Status = EQuestStatus.ReadyToTurnIn;
        }

        /// <summary>标记为"已完成"（奖励已发放）</summary>
        public void MarkCompleted()
        {
            Status = EQuestStatus.Completed;
        }
    }
}
