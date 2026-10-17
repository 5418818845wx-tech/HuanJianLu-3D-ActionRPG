using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 技能数据 ScriptableObject：定义单个主动技能的所有配置
    /// 设计原则：数据驱动，技能逻辑由 SkillSystem 统一调度，配置由本资产提供
    /// 创建方式：在 Project 窗口右键 → Create → 游戏数据 → 技能（Skill）
    /// </summary>
    [CreateAssetMenu(fileName = "SkillData", menuName = "游戏数据/技能（Skill）", order = 0)]
    public class SkillDataSO : ScriptableObject
    {
        [Header("基础信息")]
        [Tooltip("技能唯一 ID：技能升级按此值记录等级，也是存档里的键。" +
                 "留空则退化为资产名（与 ItemDataSO 同样处理）")]
        [SerializeField] private string m_skillId = "";

        [Tooltip("技能名称（仅用于Inspector识别）")]
        [SerializeField] private string m_skillName = "New Skill";

        [Tooltip("技能图标（技能面板的卡片显示）。留空则只显示文字")]
        [SerializeField] private Sprite m_icon;

        [Tooltip("技能槽位：Q/E/R/F 四选一")]
        [SerializeField] private ESkillSlot m_slot = ESkillSlot.Slot_Q;

        [Header("数值配置")]
        [Tooltip("技能冷却时间（秒）")]
        [SerializeField] private float m_cooldown = 5f;

        [Tooltip("伤害倍率（相对攻击力）")]
        [SerializeField] private float m_damageMultiplier = 2.5f;

        [Tooltip("命中检测半径（覆盖 HitboxController 默认值，0 表示用默认）")]
        [SerializeField] private float m_hitRadiusOverride = 0f;

        [Header("动画配置")]
        [Tooltip("Animator Trigger 参数名（用于触发技能动画状态）")]
        [SerializeField] private string m_animTriggerParam = "Skill1";

        [Tooltip("是否需要能量积累（仅 F 槽位大招需要）")]
        [SerializeField] private bool m_requiresEnergy = false;

        [Tooltip("大招所需能量值（仅 requiresEnergy=true 时生效）")]
        [SerializeField] private float m_energyCost = 100f;

        [Header("特效配置")]
        [Tooltip("技能释放时播放的特效预制体（无则不播放）")]
        [SerializeField] private GameObject m_vfxPrefab;

        [Tooltip("特效播放位置（角色根节点下的子物体路径，空则用角色自身位置）")]
        [SerializeField] private string m_vfxBonePath = "";

        [Tooltip("特效缩放（1 = 原始大小）。注意：Hovl 这套素材是按约 10 倍尺寸制作的，" +
                 "直接挂到 1.7 米高的角色上会过大，通常需要填 0.1 ~ 0.4")]
        [SerializeField] private float m_vfxScale = 1f;

        [Tooltip("特效相对挂载骨骼的位置偏移（骨骼局部坐标）。" +
                 "本工程武器刀身沿骨骼局部 -X 方向延伸、刀柄在原点，" +
                 "把特效从刀柄挪到刀身就填 (-0.7, 0, 0)")]
        [SerializeField] private Vector3 m_vfxLocalOffset = Vector3.zero;

        [Tooltip("特效相对挂载骨骼的旋转偏移（欧拉角）。" +
                 "素材作者的刀光弧朝向可能与本动作的挥砍方向不一致，用这个把弧线转到挥砍平面上")]
        [SerializeField] private Vector3 m_vfxLocalEuler = Vector3.zero;

        [Tooltip("特效播放速度倍率。素材时间轴动辄 1~8 秒，而一次挥砍只有 0.3~0.5 秒，" +
                 "需要加速到 1.5~2.5 才能和挥砍同步；填 1 表示原速")]
        [SerializeField] private float m_vfxPlaySpeed = 1f;

        // ==================== 只读属性（外部访问用） ====================

        /// <summary>技能唯一 ID（为空时退化为资产名，方便早期省略填写）</summary>
        public string Id => string.IsNullOrEmpty(m_skillId) ? name : m_skillId;

        /// <summary>技能名称</summary>
        public string SkillName => m_skillName;

        /// <summary>技能图标（未配置时为 null）</summary>
        public Sprite Icon => m_icon;

        /// <summary>技能槽位</summary>
        public ESkillSlot Slot => m_slot;

        /// <summary>冷却时间（秒）</summary>
        public float Cooldown => m_cooldown;

        /// <summary>伤害倍率</summary>
        public float DamageMultiplier => m_damageMultiplier;

        /// <summary>命中检测半径覆盖（0 表示用 HitboxController 默认值）</summary>
        public float HitRadiusOverride => m_hitRadiusOverride;

        /// <summary>Animator Trigger 参数名</summary>
        public string AnimTriggerParam => m_animTriggerParam;

        /// <summary>是否需要能量积累</summary>
        public bool RequiresEnergy => m_requiresEnergy;

        /// <summary>大招所需能量值</summary>
        public float EnergyCost => m_energyCost;

        /// <summary>技能特效预制体</summary>
        public GameObject VfxPrefab => m_vfxPrefab;

        /// <summary>特效挂载骨骼路径</summary>
        public string VfxBonePath => m_vfxBonePath;

        /// <summary>特效缩放倍率（<=0 视为 1）</summary>
        public float VfxScale => m_vfxScale <= 0f ? 1f : m_vfxScale;

        /// <summary>特效位置偏移（骨骼局部坐标）</summary>
        public Vector3 VfxLocalOffset => m_vfxLocalOffset;

        /// <summary>特效旋转偏移（欧拉角）</summary>
        public Vector3 VfxLocalEuler => m_vfxLocalEuler;

        /// <summary>特效播放速度倍率（<=0 视为 1）</summary>
        public float VfxPlaySpeed => m_vfxPlaySpeed <= 0f ? 1f : m_vfxPlaySpeed;
    }
}
