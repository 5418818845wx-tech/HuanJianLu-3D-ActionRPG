namespace MyGame.Audio
{
    /// <summary>
    /// 全部音效的枚举 ID
    ///
    /// 为什么用枚举而不是字符串：
    /// 字符串拼错不会编译报错，只会在运行时静默没声音——这是最难查的一类问题。
    /// 枚举写错立刻编译失败，而且 IDE 能自动补全。
    ///
    /// 命名与 Assets/MyAssets/Audio/SFX 下的文件名一一对应：
    /// 文件名 = "SFX_" + 枚举名 + 扩展名。
    /// 例如枚举 Swing_Light_01 → 文件 SFX_Swing_Light_01.wav
    /// 该对应关系由编辑器工具「Tools/音频/自动填充音频库」自动建立，不用手拖
    /// </summary>
    public enum ESfx
    {
        // ---- 玩家战斗 ----
        /// <summary>普攻/连招挥空（4 段）</summary>
        Swing_Light_01, Swing_Light_02, Swing_Light_03, Swing_Light_04,

        /// <summary>命中肉体</summary>
        Hit_Flesh_01, Hit_Flesh_02,

        /// <summary>命中金属/格挡</summary>
        Hit_Metal_01,

        /// <summary>重击命中</summary>
        Heavy_Impact,

        /// <summary>蓄力起手</summary>
        Charge_Start,

        /// <summary>蓄力循环（AudioSource 需循环播放）</summary>
        Charge_Loop,

        /// <summary>重击释放</summary>
        Charge_Release,

        /// <summary>技能起手 ×4（跑攻一/跑攻二/上挑/弹反）</summary>
        Skill_Cast_01, Skill_Cast_02, Skill_Cast_03, Skill_Cast_04,

        /// <summary>技能命中 ×4</summary>
        Skill_Hit_01, Skill_Hit_02, Skill_Hit_03, Skill_Hit_04,

        /// <summary>弹反成功</summary>
        Parry_Success,

        /// <summary>切换武器</summary>
        Weapon_Switch,

        /// <summary>玩家受伤</summary>
        Player_Hurt_01, Player_Hurt_02,

        /// <summary>玩家死亡</summary>
        Player_Death,

        /// <summary>升级</summary>
        LevelUp,

        // ---- 敌人 ----
        /// <summary>敌人挥砍</summary>
        Enemy_Swing_01, Enemy_Swing_02,

        /// <summary>敌人受伤</summary>
        Enemy_Hurt_01, Enemy_Hurt_02,

        /// <summary>敌人死亡</summary>
        Enemy_Death,

        // ---- 移动 ----
        /// <summary>翻滚闪避</summary>
        Dodge,

        /// <summary>起跳</summary>
        Jump,

        /// <summary>落地</summary>
        Land,

        /// <summary>脚步（4 个变体轮换，避免重复感）</summary>
        Footstep_01, Footstep_02, Footstep_03, Footstep_04,

        // ---- 世界交互 ----
        /// <summary>拾取物品</summary>
        Pickup_Item,

        /// <summary>拾取金币</summary>
        Pickup_Coin,

        /// <summary>掉落物落地</summary>
        Drop_Land,

        /// <summary>采集（拔草）</summary>
        Harvest,

        /// <summary>交互提示出现</summary>
        Interact_Prompt,

        // ---- UI 通用 ----
        /// <summary>按钮点击</summary>
        UI_Click,

        /// <summary>鼠标悬停</summary>
        UI_Hover,

        /// <summary>不可用/错误</summary>
        UI_Disabled,

        /// <summary>面板打开</summary>
        UI_Panel_Open,

        /// <summary>面板关闭</summary>
        UI_Panel_Close,

        // ---- 背包 / 装备 ----
        /// <summary>背包选中</summary>
        Inv_Select,

        /// <summary>装备武器</summary>
        Inv_Equip,

        /// <summary>使用消耗品（喝药）</summary>
        Inv_UsePotion,

        // ---- 商店 ----
        /// <summary>购买成功</summary>
        Shop_Buy,

        /// <summary>卖出成功</summary>
        Shop_Sell,

        /// <summary>交易失败（金币不足等）</summary>
        Shop_Fail,

        // ---- 任务 ----
        /// <summary>接取任务</summary>
        Quest_Accept,

        /// <summary>任务目标推进</summary>
        Quest_Progress,

        /// <summary>任务完成</summary>
        Quest_Complete,

        // ---- 对话 ----
        /// <summary>对话推进（换行）</summary>
        Dialogue_Next,

        /// <summary>对话选项出现</summary>
        Dialogue_Choice,
    }

    /// <summary>
    /// 背景音乐轨道 ID
    ///
    /// Field 是**播放列表**（当前有 Field1 / Field2 两首），
    /// 切到"野外"时会随机挑一首，并避免与上一首重复
    /// </summary>
    public enum EMusic
    {
        /// <summary>无（静音）</summary>
        None,

        /// <summary>主菜单</summary>
        MainMenu,

        /// <summary>野外探索（播放列表，随机不重复）</summary>
        Field,

        /// <summary>战斗</summary>
        Combat,

        /// <summary>结算·胜利（不循环）</summary>
        ResultWin,

        /// <summary>结算·失败（不循环）</summary>
        ResultLose,
    }
}
