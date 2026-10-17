using System.Collections;
using MyGame.Dungeon;
using MyGame.Save;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 暂停菜单面板（UGUI 版）：ESC 触发的居中模态弹窗
    ///
    /// 按钮绑定方式：**在 Inspector 里拖曳绑定**
    /// 选中任意按钮 → Inspector 的 Button → On Click () → 点 + → 拖入本脚本所在物体
    /// → 在右侧下拉里选对应方法（如 UGUIPauseMenuPanel.HandleResumeClicked）
    ///
    /// 这样做的好处：加新按钮、改行为都不用改代码，Unity 里点几下就完事
    /// 代价：改方法名会让 Inspector 里的绑定静默断开（红色警告会提示）
    ///
    /// 因此本脚本不再需要 [SerializeField] Button 字段——按钮引用由 UnityEvent 自己保存。
    /// 唯一的例外是「保存游戏」按钮上的文字：它要跟着保存结果临时变化，所以必须持有引用
    /// </summary>
    public class UGUIPauseMenuPanel : UGUIPanel
    {
        /// <summary>主菜单场景名（与 Build Settings 中注册的名字一致）</summary>
        private const string k_mainMenuSceneName = "MainScene";

        // ====================================================================
        // 文案常量
        // ====================================================================

        private const string k_saveLabel = "保存游戏";
        private const string k_savedLabel = "已保存";
        private const string k_saveFailedLabel = "保存失败";
        private const string k_noSaveSystemLabel = "存档不可用";

        /// <summary>保存结果在按钮上停留的秒数</summary>
        private const float k_saveFeedbackSeconds = 1.2f;

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("按钮")]
        [Tooltip("「保存游戏」按钮上的文字（保存后会临时变成「已保存」再变回来）")]
        [SerializeField] private TextMeshProUGUI m_saveButtonLabel;

        [Tooltip("「返回大世界」按钮。只在秘境（副本）里显示，用来中途放弃本次挑战。\n" +
                 "它的点击回调由代码在 Awake 里挂上，不需要在 Inspector 里手动绑定")]
        [SerializeField] private Button m_returnOverworldButton;

        // ====================================================================
        // 运行时状态
        // ====================================================================

        /// <summary>保存结果反馈的协程句柄（连点保存时要先停掉上一条）</summary>
        private Coroutine m_saveFeedbackRoutine;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.PauseMenu;

            // 「返回大世界」按钮的回调直接在代码里挂：
            // 它是后面才加进来的按钮，走代码绑定不用再去 Inspector 里手动拖一次
            if (m_returnOverworldButton != null)
            {
                m_returnOverworldButton.onClick.AddListener(HandleReturnOverworldClicked);
            }
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板显示时把按钮文字复位，并按"是否在秘境里"决定要不要显示「返回大世界」</summary>
        public override void OnEnter()
        {
            base.OnEnter();

            // 面板被隐藏时协程是被**暂停**而不是结束，上次的「已保存」可能还挂在按钮上，
            // 所以每次打开都从「保存游戏」开始，不指望协程一定会跑完
            ResetSaveButtonLabel();

            // 「中途退出副本」只在大世界里才有意义：秘境里给玩家一条退路，
            // 大世界里这个按钮就是多余的（本来就该在主界面/暂停菜单里操作）
            if (m_returnOverworldButton != null)
            {
                m_returnOverworldButton.gameObject.SetActive(DungeonRunContext.IsInDungeon);
            }
        }

        /// <summary>面板退出显示时停掉反馈协程</summary>
        public override void OnExit()
        {
            if (m_saveFeedbackRoutine != null)
            {
                StopCoroutine(m_saveFeedbackRoutine);
                m_saveFeedbackRoutine = null;
            }

            base.OnExit();
        }

        // ====================================================================
        // 按钮回调（以下方法由 Inspector 的 Button.OnClick 拖曳绑定）
        // 必须是 public 才能在 Inspector 的 OnClick 下拉里选到
        // ====================================================================

        /// <summary>"继续游戏"按钮回调（点背景遮罩也绑定到此方法）</summary>
        public void HandleResumeClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：继续游戏", this);
            m_manager?.HidePanel(EUIPanelID.PauseMenu);
        }

        /// <summary>"保存游戏"按钮回调：写存档，并把结果写到按钮文字上作为反馈</summary>
        public void HandleSaveClicked()
        {
            ShowSaveResult(SaveNow());
        }

        /// <summary>"设置"按钮回调：打开设置面板</summary>
        public void HandleSettingsClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：打开设置面板", this);
            m_manager?.ShowPanel(EUIPanelID.Settings);
        }

        /// <summary>
        /// "返回大世界"按钮回调：中途放弃本次秘境，回到进入副本前的大世界场景。
        ///
        /// 为什么不在秘境里也提供"回主菜单"：玩家中途不想打了，想去的通常是
        /// "刚才那个大世界"，让他先回主菜单再重新开始一局是绕远路。
        /// 回程场景由 DungeonRunContext 记着（进副本那一刻存下来的）
        /// </summary>
        public void HandleReturnOverworldClicked()
        {
            DungeonManager dungeonManager = DungeonManager.Instance;
            if (dungeonManager == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景里没有 DungeonManager，无法返回大世界", this);
                return;
            }

            Debug.Log($"[{GetType().Name}] 点击：放弃秘境，返回大世界", this);
            dungeonManager.ReturnToOverworld();
        }

        /// <summary>
        /// "返回主菜单"按钮回调：**先自动存一次**再加载主菜单场景。
        ///
        /// 为什么必须自动存：玩家不会记得手动按保存，而主菜单的「继续游戏」
        /// 是按"有没有存档文件"决定能不能点的——不存就永远是灰的
        /// </summary>
        public void HandleMainMenuClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：返回主菜单，加载场景 {k_mainMenuSceneName}", this);

            SaveNow();

            // 加载主菜单场景，当前战斗场景会被自动卸载（含其上的 UGUIManager 与本面板）
            SceneLoader.Load(k_mainMenuSceneName);
        }

        /// <summary>"退出游戏"按钮回调</summary>
        public void HandleQuitClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：退出游戏", this);

            // 退出前也存一次，避免"刚打完一局，手滑点了退出"的损失
            SaveNow();

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ====================================================================
        // 存档
        // ====================================================================

        /// <summary>执行一次存档，返回给玩家看的结果文字</summary>
        private string SaveNow()
        {
            SaveManager saveManager = SaveManager.Instance;

            if (saveManager == null)
            {
                Debug.LogWarning($"[{GetType().Name}] 场景中没有 SaveManager，无法保存", this);
                return k_noSaveSystemLabel;
            }

            return saveManager.SaveGame() ? k_savedLabel : k_saveFailedLabel;
        }

        /// <summary>把结果写到按钮上，过一会儿再变回「保存游戏」</summary>
        private void ShowSaveResult(string resultText)
        {
            if (m_saveButtonLabel == null)
            {
                return;
            }

            // 连点保存时先停掉上一条，避免两条协程抢着改同一段文字
            if (m_saveFeedbackRoutine != null)
            {
                StopCoroutine(m_saveFeedbackRoutine);
            }

            m_saveFeedbackRoutine = StartCoroutine(ShowSaveResultCo(resultText));
        }

        /// <summary>先把结果写上，等一会儿再复位</summary>
        private IEnumerator ShowSaveResultCo(string resultText)
        {
            m_saveButtonLabel.text = resultText;

            // 必须用 Realtime：暂停菜单打开时 Time.timeScale == 0，
            // WaitForSeconds 会永远等不到（这是暂停界面的经典坑）
            yield return new WaitForSecondsRealtime(k_saveFeedbackSeconds);

            if (this == null || !isActiveAndEnabled)
            {
                yield break;
            }

            m_saveFeedbackRoutine = null;
            ResetSaveButtonLabel();
        }

        /// <summary>把按钮文字复位成「保存游戏」</summary>
        private void ResetSaveButtonLabel()
        {
            if (m_saveButtonLabel != null)
            {
                m_saveButtonLabel.text = k_saveLabel;
            }
        }
    }
}
