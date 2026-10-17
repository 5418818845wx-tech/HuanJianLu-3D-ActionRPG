using System.Collections.Generic;
using MyGame.Audio;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 设置面板（UGUI 版）：音量（主/音乐/音效）/ 画质 / 全屏 / 语言
    ///
    /// 设计原则：面板里的每一项设置都真实作用于 Unity，不是摆设
    /// - 主音量 → AudioListener.volume（全局音量乘数，即使尚未接入音频系统也有效）
    /// - 音乐/音效 → AudioManager 的分类音量（最终音量 = 主音量 × 分类音量）
    /// - 画质   → QualitySettings.SetQualityLevel（切换 Unity 画质等级）
    /// - 全屏   → Screen.fullScreen
    /// - 语言   → 通过 ILanguageSelector 转交给对话系统（面板本身不写死任何语言，
    ///            场景里没有语言系统时整行自动隐藏）
    ///
    /// 持久化：各项设置都写入 PlayerPrefs（键名统一取自 AudioManager 的公开常量，
    /// 避免面板与音频系统各写一份字符串导致对不上），关闭面板时落盘，重启后自动恢复
    ///
    /// 绑定方式：
    /// - 「关闭」按钮是固定控件 → Inspector 拖曳绑定
    /// - 各控件的 onValueChanged 用**代码绑定**（OnEnter 加、OnExit 解）。
    ///   原因：面板每次打开都会重新执行 OnEnter，拖曳绑定是持久化的，会随每次打开叠加监听；
    ///   代码绑定能保证加/解对称，不会累积
    ///
    /// 控件初始化顺序（重要）：
    /// 1. 缓存引用 → 2. 设取值范围 → 3. 套用存档 → 4. 同步控件显示 → 5. 最后才绑回调
    /// 先绑回调再改值会导致"程序化改值"被当成"用户操作"，从而触发多余的设置应用
    /// </summary>
    public class UGUISettingsPanel : UGUIPanel
    {
        // ====================================================================
        // PlayerPrefs 键名（集中定义，避免魔法字符串散落各处）
        // ====================================================================

        /// <summary>主音量键：复用 AudioManager 的公开常量，保证两边读写的是同一个键</summary>
        private const string k_volumePrefsKey = AudioManager.MasterVolumeKey;

        private const string k_qualityPrefsKey = "settings.qualityLevel";
        private const string k_fullscreenPrefsKey = "settings.fullscreen";

        /// <summary>帧率下拉框的选项文本（下标与 s_frameRateValues 一一对应）</summary>
        private static readonly string[] s_frameRateLabels = { "30", "60", "90", "120", "不限" };

        /// <summary>帧率下拉框各选项对应的目标帧率（-1 = 不限帧）</summary>
        private static readonly int[] s_frameRateValues = { 30, 60, 90, 120, FrameRateSetting.Unlimited };

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("控件")]
        [Tooltip("主音量滑条（取值范围由代码设为 0~100，便于显示百分比）")]
        [SerializeField] private Slider m_volumeSlider;

        [Tooltip("音量百分比文本，如「80%」")]
        [SerializeField] private TextMeshProUGUI m_volumeValueText;

        [Tooltip("背景音乐音量滑条（同样 0~100）")]
        [SerializeField] private Slider m_musicSlider;

        [Tooltip("音乐音量百分比文本")]
        [SerializeField] private TextMeshProUGUI m_musicValueText;

        [Tooltip("音效音量滑条（同样 0~100）")]
        [SerializeField] private Slider m_sfxSlider;

        [Tooltip("音效音量百分比文本")]
        [SerializeField] private TextMeshProUGUI m_sfxValueText;

        [Header("按键")]
        [Tooltip("「修改按键」按钮：打开按键设置面板（引用在代码里绑定回调，不用拖 OnClick）")]
        [SerializeField] private Button m_keyBindButton;

        [Tooltip("画质等级下拉框（选项来自 QualitySettings.names）")]
        [SerializeField] private TMP_Dropdown m_qualityDropdown;

        [Tooltip("全屏勾选框")]
        [SerializeField] private Toggle m_fullscreenToggle;

        [Tooltip("语言下拉框（选项在运行时由语言系统填充，面板本身不写死任何语言）")]
        [SerializeField] private TMP_Dropdown m_languageDropdown;

        [Tooltip("「语言」整行的根节点：场景里没有语言系统时整行隐藏，避免出现点了没反应的控件")]
        [SerializeField] private GameObject m_languageRow;

        [Tooltip("帧率下拉框（30 / 60 / 90 / 120 / 不限）。\n" +
                 "选具体帧率时代码会自动关掉垂直同步——垂直同步开着时限帧是不生效的")]
        [SerializeField] private TMP_Dropdown m_frameRateDropdown;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>场景里的语言系统（找不到则为 null，例如只有主菜单的场景）</summary>
        private ILanguageSelector m_languageSelector;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.Settings;
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板进入显示：缓存引用 → 设范围 → 应用存档 → 同步显示 → 绑定回调（顺序不可颠倒）</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            ResolveLanguageSelector();
            SetupControlRanges();
            ApplySavedSettings();
            SyncControlsFromCurrent();
            BindEvents();
        }

        /// <summary>面板退出显示：解绑回调并把 PlayerPrefs 落盘</summary>
        public override void OnExit()
        {
            UnbindEvents();

            // 立即落盘，避免玩家改完设置直接关掉游戏导致丢失
            PlayerPrefs.Save();

            base.OnExit();
        }

        // ====================================================================
        // 控件初始化
        // ====================================================================

        /// <summary>
        /// 找场景里的语言系统（任何实现了 ILanguageSelector 的组件）。
        /// 找不到时把「语言」整行隐藏：只有主菜单的场景没有对话系统，
        /// 不该显示一个点了没反应的控件
        /// </summary>
        private void ResolveLanguageSelector()
        {
            m_languageSelector = null;

            MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is ILanguageSelector selector)
                {
                    m_languageSelector = selector;
                    break;
                }
            }

            if (m_languageRow != null)
            {
                m_languageRow.SetActive(m_languageSelector != null);
            }
        }

        /// <summary>初始化控件的取值范围：音量用 0~100 便于显示百分比，实际应用时再除以 100</summary>
        private void SetupControlRanges()
        {
            SetVolumeSliderRange(m_volumeSlider);
            SetVolumeSliderRange(m_musicSlider);
            SetVolumeSliderRange(m_sfxSlider);
        }

        /// <summary>把一条音量滑条设成 0~100（用整数百分比显示，比 0~1 好读）</summary>
        private static void SetVolumeSliderRange(Slider slider)
        {
            if (slider == null)
            {
                return;
            }

            slider.minValue = 0f;
            slider.maxValue = 100f;
        }

        /// <summary>
        /// 读取 PlayerPrefs 中的设置并应用到 Unity
        /// 首次运行（无存档）时以 Unity 当前状态为默认值，避免凭空重置玩家既有配置
        /// </summary>
        private void ApplySavedSettings()
        {
            // 主音量：默认取当前全局音量
            float volume = PlayerPrefs.GetFloat(k_volumePrefsKey, AudioListener.volume);
            AudioListener.volume = Mathf.Clamp01(volume);

            // 画质等级：默认取当前等级，并夹取到合法区间防止存档越界
            if (QualitySettings.names.Length > 0)
            {
                int quality = PlayerPrefs.GetInt(k_qualityPrefsKey, QualitySettings.GetQualityLevel());
                quality = Mathf.Clamp(quality, 0, QualitySettings.names.Length - 1);
                QualitySettings.SetQualityLevel(quality, true);
            }

            // 全屏：默认取当前状态
            bool fullscreen = PlayerPrefs.GetInt(k_fullscreenPrefsKey, Screen.fullScreen ? 1 : 0) == 1;
            Screen.fullScreen = fullscreen;

            // 目标帧率：有存档才套用。没有存档时保持 Unity 当前设置不动
            // （常驻的 FrameRateSetting 在进游戏时已经套过一次，这里只是让面板打开也保持同步）
            if (PlayerPrefs.HasKey(FrameRateSetting.PrefsKey))
            {
                FrameRateSetting.Apply(PlayerPrefs.GetInt(FrameRateSetting.PrefsKey, FrameRateSetting.Unlimited));
            }
        }

        /// <summary>
        /// 把 Unity 当前状态同步到控件显示（必须在绑定回调之前调用）
        /// UGUI 里程序化改 value 会触发 onValueChanged，所以此刻还不能有监听
        /// </summary>
        private void SyncControlsFromCurrent()
        {
            // 主音量取 AudioListener 当前值；音乐/音效取 AudioManager 当前值。
            // 场景里没有 AudioManager（例如只搭了 UI 的测试场景）时退回直接读 PlayerPrefs，
            // 保证面板不会因为缺系统就显示成 0
            AudioManager audio = AudioManager.Instance;

            if (m_volumeSlider != null)
            {
                m_volumeSlider.value = AudioListener.volume * 100f;
            }

            if (m_musicSlider != null)
            {
                float music = audio != null
                    ? audio.MusicVolume
                    : PlayerPrefs.GetFloat(AudioManager.MusicVolumeKey, 0.7f);
                m_musicSlider.value = music * 100f;
            }

            if (m_sfxSlider != null)
            {
                float sfx = audio != null
                    ? audio.SfxVolume
                    : PlayerPrefs.GetFloat(AudioManager.SfxVolumeKey, 1f);
                m_sfxSlider.value = sfx * 100f;
            }

            RefreshVolumeLabels();

            // 画质下拉：选项来自 Unity 的画质等级名
            if (m_qualityDropdown != null && QualitySettings.names.Length > 0)
            {
                m_qualityDropdown.ClearOptions();
                m_qualityDropdown.AddOptions(new System.Collections.Generic.List<string>(QualitySettings.names));
                m_qualityDropdown.value = QualitySettings.GetQualityLevel();
                m_qualityDropdown.RefreshShownValue();
            }

            if (m_fullscreenToggle != null)
            {
                m_fullscreenToggle.isOn = Screen.fullScreen;
            }

            // 语言下拉：选项与当前值都来自语言系统，面板不写死任何语言
            if (m_languageDropdown != null && m_languageSelector != null)
            {
                m_languageDropdown.ClearOptions();
                m_languageDropdown.AddOptions(
                    new List<string>(m_languageSelector.LanguageDisplayNames));

                int currentIndex = IndexOfLanguageCode(m_languageSelector.CurrentLanguage);
                m_languageDropdown.value = currentIndex >= 0 ? currentIndex : 0;
                m_languageDropdown.RefreshShownValue();
            }

            // 帧率下拉：选项由代码写死（30/60/90/120/不限），当前值从"实际生效的帧率"反推，
            // 这样即使玩家是用别的方式改过帧率，面板显示也不会对不上
            if (m_frameRateDropdown != null)
            {
                m_frameRateDropdown.ClearOptions();
                m_frameRateDropdown.AddOptions(new List<string>(s_frameRateLabels));
                m_frameRateDropdown.value = IndexOfFrameRate(FrameRateSetting.Resolve());
                m_frameRateDropdown.RefreshShownValue();
            }
        }

        /// <summary>某个目标帧率在选项数组里的下标（找不到就当"不限"）</summary>
        private static int IndexOfFrameRate(int targetFrameRate)
        {
            for (int i = 0; i < s_frameRateValues.Length; i++)
            {
                if (s_frameRateValues[i] == targetFrameRate)
                {
                    return i;
                }
            }

            return s_frameRateValues.Length - 1;
        }

        /// <summary>当前语言在支持列表里的下标（找不到返回 -1，例如上次选的语言已被移除）</summary>
        private int IndexOfLanguageCode(string languageCode)
        {
            IReadOnlyList<string> codes = m_languageSelector?.LanguageCodes;
            if (codes == null)
            {
                return -1;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i] == languageCode)
                {
                    return i;
                }
            }

            return -1;
        }

        // ====================================================================
        // 事件绑定
        // ====================================================================

        private void BindEvents()
        {
            if (m_volumeSlider != null)
            {
                m_volumeSlider.onValueChanged.AddListener(HandleVolumeChanged);
            }

            if (m_musicSlider != null)
            {
                m_musicSlider.onValueChanged.AddListener(HandleMusicChanged);
            }

            if (m_sfxSlider != null)
            {
                m_sfxSlider.onValueChanged.AddListener(HandleSfxChanged);
            }

            if (m_keyBindButton != null)
            {
                m_keyBindButton.onClick.AddListener(HandleKeyBindClicked);
            }

            if (m_qualityDropdown != null)
            {
                m_qualityDropdown.onValueChanged.AddListener(HandleQualityChanged);
            }

            if (m_fullscreenToggle != null)
            {
                m_fullscreenToggle.onValueChanged.AddListener(HandleFullscreenChanged);
            }

            if (m_languageDropdown != null)
            {
                m_languageDropdown.onValueChanged.AddListener(HandleLanguageChanged);
            }

            if (m_frameRateDropdown != null)
            {
                m_frameRateDropdown.onValueChanged.AddListener(HandleFrameRateChanged);
            }
        }

        private void UnbindEvents()
        {
            if (m_volumeSlider != null)
            {
                m_volumeSlider.onValueChanged.RemoveListener(HandleVolumeChanged);
            }

            if (m_musicSlider != null)
            {
                m_musicSlider.onValueChanged.RemoveListener(HandleMusicChanged);
            }

            if (m_sfxSlider != null)
            {
                m_sfxSlider.onValueChanged.RemoveListener(HandleSfxChanged);
            }

            if (m_keyBindButton != null)
            {
                m_keyBindButton.onClick.RemoveListener(HandleKeyBindClicked);
            }

            if (m_qualityDropdown != null)
            {
                m_qualityDropdown.onValueChanged.RemoveListener(HandleQualityChanged);
            }

            if (m_fullscreenToggle != null)
            {
                m_fullscreenToggle.onValueChanged.RemoveListener(HandleFullscreenChanged);
            }

            if (m_languageDropdown != null)
            {
                m_languageDropdown.onValueChanged.RemoveListener(HandleLanguageChanged);
            }

            if (m_frameRateDropdown != null)
            {
                m_frameRateDropdown.onValueChanged.RemoveListener(HandleFrameRateChanged);
            }
        }

        // ====================================================================
        // 控件回调：改值即生效 + 立即写入 PlayerPrefs（内存），关闭面板时落盘
        // ====================================================================

        /// <summary>主音量滑块拖动：应用全局音量并刷新百分比文本</summary>
        private void HandleVolumeChanged(float newValue)
        {
            float volume = Mathf.Clamp01(newValue / 100f);

            // 有 AudioManager 时交给它（它会同时写 PlayerPrefs，保证与音频系统同源）；
            // 没有时直接作用于 AudioListener，面板本身仍然可用
            AudioManager audio = AudioManager.Instance;
            if (audio != null)
            {
                audio.SetMasterVolume(volume);
            }
            else
            {
                AudioListener.volume = volume;
                PlayerPrefs.SetFloat(k_volumePrefsKey, volume);
            }

            RefreshVolumeLabels();
        }

        /// <summary>背景音乐滑块拖动：调整音乐分类音量（只影响 BGM，不影响音效）</summary>
        private void HandleMusicChanged(float newValue)
        {
            float volume = Mathf.Clamp01(newValue / 100f);

            AudioManager audio = AudioManager.Instance;
            if (audio != null)
            {
                audio.SetMusicVolume(volume);
            }
            else
            {
                PlayerPrefs.SetFloat(AudioManager.MusicVolumeKey, volume);
            }

            RefreshVolumeLabels();
        }

        /// <summary>音效滑块拖动：调整音效分类音量（只影响音效，不影响 BGM）</summary>
        private void HandleSfxChanged(float newValue)
        {
            float volume = Mathf.Clamp01(newValue / 100f);

            AudioManager audio = AudioManager.Instance;
            if (audio != null)
            {
                audio.SetSfxVolume(volume);
            }
            else
            {
                PlayerPrefs.SetFloat(AudioManager.SfxVolumeKey, volume);
            }

            RefreshVolumeLabels();
        }

        /// <summary>画质下拉切换：应用画质等级</summary>
        private void HandleQualityChanged(int index)
        {
            if (index < 0 || QualitySettings.names.Length == 0)
            {
                return;
            }

            QualitySettings.SetQualityLevel(index, true);
            PlayerPrefs.SetInt(k_qualityPrefsKey, index);
        }

        /// <summary>全屏勾选框切换</summary>
        private void HandleFullscreenChanged(bool isFullscreen)
        {
            Screen.fullScreen = isFullscreen;
            PlayerPrefs.SetInt(k_fullscreenPrefsKey, isFullscreen ? 1 : 0);
        }

        /// <summary>
        /// 帧率下拉切换：套用目标帧率并写存档。
        ///
        /// 具体怎么套用交给 FrameRateSetting——"限帧必须关垂直同步"这个坑统一在那里处理，
        /// 避免设置面板和启动脚本各写一份、以后改一处漏一处
        /// </summary>
        private void HandleFrameRateChanged(int index)
        {
            if (index < 0 || index >= s_frameRateValues.Length)
            {
                return;
            }

            int target = s_frameRateValues[index];
            FrameRateSetting.Apply(target);
            PlayerPrefs.SetInt(FrameRateSetting.PrefsKey, target);
        }

        /// <summary>
        /// 语言下拉切换：转交给语言系统，本面板不知道语言是怎么生效的
        /// （连 PlayerPrefs 的读写也在语言系统那边，面板只负责把选择传过去）
        /// </summary>
        private void HandleLanguageChanged(int index)
        {
            if (m_languageSelector == null)
            {
                return;
            }

            IReadOnlyList<string> codes = m_languageSelector.LanguageCodes;
            if (codes == null || index < 0 || index >= codes.Count)
            {
                return;
            }

            m_languageSelector.SetLanguage(codes[index]);
        }

        /// <summary>把三条音量（主/音乐/音效）同步显示为百分比文本</summary>
        private void RefreshVolumeLabels()
        {
            if (m_volumeValueText != null)
            {
                m_volumeValueText.text = $"{Mathf.RoundToInt(AudioListener.volume * 100f)}%";
            }

            AudioManager audio = AudioManager.Instance;

            if (m_musicValueText != null)
            {
                float music = audio != null
                    ? audio.MusicVolume
                    : PlayerPrefs.GetFloat(AudioManager.MusicVolumeKey, 0.7f);
                m_musicValueText.text = $"{Mathf.RoundToInt(music * 100f)}%";
            }

            if (m_sfxValueText != null)
            {
                float sfx = audio != null
                    ? audio.SfxVolume
                    : PlayerPrefs.GetFloat(AudioManager.SfxVolumeKey, 1f);
                m_sfxValueText.text = $"{Mathf.RoundToInt(sfx * 100f)}%";
            }
        }

        // ====================================================================
        // 事件回调
        // ====================================================================

        /// <summary>「修改按键」按钮：打开按键设置面板（本面板会被压栈暂停）</summary>
        public void HandleKeyBindClicked()
        {
            m_manager?.ShowPanel(EUIPanelID.KeyBindings);
        }

        /// <summary>「关闭」按钮回调（Inspector 拖曳绑定）</summary>
        public void HandleCloseClicked()
        {
            m_manager?.HidePanel(EUIPanelID.Settings);
        }
    }
}
