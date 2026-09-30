# Unity Editor Play Mode 动态方法拦截：引用笔记

**摘要（不超过 350 字）**：在具备动态 IL 能力的 Editor Mono 环境，可由已运行的 Editor 侧代码对当前域内已加载的 C# 方法安装 Harmony 补丁，以回调包裹或改写调用；这不是重编译游戏脚本，也不改写原 DLL。域重载会替换代码及静态状态，跨 Play 的重挂应保存“意图”并在新域、适当的 Play 状态重新解析目标和安装；禁用 Domain Reload 不能消除脚本变更引发的代码重载。Pipeline 的 `[HotReload]`/watch 是另一套需预先埋点的机制，不等同任意方法注入。以下均为文档研究，未在目标项目验证。

## 官方明确说明

- Harmony 在运行中的 CIL/.NET/Mono 方法上修改行为，不改磁盘文件；需要宿主先执行启动补丁的代码。Prefix 在原方法前，可改参数、结果或跳过原方法；Postfix 在后，可读改结果；Transpiler 在生成替换方法时处理 IL，**不是每次调用执行的回调**。它只作用于当前 AppDomain；可内联、泛型共享、无 IL 的 native/extern 方法和过早触发 Unity 外部方法均有限制。普通 prefix/postfix 不能直接包裹无 IL 的 Unity native 方法。[1][2][3]
- Unity 的 Mono 代码重载包含重编译、卸载/销毁旧程序集、加载新程序集。`[InitializeOnLoad]` 静态构造器在 Editor 启动、脚本重编译，以及启用该配置时进入 Play 的域重载后调用；它早于资产导入结束，不宜直接做资产加载。`SessionState` 在程序集重载间保留 Editor 会话键值，Editor 退出即清空；`EditorApplication.playModeStateChanged` 通知 Play 状态变化，订阅本身须随重载重新建立并避免重复订阅。[4][5][6][7]
- 禁用 Play 入口的 Domain Reload 会令静态字段与静态事件订阅跨 Play 保留，需要显式清理；它不是关闭所有代码重载的开关，资产刷新/脚本变更仍可触发代码重载。[4][8]
- Unity Pipeline 0.6 的 `[HotReload]` 在**项目程序集编译时**给被标记方法注入每次调用检查；默认 Roslyn 在内存编译并以 `Assembly.Load` 将结果并列加载（Editor Play Mode/Mono 开发构建），除指定 `assemblyDir` 外不写磁盘。只有带标记的方法能这样改；新方法不能在尚无编译调用点时成为重载入口。watch 在 Editor 中使用解释器后端，监视 `Assets` 下提及 `HotReload` 的 `.cs` 保存，监视期间关闭 Auto Refresh；watch 跨 domain reload 存活并重施已被 Play reload 擦除的 **Pipeline override**。这不表示任意 Harmony hook 自动恢复；手动刷新后仍须面对正常导入/重载。[9]
- .NET Framework 的加载指南区分 `Load`、`LoadFrom` 和字节数组的加载上下文，并警告同标识/多上下文会产生类型身份与绑定故障；这份指南**不是**特定 Unity/Mono 版本的兼容性保证。[10]

## 条件性架构推论

- 如果外部预编译 DLL 已由宿主引导加载，且其依赖能正确绑定，可用它提供补丁和回调；`Assembly.Load` 只是把新程序集加载到当前运行环境，**不会就地替换**既有 `Assembly-CSharp` 类型或自动重定向既有调用。基于 Harmony 的当前域限制和 Unity 的卸载/重载生命周期，补丁应视为随目标代码/域重载失效；重挂时重新取新域的 `MethodInfo`，按稳定意图去重、安装并可撤销。禁用 Play 域重载时反而要防止重复补丁和遗留回调。[1][2][4][6][8][10]
- 在运行中的 Editor 直接改写磁盘 `Assembly-CSharp.dll` 不能保证改变已加载的方法；它可能与 Unity 自己的编译/导入产物竞争，随后代码重载也可能覆盖该文件。**不应把磁盘覆盖当作热替换方案**。这是根据 Unity 重载流程及程序集绑定模型作出的风险判断，不是官方对每种文件锁定、覆盖顺序的保证。[4][10]
- 可将“目标方法标识和是否启用”保存在 `SessionState`，用 `[InitializeOnLoad]` 在新域重新注册 Play 状态监听，在进入合适的 Play 状态后重新解析并安装；退出 Play 或失去目标时清理。初始化时机与目标生命周期、静态订阅及异常回滚仍需针对实际 Editor/Mono 版本验证。[2][4][5][6][7]

## 本仓库源码观察（未在目标项目核验）

- [`UnityCliPipelineHandoff.cs`](../unitypkg/Editor/Infrastructure/UnityCliPipelineHandoff.cs) 的 `EnsureAppliedOnce` 将 Pipeline manager 的 AutoStart 设为 false、停止并核验其独立 Editor server，交接给本仓库控制面；不能假设 Pipeline 的独立 server 或 watch 在目标项目可直接使用。
- [`OfficialAttributeToolRegistry.cs`](../unitypkg/Editor/ProjectTools/OfficialAttributeToolRegistry.cs) 按声明方法所属程序集的 exact `com.unity.pipeline` Package ID 排除 Pipeline 包自身命令，非以名称/命名空间排除其他项目命令。

## 主要证据（10）

1. [Harmony Introduction](https://harmony.pardeike.net/articles/intro.html)（Mono、运行时/磁盘区别及前置引导）
2. [Harmony Patching](https://harmony.pardeike.net/articles/patching.html)（prefix/postfix/transpiler、当前域）
3. [Harmony Edge Cases](https://harmony.pardeike.net/articles/patching-edgecases.html)（内联、泛型、extern、启动时机）
4. [Unity: Code reload and the code lifecycle](https://docs.unity3d.com/6000.6/Documentation/Manual/programming-code-lifecycle.html)
5. [Unity: InitializeOnLoadAttribute](https://docs.unity3d.com/ScriptReference/InitializeOnLoadAttribute.html)
6. [Unity: SessionState](https://docs.unity3d.com/ScriptReference/SessionState.html)
7. [Unity: EditorApplication.playModeStateChanged](https://docs.unity3d.com/ScriptReference/EditorApplication-playModeStateChanged.html)
8. [Unity: Enter Play mode without domain reload](https://docs.unity3d.com/6000.6/Documentation/Manual/domain-reloading.html)
9. [Unity Pipeline 0.6: Hot reload](https://docs.unity3d.com/Packages/com.unity.pipeline@0.6/manual/hot-reload.html)
10. [Microsoft: Best Practices for Assembly Loading (.NET Framework)](https://learn.microsoft.com/en-us/dotnet/framework/deployment/best-practices-for-assembly-loading)
