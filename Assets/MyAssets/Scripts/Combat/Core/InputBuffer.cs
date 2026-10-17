namespace MyGame.Combat
{
    /// <summary>
    /// 输入缓冲器：用于连招系统的点击缓冲
    /// 核心规则：每段攻击动画期间最多缓冲1次点击，多余的点击直接丢弃
    /// 当动画播放完毕后，系统检查缓冲器：有缓冲则衔接下一段，无缓冲则回到待机
    /// 这样设计能避免"动画播放期间疯狂点击导致动画播完后还在执行缓存点击"的问题
    /// </summary>
    public class InputBuffer
    {
        /// <summary>是否已缓冲一次攻击输入</summary>
        private bool m_hasBufferedInput;

        /// <summary>是否已缓冲攻击输入（只读属性）</summary>
        public bool HasBufferedInput => m_hasBufferedInput;

        /// <summary>
        /// 缓冲一次攻击输入
        /// 如果已经缓冲过（m_hasBufferedInput 为 true），则本次调用被忽略
        /// 这保证了每段动画最多只缓冲1次点击，多余的点击被丢弃
        /// </summary>
        public void BufferAttack()
        {
            // 已有缓冲则丢弃本次输入，保证"每段仅缓冲1次"
            if (m_hasBufferedInput)
            {
                return;
            }

            m_hasBufferedInput = true;
        }

        /// <summary>
        /// 消费缓冲的输入：取出缓冲状态并清空
        /// 在攻击动画播放完毕后调用：返回 true 表示有缓冲输入，应衔接下一段连招
        /// </summary>
        /// <returns>是否有被缓冲的攻击输入</returns>
        public bool ConsumeBuffer()
        {
            bool hadInput = m_hasBufferedInput;
            m_hasBufferedInput = false;
            return hadInput;
        }

        /// <summary>
        /// 清空缓冲：用于连招中断场景（如闪避打断攻击时）
        /// 确保中断后不会残留旧的点击缓冲导致意外衔接
        /// </summary>
        public void ClearBuffer()
        {
            m_hasBufferedInput = false;
        }
    }
}
