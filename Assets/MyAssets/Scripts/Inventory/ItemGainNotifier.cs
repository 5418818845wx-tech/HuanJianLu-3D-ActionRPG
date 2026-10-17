using System;

namespace MyGame.Inventory
{
    /// <summary>
    /// 「玩家获得了物品」的全局通知器（右下角获得提示的数据源）
    ///
    /// 为什么用静态事件而不是让 UI 去轮询背包：
    /// 获得提示是"一次性的事件"（获得了才能弹），不是"持续的状态"，
    /// 而背包内容变化（ItemsChanged）分不清"增加"还是"减少"、
    /// 也分不清是玩家捡的还是读档恢复的。这里由**真正的获得方**主动上报，
    /// 语义最准，UI 也不用猜。
    ///
    /// 与 EnemyAI.AnyEnemyDied 同一套路：生产者不依赖消费者，
    /// 提示面板不挂上去，游戏照常运行
    /// </summary>
    public static class ItemGainNotifier
    {
        /// <summary>玩家获得物品：参数为物品 ID 与实际入包数量</summary>
        public static event Action<string, int> ItemGained;

        /// <summary>上报一次获得。数量 &lt;= 0 直接忽略（背包满等情况下不该弹提示）</summary>
        public static void Notify(string itemId, int amount)
        {
            if (string.IsNullOrEmpty(itemId) || amount <= 0)
            {
                return;
            }

            ItemGained?.Invoke(itemId, amount);
        }
    }
}
