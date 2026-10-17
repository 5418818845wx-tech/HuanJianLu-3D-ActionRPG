using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyGame.KeyBindings
{
    /// <summary>
    /// 可自定义的动作按键。
    ///
    /// 只包含"动作键"：移动的 WASD 走的是 Unity Input Manager 的轴（Horizontal/Vertical），
    /// 不属于本系统管理；改移动键要到 Project Settings → Input Manager 里改，
    /// 本系统管的是"按一下触发一件事"的那些键
    /// </summary>
    public enum EGameKey
    {
        /// <summary>攻击 / 蓄力（按住）</summary>
        Attack,

        /// <summary>技能 1（Q 槽）</summary>
        Skill_Q,

        /// <summary>技能 2（E 槽）</summary>
        Skill_E,

        /// <summary>技能 3（R 槽）</summary>
        Skill_R,

        /// <summary>技能 4（F 槽，大招）</summary>
        Skill_F,

        /// <summary>跳跃</summary>
        Jump,

        /// <summary>闪避</summary>
        Dodge,

        /// <summary>下蹲</summary>
        Crouch,

        /// <summary>交互（对话 / 采集，默认 G）</summary>
        Interact,

        /// <summary>切换武器</summary>
        SwitchWeapon,

        /// <summary>暂停菜单</summary>
        Pause,

        /// <summary>背包</summary>
        Inventory,

        /// <summary>技能面板</summary>
        SkillPanel,

        /// <summary>角色属性</summary>
        Character,

        /// <summary>任务日志</summary>
        QuestLog,
    }

    /// <summary>
    /// 键位管理：全项目唯一的按键查询入口，负责自定义键位的读写与持久化
    ///
    /// 为什么要集中管理：
    /// 原来 KeyCode 硬编码在 5 个脚本里（战斗、交互、UI、移动），
    /// 想做"改键"就必须改每一个文件，而且漏一个就会出现"这个键改了那个没改"的诡异现象。
    /// 现在所有脚本统一问本类要键位，改键只需要改这一处数据
    ///
    /// 持久化：每个动作一个 PlayerPrefs 键（keybind.动作名），存 KeyCode 的整数。
    /// 查询前会 lazy 加载一次，避免依赖任何 MonoBehaviour 的初始化顺序
    ///
    /// 冲突处理：设置某个动作的键时，如果该键已被别的动作占用，两者**交换**。
    /// 交换比"拒绝设置"更符合直觉——玩家想把跳跃换成闪避的键，交换后双方都还能用
    /// </summary>
    public static class KeyBindingManager
    {
        /// <summary>PlayerPrefs 键前缀</summary>
        private const string k_prefsPrefix = "keybind.";

        /// <summary>默认键位（与引入本系统之前的硬编码保持一致，老玩家不会突然不会玩）</summary>
        private static readonly Dictionary<EGameKey, KeyCode> s_defaults = new()
        {
            { EGameKey.Attack, KeyCode.Mouse0 },
            { EGameKey.Skill_Q, KeyCode.Q },
            { EGameKey.Skill_E, KeyCode.E },
            { EGameKey.Skill_R, KeyCode.R },
            { EGameKey.Skill_F, KeyCode.F },
            { EGameKey.Jump, KeyCode.Space },
            { EGameKey.Dodge, KeyCode.LeftShift },
            { EGameKey.Crouch, KeyCode.C },
            { EGameKey.Interact, KeyCode.G },
            { EGameKey.SwitchWeapon, KeyCode.Tab },
            { EGameKey.Pause, KeyCode.Escape },
            { EGameKey.Inventory, KeyCode.I },
            { EGameKey.SkillPanel, KeyCode.K },
            { EGameKey.Character, KeyCode.P },
            { EGameKey.QuestLog, KeyCode.L },
        };

        /// <summary>全部动作（顺序即 UI 里的显示顺序）</summary>
        private static readonly EGameKey[] s_allKeys =
        {
            EGameKey.Attack,
            EGameKey.Skill_Q, EGameKey.Skill_E, EGameKey.Skill_R, EGameKey.Skill_F,
            EGameKey.Jump, EGameKey.Dodge, EGameKey.Crouch,
            EGameKey.Interact, EGameKey.SwitchWeapon,
            EGameKey.Pause, EGameKey.Inventory, EGameKey.SkillPanel, EGameKey.Character, EGameKey.QuestLog,
        };

        /// <summary>当前键位表（首次访问时从 PlayerPrefs 载入）</summary>
        private static Dictionary<EGameKey, KeyCode> s_current;

        /// <summary>是否已从 PlayerPrefs 载入过</summary>
        private static bool s_loaded;

        /// <summary>键位发生变化（UI 订阅它刷新显示）</summary>
        public static event Action Changed;

        /// <summary>
        /// 是否正在"等待玩家按下一个键"（改键界面用）。
        /// 改键期间必须屏蔽其它按键逻辑：否则玩家想把"背包"改成 K，
        /// 按下 K 的同一帧还会顺便把技能面板打开
        /// </summary>
        public static bool IsCapturing { get; set; }

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>全部动作（UI 按此顺序生成列表）</summary>
        public static IReadOnlyList<EGameKey> AllKeys => s_allKeys;

        /// <summary>取某个动作当前的键位</summary>
        public static KeyCode Get(EGameKey key)
        {
            EnsureLoaded();
            return s_current.TryGetValue(key, out KeyCode code) ? code : s_defaults[key];
        }

        /// <summary>取某个动作的默认键位（UI 上标注"默认：X"用）</summary>
        public static KeyCode GetDefault(EGameKey key)
        {
            return s_defaults.TryGetValue(key, out KeyCode code) ? code : KeyCode.None;
        }

        /// <summary>是否被按下的这一帧</summary>
        public static bool IsDown(EGameKey key)
        {
            return Input.GetKeyDown(Get(key));
        }

        /// <summary>是否正被按住</summary>
        public static bool IsHeld(EGameKey key)
        {
            return Input.GetKey(Get(key));
        }

        /// <summary>是否在这一帧被松开</summary>
        public static bool IsUp(EGameKey key)
        {
            return Input.GetKeyUp(Get(key));
        }

        // ====================================================================
        // 修改
        // ====================================================================

        /// <summary>
        /// 设置某个动作的键位。
        /// 若该键已被别的动作占用则**两者交换**，避免出现两个动作共用一个键
        /// </summary>
        /// <param name="key">要改的动作</param>
        /// <param name="code">新的按键</param>
        public static void Set(EGameKey key, KeyCode code)
        {
            EnsureLoaded();

            if (code == KeyCode.None)
            {
                return;
            }

            KeyCode previous = Get(key);

            // 找出占用这个键的其它动作（如果有）
            EGameKey? conflict = null;
            foreach (KeyValuePair<EGameKey, KeyCode> pair in s_current)
            {
                if (pair.Key != key && pair.Value == code)
                {
                    conflict = pair.Key;
                    break;
                }
            }

            s_current[key] = code;
            PlayerPrefs.SetInt(k_prefsPrefix + key, (int)code);

            if (conflict.HasValue)
            {
                // 交换：把对方改成这个动作原来的键，双方都还能用
                s_current[conflict.Value] = previous;
                PlayerPrefs.SetInt(k_prefsPrefix + conflict.Value, (int)previous);

                Debug.Log($"[{nameof(KeyBindingManager)}] 「{GetDisplayName(key)}」与「{GetDisplayName(conflict.Value)}」交换了按键", null);
            }

            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>把所有动作恢复成默认键位</summary>
        public static void ResetToDefaults()
        {
            EnsureLoaded();

            foreach (KeyValuePair<EGameKey, KeyCode> pair in s_defaults)
            {
                s_current[pair.Key] = pair.Value;
                PlayerPrefs.SetInt(k_prefsPrefix + pair.Key, (int)pair.Value);
            }

            PlayerPrefs.Save();
            Changed?.Invoke();

            Debug.Log($"[{nameof(KeyBindingManager)}] 已恢复默认键位", null);
        }

        // ====================================================================
        // 显示用文案（UI 用；放这里是为了"动作名/按键名"只有一份定义）
        // ====================================================================

        /// <summary>动作的中文名（UI 列表左侧显示）</summary>
        public static string GetDisplayName(EGameKey key)
        {
            switch (key)
            {
                case EGameKey.Attack: return "攻击 / 蓄力";
                case EGameKey.Skill_Q: return "技能 Q";
                case EGameKey.Skill_E: return "技能 E";
                case EGameKey.Skill_R: return "技能 R";
                case EGameKey.Skill_F: return "技能 F（大招）";
                case EGameKey.Jump: return "跳跃";
                case EGameKey.Dodge: return "闪避";
                case EGameKey.Crouch: return "下蹲";
                case EGameKey.Interact: return "交互 / 采集";
                case EGameKey.SwitchWeapon: return "切换武器";
                case EGameKey.Pause: return "暂停菜单";
                case EGameKey.Inventory: return "背包";
                case EGameKey.SkillPanel: return "技能面板";
                case EGameKey.Character: return "角色属性";
                case EGameKey.QuestLog: return "任务日志";
                default: return key.ToString();
            }
        }

        /// <summary>按键的可读名称（KeyCode 原样显示对玩家不友好）</summary>
        public static string GetKeyLabel(KeyCode code)
        {
            switch (code)
            {
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                case KeyCode.Space: return "空格";
                case KeyCode.LeftShift: return "左 Shift";
                case KeyCode.RightShift: return "右 Shift";
                case KeyCode.LeftControl: return "左 Ctrl";
                case KeyCode.RightControl: return "右 Ctrl";
                case KeyCode.LeftAlt: return "左 Alt";
                case KeyCode.RightAlt: return "右 Alt";
                case KeyCode.Escape: return "Esc";
                case KeyCode.Return: return "回车";
                case KeyCode.Tab: return "Tab";
                case KeyCode.Backspace: return "退格";
                case KeyCode.UpArrow: return "方向键上";
                case KeyCode.DownArrow: return "方向键下";
                case KeyCode.LeftArrow: return "方向键左";
                case KeyCode.RightArrow: return "方向键右";
                case KeyCode.None: return "未设置";
                default: return code.ToString();
            }
        }

        // ====================================================================
        // 内部：载入
        // ====================================================================

        /// <summary>
        /// 从 PlayerPrefs 载入键位（只做一次）。
        /// 用 lazy 载入而不是静态构造：静态构造时机不受控，
        /// 而 PlayerPrefs 在这里访问是安全的（纯数据，不依赖任何对象）
        /// </summary>
        private static void EnsureLoaded()
        {
            if (s_loaded)
            {
                return;
            }

            s_loaded = true;
            s_current = new Dictionary<EGameKey, KeyCode>();

            foreach (KeyValuePair<EGameKey, KeyCode> pair in s_defaults)
            {
                // 没存过就用默认值：老玩家第一次跑新版本时键位保持不变
                int stored = PlayerPrefs.GetInt(k_prefsPrefix + pair.Key, (int)pair.Value);
                s_current[pair.Key] = (KeyCode)stored;
            }
        }
    }
}
