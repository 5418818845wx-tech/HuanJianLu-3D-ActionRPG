namespace MyGame.UI
{
    /// <summary>
    /// UI 面板唯一标识枚举
    ///
    /// 用途：UIManager 用此枚举作为面板注册字典的 Key，外部调用 ShowPanel/HidePanel/TogglePanel 时只传枚举值
    /// 扩展：新增面板时只需在此枚举追加一项，然后在 UIManager Inspector 中挂载对应 UIPanel 引用
    ///
    /// 命名约定：使用 PascalCase，不添加 Enum/Type 等后缀
    /// </summary>
    public enum EUIPanelID
    {
        // 战斗 HUD（常驻显示，玩家血条与敌人血条宿主层）
        CombatHUD,

        // 主菜单（标题界面，开始游戏/继续/退出）
        MainMenu,

        // 暂停菜单（ESC 触发，继续/设置/返回主菜单）
        PauseMenu,

        // 背包面板（物品网格 + 详情侧栏）
        Inventory,

        // 角色属性面板（HP/MP/攻击/防御等属性详情）
        Character,

        // 技能面板（技能列表 + 装备槽 + 升级）
        Skill,

        // 设置面板（音量/画质/键位/语言）
        Settings,

        // 任务日志（主线/支线/已完成）
        QuestLog,

        // 任务追踪（屏幕左上角常驻显示进行中任务与进度）
        QuestTracker,

        // 交互提示（屏幕下方显示"[E] 与 XX 交谈"）
        InteractionPrompt,

        // 对话系统面板（NPC 头像 + 文本 + 选项）
        Dialogue,

        // 商店面板（购买/出售）
        Shop,

        // 战斗结算面板（胜利/失败 + 统计 + 重试/返回主菜单）
        Result,

        // 按键设置面板（从设置面板进入：逐条修改动作按键）
        //
        // ⚠️ 只能追加在末尾，绝不能插到中间：
        // 面板在场景里的 ID 是以**整数**序列化保存的，
        // 中间插入会让后面所有面板的数值整体后移，导致
        // "任务日志被当成按键面板"这种错位，而且编译不报错、很难查
        KeyBindings,

        // 新手引导提示（常驻 HUD：显示当前步骤与进度，可跳过）
        Guide,

        // 快捷按钮栏（常驻 HUD：右上角一排图标，点击直接打开对应面板）
        QuickBar,

        // 获得物品提示（常驻 HUD：右下角弹出"图标 + 名字 ×数量"，几秒后消失）
        PickupToast,

        // 秘境面板（列出所有秘境 + 进入按钮；由秘境入口或快捷键打开）
        Dungeon
    }
}
