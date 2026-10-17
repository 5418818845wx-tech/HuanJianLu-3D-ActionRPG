using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace MyGame.Core
{
    /// <summary>
    /// URP 运行时性能调优：把渲染分辨率（Render Scale）和阴影距离调低，降低 GPU 负载。
    ///
    /// 【为什么用代码改，而不是改 URP-Asset.asset 文件】
    /// `Assets/Settings/URP-Asset.asset` 是通过 ProjectSettings/GraphicsSettings 引用的**构建配置**，
    /// 打包时就被固化进包体了 —— 改这个文件热更换不到老客户端。
    /// 而 `UniversalRenderPipelineAsset` 的 renderScale / shadowDistance 是**可写的运行时属性**，
    /// 用代码设置就能随热更下发，老客户端也能吃到。
    ///
    /// 【用反射而不是直接引用 URP 类型的原因】
    /// 直接写 UniversalRenderPipelineAsset 需要给 Game.HotUpdate.asmdef 加 URP 程序集引用；
    /// 一旦程序集名写错会让整个工程编译失败。这里用反射按属性名设置，
    /// 既不需要改 asmdef，也能在 URP 版本变化、属性缺失时安全跳过。
    /// 只在启动时执行一次，反射开销可忽略。
    ///
    /// 【生效时机】
    /// 编辑器下由 Unity 自动调用；真机（HybridCLR）上这个特性不执行，
    /// 所以热更入口 GameEntry 还会显式调用一次。两处都调、内部幂等。
    /// </summary>
    internal static class URPPerformanceTuner
    {
        // ====================================================================
        // 可调参数（要更激进/更保守，只改这里）
        // ====================================================================

        /// <summary>
        /// 渲染分辨率缩放。1 = 原生分辨率；0.8 ≈ 省掉约 36% 的像素。
        /// 这是降低 GPU 负载最直接的一档：屏幕像素数按平方下降。
        /// </summary>
        private const float k_renderScale = 0.8f;

        /// <summary>
        /// 主光阴影距离（米）。原生 50 米对 Demo 过大，调小可同时省 GPU 与主线程（阴影投射物更少）。
        /// </summary>
        private const float k_shadowDistance = 30f;

        /// <summary>URP 渲染管线资产的具体类型名（用来确认"确实是 URP"再动手）</summary>
        private const string k_urpAssetTypeName = "UniversalRenderPipelineAsset";

        /// <summary>
        /// 编辑器下由 Unity 自动调用。
        ///
        /// ⚠️ 真机（HybridCLR）上这个特性不会执行（热更程序集是运行时才加载的），
        /// 因此热更入口 GameEntry 还会显式调用一次 Initialize()。两处都调、内部幂等。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            Initialize();
        }

        /// <summary>显式初始化：套用 URP 性能档位。可安全重复调用。</summary>
        public static void Initialize()
        {
            // 只在真机/打包后的客户端生效。
            //
            // 为什么编辑器里要跳过：URP 资产是 ScriptableObject，Unity 会**保留**运行时对它的修改，
            // 在编辑器里一进播放模式就把 Assets/Settings/URP-Asset.asset 改成 0.8/30 并留下 git 改动，
            // 这不是我们想要的效果（我们只想让客户端生效，不想动工程里的资产）。
            if (Application.isEditor)
            {
                return;
            }

            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null)
            {
                // 没启用 SRP（理论上不会）：什么都不做
                return;
            }

            Type pipelineType = pipeline.GetType();

            // 只对 URP 生效，避免以后换成别的管线时误设属性
            if (pipelineType.Name != k_urpAssetTypeName)
            {
                return;
            }

            TrySetFloatProperty(pipelineType, pipeline, "renderScale", k_renderScale);
            TrySetFloatProperty(pipelineType, pipeline, "shadowDistance", k_shadowDistance);
        }

        /// <summary>
        /// 按属性名给目标对象写一个 float。
        /// 属性不存在或不可写就跳过，不让"性能设置"反过来把启动搞崩。
        /// </summary>
        private static void TrySetFloatProperty(Type type, object target, string propertyName, float value)
        {
            PropertyInfo property = type.GetProperty(
                propertyName, BindingFlags.Public | BindingFlags.Instance);

            if (property == null || !property.CanWrite || property.PropertyType != typeof(float))
            {
                Debug.LogWarning($"[URPPerformanceTuner] 找不到可写的属性 {propertyName}，跳过该项");
                return;
            }

            try
            {
                property.SetValue(target, value);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[URPPerformanceTuner] 设置 {propertyName} 失败：{exception.Message}");
            }
        }
    }
}
