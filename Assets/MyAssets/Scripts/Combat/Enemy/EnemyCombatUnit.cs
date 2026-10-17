namespace MyGame.Combat
{
    /// <summary>
    /// 敌人战斗单位：继承通用 CombatUnit，定制敌人的死亡流程
    ///
    /// 与基类的区别：
    /// - 基类 CombatUnit.HandleDeath() 死亡时会直接 gameObject.SetActive(false)，
    ///   物体会瞬间消失，死亡动画来不及播放
    /// - 敌人需要"播放死亡动画 → 尸体停留几秒 → 销毁"的完整击败反馈，
    ///   所以重写 HandleDeath()：不隐藏物体
    ///
    /// 注意：C# 的 event 只能在声明它的类中触发，子类无法直接调用基类的 Died 事件，
    /// 因此本类重写后不触发 Died 事件；EnemyAI 通过每帧轮询 IsAlive 检测死亡
    /// （EnemyAI 本来就在 Update 中运行，轮询零额外成本）
    ///
    /// 挂载位置：与 EnemyAI、NavMeshAgent 一起挂在敌人根物体上，
    /// Inspector 中 Team 必须设为 Enemy
    /// </summary>
    public class EnemyCombatUnit : CombatUnit
    {
        /// <summary>
        /// 重写死亡处理：只记录日志，不隐藏物体
        /// 死亡动画播放、停止移动、延迟销毁等后续表现全部由 EnemyAI 的 Dead 状态负责
        /// </summary>
        protected override void HandleDeath()
        {
            // 保留死亡日志，方便调试确认击杀流程
            UnityEngine.Debug.Log($"[{gameObject.name}] 已死亡，等待 EnemyAI 播放死亡表现", this);

            // 故意不调用 base.HandleDeath()：
            // base 中会执行 gameObject.SetActive(false)，导致敌人瞬间消失、死亡动画无法播放
            // 敌人的死亡检测由 EnemyAI 在 Update 中通过 IsAlive == false 完成
        }
    }
}
