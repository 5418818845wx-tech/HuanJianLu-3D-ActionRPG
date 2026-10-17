using MyGame.Boot;

namespace MyGame.HotUpdate
{
    /// <summary>
    /// 热更运行时的全局服务入口
    ///
    /// 为什么需要它：像 <see cref="MyGame.UI.SceneLoader"/> 这种静态工具类，
    /// 没法通过参数拿到启动壳注入的 <see cref="IAssetService"/>（它是个静态入口，
    /// 到处被直接调用）。所以由 GameEntry 在启动时把服务存到这里，静态代码从这里取。
    ///
    /// 注意：只存接口，不存具体实现——热更代码依然不认识 YooAsset。
    /// </summary>
    public static class HotUpdateRuntime
    {
        /// <summary>资源服务（热更业务加载资源统一走它）</summary>
        public static IAssetService Assets { get; private set; }

        /// <summary>数据表服务</summary>
        public static IDataTableService DataTables { get; private set; }

        /// <summary>可写根目录</summary>
        public static string WritableRoot { get; private set; }

        /// <summary>代码版本</summary>
        public static string CodeVersion { get; private set; }

        /// <summary>由 GameEntry 在启动时调用一次</summary>
        public static void Initialize(IHotUpdateContext context)
        {
            if (context == null)
            {
                return;
            }

            Assets = context.Assets;
            DataTables = context.DataTables;
            WritableRoot = context.WritableRoot;
            CodeVersion = context.CodeVersion;
        }
    }
}
