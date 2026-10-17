using MyGame.Save;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MyGame.UI
{
    /// <summary>
    /// 主菜单面板（UGUI 版）：标题界面，提供开始 / 继续 / 设置 / 退出
    ///
    /// 按钮绑定方式：**Inspector 拖曳绑定**（见下面各 HandleXxx 方法）
    ///
    /// 场景组织：
    /// 主菜单是独立场景（MainScene），点「开始新游戏」后加载**大世界场景**（Demo_Scene_Pines_1）。
    /// 主菜单面板配置为 m_showOnStart = true，进入场景后由 UGUIManager 自动显示
    ///
    /// 场景流转（秘境玩法）：
    /// MainScene（主菜单）→ Demo_Scene_Pines_1（大世界，玩家自由活动 + 秘境入口）
    ///   → 进入秘境 → Demo_Scene_Puddle（副本战斗）→ 通关发奖 → 回到大世界
    ///
    /// 「继续游戏」为什么要绕一圈静态意图：
    /// 读档只能由游戏场景里的 SaveManager 执行（它要驱动任务 / 背包 / 装备），
    /// 而此刻那些对象还不存在。所以这里只留下"这次要读档"的意图，切场景后由 SaveManager 消费。
    /// 详见 GameStartIntent 的注释
    /// </summary>
    public class UGUIMainMenuPanel : UGUIPanel
    {
        /// <summary>
        /// 大世界场景名（与 Build Settings 中注册的名字一致）。
        /// 注意：这里填的是 Toon Fantasy Nature 素材包的场景，路径
        /// Assets/Toon Fantasy Nature/Scenes/Demo_Scene_Pines_1.unity，
        /// 名字必须是**不带路径的纯场景名**，LoadScene 才能找到。
        /// </summary>
        private const string k_gameplaySceneName = "Demo_Scene_Pines_1";

        // ====================================================================
        // 序列化字段
        // ====================================================================

        [Header("按钮")]
        [Tooltip("「继续游戏」按钮：没有存档时会被置灰（需要改它的 interactable，所以只能用字段引用）")]
        [SerializeField] private Button m_continueButton;

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        protected override void Awake()
        {
            base.Awake();

            // 设置面板 ID（与 Inspector 配置一致，这里硬编码防止漏配）
            m_panelID = EUIPanelID.MainMenu;
        }

        // ====================================================================
        // 面板生命周期
        // ====================================================================

        /// <summary>面板显示时刷新「继续游戏」的可用状态</summary>
        public override void OnEnter()
        {
            base.OnEnter();
            RefreshContinueButton();
        }

        // ====================================================================
        // 按钮回调（以下方法由 Inspector 的 Button.OnClick 拖曳绑定）
        // 必须是 public 才能在 Inspector 的 OnClick 下拉里选到
        // ====================================================================

        /// <summary>"开始新游戏"按钮回调：加载战斗场景</summary>
        public void HandleStartClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：开始新游戏，加载场景 {k_gameplaySceneName}", this);

            // 明确清掉读档意图：上一次点过「继续游戏」的话，残留的意图会让新游戏把旧档读回来
            GameStartIntent.RequestNewGame();

            // 加载战斗场景，主菜单场景会被自动卸载（含其上的 UGUIManager 与本面板）
            // 不需要 HidePanel：场景卸载会一并销毁 UI
            SceneLoader.Load(k_gameplaySceneName);
        }

        /// <summary>"继续游戏"按钮回调：留下读档意图并加载战斗场景</summary>
        public void HandleContinueClicked()
        {
            if (!SaveStorage.Exists)
            {
                // 按钮本该是灰的，这里只是兜底（比如存档在打开主菜单后被手动删掉）
                Debug.LogWarning($"[{GetType().Name}] 没有存档，无法继续游戏", this);
                RefreshContinueButton();
                return;
            }

            Debug.Log($"[{GetType().Name}] 点击：继续游戏，加载场景 {k_gameplaySceneName} 并读取存档", this);

            GameStartIntent.RequestLoadSave();
            SceneLoader.Load(k_gameplaySceneName);
        }

        /// <summary>"设置"按钮回调：打开设置面板</summary>
        public void HandleSettingsClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：打开设置面板", this);
            m_manager?.ShowPanel(EUIPanelID.Settings);
        }

        /// <summary>"退出游戏"按钮回调</summary>
        public void HandleQuitClicked()
        {
            Debug.Log($"[{GetType().Name}] 点击：退出游戏", this);

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ====================================================================
        // 界面状态
        // ====================================================================

        /// <summary>按"有没有存档"决定「继续游戏」能不能点</summary>
        private void RefreshContinueButton()
        {
            if (m_continueButton == null)
            {
                return;
            }

            m_continueButton.interactable = SaveStorage.Exists;
        }
    }
}
