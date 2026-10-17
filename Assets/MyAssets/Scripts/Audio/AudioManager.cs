using UnityEngine;

namespace MyGame.Audio
{
    /// <summary>
    /// 音频总管：全局单例，统一负责「放音效」「放背景音乐」「音量管理」
    ///
    /// 三层音量结构（互相相乘，符合玩家直觉）：
    ///   最终音量 = 主音量(AudioListener.volume) × 分类音量(音乐/音效)
    /// 主音量沿用设置面板已有的 PlayerPrefs 键 settings.volume，
    /// 所以设置面板的滑条和本系统天然是同一个值，不会出现"两套音量打架"
    ///
    /// 为什么音效要用对象池：
    /// PlayClipAtPoint 每次都会新建并销毁 GameObject，连招/乱战时每秒可能触发十几次，
    /// 会持续产生 GC 垃圾。池化后运行期零分配
    ///
    /// 为什么淡变用 unscaledDeltaTime：
    /// 打开 UI 面板时游戏 Time.timeScale=0，用普通 deltaTime 会让淡变永久卡住
    ///
    /// 挂载位置：每个场景一个（主菜单场景与游戏场景各一个，各自配默认 BGM）
    /// </summary>
    [DisallowMultipleComponent]
    public class AudioManager : MonoBehaviour
    {
        // ====================================================================
        // PlayerPrefs 键名
        // ====================================================================

        /// <summary>主音量键（与 UGUISettingsPanel 共用同一个键，保证两边一致）</summary>
        public const string MasterVolumeKey = "settings.volume";

        /// <summary>背景音乐分类音量键</summary>
        public const string MusicVolumeKey = "audio.musicVolume";

        /// <summary>音效分类音量键</summary>
        public const string SfxVolumeKey = "audio.sfxVolume";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("音频目录")]
        [Tooltip("音频目录资产（枚举 ID → 音频文件）。留空则整个系统静默不发声")]
        [SerializeField] private AudioLibrary m_library;

        [Header("本场景默认音乐")]
        [Tooltip("进入本场景后自动播放的背景音乐（None = 不自动播放）")]
        [SerializeField] private EMusic m_defaultTrack = EMusic.None;

        [Header("音量（0~1）")]
        [Tooltip("主音量。会写入 AudioListener.volume 与设置面板共用同一个 PlayerPrefs 键")]
        [SerializeField, Range(0f, 1f)] private float m_masterVolume = 1f;

        [Tooltip("背景音乐分类音量")]
        [SerializeField, Range(0f, 1f)] private float m_musicVolume = 0.7f;

        [Tooltip("音效分类音量")]
        [SerializeField, Range(0f, 1f)] private float m_sfxVolume = 1f;

        [Header("音效池")]
        [Tooltip("同时可叠加播放的音效数量。连招+技能+敌人混战时建议 12~16")]
        [SerializeField, Range(4, 32)] private int m_sfxPoolSize = 12;

        [Header("空间音效（3D）")]
        [Tooltip("3D 音源开始衰减的距离（米）。比这更近按原音量播放")]
        [SerializeField, Range(0.1f, 20f)] private float m_spatialMinDistance = 2f;

        [Tooltip("3D 音源衰减到听不见的距离（米）。敌人/掉落等世界音效按此衰减")]
        [SerializeField, Range(5f, 200f)] private float m_spatialMaxDistance = 30f;

        [Header("音乐淡变")]
        [Tooltip("切歌时的交叉淡变时长（秒）。0 = 硬切")]
        [SerializeField, Range(0f, 5f)] private float m_musicFadeSeconds = 1.2f;

        // ====================================================================
        // 单例
        // ====================================================================

        /// <summary>全局单例（当前场景中的那个）</summary>
        public static AudioManager Instance { get; private set; }

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>音效源池</summary>
        private AudioSource[] m_sfxPool;

        /// <summary>池中下一个要用的源（轮转使用，避免总是打断同一个源）</summary>
        private int m_nextSfxIndex;

        /// <summary>
        /// 空间音效源池：与 2D 池分开。
        /// 分开的原因：3D 音源每次播放都要先把它挪到发声位置，
        /// 混用同一个池会让"上一次的坐标"串到下一次播放上
        /// </summary>
        private AudioSource[] m_spatialPool;

        /// <summary>空间池中下一个要用的源</summary>
        private int m_nextSpatialIndex;

        /// <summary>双音乐源：交叉淡变时一个在淡出、一个在淡入</summary>
        private AudioSource m_musicA;
        private AudioSource m_musicB;

        /// <summary>当前正在播放的音乐源</summary>
        private AudioSource m_activeMusic;

        /// <summary>当前音乐轨道（用于避免重复切同一首）</summary>
        private EMusic m_currentTrack = EMusic.None;

        /// <summary>野外列表上一首播放的曲目（切歌时避免随机到同一首）</summary>
        private AudioClip m_lastFieldClip;

        /// <summary>当前淡变协程（切歌时先停掉上一个，避免两个协程抢同一个源）</summary>
        private Coroutine m_fadeRoutine;

        /// <summary>循环音效专用源（蓄力这类需要持续循环的音）</summary>
        private AudioSource m_loopSource;

        /// <summary>
        /// 脚步专用源：独立于 3D 音效池。
        /// 独立的原因：脚步样本长度不可控（本项目里就有 20 多秒的样本），
        /// 角色停下时必须能**立刻掐断**正在响的这一声；混在共用的池子里没法单独停
        /// </summary>
        private AudioSource m_footstepSource;

        // ====================================================================
        // 只读访问
        // ====================================================================

        /// <summary>主音量（0~1）</summary>
        public float MasterVolume => m_masterVolume;

        /// <summary>音乐分类音量（0~1）</summary>
        public float MusicVolume => m_musicVolume;

        /// <summary>音效分类音量（0~1）</summary>
        public float SfxVolume => m_sfxVolume;

        /// <summary>音频目录（其它脚本需要随机取变体音时用）</summary>
        public AudioLibrary Library => m_library;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{GetType().Name}] 已存在 AudioManager 实例，重复实例将被销毁", this);
                Destroy(this);
                return;
            }

            Instance = this;

            LoadVolumes();
            BuildSources();
        }

        private void Start()
        {
            // 放 Start：此时 AudioLibrary 资产的引用与各系统都已就绪
            if (m_defaultTrack != EMusic.None)
            {
                PlayMusic(m_defaultTrack);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ====================================================================
        // 初始化
        // ====================================================================

        /// <summary>读取 PlayerPrefs 中的三层音量并应用到 Unity</summary>
        private void LoadVolumes()
        {
            // 主音量默认取当前 AudioListener 值：首次运行不凭空重置玩家的既有设置
            m_masterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(MasterVolumeKey, AudioListener.volume));
            m_musicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(MusicVolumeKey, m_musicVolume));
            m_sfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(SfxVolumeKey, m_sfxVolume));

            AudioListener.volume = m_masterVolume;
        }

        /// <summary>创建音效池与音乐源</summary>
        private void BuildSources()
        {
            m_sfxPool = new AudioSource[Mathf.Max(4, m_sfxPoolSize)];
            for (int i = 0; i < m_sfxPool.Length; i++)
            {
                m_sfxPool[i] = CreateSource($"SFX_{i:00}", loop: false, spatial: false);
            }

            // 3D 池与 2D 池等量：战斗里"敌人的声音"与"自己的声音"一样密集
            m_spatialPool = new AudioSource[Mathf.Max(4, m_sfxPoolSize)];
            for (int i = 0; i < m_spatialPool.Length; i++)
            {
                m_spatialPool[i] = CreateSource($"SFX3D_{i:00}", loop: false, spatial: true);
            }

            m_musicA = CreateSource("Music_A", loop: true, spatial: false);
            m_musicB = CreateSource("Music_B", loop: true, spatial: false);
            m_loopSource = CreateSource("LoopSfx", loop: false, spatial: false);

            // 脚步单独一条 3D 源：停下时要能立刻掐断（见 StopFootsteps）
            m_footstepSource = CreateSource("Footstep", loop: false, spatial: true);
        }

        /// <summary>创建一个音源并挂到自己身上</summary>
        /// <param name="name">物体名（便于调试时看出是哪一路）</param>
        /// <param name="loop">是否循环</param>
        /// <param name="spatial">true = 3D 空间音（按距离衰减），false = 2D 全局音</param>
        private AudioSource CreateSource(string name, bool loop, bool spatial)
        {
            GameObject holder = new GameObject(name);
            holder.transform.SetParent(transform, false);

            AudioSource source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;

            if (spatial)
            {
                // 3D：世界里的声音按距离衰减——靠近敌人更响、走远变小声
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = Mathf.Max(0.1f, m_spatialMinDistance);
                source.maxDistance = Mathf.Max(source.minDistance + 0.1f, m_spatialMaxDistance);
                source.spread = 0f;
            }
            else
            {
                // 2D：玩家自身与 UI 的声音不该随距离变化
                source.spatialBlend = 0f;
            }

            return source;
        }

        // ====================================================================
        // 音效播放（静态便捷入口，实例缺失时静默）
        // ====================================================================

        /// <summary>播放一条音效</summary>
        /// <param name="id">音效 ID</param>
        /// <param name="volumeScale">本条音量的额外缩放（0~1 常用，1 = 原音量）</param>
        /// <param name="pitch">音高（1 = 原速；0.9~1.1 可做微变化避免重复感）</param>
        public static void Play(ESfx id, float volumeScale = 1f, float pitch = 1f)
        {
            Instance?.PlaySfxInternal(id, volumeScale, pitch);
        }

        /// <summary>在给定变体里随机播一条（用于挥砍/脚步/受伤这类有多个样本的音）</summary>
        public static void PlayVariant(ESfx a, ESfx b, float volumeScale = 1f)
        {
            if (Instance == null)
            {
                return;
            }

            Instance.PlaySfxInternal(Random.value < 0.5f ? a : b, volumeScale, Random.Range(0.95f, 1.05f));
        }

        /// <summary>
        /// 在指定世界坐标播放音效（3D，按距离衰减）。
        /// 用于"从世界里某个位置发出"的声音：敌人的动作、掉落物落地等
        /// </summary>
        public static void PlayAt(ESfx id, Vector3 position, float volumeScale = 1f, float pitch = 1f)
        {
            Instance?.PlaySpatialInternal(id, position, volumeScale, pitch);
        }

        /// <summary>在指定世界坐标随机播两个变体之一</summary>
        public static void PlayVariantAt(ESfx a, ESfx b, Vector3 position, float volumeScale = 1f)
        {
            if (Instance == null)
            {
                return;
            }

            Instance.PlaySpatialInternal(Random.value < 0.5f ? a : b, position, volumeScale, Random.Range(0.95f, 1.05f));
        }

        /// <summary>
        /// 在指定世界坐标播放脚步（从 4 个脚步样本里随机抽一条）。
        ///
        /// 走 3D 而不是 2D：脚步是"角色站在世界里某处发出的声音"，
        /// 用 2D 播的话音量恒定、也没有方位感，听起来像"从哪儿都在响"，
        /// 玩家会误以为是远处的敌人发出来的
        /// </summary>
        public static void PlayFootstepAt(Vector3 position, float volumeScale = 1f)
        {
            if (Instance == null)
            {
                return;
            }

            int index = Random.Range(0, 4);
            ESfx id = index switch
            {
                0 => ESfx.Footstep_01,
                1 => ESfx.Footstep_02,
                2 => ESfx.Footstep_03,
                _ => ESfx.Footstep_04,
            };

            Instance.PlayFootstepInternal(id, position, volumeScale);
        }

        /// <summary>
        /// 立刻停掉正在响的脚步。角色一停下就调它。
        ///
        /// 为什么必须显式停：脚步样本长度不可控（本项目里 Footstep_02/03 长达 21~22 秒），
        /// 不掐断的话最后一声会一直响到样本播完，听起来就是"人都站住了还在走"
        /// </summary>
        public static void StopFootsteps()
        {
            Instance?.StopFootstepsInternal();
        }

        /// <summary>开始循环播放一条音效（蓄力等）。重复调用会替换当前循环音</summary>
        public static void PlayLoop(ESfx id, float volumeScale = 1f)
        {
            Instance?.PlayLoopInternal(id, volumeScale);
        }

        /// <summary>停止循环音效（可安全重复调用）</summary>
        public static void StopLoop()
        {
            Instance?.StopLoopInternal();
        }

        // ====================================================================
        // 音效内部实现
        // ====================================================================

        private void PlaySfxInternal(ESfx id, float volumeScale, float pitch)
        {
            AudioClip clip = GetClip(id);
            if (clip == null)
            {
                return;
            }

            AudioSource source = m_sfxPool[m_nextSfxIndex];
            m_nextSfxIndex = (m_nextSfxIndex + 1) % m_sfxPool.Length;

            source.pitch = pitch;
            // PlayOneShot 的第二个参数是相对音源的音量，真正的分类音量在这里乘进去
            source.PlayOneShot(clip, Mathf.Clamp01(m_sfxVolume * volumeScale));
        }

        /// <summary>空间音效内部实现（3D 池：先把音源挪到发声点再播）</summary>
        private void PlaySpatialInternal(ESfx id, Vector3 position, float volumeScale, float pitch)
        {
            AudioClip clip = GetClip(id);
            if (clip == null)
            {
                return;
            }

            AudioSource source = m_spatialPool[m_nextSpatialIndex];
            m_nextSpatialIndex = (m_nextSpatialIndex + 1) % m_spatialPool.Length;

            // 必须先挪坐标再 PlayOneShot：3D 音源按"当前所在位置"计算衰减，
            // 先播后挪会让这一次的衰减用的是上一次的坐标
            source.transform.position = position;
            source.pitch = pitch;
            source.PlayOneShot(clip, Mathf.Clamp01(m_sfxVolume * volumeScale));
        }

        /// <summary>
        /// 脚步内部实现（走专用源）
        /// 用 Play 而不是 PlayOneShot：PlayOneShot 的音是"打进去就失控"的，
        /// 没法单独停；用 Play 才能让角色停下时把这一声立刻掐掉
        /// </summary>
        private void PlayFootstepInternal(ESfx id, Vector3 position, float volumeScale)
        {
            AudioClip clip = GetClip(id);
            if (clip == null || m_footstepSource == null)
            {
                return;
            }

            m_footstepSource.transform.position = position;
            m_footstepSource.pitch = Random.Range(0.92f, 1.08f);
            m_footstepSource.clip = clip;
            m_footstepSource.volume = Mathf.Clamp01(m_sfxVolume * volumeScale);
            m_footstepSource.Play();
        }

        /// <summary>停掉正在响的脚步（没在响就什么都不做）</summary>
        private void StopFootstepsInternal()
        {
            if (m_footstepSource != null && m_footstepSource.isPlaying)
            {
                m_footstepSource.Stop();
            }
        }

        private void PlayLoopInternal(ESfx id, float volumeScale)
        {
            AudioClip clip = GetClip(id);
            if (clip == null)
            {
                return;
            }

            if (m_loopSource.clip == clip && m_loopSource.isPlaying)
            {
                return;   // 同一循环音已在播，不重启（避免每次调用都从头开始）
            }

            m_loopSource.clip = clip;
            m_loopSource.volume = Mathf.Clamp01(m_sfxVolume * volumeScale);
            m_loopSource.Play();
        }

        private void StopLoopInternal()
        {
            if (m_loopSource.isPlaying)
            {
                m_loopSource.Stop();
            }

            m_loopSource.clip = null;
        }

        private AudioClip GetClip(ESfx id)
        {
            if (m_library == null)
            {
                return null;
            }

            // 没配就静默跳过：音频缺失不该打断战斗，也不该每帧刷警告
            return m_library.GetSfx(id);
        }

        // ====================================================================
        // 背景音乐
        // ====================================================================

        /// <summary>播放指定轨道（已在播同一首则忽略）</summary>
        public static void PlayMusic(EMusic track)
        {
            Instance?.PlayMusicInternal(track, false);
        }

        /// <summary>强制从头播放指定轨道（重开一局等场合）</summary>
        public static void RestartMusic(EMusic track)
        {
            Instance?.PlayMusicInternal(track, true);
        }

        /// <summary>停止背景音乐（淡出）</summary>
        public static void StopMusic()
        {
            Instance?.PlayMusicInternal(EMusic.None, false);
        }

        private void PlayMusicInternal(EMusic track, bool forceRestart)
        {
            if (m_library == null)
            {
                return;
            }

            if (!forceRestart && track == m_currentTrack)
            {
                return;
            }

            AudioClip clip = ResolveMusicClip(track);
            m_currentTrack = track;

            if (clip == null)
            {
                // 该轨道没配（或选了 None）：停掉当前音乐
                StopActiveMusic();
                return;
            }

            if (m_fadeRoutine != null)
            {
                StopCoroutine(m_fadeRoutine);
            }

            // 结算短旋律不循环（播完自然停），其余轨道都循环
            bool loop = track == EMusic.MainMenu || track == EMusic.Field || track == EMusic.Combat;
            m_fadeRoutine = StartCoroutine(CrossfadeTo(clip, m_musicFadeSeconds, loop));
        }

        /// <summary>把轨道 ID 翻成具体 Clip（Field 从播放列表里随机挑，且不与上一首重复）</summary>
        private AudioClip ResolveMusicClip(EMusic track)
        {
            if (track == EMusic.Field)
            {
                return PickFieldTrack();
            }

            return m_library.GetMusic(track);
        }

        /// <summary>从野外播放列表里随机挑一首（避开上一次播放的那首）</summary>
        private AudioClip PickFieldTrack()
        {
            System.Collections.Generic.IReadOnlyList<AudioClip> tracks = m_library.FieldTracks;
            if (tracks == null || tracks.Count == 0)
            {
                return null;
            }

            if (tracks.Count == 1)
            {
                m_lastFieldClip = tracks[0];
                return tracks[0];
            }

            // 避免与上一首相同：最多重摇几次，避免列表里大量重复项时死循环
            for (int attempt = 0; attempt < 8; attempt++)
            {
                AudioClip candidate = tracks[Random.Range(0, tracks.Count)];
                if (candidate != null && candidate != m_lastFieldClip)
                {
                    m_lastFieldClip = candidate;
                    return candidate;
                }
            }

            m_lastFieldClip = tracks[0];
            return tracks[0];
        }

        /// <summary>交叉淡变到新曲子：新曲从 0 淡入、旧曲同时淡出</summary>
        private System.Collections.IEnumerator CrossfadeTo(AudioClip clip, float fadeSeconds, bool loop)
        {
            AudioSource from = m_activeMusic;
            AudioSource to = m_activeMusic == m_musicA ? m_musicB : m_musicA;
            m_activeMusic = to;

            to.clip = clip;
            to.loop = loop;
            to.volume = 0f;
            to.Play();

            if (fadeSeconds <= 0f)
            {
                if (from != null)
                {
                    from.Stop();
                    from.clip = null;
                }

                to.volume = m_musicVolume;
                m_fadeRoutine = null;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < fadeSeconds)
            {
                // 用 unscaledDeltaTime：UI 面板打开时 timeScale=0，否则淡变会永久卡住
                elapsed += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(elapsed / fadeSeconds);

                to.volume = k * m_musicVolume;
                if (from != null)
                {
                    from.volume = (1f - k) * m_musicVolume;
                }

                yield return null;
            }

            to.volume = m_musicVolume;
            if (from != null)
            {
                from.Stop();
                from.clip = null;
            }

            m_fadeRoutine = null;
        }

        /// <summary>立刻停掉当前音乐（用于切到"无音乐"）</summary>
        private void StopActiveMusic()
        {
            if (m_fadeRoutine != null)
            {
                StopCoroutine(m_fadeRoutine);
                m_fadeRoutine = null;
            }

            if (m_activeMusic != null)
            {
                m_activeMusic.Stop();
                m_activeMusic.clip = null;
            }
        }

        // ====================================================================
        // 音量设置（供设置面板调用）
        // ====================================================================

        /// <summary>设置主音量（会同时写入 AudioListener 与 PlayerPrefs）</summary>
        public void SetMasterVolume(float value)
        {
            m_masterVolume = Mathf.Clamp01(value);
            AudioListener.volume = m_masterVolume;
            PlayerPrefs.SetFloat(MasterVolumeKey, m_masterVolume);
            PlayerPrefs.Save();
        }

        /// <summary>设置背景音乐音量（立即作用于正在播放的音乐）</summary>
        public void SetMusicVolume(float value)
        {
            m_musicVolume = Mathf.Clamp01(value);
            if (m_activeMusic != null)
            {
                m_activeMusic.volume = m_musicVolume;
            }

            PlayerPrefs.SetFloat(MusicVolumeKey, m_musicVolume);
            PlayerPrefs.Save();
        }

        /// <summary>设置音效音量（对已经播放出去的一次性音效不追溯）</summary>
        public void SetSfxVolume(float value)
        {
            m_sfxVolume = Mathf.Clamp01(value);
            if (m_loopSource != null && m_loopSource.isPlaying)
            {
                m_loopSource.volume = m_sfxVolume;
            }

            PlayerPrefs.SetFloat(SfxVolumeKey, m_sfxVolume);
            PlayerPrefs.Save();
        }
    }
}
