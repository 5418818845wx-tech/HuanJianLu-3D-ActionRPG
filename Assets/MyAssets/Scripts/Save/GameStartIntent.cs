namespace MyGame.Save
{
    /// <summary>
    /// 跨场景传递"这次进游戏场景要不要读档"的意图
    ///
    /// 为什么需要它：
    /// 「继续游戏」是在**主菜单场景**点的，但读档只能由游戏场景里的 SaveManager 执行
    /// （它要驱动任务 / 背包 / 装备）。主菜单拿不到那个还没出生的对象，
    /// 所以只能先留一个意图，等场景切过去、SaveManager 就绪后再消费
    ///
    /// 用静态字段是安全的：Unity 切场景不会重置静态区（同一个进程内），
    /// 这正是"跨场景传一个值"最省事的做法——不必为了一个 bool 去建 ScriptableObject 或在场景间找物体
    ///
    /// ⚠️ 意图必须**消费掉**（读完就清）：
    /// 否则玩家这次点了「继续游戏」、下次点「开始新游戏」时，旧意图还挂着，
    /// 新游戏会莫名其妙地把存档读回来
    /// </summary>
    public static class GameStartIntent
    {
        /// <summary>当前是否挂着"进游戏场景后读档"的意图</summary>
        public static bool LoadSaveRequested { get; private set; }

        /// <summary>主菜单点「继续游戏」时调用</summary>
        public static void RequestLoadSave()
        {
            LoadSaveRequested = true;
        }

        /// <summary>主菜单点「开始新游戏」时调用：明确清掉意图，保证是全新一局</summary>
        public static void RequestNewGame()
        {
            LoadSaveRequested = false;
        }

        /// <summary>
        /// 取出并清空意图（由 SaveManager 在进入游戏场景时调用一次）。
        /// "取出即清空"合在一起，避免调用方忘记复位
        /// </summary>
        public static bool ConsumeLoadSaveRequest()
        {
            bool requested = LoadSaveRequested;
            LoadSaveRequested = false;
            return requested;
        }
    }
}
