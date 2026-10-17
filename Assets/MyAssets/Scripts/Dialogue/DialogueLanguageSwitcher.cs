using System.Collections.Generic;
using MyGame.UI;
using UnityEngine;
using Yarn.Unity;

namespace MyGame.Dialogue
{
    /// <summary>
    /// 对话语言切换：改掉 Yarn 行提供者的 LocaleCode，之后的台词就会从对应语言的
    /// Localization 里取。
    ///
    /// 分工（重要）：
    /// - **文案本身**由 `.yarnproject` 的 localisation 配置 + 各语言的 CSV 提供，本类不存任何文字
    /// - 本类只负责「切到哪一种语言」这一件事
    /// 所以加一种语言（比如 ja）不需要改这里，只需要加一行配置 + 一个 CSV 文件
    ///
    /// 为什么改 LocaleCode 就够了：
    /// BuiltinLocalisedLineProvider 是**逐行**去查 Localization 的，
    /// 所以切换后下一句就生效，不需要重载剧本、也不需要重开对话
    ///
    /// 对外接口：实现 ILanguageSelector，供设置面板驱动（面板不认识对话系统，只认接口）
    /// 挂载位置：与 DialogueRunner 同一物体（或手动把 Runner 拖到 m_dialogueRunner）
    /// </summary>
    [DisallowMultipleComponent]
    public class DialogueLanguageSwitcher : MonoBehaviour, ILanguageSelector
    {
        /// <summary>语言偏好的 PlayerPrefs 键（与设置面板的键名风格保持一致）</summary>
        private const string k_languagePrefsKey = "settings.language";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Tooltip("对话驱动器。留空则在场景中自动查找")]
        [SerializeField] private DialogueRunner m_dialogueRunner;

        [Tooltip("支持的语言代码：必须与 .yarnproject 的 localisation 键、各 CSV 里的 language 列一致")]
        [SerializeField] private string[] m_languages = { "zh", "en" };

        [Tooltip("语言的自称（顺序对应上面的代码），显示在设置面板的下拉框里")]
        [SerializeField] private string[] m_languageDisplayNames = { "中文", "English" };

        [Tooltip("默认语言：仅在玩家从未选过语言时使用")]
        [SerializeField] private string m_defaultLanguage = "zh";

        [Header("测试快捷键")]
        [Tooltip("打开后可按快捷键轮换语言，方便验证；正式发行应关掉")]
        [SerializeField] private bool m_enableHotkey = true;

        [Tooltip("切换语言的快捷键")]
        [SerializeField] private KeyCode m_hotkey = KeyCode.F2;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>当前语言代码（Awake 时从 PlayerPrefs 恢复）</summary>
        private string m_currentLanguage;

        // ====================================================================
        // ILanguageSelector 实现
        // ====================================================================

        /// <inheritdoc/>
        public IReadOnlyList<string> LanguageCodes => m_languages;

        /// <inheritdoc/>
        public IReadOnlyList<string> LanguageDisplayNames => m_languageDisplayNames;

        /// <inheritdoc/>
        public string CurrentLanguage => m_currentLanguage;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (m_dialogueRunner == null)
            {
                m_dialogueRunner = FindAnyObjectByType<DialogueRunner>();
            }

            // 玩家上次选的语言优先，其次才用默认值
            m_currentLanguage = PlayerPrefs.GetString(k_languagePrefsKey, m_defaultLanguage);
        }

        private void Start()
        {
            // 开局先应用一次，保证不依赖快捷键也能处在配置的语言上
            ApplyLanguage(m_currentLanguage);
        }

        private void Update()
        {
            if (!m_enableHotkey || m_languages == null || m_languages.Length == 0)
            {
                return;
            }

            if (Input.GetKeyDown(m_hotkey))
            {
                SwitchToNextLanguage();
            }
        }

        // ====================================================================
        // 对外接口
        // ====================================================================

        /// <summary>切到指定语言：之后显示的台词都用该语言（已在屏上的那句不变）</summary>
        public void SetLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                return;
            }

            m_currentLanguage = languageCode;

            // 只写进 PlayerPrefs 的内存副本；落盘由设置面板 OnExit 或退出游戏时统一执行
            PlayerPrefs.SetString(k_languagePrefsKey, languageCode);

            ApplyLanguage(languageCode);
        }

        /// <summary>轮换到下一种语言（快捷键用；设置面板也可以直接调 SetLanguage）</summary>
        public void SwitchToNextLanguage()
        {
            if (m_languages == null || m_languages.Length == 0)
            {
                return;
            }

            int currentIndex = System.Array.IndexOf(m_languages, m_currentLanguage);
            int nextIndex = (currentIndex + 1) % m_languages.Length;
            SetLanguage(m_languages[nextIndex]);
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>把语言写进行提供者</summary>
        private void ApplyLanguage(string languageCode)
        {
            if (m_dialogueRunner == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 DialogueRunner，语言无法应用", this);
                return;
            }

            // LineProvider 是「取的时候按需创建」的，所以这里取一次就等于保证它存在
            LineProviderBehaviour provider = m_dialogueRunner.LineProvider as LineProviderBehaviour;
            if (provider == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 取不到行提供者，语言无法应用", this);
                return;
            }

            provider.LocaleCode = languageCode;
            Debug.Log($"[{GetType().Name}] 对话语言已切换为「{languageCode}」", this);
        }
    }
}
