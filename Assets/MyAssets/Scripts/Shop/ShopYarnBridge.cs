using MyGame.UI;
using UnityEngine;
using Yarn.Unity;

namespace MyGame.Shop
{
    /// <summary>
    /// 商店的 Yarn 桥接：剧本里写 &lt;&lt;open_shop "Shop_General"&gt;&gt; 就能打开商店
    ///
    /// 与 QuestYarnBridge 的分工完全一致：
    /// 剧本只负责说"做什么"（打开哪家店），具体怎么打开、怎么买卖留在系统里。
    /// 所以策划要在新 NPC 身上开店，只需要写一行命令，不需要碰 C#
    ///
    /// 挂载位置：与 DialogueRunner 同一物体（静态命令靠特性注册，不看实例）
    /// </summary>
    [DisallowMultipleComponent]
    public class ShopYarnBridge : MonoBehaviour
    {
        /// <summary>打开商店（剧本命令）</summary>
        [YarnCommand("open_shop")]
        public static void HandleOpenShop(string shopId)
        {
            if (string.IsNullOrEmpty(shopId))
            {
                Debug.LogWarning($"[{nameof(ShopYarnBridge)}] open_shop 没有给商店 ID");
                return;
            }

            ShopSystem shopSystem = ShopSystem.Instance;
            if (shopSystem == null)
            {
                Debug.LogWarning($"[{nameof(ShopYarnBridge)}] 场景中没有 ShopSystem，无法开店");
                return;
            }

            if (shopSystem.FindShop(shopId) == null)
            {
                Debug.LogWarning($"[{nameof(ShopYarnBridge)}] 商店目录里没有「{shopId}」，检查是不是漏登记了进货表");
                return;
            }

            UGUIManager uiManager = UGUIManager.Instance;
            if (uiManager == null)
            {
                Debug.LogWarning($"[{nameof(ShopYarnBridge)}] 场景中没有 UGUIManager，无法开店");
                return;
            }

            UGUIShopPanel panel = uiManager.GetPanel<UGUIShopPanel>(EUIPanelID.Shop);
            if (panel == null)
            {
                Debug.LogWarning($"[{nameof(ShopYarnBridge)}] 找不到商店面板");
                return;
            }

            // 先收起对话面板：**此刻它还在栈顶**，能正常关闭。
            // 如果等对话自己结束再关，它会因为商店已经压在栈顶而关不掉
            // （UGUIManager 只允许关栈顶），结果对话框一直卡在商店下面
            uiManager.HidePanel(EUIPanelID.Dialogue);

            panel.OpenShop(shopId);
        }
    }
}
