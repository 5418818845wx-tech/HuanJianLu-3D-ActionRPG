using UnityEngine;

namespace MyGame.Interaction
{
    /// <summary>
    /// 可交互物接口：一切"靠近后按键交互"的东西都实现它
    /// （NPC 对话、商店、宝箱、传送点等，后续可直接复用）
    ///
    /// 分工（关键）：
    /// - 本接口只描述"能不能交互、提示什么、交互时做什么"
    /// - 谁被检测、按哪个键、什么时候屏蔽，全部由 InteractionDetector 决定
    /// 这样新增一种可交互物时不需要改检测逻辑
    /// </summary>
    public interface IInteractable
    {
        /// <summary>交互提示文本（显示在屏幕提示条上，如"与 药师阿禾 交谈"）</summary>
        string InteractionPrompt { get; }

        /// <summary>当前是否可交互（false 时检测器会忽略它，提示条也不会出现）</summary>
        bool CanInteract { get; }

        /// <summary>
        /// 执行交互
        /// </summary>
        /// <param name="interactor">发起交互的对象（通常是玩家，可用于读取玩家身上的组件）</param>
        void Interact(GameObject interactor);
    }
}
