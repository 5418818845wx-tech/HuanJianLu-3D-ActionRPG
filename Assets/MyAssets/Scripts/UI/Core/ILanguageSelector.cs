using System.Collections.Generic;

namespace MyGame.UI
{
    /// <summary>
    /// 「语言选择」的抽象：设置面板只认这个接口，完全不认识对话系统
    ///
    /// 为什么要抽接口：
    /// 实现方 DialogueLanguageSwitcher 在 MyGame.Dialogue 里。若面板直接依赖它，
    /// UI 层就反过来依赖了业务层（而现有方向是 Dialogue → UI）。
    /// 用接口把依赖方向掰回来，顺带解决一个问题：
    /// 只有主菜单的场景（MainScene）里没有对话系统，面板查不到实现方时可以直接隐藏这个控件，
    /// 而不是显示一个点了没反应的死控件
    /// </summary>
    public interface ILanguageSelector
    {
        /// <summary>支持的语言代码（如 zh / en），与 .yarnproject 的 localisation 键一致</summary>
        IReadOnlyList<string> LanguageCodes { get; }

        /// <summary>语言的自称（显示给玩家看，如「中文」「English」），与 LanguageCodes 一一对应</summary>
        IReadOnlyList<string> LanguageDisplayNames { get; }

        /// <summary>当前语言代码</summary>
        string CurrentLanguage { get; }

        /// <summary>切换语言：之后的台词都用该语言</summary>
        void SetLanguage(string languageCode);
    }
}
