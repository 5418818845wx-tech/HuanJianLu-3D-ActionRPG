using System.IO;
using UnityEngine;

namespace MyGame.Save
{
    /// <summary>
    /// 存档文件的位置与存在性查询（纯静态，不依赖任何管理器）
    ///
    /// 为什么要单独抽出来：
    /// 主菜单场景里**没有 SaveManager**——它是为游戏场景准备的管理器，读档时会去驱动
    /// 任务 / 背包 / 装备等系统，在主菜单里只会刷一屏"场景中没有 XXX"的警告。
    /// 但"有没有存档"这件事主菜单必须知道（决定「继续游戏」能不能点），
    /// 所以把路径与判断放在这里，两边共用同一份真相，不会出现两处路径写法不一致
    ///
    /// 文件名在这里写死（不再放 Inspector）：主菜单拿不到游戏场景的 Inspector 配置，
    /// 一旦两边配置不同，就会出现"主菜单说没存档、进游戏却能读出来"的诡异现象
    /// </summary>
    public static class SaveStorage
    {
        /// <summary>存档文件名（落在 Application.persistentDataPath 下）</summary>
        public const string FileName = "demo_save.json";

        /// <summary>存档文件的完整路径</summary>
        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>是否已有存档</summary>
        public static bool Exists => File.Exists(FilePath);
    }
}
