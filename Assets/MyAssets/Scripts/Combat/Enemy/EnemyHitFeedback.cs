using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MyGame.Combat
{
    /// <summary>
    /// 受击反馈组件：挂载到敌人/受击单位上，提供三种打击感反馈
    /// 1. 受击闪白/闪红：命中瞬间把模型颜色改为高亮色再恢复
    /// 2. 受击击退：命中瞬间把单位沿"背对攻击者"方向推出一段距离
    /// 3. 伤害飘字：命中位置向上飘出伤害数字（用世界空间 TMP 文字，面向主相机）
    ///
    /// 工作方式：订阅本物体上 CombatUnit.HitTaken 事件，每次实际扣血后自动触发
    /// 每个反馈都可以在 Inspector 单独开关，便于分步验证
    /// </summary>
    [RequireComponent(typeof(CombatUnit))]
    public class EnemyHitFeedback : MonoBehaviour
    {
        [Header("组件引用")]
        [Tooltip("受击单位（自动查找，无需手动指定）")]
        [SerializeField] private CombatUnit m_unit;

        [Tooltip("用于闪白的渲染器列表（留空自动收集本物体及子物体的所有 Renderer）")]
        [SerializeField] private List<Renderer> m_renderers = new List<Renderer>();

        [Tooltip("命中单位需要带刚体才会有击退效果（自动查找）")]
        [SerializeField] private Rigidbody m_rigidbody;

        [Header("受击闪白")]
        [Tooltip("是否启用受击闪白")]
        [SerializeField] private bool m_enableFlash = true;

        [Tooltip("闪白颜色（命中瞬间显示的颜色，默认白色）")]
        [SerializeField] private Color m_flashColor = Color.white;

        [Tooltip("闪白持续时间（秒）")]
        [SerializeField] private float m_flashDuration = 0.08f;

        [Header("受击击退")]
        [Tooltip("是否启用受击击退")]
        [SerializeField] private bool m_enableKnockback = true;

        [Tooltip("击退力度（越大飞得越远）")]
        [SerializeField] private float m_knockbackForce = 2.5f;

        [Tooltip("击退上挑力度（让击退带一点向上的抛物感）")]
        [SerializeField] private float m_knockbackUpward = 0.5f;

        [Header("伤害飘字")]
        [Tooltip("是否启用伤害飘字")]
        [SerializeField] private bool m_enableDamageText = true;

        [Tooltip("飘字字体大小（Canvas 缩放 0.01 后，建议 30≈世界0.3米高）")]
        [SerializeField] private int m_damageTextFontSize = 30;

        [Tooltip("飘字颜色（可用金黄色突显暴击感）")]
        [SerializeField] private Color m_damageTextColor = new Color(1f, 0.85f, 0.3f, 1f);

        [Tooltip("飘字在世界空间的上飘速度（米/秒）")]
        [SerializeField] private float m_textRiseSpeed = 1.5f;

        [Tooltip("飘字存留时间（秒），到时自动消失")]
        [SerializeField] private float m_textLifetime = 0.8f;

        [Tooltip("飘字出生位置相对受击单位头顶的高度偏移")]
        [SerializeField] private float m_textHeightOffset = 1.8f;

        /// <summary>闪白协程的引用（防止连续命中时闪白叠加错乱）</summary>
        private Coroutine m_flashCoroutine;

        /// <summary>飘字对象池（重复利用避免频繁创建/销毁造成卡顿）</summary>
        private readonly Queue<DamageTextEntry> m_textPool = new Queue<DamageTextEntry>();

        /// <summary>当前正在飘动的文字列表（用于驱动上飘与淡出）</summary>
        private readonly List<DamageTextEntry> m_activeTexts = new List<DamageTextEntry>();

        /// <summary>
        /// 闪白目标：一个渲染器 + 它的主颜色属性 ID。
        /// 用结构体数组而不是 Dictionary，遍历时零查找、零装箱
        /// </summary>
        private struct FlashTarget
        {
            /// <summary>要变色的渲染器</summary>
            public Renderer Renderer;

            /// <summary>该渲染器材质上主颜色属性的 Hash（_BaseColor 或 _Color）</summary>
            public int ColorPropertyId;
        }

        /// <summary>
        /// 预先算好的闪白目标列表（Awake 里建一次）。
        /// 关键优化：改用 MaterialPropertyBlock 覆盖颜色，而不是 renderer.material。
        /// renderer.material 会为每个渲染器克隆一份材质（占内存、破坏合批），
        /// MaterialPropertyBlock 只在渲染时覆盖指定属性，不动材质本身
        /// </summary>
        private FlashTarget[] m_flashTargets = System.Array.Empty<FlashTarget>();

        /// <summary>复用的属性块（避免每次闪白都 new）</summary>
        private MaterialPropertyBlock m_propertyBlock;

        /// <summary>缓存好的等待对象（每帧 new WaitForSeconds 会产生垃圾）</summary>
        private WaitForSeconds m_flashWait;

        /// <summary>URP/Lit 等现代 Shader 的主颜色属性名</summary>
        private const string k_baseColorProp = "_BaseColor";

        /// <summary>内置管线/旧 Shader 的主颜色属性名</summary>
        private const string k_colorProp = "_Color";

        /// <summary>主相机缓存（飘字始终面向相机）</summary>
        private Camera m_mainCamera;

        private void Awake()
        {
            // 自动查找组件，降低手动配置负担
            if (m_unit == null) m_unit = GetComponent<CombatUnit>();
            if (m_rigidbody == null) m_rigidbody = GetComponentInParent<Rigidbody>();
            if (m_renderers.Count == 0) CollectRenderers();

            // 复用的属性块 + 缓存等待对象：只在 Awake 建一次，运行期零分配
            m_propertyBlock = new MaterialPropertyBlock();
            m_flashWait = new WaitForSeconds(m_flashDuration);

            // 预先算好闪白目标
            // 注意：不同 Shader 的主颜色属性名不同（URP/Lit 用 _BaseColor，内置/旧 Shader 用 _Color），
            // 自定义卡通 Shader 可能没有颜色属性——这些渲染器跳过闪白
            m_flashTargets = BuildFlashTargets();

            // 飘字由世界空间 Canvas + TextMeshProUGUI 组成（比 3D TextMeshPro 运行时创建更可靠）
            // 用一个"空物体 + 池"管理，每条文字是 Canvas 下的子物体，逐条独立世界坐标
            if (m_mainCamera == null && Camera.main != null)
            {
                m_mainCamera = Camera.main;
            }
        }

        /// <summary>
        /// 扫描所有渲染器，找出能变色的（材质上带 _BaseColor 或 _Color 的），
        /// 记录"渲染器 + 颜色属性 ID"。只读 sharedMaterial，绝不实例化材质
        /// </summary>
        private FlashTarget[] BuildFlashTargets()
        {
            if (m_renderers.Count == 0)
            {
                CollectRenderers();
            }

            int baseColorId = Shader.PropertyToID(k_baseColorProp);
            int colorId = Shader.PropertyToID(k_colorProp);

            List<FlashTarget> targets = new List<FlashTarget>(m_renderers.Count);

            for (int i = 0; i < m_renderers.Count; i++)
            {
                Renderer renderer = m_renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                // 用 sharedMaterial 读着色器属性，避免生成材质实例
                Material material = renderer.sharedMaterial;
                if (material == null)
                {
                    continue;
                }

                if (material.HasProperty(baseColorId))
                {
                    targets.Add(new FlashTarget { Renderer = renderer, ColorPropertyId = baseColorId });
                }
                else if (material.HasProperty(colorId))
                {
                    targets.Add(new FlashTarget { Renderer = renderer, ColorPropertyId = colorId });
                }
            }

            return targets.ToArray();
        }

        private void OnEnable()
        {
            // 订阅受击事件：每次实际扣血都触发反馈
            if (m_unit != null) m_unit.HitTaken += HandleHitTaken;
        }

        private void OnDisable()
        {
            // 取消订阅，防止物体失活后仍被调用（内存泄漏/空引用）
            if (m_unit != null) m_unit.HitTaken -= HandleHitTaken;

            // 在闪白中途被禁用（如敌人瞬移/回收进池）时，协程会被暂停而不是结束，
            // 颜色会一直留着"惨白"状态。这里显式停掉并还原
            if (m_flashCoroutine != null)
            {
                StopCoroutine(m_flashCoroutine);
                m_flashCoroutine = null;
                ClearFlashColor();
            }
        }

        private void Update()
        {
            // 逐帧驱动所有正在飘动的文字：面向相机 + 上飘 + 淡出 + 到点回收
            for (int i = m_activeTexts.Count - 1; i >= 0; i--)
            {
                DamageTextEntry item = m_activeTexts[i];
                item.Age += Time.deltaTime;

                if (item.Age >= m_textLifetime)
                {
                    // 存活时间结束：把文字回收进对象池，并从活跃列表移除
                    item.CanvasGo.SetActive(false);
                    m_textPool.Enqueue(item);
                    m_activeTexts.RemoveAt(i);
                    continue;
                }

                // 面向主相机（保持数字始终正对屏幕，不随角色转身）
                if (m_mainCamera != null)
                {
                    item.CanvasGo.transform.LookAt(
                        item.CanvasGo.transform.position + m_mainCamera.transform.rotation * Vector3.forward,
                        m_mainCamera.transform.rotation * Vector3.up);
                }

                // 上飘：沿世界 Y 轴向上移动
                item.CanvasGo.transform.position += Vector3.up * (m_textRiseSpeed * Time.deltaTime);

                // 淡出：后半段逐渐变透明
                float alpha = Mathf.Lerp(1f, 0f, item.Age / m_textLifetime);
                Color color = item.Text.color;
                color.a = alpha;
                item.Text.color = color;
            }
        }

        /// <summary>
        /// 受击事件回调：命中后依次执行闪白、击退、飘字
        /// </summary>
        private void HandleHitTaken(CombatUnit attacker, EAttackType attackType, float damage)
        {
            if (m_enableFlash)
            {
                StartFlash();
            }

            if (m_enableKnockback)
            {
                ApplyKnockback(attacker);
            }

            if (m_enableDamageText)
            {
                SpawnDamageText(damage);
            }
        }

        // ====================================================================
        // 反馈 1：受击闪白
        // ====================================================================

        /// <summary>
        /// 开始闪白：把所有渲染器颜色临时设为高亮色，m_flashDuration 后还原
        /// 连续命中时会先停止上一次的协程，保证每次闪白都完整执行
        /// </summary>
        private void StartFlash()
        {
            // 没有可闪白的渲染器（材质不含颜色属性）就不用起协程了
            if (m_flashTargets.Length == 0)
            {
                return;
            }

            if (m_flashCoroutine != null)
            {
                StopCoroutine(m_flashCoroutine);
            }
            m_flashCoroutine = StartCoroutine(FlashCoroutine());
        }

        /// <summary>
        /// 闪白协程：先变色，等待后还原。
        /// 等待用缓存的 m_flashWait，避免每次命中都 new WaitForSeconds
        /// </summary>
        private IEnumerator FlashCoroutine()
        {
            ApplyFlashColor(m_flashColor);

            yield return m_flashWait;

            ClearFlashColor();

            m_flashCoroutine = null;
        }

        /// <summary>把闪白颜色以"属性覆盖"的方式写进所有渲染器（不改材质本身）</summary>
        private void ApplyFlashColor(Color color)
        {
            for (int i = 0; i < m_flashTargets.Length; i++)
            {
                Renderer renderer = m_flashTargets[i].Renderer;
                if (renderer == null)
                {
                    continue;
                }

                // 先读回该渲染器已有的属性块，只覆盖颜色，保留其它覆盖项
                renderer.GetPropertyBlock(m_propertyBlock, 0);
                m_propertyBlock.SetColor(m_flashTargets[i].ColorPropertyId, color);
                renderer.SetPropertyBlock(m_propertyBlock);
            }
        }

        /// <summary>
        /// 清除属性覆盖 → 渲染器自动回落到材质本身的颜色。
        /// 因此不需要再记录"原始颜色"，代码更简单也更省内存
        /// </summary>
        private void ClearFlashColor()
        {
            for (int i = 0; i < m_flashTargets.Length; i++)
            {
                Renderer renderer = m_flashTargets[i].Renderer;
                if (renderer == null)
                {
                    continue;
                }

                renderer.SetPropertyBlock(null);
            }
        }

        // ====================================================================
        // 反馈 2：受击击退
        // ====================================================================

        /// <summary>
        /// 击退：沿"受击单位背对攻击者"的水平方向施加一个冲量
        /// 需要单位带 Rigidbody（普通刚体即可，角色控制器类另配）
        /// </summary>
        /// <param name="attacker">攻击者的 CombatUnit</param>
        private void ApplyKnockback(CombatUnit attacker)
        {
            if (m_rigidbody == null) return;

            // 击退方向：从攻击者指向受击单位，拍平到水平面（去掉上下分量）
            Vector3 knockbackDir = transform.position - attacker.transform.position;
            knockbackDir.y = 0f;

            // 方向太小时给一个默认的后退方向，避免零向量报错
            if (knockbackDir.sqrMagnitude < 0.0001f)
            {
                knockbackDir = -attacker.transform.forward;
            }
            knockbackDir.Normalize();

            // 施加冲量：水平推力 + 少量上挑，形成"被砍飞"的手感
            Vector3 impulse = knockbackDir * m_knockbackForce + Vector3.up * m_knockbackUpward;
            m_rigidbody.AddForce(impulse, ForceMode.Impulse);
        }

        // ====================================================================
        // 反馈 3：伤害飘字
        // ====================================================================

        /// <summary>
        /// 生成伤害飘字：从对象池取文字（或新建），设置数字并加入活跃列表
        /// </summary>
        /// <param name="damage">本次造成的最终伤害</param>
        private void SpawnDamageText(float damage)
        {
            // 相机可能晚于敌人创建（异步加载/网络模式）：首次需要飘字时补找一次
            if (m_mainCamera == null)
            {
                m_mainCamera = Camera.main;
            }

            // 从池中取一个空闲文字，没有就新建
            DamageTextEntry entry;
            if (m_textPool.Count > 0)
            {
                entry = m_textPool.Dequeue();
                entry.CanvasGo.SetActive(true);
            }
            else
            {
                entry = CreateDamageTextEntry();
            }

            // 放到受击单位头顶偏上
            entry.CanvasGo.transform.position = transform.position + Vector3.up * m_textHeightOffset;

            // 写入伤害数值（取整显示更清爽）
            entry.Text.text = Mathf.RoundToInt(damage).ToString();

            // 重置透明度与存活时间
            Color color = m_damageTextColor;
            color.a = 1f;
            entry.Text.color = color;
            entry.Age = 0f;

            m_activeTexts.Add(entry);
        }

        /// <summary>
        /// 新建一条飘字：世界空间 Canvas 下挂 TextMeshProUGUI
        /// Canvas 尺寸调小以贴合"3D 文字贴在世界里"的比例
        /// </summary>
        private DamageTextEntry CreateDamageTextEntry()
        {
            // 外层：世界空间 Canvas
            GameObject canvasGo = new GameObject("DamageTextCanvas", typeof(Canvas));
            canvasGo.transform.SetParent(transform, false);

            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            // 世界空间 Canvas 默认 1 单位=1米，整体缩小到 1/100 让文字尺寸合适
            canvasGo.transform.localScale = Vector3.one * 0.01f;

            // 内层：文字本体
            GameObject textGo = new GameObject("Text", typeof(TextMeshProUGUI));
            textGo.transform.SetParent(canvasGo.transform, false);

            TextMeshProUGUI text = textGo.GetComponent<TextMeshProUGUI>();

            // 显式指定字体：使用 TMP 全局默认字体（若不指定，运行时新建的文字不会显示）
            TMP_FontAsset defaultFont = TMP_Settings.defaultFontAsset;
            if (defaultFont != null)
            {
                text.font = defaultFont;
            }
            text.fontSize = m_damageTextFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = m_damageTextColor;

            // 宽高给足，防止数字被裁切
            RectTransform rect = textGo.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(500f, 100f);

            return new DamageTextEntry(canvasGo, text);
        }

        /// <summary>
        /// 自动收集本物体及所有子物体的渲染器（含 SkinnedMeshRenderer）
        /// </summary>
        private void CollectRenderers()
        {
            m_renderers.Clear();
            GetComponentsInChildren(true, m_renderers);
        }

        /// <summary>
        /// 单条飘字的数据：Canvas 物体 + 文字组件 + 存活时间
        /// </summary>
        private class DamageTextEntry
        {
            public GameObject CanvasGo { get; }

            public TextMeshProUGUI Text { get; }

            public float Age { get; set; }

            public DamageTextEntry(GameObject canvasGo, TextMeshProUGUI text)
            {
                CanvasGo = canvasGo;
                Text = text;
                Age = 0f;
            }
        }
    }
}
