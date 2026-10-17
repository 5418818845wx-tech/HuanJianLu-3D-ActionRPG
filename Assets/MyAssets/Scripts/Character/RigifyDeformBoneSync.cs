using System.Collections.Generic;
using UnityEngine;

namespace MyGame.Character
{
    /// <summary>
    /// Rigify 风格模型的「双骨骼联动」修复组件。
    ///
    /// 【问题背景】
    /// Blender 的 Rigify 骨架里，同一根骨头通常存在两套骨骼：
    ///  · ORG- 开头：控制骨骼（Original），动画/IK 直接驱动它；
    ///  · DEF- 开头：变形骨骼（Deform），网格顶点权重真正绑在它身上。
    /// 在 Blender 里，DEF 骨骼通过「Copy Transforms 复制变换」约束跟随 ORG 骨骼。
    ///
    /// 【症状】
    /// FBX 格式**不支持导出骨骼约束**。导入 Unity 后 DEF 骨骼失去跟随能力，
    /// 只跟着父级（锁骨/骨盆）走。于是 Unity 把 ORG 手臂转到胯下，
    /// 而 DEF 手臂还僵在绑定姿势（举在头边），网格被硬生生拉长 —— 手脚看起来就扭曲了。
    ///
    /// 【两个必须避开的坑】
    /// 坑一：不能直接照抄世界变换。
    ///   实测静止姿势下 ORG 与 DEF **位置完全重合，但旋转不同**
    ///   （左上臂 94.5°、左前臂 66.0°、右手 11.5°、左大腿 0°）。
    ///   若把 ORG 的世界旋转直接赋给 DEF，等于给左臂额外扭 94.5°，
    ///   静止姿势都会变形——这正是「左臂比右臂严重得多」的原因。
    ///
    /// 坑二：不能假设两套骨骼父节点相同。
    ///   实测 62 对里只有 15 对父节点一致，用「局部变换镜像」会漏掉绝大多数骨骼。
    ///
    /// 【正确做法：世界空间「增量矩阵」】
    /// 对每一对骨骼，在绑定姿势下记录：
    ///   · ORG 的世界逆矩阵 orgBindInv
    ///   · DEF 的世界位置 / 旋转 / 局部缩放
    /// 每帧计算 ORG 相对绑定姿势的刚体运动增量：
    ///   delta = ORG.当前世界矩阵 × orgBindInv
    /// 再把该增量作用到 DEF 自己的绑定姿势上：
    ///   DEF.世界位置 = delta × DEF绑定世界位置
    ///   DEF.世界旋转 = delta.rotation × DEF绑定世界旋转
    /// 这样：
    ///   · 静止时 delta 为单位阵，DEF 精确回到自己的绑定姿势 → 网格不变形；
    ///   · 动画时 DEF 与 ORG 做完全相同的刚体运动 → 网格正确跟随；
    ///   · 与父节点结构无关，62 对全部生效。
    ///
    /// 【使用方式】
    /// 挂到模型根节点（与 Animator 同一物体，或其任意父级）即可。
    /// 其他 Rigify 模型出现同样症状时可直接复用。
    /// </summary>
    [DisallowMultipleComponent]
    public class RigifyDeformBoneSync : MonoBehaviour
    {
        // ====================================================================
        // 序列化配置
        // ====================================================================

        [Tooltip("控制骨骼前缀（Rigify 默认 ORG-）")]
        [SerializeField] private string m_controlPrefix = "ORG-";

        [Tooltip("变形骨骼前缀（Rigify 默认 DEF-）")]
        [SerializeField] private string m_deformPrefix = "DEF-";

        [Tooltip("每帧同步的骨骼对数上限保护（防止前缀配错时扫描到海量骨骼）")]
        [SerializeField] private int m_maxPairs = 2000;

        // ====================================================================
        // 运行时数据
        // ====================================================================

        /// <summary>
        /// 一对「主骨骼 → 从骨骼」的绑定姿势快照。
        ///
        /// 主骨骼 = Unity 的 Humanoid 动画真正驱动的那个（由 Avatar 映射决定）；
        /// 从骨骼 = 另一个，需要每帧跟随主骨骼。
        ///
        /// 注意：主从方向**不是固定的**。本模型原始的 Avatar 映射到 ORG- 骨骼，
        /// 此时 ORG 是主；但如果把 Avatar 改成映射到 DEF- 骨骼（网格权重实际所在的骨骼），
        /// 方向就会反过来。因此这里在 Awake 里根据 Avatar 的映射关系自动判定。
        /// </summary>
        private struct BonePair
        {
            public Transform Master;
            public Transform Follower;

            /// <summary>主骨骼在绑定姿势下的世界逆矩阵（用于求每帧的运动增量）</summary>
            public Matrix4x4 MasterBindInverse;

            /// <summary>从骨骼在绑定姿势下的世界位置 / 世界旋转 / 局部缩放</summary>
            public Vector3 FollowerBindWorldPosition;
            public Quaternion FollowerBindWorldRotation;
            public Vector3 FollowerBindLocalScale;
        }

        private readonly List<BonePair> m_pairs = new List<BonePair>();

        // ====================================================================
        // Unity 生命周期
        // ====================================================================

        /// <summary>
        /// 在 Awake 记录绑定姿势快照。此时 Animator 尚未求值，骨骼仍是场景里保存的
        /// FBX 静止姿势，正好可以当作绑定姿势使用。
        /// </summary>
        private void Awake()
        {
            BuildPairs();
            CaptureBindPose();
        }

        /// <summary>
        /// 在 LateUpdate 同步：此时 Animator 已把本帧动画写进 ORG 骨骼，
        /// 而网格蒙皮要到渲染阶段才计算，所以这里写入的值能够生效。
        /// </summary>
        private void LateUpdate()
        {
            for (int i = 0; i < m_pairs.Count; i++)
            {
                BonePair pair = m_pairs[i];
                if (pair.Master == null || pair.Follower == null) continue;

                // 1. 求主骨骼相对绑定姿势的刚体运动增量
                Matrix4x4 delta = pair.Master.localToWorldMatrix * pair.MasterBindInverse;

                // 2. 把增量作用到从骨骼自己的绑定姿势上
                Vector3 worldPos = delta.MultiplyPoint3x4(pair.FollowerBindWorldPosition);
                Quaternion worldRot = delta.rotation * pair.FollowerBindWorldRotation;

                // 3. 写回世界变换；局部缩放还原为绑定值，避免增量里的缩放污染骨骼长度
                pair.Follower.SetPositionAndRotation(worldPos, worldRot);
                pair.Follower.localScale = pair.FollowerBindLocalScale;
            }
        }

        // ====================================================================
        // 内部实现
        // ====================================================================

        /// <summary>
        /// 扫描子层级，把「DEF-xxx」与「ORG-xxx」配成对。
        /// 找不到同名 ORG 的（如 DEF-xxx.001 中间骨骼）会被跳过，它们跟随父级即可。
        /// </summary>
        private void BuildPairs()
        {
            m_pairs.Clear();

            // 按名字建立索引（同名只保留第一个，避免 .001 重名歧义）
            var nameMap = new Dictionary<string, Transform>();
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                if (!nameMap.ContainsKey(t.name)) nameMap[t.name] = t;
            }

            // 收集 Avatar 真正驱动了哪些骨骼——用来决定每一对的主从方向
            var mappedBones = CollectAvatarMappedBoneNames();

            int orgMaster = 0, defMaster = 0;
            foreach (var kv in nameMap)
            {
                if (!kv.Key.StartsWith(m_deformPrefix)) continue;
                if (kv.Key.EndsWith(".001")) continue; // 没有同名 ORG，跳过

                string controlName = m_controlPrefix + kv.Key.Substring(m_deformPrefix.Length);
                if (!nameMap.TryGetValue(controlName, out Transform org)) continue;

                Transform def = kv.Value;

                // 哪一边被 Avatar 驱动，哪一边就是「主」，另一边负责跟随
                bool orgIsMapped = mappedBones.Contains(org.name);
                bool defIsMapped = mappedBones.Contains(def.name);

                Transform master, follower;
                if (defIsMapped && !orgIsMapped) { master = def; follower = org; defMaster++; }
                else { master = org; follower = def; orgMaster++; }

                m_pairs.Add(new BonePair { Master = master, Follower = follower });
                if (m_pairs.Count >= m_maxPairs) break;
            }

            Debug.Log($"[{nameof(RigifyDeformBoneSync)}]「{name}」已配对 {m_pairs.Count} 对骨骼"
                      + $"（ORG 为主 {orgMaster} 对，DEF 为主 {defMaster} 对）。", this);
        }

        /// <summary>
        /// 读取当前 Animator 上 Avatar 的 Humanoid 映射，返回「被动画驱动的骨骼名」集合。
        /// 没有 Avatar 时返回空集合，此时一律按 ORG 为主处理。
        /// </summary>
        private HashSet<string> CollectAvatarMappedBoneNames()
        {
            var set = new HashSet<string>();
            Animator animator = GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null) return set;

            HumanBone[] humans = animator.avatar.humanDescription.human;
            if (humans == null) return set;
            for (int i = 0; i < humans.Length; i++)
            {
                string boneName = humans[i].boneName;
                if (!string.IsNullOrEmpty(boneName)) set.Add(boneName);
            }
            return set;
        }

        /// <summary>记录每对骨骼的绑定姿势快照（必须在 Animator 首次求值之前调用）</summary>
        private void CaptureBindPose()
        {
            for (int i = 0; i < m_pairs.Count; i++)
            {
                BonePair pair = m_pairs[i];
                if (pair.Master == null || pair.Follower == null) continue;

                pair.MasterBindInverse = pair.Master.worldToLocalMatrix;
                pair.FollowerBindWorldPosition = pair.Follower.position;
                pair.FollowerBindWorldRotation = pair.Follower.rotation;
                pair.FollowerBindLocalScale = pair.Follower.localScale;

                m_pairs[i] = pair;
            }
        }
    }
}
