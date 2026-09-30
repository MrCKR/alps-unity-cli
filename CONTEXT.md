# UnityCLI 领域语言

UnityCLI 为 AI 提供 Unity 内的动作与观察能力。以下术语用于区分能力、目标身份和动态 hook 的有效范围。

## Language

**Editor Instance**：CLI 可独立选择和寻址的一个 Unity Editor 实例。多个实例是不同的操作目标。
_Avoid_：把项目等同于当前运行实例。

**Editor Session**：同一 Editor 实例的一次启动生命周期。进入或退出 Play、域重载都不产生新的 Editor Session，Editor 重启则产生新的会话。
_Avoid_：把 Editor Session 与 Play Session 混用。

**Play Session**：一次进入 Play 到退出 Play 的运行周期。同一 Editor Session 可以包含多个 Play Session。
_Avoid_：用“会话”同时指代 Editor 启动和一次游戏运行。

**Hook Definition**：某一 Editor Session 中获授权的拦截意图，包括目标、回调和生效范围。它不是当前域中的实际补丁。
_Avoid_：用“补丁还在”表示定义仍被保留。

**Hook Attachment**：Hook Definition 在当前代码域中实际建立的拦截关系。定义可以跨域保留，Attachment 不能沿用旧域身份。
_Avoid_：把重挂描述成旧委托继续存活。

**Hook Invocation**：一次被拦截的方法调用。安装成功与发生 Invocation 是不同事实。
_Avoid_：把安装成功当作游戏行为已被观察或修改。

**Hook Event**：由 Invocation 或 hook 生命周期产生的有界业务记录。事件用于读取观察结果，不是仍未完成的 CLI 请求。
_Avoid_：把事件缓冲区称为请求日志或永久审计档案。
