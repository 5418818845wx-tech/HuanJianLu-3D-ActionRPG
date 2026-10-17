namespace MyGame.Combat
{
    /// <summary>
    /// 战斗状态枚举：标识角色当前所处的战斗阶段
    /// 由 Fighter（战斗大脑）统一维护，用于调度各子系统切换
    /// 设计原则：每个状态对应一组可取消动作的窗口规则
    /// </summary>
    public enum ECombatState
    {
        /// <summary>空闲状态：未进行任何攻击动作，可接受所有新输入</summary>
        Idle,

        /// <summary>普攻连招进行中：正在播放某一段普攻动画</summary>
        NormalAttacking,

        /// <summary>蓄力中：玩家长按左键，角色正在积蓄重击能量</summary>
        Charging,

        /// <summary>重击释放中：玩家松开左键后，正在播放重击释放动画</summary>
        ChargeReleasing,

        /// <summary>技能释放中：正在播放某个主动技能动画</summary>
        SkillCasting,

        /// <summary>武器切换中：正在播放武器切换动画，期间锁定其他输入</summary>
        WeaponSwitching,
    }

    /// <summary>
    /// 蓄力等级枚举：双阶段蓄力机制的核心
    /// Level1 = 弱重击（蓄力较短即可达成）
    /// Level2 = 强重击（蓄力更久达成，伤害和范围更大）
    /// </summary>
    public enum EChargeLevel
    {
        /// <summary>未蓄力：松开会取消攻击</summary>
        None,

        /// <summary>一阶蓄力：达到最小蓄力阈值，可释放弱重击</summary>
        Level1,

        /// <summary>二阶蓄力：达到最大蓄力阈值，可释放强重击</summary>
        Level2,
    }

    /// <summary>
    /// 队伍枚举：区分玩家与敌人，命中检测时用于过滤友军伤害
    /// </summary>
    public enum ETeam
    {
        Player,
        Enemy,
    }

    /// <summary>
    /// 攻击类型枚举：区分普攻/重击/技能，用于伤害计算与命中反馈差异化
    /// </summary>
    public enum EAttackType
    {
        /// <summary>普攻：4段连招中的任意一段</summary>
        NormalAttack,

        /// <summary>重击：蓄力后释放的强力攻击</summary>
        ChargeAttack,

        /// <summary>技能：通过技能槽释放的特殊攻击</summary>
        SkillAttack,
    }

    /// <summary>
    /// 技能槽位枚举：标识4个技能键位（Q/E/R/F）
    /// Q = 轻技能 / E = 重技能 / R = 爆发技能 / F = 大招
    /// 使用枚举避免魔法数字，便于扩展
    /// </summary>
    public enum ESkillSlot
    {
        /// <summary>Q 键位：轻量级技能，CD 较短</summary>
        Slot_Q = 0,

        /// <summary>E 键位：重量级技能，伤害较高</summary>
        Slot_E = 1,

        /// <summary>R 键位：爆发技能，伤害高 CD 长</summary>
        Slot_R = 2,

        /// <summary>F 键位：终极技能，需要能量积累</summary>
        Slot_F = 3,
    }

    /// <summary>
    /// 武器槽位枚举：双武器切换系统的索引
    /// 单角色最多携带两把武器，按 Tab 切换
    /// </summary>
    public enum EWeaponIndex
    {
        /// <summary>武器1：主武器（默认装备）</summary>
        Weapon1 = 0,

        /// <summary>武器2：副武器</summary>
        Weapon2 = 1,
    }
}
