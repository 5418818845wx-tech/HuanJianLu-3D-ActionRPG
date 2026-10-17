using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyGame.Audio
{
    /// <summary>
    /// 音频目录：把"枚举 ID"翻译成"真实 AudioClip"
    ///
    /// 为什么需要一层目录，而不是到处 Resources.Load / 拖引用：
    /// - 调用方（战斗、UI）只认 ESfx 枚举，不关心文件叫什么、放在哪、是 wav 还是 mp3
    /// - 以后换音效，只要把文件替换掉、文件名不变，代码一行都不用改
    /// - 所有引用集中在一个资产里，漏配 / 错配一眼能看出来
    ///
    /// 维护方式：把音频丢进 Assets/MyAssets/Audio 对应目录后，
    /// 点菜单「Tools/音频/自动填充音频库」即可按文件名自动建立对应关系
    /// </summary>
    [CreateAssetMenu(fileName = "AudioLibrary", menuName = "游戏数据/音频目录（Audio Library）", order = 3)]
    public class AudioLibrary : ScriptableObject
    {
        /// <summary>一条音效映射：ID → 音频文件</summary>
        [Serializable]
        public struct SfxEntry
        {
            [Tooltip("音效 ID")]
            public ESfx Id;

            [Tooltip("对应的音频文件")]
            public AudioClip Clip;
        }

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("音效")]
        [Tooltip("全部音效映射。由「Tools/音频/自动填充音频库」按文件名生成，一般不用手改")]
        [SerializeField] private List<SfxEntry> m_sfxEntries = new();

        [Header("背景音乐")]
        [Tooltip("主菜单 BGM")]
        [SerializeField] private AudioClip m_bgmMainMenu;

        [Tooltip("野外 BGM 播放列表（随机不重复）。可以放多首，也可以只放一首")]
        [SerializeField] private List<AudioClip> m_bgmField = new();

        [Tooltip("战斗 BGM")]
        [SerializeField] private AudioClip m_bgmCombat;

        [Tooltip("结算·胜利（短旋律，不循环）")]
        [SerializeField] private AudioClip m_bgmResultWin;

        [Tooltip("结算·失败（短旋律，不循环）")]
        [SerializeField] private AudioClip m_bgmResultLose;

        // ====================================================================
        // 运行时缓存
        // ====================================================================

        /// <summary>ID → Clip 的查找表，首次查询时构建一次</summary>
        private Dictionary<ESfx, AudioClip> m_lookup;

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>取音效（没配则返回 null，调用方应静默跳过而不是报错）</summary>
        public AudioClip GetSfx(ESfx id)
        {
            EnsureLookup();

            return m_lookup.TryGetValue(id, out AudioClip clip) ? clip : null;
        }

        /// <summary>野外 BGM 播放列表（只读）</summary>
        public IReadOnlyList<AudioClip> FieldTracks => m_bgmField;

        /// <summary>按轨道 ID 取背景音乐（Field 返回 null，因为它要随机挑，由 AudioManager 处理）</summary>
        public AudioClip GetMusic(EMusic track)
        {
            switch (track)
            {
                case EMusic.MainMenu: return m_bgmMainMenu;
                case EMusic.Combat: return m_bgmCombat;
                case EMusic.ResultWin: return m_bgmResultWin;
                case EMusic.ResultLose: return m_bgmResultLose;
                default: return null;
            }
        }

        // ====================================================================
        // 内部
        // ====================================================================

        /// <summary>构建查找表（幂等；资产被改动后由编辑器工具调用 ResetCache 使其失效）</summary>
        private void EnsureLookup()
        {
            if (m_lookup != null)
            {
                return;
            }

            m_lookup = new Dictionary<ESfx, AudioClip>();
            for (int i = 0; i < m_sfxEntries.Count; i++)
            {
                SfxEntry entry = m_sfxEntries[i];
                if (entry.Clip == null)
                {
                    continue;
                }

                // 重复配置时后一条覆盖前一条，并在编辑器里能看出顺序
                m_lookup[entry.Id] = entry.Clip;
            }
        }

        /// <summary>让查找表失效（编辑器工具填充完数据后必须调用，否则运行时读到的还是旧表）</summary>
        public void ResetCache()
        {
            m_lookup = null;
        }

#if UNITY_EDITOR
        /// <summary>编辑器工具用：写入一条音效映射（仅编辑器）</summary>
        public void EditorSetSfxEntries(List<SfxEntry> entries)
        {
            m_sfxEntries = entries;
            ResetCache();
        }

        /// <summary>编辑器工具用：写入背景音乐（仅编辑器）</summary>
        public void EditorSetMusic(AudioClip mainMenu, List<AudioClip> field, AudioClip combat, AudioClip resultWin, AudioClip resultLose)
        {
            m_bgmMainMenu = mainMenu;
            m_bgmField = field;
            m_bgmCombat = combat;
            m_bgmResultWin = resultWin;
            m_bgmResultLose = resultLose;
        }
#endif
    }
}
