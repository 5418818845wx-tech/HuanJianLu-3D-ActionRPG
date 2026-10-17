using System;

namespace MyGame.HotUpdate
{
    /// <summary>
    /// 游戏配置表模型（对应 DataTables/game_config.json）
    ///
    /// 刻意放在热更程序集里：连"配置表长什么样"都能随热更改，
    /// 加个字段不需要重新出包——只要配套的 JSON 一起发出去就行。
    ///
    /// 注意：字段名必须与 json 的键名完全一致（JsonUtility 按字段名匹配，大小写敏感）。
    /// </summary>
    [Serializable]
    public class GameConfig
    {
        /// <summary>游戏标题</summary>
        public string gameTitle = "未命名";

        /// <summary>开局金币</summary>
        public int startingGold = 1000;

        /// <summary>经验倍率</summary>
        public float expRate = 1f;

        /// <summary>目标帧率（&lt;=0 表示不限制）</summary>
        public int targetFrameRate = 60;

        /// <summary>欢迎语</summary>
        public string welcomeMessage = "";
    }
}
