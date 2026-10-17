using MyGame.Audio;
using MyGame.Combat;
using MyGame.Dungeon;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 战斗结算面板（UGUI 版）：战斗结束后弹出，展示胜负与统计
    ///
    /// 按钮绑定方式：**Inspector 拖曳绑定**（重试 / 返回主菜单）
    ///
    /// 与其他面板的关键差异：
    /// 本面板不由玩家按键打开，而是由战斗 HUD 在收到 CombatWaveManager.CombatEnded 后
    /// 调用 ShowResult() 弹出来。
    ///
    /// 为什么不自己在 Update 里轮询波次管理器：
    /// 面板在关闭时物体是停用的（SetActive(false)），Update 根本不会执行，
    /// 轮询代码永远等不到战斗结束那一刻。所以「谁在战斗结束时通知 UI」这件事
    /// 必须由始终激活的对象承担——这里交给常驻的战斗 HUD。
    ///
    /// 数据来源：CombatWaveManager.CombatEnded 事件（经 CombatHUD 转发）
    /// 职责边界：面板只展示结果与跳转，胜负判定完全由 CombatWaveManager 负责
    /// </summary>
    public class UGUIResultPanel : UGUIPanel
    {
        // ====================================================================
        // 文案常量
        // ====================================================================

        private const string k_victoryTitle = "战斗胜利";
        private const string k_defeatTitle = "战斗失败";
        private const string k_victorySubtitle = "试炼场的敌人已被肃清。";
        private const string k_defeatSubtitle = "你倒下了。整备之后再来一次吧。";

        /// <summary>秘境通关时追加在胜利副标题后的提示（奖励由 DungeonManager 发放）</summary>
        private const string k_dungeonRewardHint = "秘境奖励已发放，可从背包查看。";

        /// <summary>主菜单场景名（与 Build Settings 中注册的名字一致）</summary>
        private const string k_mainMenuSceneName = "MainScene";

        /// <summary>返回按钮文字：秘境中回大世界</summary>
        private const string k_backToOverworldLabel = "返回大世界";

        /// <summary>返回按钮文字：非秘境流程回主菜单</summary>
        private const string k_backToMainMenuLabel = "返回主菜单";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("文本引用")]
        [Tooltip("结算标题（战斗胜利 / 战斗失败）")]
        [SerializeField] private TextMeshProUGUI m_titleText;

        [Tooltip("副标题（结算说明文字）")]
        [SerializeField] private TextMeshProUGUI m_subtitleText;

        [Tooltip("击杀数数值")]
        [SerializeField] private TextMeshProUGUI m_statKills;

        [Tooltip("得分数值")]
        [SerializeField] private TextMeshProUGUI m_statScore;

        [Tooltip("用时数值")]
        [SerializeField] private TextMeshProUGUI m_statTime;

        [Tooltip("返回按钮上的文字。秘境中会改成「返回大世界」，其余场景保持「返回主菜单」")]
        [SerializeField] private TextMeshProUGUI m_backButtonLabel;

        [Header("胜负底板素材（留空则用纯色）")]
        [Tooltip("结算窗口的底板 Image（拖 Window 节点的 Image）")]
        [SerializeField] private Image m_windowImage;

        [Tooltip("胜利时的窗口底板素材（如 panel_blue）。留空则不切换")]
        [SerializeField] private Sprite m_victorySprite;

        [Tooltip("失败时的窗口底板素材（如 panel_red）。留空则不切换")]
        [SerializeField] private Sprite m_defeatSprite;

        [Header("标题配色")]
        [Tooltip("胜利时标题颜色（默认金黄）")]
        [SerializeField] private Color m_victoryColor = new Color(1f, 0.769f, 0f, 1f);

        [Tooltip("失败时标题颜色（默认红色）")]
        [SerializeField] private Color m_defeatColor = new Color(0.886f, 0.267f, 0.267f, 1f);

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.Result;
        }

        // ====================================================================
        // 对外接口：由战斗 HUD 调用
        // ====================================================================

        /// <summary>
        /// 弹出结算面板并展示结果（由常驻的战斗 HUD 在战斗结束时调用）
        ///
        /// 顺序说明：
        /// 1. 先清掉暂停菜单、背包等弹窗，保证结算界面是唯一可见的面板
        /// 2. 再 ShowPanel —— 面板物体此时才被激活，组件引用随之可用
        /// 3. 最后填数据：UGUI 的文本组件一直存在于场景中，但停用状态下写入不方便，
        ///    先激活再写是更稳妥的顺序
        /// </summary>
        public void ShowResult(CombatResult result)
        {
            if (m_manager == null)
            {
                Debug.LogWarning($"[{GetType().Name}] UGUIManager 未注入，无法弹出结算面板", this);
                return;
            }

            m_manager.HideAllOverlay();
            m_manager.ShowPanel(EUIPanelID.Result);

            // 结算音乐：胜利用凯旋短旋律、失败用低沉短旋律（都不循环，播完即止）
            AudioManager.PlayMusic(result.IsVictory ? EMusic.ResultWin : EMusic.ResultLose);

            ApplyResult(result);
        }

        /// <summary>把战斗结果填充到界面</summary>
        private void ApplyResult(CombatResult result)
        {
            // 胜负底板：赢了换蓝底、输了换红底（没配素材就保持原样）
            ApplyWindowSprite(result.IsVictory);

            if (m_titleText != null)
            {
                m_titleText.text = result.IsVictory ? k_victoryTitle : k_defeatTitle;
                m_titleText.color = result.IsVictory ? m_victoryColor : m_defeatColor;
            }

            if (m_subtitleText != null)
            {
                string subtitle = result.IsVictory ? k_victorySubtitle : k_defeatSubtitle;

                // 秘境通关：追加一句"奖励已发"的提示（非秘境流程完全不受影响）
                if (result.IsVictory && DungeonRunContext.IsInDungeon)
                {
                    subtitle += k_dungeonRewardHint;
                }

                m_subtitleText.text = subtitle;
            }

            if (m_statKills != null)
            {
                m_statKills.text = result.KillCount.ToString();
            }

            if (m_statScore != null)
            {
                m_statScore.text = result.Score.ToString();
            }

            if (m_statTime != null)
            {
                m_statTime.text = $"{result.DurationInSeconds:0.0}s";
            }

            // 返回按钮文字：秘境里点了是回大世界，写「返回主菜单」会误导玩家
            if (m_backButtonLabel != null)
            {
                m_backButtonLabel.text = DungeonRunContext.IsInDungeon ? k_backToOverworldLabel : k_backToMainMenuLabel;
            }
        }

        /// <summary>
        /// 按胜负切换结算窗口底板。任一素材没配就整段跳过，
        /// 这样只配了胜利图、失败图还没找好时也不会把窗口弄成白板
        /// </summary>
        private void ApplyWindowSprite(bool isVictory)
        {
            if (m_windowImage == null)
            {
                return;
            }

            Sprite target = isVictory ? m_victorySprite : m_defeatSprite;
            if (target == null)
            {
                return;
            }

            m_windowImage.sprite = target;
            m_windowImage.color = Color.white;
            m_windowImage.type = target.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
        }

        // ====================================================================
        // 按钮回调（由 Inspector 的 Button.OnClick 拖曳绑定）
        // ====================================================================

        /// <summary>"重试"按钮回调：重新加载当前场景</summary>
        public void HandleRetryClicked()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            Debug.Log($"[{GetType().Name}] 重试：重新加载场景 {activeScene.name}", this);
            SceneLoader.Load(activeScene.name);
        }

        /// <summary>
        /// 「返回」按钮回调。
        /// 秘境通关 → 回大世界（走 DungeonManager.ReturnToOverworld，回进入秘境前所在场景）；
        /// 其它情况 → 与原来完全一致，返回主菜单场景。
        /// 若处于秘境中但管理器缺失（场景没挂），兜底回主菜单，避免按钮点了没反应
        /// </summary>
        public void HandleMainMenuClicked()
        {
            DungeonManager dungeonManager = DungeonManager.Instance;
            if (DungeonRunContext.IsInDungeon && dungeonManager != null)
            {
                Debug.Log($"[{GetType().Name}] 秘境结束，返回大世界", this);
                dungeonManager.ReturnToOverworld();
                return;
            }

            Debug.Log($"[{GetType().Name}] 返回主菜单，加载场景 {k_mainMenuSceneName}", this);
            SceneLoader.Load(k_mainMenuSceneName);
        }
    }
}
