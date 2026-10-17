namespace MyGame.Dungeon
{
    /// <summary>
    /// 跨场景传递"当前正在进行的秘境"这一状态
    ///
    /// 为什么需要它：
    /// 玩家在大世界点击秘境入口 → 加载秘境场景；秘境里打完一波 → 结算面板弹出。
    /// 但结算/发奖发生在**另一个场景**里，那里的对象（DungeonManager）是刚出生的，
    /// 根本不知道"玩家是从哪张图、进的哪个秘境、回程该回哪"。
    /// 所以只能先留一份状态，等秘境场景就绪后再消费。
    ///
    /// 用静态字段是安全的：Unity 切场景不会重置静态区（同一个进程内），
    /// 这正是"跨场景传几个值"最省事的做法——不必为一个引用去建 ScriptableObject 或在场景间找物体
    /// （与 GameStartIntent 同一套路）
    ///
    /// ⚠️ 状态必须被**清理掉**：
    /// 回到大世界时若还挂着"正在秘境中"，之后大世界的任何一场战斗都会被误判成秘境结算，
    /// 结算面板会显示秘境文案、还试图再发一次奖。所以 DungeonManager.ReturnToOverworld 里会 Clear
    /// </summary>
    public static class DungeonRunContext
    {
        /// <summary>当前正在进行的秘境配置（null 表示不在秘境中）</summary>
        public static DungeonDefinitionSO ActiveDungeon { get; private set; }

        /// <summary>进入秘境前所在的场景名（回程用；为空时调用方回退到主菜单场景）</summary>
        public static string ReturnSceneName { get; private set; } = string.Empty;

        /// <summary>本次是否已经发过奖（防止重复结算）</summary>
        public static bool HasCompleted { get; private set; }

        /// <summary>当前是否处于秘境中</summary>
        public static bool IsInDungeon => ActiveDungeon != null;

        /// <summary>
        /// 开始一次秘境：由入口或面板在加载秘境场景**之前**调用
        /// （必须在切场景前记下回程场景名，切过去之后就取不到"上一层"了）
        /// </summary>
        /// <param name="dungeon">要进入的秘境配置</param>
        /// <param name="returnScene">进入前所在场景名，作为通关后的回程目标</param>
        public static void BeginRun(DungeonDefinitionSO dungeon, string returnScene)
        {
            ActiveDungeon = dungeon;
            ReturnSceneName = returnScene;

            // 每次进入都是新的一轮，重置发奖标记
            HasCompleted = false;
        }

        /// <summary>标记本次秘境的奖励已发放（发奖前先查 HasCompleted，避免重复发）</summary>
        public static void MarkCompleted()
        {
            HasCompleted = true;
        }

        /// <summary>清空秘境状态（回到大世界时调用）</summary>
        public static void Clear()
        {
            ActiveDungeon = null;
            ReturnSceneName = string.Empty;
            HasCompleted = false;
        }
    }
}
