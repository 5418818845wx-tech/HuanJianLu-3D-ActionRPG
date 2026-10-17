using System;
using System.Collections.Generic;
using MyGame.Audio;
using UnityEditor;
using UnityEngine;

namespace MyGame.EditorTools
{
    /// <summary>
    /// 音频目录自动填充工具（编辑器专用）
    ///
    /// 为什么需要它：
    /// 音频目录里有 58 条音效映射，手拖一遍既枯燥又容易漏。
    /// 而文件名本身就是"SFX_ + 枚举名"的约定，完全可以自动建立对应关系。
    ///
    /// 使用方式：把音频文件放进 Assets/MyAssets/Audio/SFX 与 /BGM 后，
    /// 点菜单「Tools/音频/自动填充音频库」。新增音效时重复这个动作即可。
    ///
    /// 约定：
    /// - 音效：SFX_{枚举名}.wav|mp3，例如 SFX_Swing_Light_01.wav → ESfx.Swing_Light_01
    /// - 音乐：BGM_MainMenu / BGM_Field1 / BGM_Field2 / BGM_Combat / BGM_Result_Win / BGM_Result_Lose
    /// </summary>
    public static class AudioLibraryAutoFill
    {
        private const string k_menuPath = "Tools/音频/自动填充音频库";

        private const string k_libraryPath = "Assets/MyAssets/Audio/AudioLibrary.asset";
        private const string k_sfxFolder = "Assets/MyAssets/Audio/SFX";
        private const string k_bgmFolder = "Assets/MyAssets/Audio/BGM";

        private const string k_sfxPrefix = "SFX_";
        private const string k_bgmPrefix = "BGM_";

        [MenuItem(k_menuPath)]
        public static void Fill()
        {
            AudioLibrary library = LoadOrCreateLibrary();
            if (library == null)
            {
                return;
            }

            List<AudioLibrary.SfxEntry> sfxEntries = CollectSfx();
            MusicSlots music = CollectMusic(library);

            library.EditorSetSfxEntries(sfxEntries);
            library.EditorSetMusic(music.MainMenu, music.Field, music.Combat, music.ResultWin, music.ResultLose);

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();

            ReportResult(sfxEntries, music);
        }

        // ====================================================================
        // 收集音效
        // ====================================================================

        private static List<AudioLibrary.SfxEntry> CollectSfx()
        {
            List<AudioLibrary.SfxEntry> entries = new();
            HashSet<ESfx> filled = new();

            foreach (AudioClip clip in LoadClipsInFolder(k_sfxFolder))
            {
                string clipName = clip.name;
                if (!clipName.StartsWith(k_sfxPrefix, StringComparison.Ordinal))
                {
                    Debug.LogWarning($"[音频库] 跳过不符合命名规范的文件「{clipName}」，应为 {k_sfxPrefix}枚举名");
                    continue;
                }

                string idText = clipName.Substring(k_sfxPrefix.Length);
                if (!Enum.TryParse(idText, out ESfx id))
                {
                    Debug.LogWarning($"[音频库] 文件名「{clipName}」在 ESfx 里找不到对应枚举值，已跳过");
                    continue;
                }

                // 同一 ID 出现多次时保留第一条，并明确提示（一般是同名的 wav/mp3 并存）
                if (!filled.Add(id))
                {
                    Debug.LogWarning($"[音频库] 音效 ID {id} 有多个文件，只取第一个（已忽略「{clipName}」）");
                    continue;
                }

                entries.Add(new AudioLibrary.SfxEntry { Id = id, Clip = clip });
            }

            // 按枚举顺序排序，Inspector 里看起来更整齐
            entries.Sort((a, b) => a.Id.CompareTo(b.Id));
            return entries;
        }

        // ====================================================================
        // 收集音乐
        // ====================================================================

        private struct MusicSlots
        {
            public AudioClip MainMenu;
            public List<AudioClip> Field;
            public AudioClip Combat;
            public AudioClip ResultWin;
            public AudioClip ResultLose;
        }

        private static MusicSlots CollectMusic(AudioLibrary library)
        {
            MusicSlots slots = new() { Field = new List<AudioClip>() };

            foreach (AudioClip clip in LoadClipsInFolder(k_bgmFolder))
            {
                string clipName = clip.name;
                if (!clipName.StartsWith(k_bgmPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                // "BGM_" 之后的部分作为槽位名
                string slot = clipName.Substring(k_bgmPrefix.Length);

                if (slot.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase))
                {
                    slots.MainMenu = clip;
                }
                else if (slot.StartsWith("Field", StringComparison.OrdinalIgnoreCase))
                {
                    slots.Field.Add(clip);          // Field 是播放列表，可以有多首
                }
                else if (slot.StartsWith("Combat", StringComparison.OrdinalIgnoreCase))
                {
                    slots.Combat = clip;
                }
                else if (slot.StartsWith("Result_Win", StringComparison.OrdinalIgnoreCase))
                {
                    slots.ResultWin = clip;
                }
                else if (slot.StartsWith("Result_Lose", StringComparison.OrdinalIgnoreCase))
                {
                    slots.ResultLose = clip;
                }
                else
                {
                    Debug.LogWarning($"[音频库] 音乐文件「{clipName}」无法识别槽位，已跳过");
                }
            }

            slots.Field.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return slots;
        }

        // ====================================================================
        // 工具
        // ====================================================================

        /// <summary>取目录下全部音频片段（自动带上 .meta 生成后的资源）</summary>
        private static List<AudioClip> LoadClipsInFolder(string folder)
        {
            List<AudioClip> clips = new();

            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[音频库] 目录不存在：{folder}");
                return clips;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                {
                    clips.Add(clip);
                }
            }

            return clips;
        }

        private static AudioLibrary LoadOrCreateLibrary()
        {
            AudioLibrary library = AssetDatabase.LoadAssetAtPath<AudioLibrary>(k_libraryPath);
            if (library != null)
            {
                return library;
            }

            library = ScriptableObject.CreateInstance<AudioLibrary>();
            AssetDatabase.CreateAsset(library, k_libraryPath);
            Debug.Log($"[音频库] 已创建 {k_libraryPath}");
            return library;
        }

        /// <summary>输出填充结果，并把"枚举有、文件没有"的缺口列出来</summary>
        private static void ReportResult(List<AudioLibrary.SfxEntry> entries, MusicSlots music)
        {
            List<string> missing = new();
            foreach (ESfx id in Enum.GetValues(typeof(ESfx)))
            {
                bool found = false;
                foreach (AudioLibrary.SfxEntry entry in entries)
                {
                    if (entry.Id == id)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    missing.Add(id.ToString());
                }
            }

            Debug.Log($"[音频库] 填充完成：音效 {entries.Count}/{Enum.GetValues(typeof(ESfx)).Length} 条，"
                + $"音乐 主菜单={Describe(music.MainMenu)} 野外={music.Field.Count}首 "
                + $"战斗={Describe(music.Combat)} 胜利={Describe(music.ResultWin)} 失败={Describe(music.ResultLose)}");

            if (missing.Count > 0)
            {
                Debug.LogWarning($"[音频库] 以下 {missing.Count} 个音效还没有对应文件：{string.Join("、", missing)}");
            }
        }

        private static string Describe(AudioClip clip)
        {
            return clip != null ? clip.name : "(未配)";
        }
    }
}
