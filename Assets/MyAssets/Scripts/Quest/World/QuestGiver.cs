using MyGame.Interaction;
using UnityEngine;
using Yarn.Unity;

namespace MyGame.Quest
{
    /// <summary>
    /// 任务发布者：挂在 NPC 身上，负责"被玩家交互 → 启动对话剧本"
    ///
    /// 定位（Yarn 化之后的职责边界）：
    /// 1. 实现 IInteractable，被 InteractionDetector 检测到并响应按键
    /// 2. 上报"对话"类任务目标进度
    /// 3. 启动本 NPC 对应的 Yarn 节点
    ///
    /// 这里**不再有任何对话内容与流程判断**：
    /// - 说什么、怎么说、什么条件下说哪句，全部写在 Assets/MyAssets/Dialogue/*.yarn 里
    /// - 接取/交付任务由剧本里的 <<accept_quest>> / <<turnin_quest>> 命令驱动（见 QuestYarnBridge）
    /// 所以策划改剧情不需要碰这个脚本
    ///
    /// 挂载位置：NPC 根物体（需要 Collider 供交互检测）
    /// </summary>
    [DisallowMultipleComponent]
    public class QuestGiver : MonoBehaviour, IInteractable
    {
        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("NPC 标识")]
        [Tooltip("NPC 唯一 ID：任务目标里的 Talk 类型按此 ID 匹配（如 Npc_Herbalist）")]
        [SerializeField] private string m_npcId = "Npc_Unnamed";

        [Tooltip("NPC 名字（仅供 Inspector 辨认，对话里的名字由剧本逐句指定）")]
        [SerializeField] private string m_displayName = "村民";

        [Header("对话剧本")]
        [Tooltip("交互时启动的 Yarn 节点名。留空则用 NPC ID（约定：一个 NPC 一个同名节点）")]
        [SerializeField] private string m_yarnNode = string.Empty;

        [Tooltip("Yarn 对话驱动器。留空则在场景中自动查找")]
        [SerializeField] private DialogueRunner m_dialogueRunner;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>NPC 唯一 ID</summary>
        public string NpcId => m_npcId;

        // ====================================================================
        // IInteractable 实现
        // ====================================================================

        /// <summary>交互提示文本（由交互提示 UI 显示为「[G] 与 XX 交谈」）</summary>
        public string InteractionPrompt => $"与 {m_displayName} 交谈";

        /// <summary>对话进行中时不可再次交互（提示条会自动消失）</summary>
        public bool CanInteract => !IsDialogueActive;

        /// <summary>执行交互：上报对话目标进度 → 启动 Yarn 剧本</summary>
        public void Interact(GameObject interactor)
        {
            if (IsDialogueActive || !TryResolveDialogueRunner())
            {
                return;
            }

            // 上报"对话"目标进度：让"与某 NPC 对话"类任务能够推进
            // （注意：这里不能用返回值判断成败，任务能否推进由 QuestManager 决定）
            QuestManager questManager = QuestManager.Instance;
            if (questManager == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 QuestManager，对话可以继续，但任务进度不会推进", this);
            }
            else
            {
                questManager.ReportProgress(EQuestObjectiveType.Talk, m_npcId, 1);
            }

            // 播哪一段完全由剧本决定：同名节点内部会根据任务状态自己分支
            m_dialogueRunner.StartDialogue(ResolveNodeName());
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>当前是否有一段对话正在跑（Yarn 的 Runner 是唯一裁判，避免自己维护状态出错）</summary>
        private bool IsDialogueActive => m_dialogueRunner != null && m_dialogueRunner.IsDialogueRunning;

        /// <summary>起始节点名：没单独配就用 NPC ID</summary>
        private string ResolveNodeName()
        {
            return string.IsNullOrEmpty(m_yarnNode) ? m_npcId : m_yarnNode;
        }

        /// <summary>取对话驱动器（允许场景里只有一个 Runner，省去逐个 NPC 拖引用）</summary>
        private bool TryResolveDialogueRunner()
        {
            if (m_dialogueRunner != null)
            {
                return true;
            }

            m_dialogueRunner = FindAnyObjectByType<DialogueRunner>();

            if (m_dialogueRunner == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 DialogueRunner，无法开始对话", this);
                return false;
            }

            return true;
        }
    }
}
