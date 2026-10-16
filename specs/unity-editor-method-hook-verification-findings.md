# Method Hook 技术验证：阶段 1（机制可行性）结果

本文件记录 Harmony 接入的第一阶段真实验证结果。验证在 Dev 沙盒进行，不含产品代码改动，也不是产品能力说明。

## 1. 验证目标与边界

阶段 1 只回答：**Harmony 在本项目最低支持线的 Unity/Mono 上能否加载并完成方法拦截，依赖闭包有多大，以及拦截的有效条件是什么。**

不在本阶段范围：Play 模式与跨 Play 生命周期、Domain Reload 自动重挂、Bridge/CLI 集成、事件与持久化、多实例隔离。这些属于阶段 2/3。

## 2. 环境

| 项 | 值 |
| --- | --- |
| Unity | 2022.3.61f1（`C:\Program Files\Unity\Unity-2022.3.61f1\Editor\Unity.exe`） |
| 运行时 | Mono 6.13.0 (Visual Studio built mono) |
| 项目 | `Dev/UnityProject`（Unity 2022.3.61f1，API 兼容级别 `apiCompatibilityLevel: 6`） |
| 目标方法所在程序集 | `Assembly-CSharp`（真实游戏程序集位置） |
| 验证器所在程序集 | `Assembly-CSharp-Editor` |
| 启动方式 | 批处理 Editor + `-executeMethod`，逐进程注入 `UNITYCLI_DATA_DIR=%LOCALAPPDATA%\UnityCli-Dev` |
| 隔离结论 | 正式根未出现 Dev 记录；Jump 项目进程 PID 3068 与启动时间未变化 |

## 3. 依赖闭包（实测）

下载并解包官方 NuGet `Lib.Harmony 2.4.2`：

- `lib/net472/0Harmony.dll`，2461696 字节，SHA-256 `7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c`。
- 程序集标识：`0Harmony, Version=2.4.2.0, Culture=neutral, PublicKeyToken=null`（无强名称）。
- nuspec 中 `net472` 依赖组为空：**零传递依赖**，闭包就是该单个 DLL。
- `lib/netstandard2.0/` 只有占位文件 `_._`，即该包不提供 netstandard2.0 构建；Unity 侧应使用 `net472`。
- 包内无独立 MonoMod DLL，相关能力已并入 `0Harmony.dll`。
- 其他可用目标框架：net35、net452、net48、net5.0–net10.0、netcoreapp3.0/3.1；`net7.0` 及以上额外依赖 `System.Text.Json`。

结论：计划中担心的"多 DLL 传递闭包、MonoMod 冲突"在当前选定的 `net472` 产物上不存在；冲突面收敛到单一 assembly identity `0Harmony`。

## 4. 机制验证结果

两次独立通道执行同一组断言：`direct` 为普通 C# 调用，`reflect` 为反射调用。

| 断言组 | direct | reflect |
| --- | --- | --- |
| 基线行为（Add/Describe/Bump/SecretMul/StaticTag/hits） | 全通过 | 全通过 |
| postfix 修改返回值 | 失败 | 通过（5 → 1005） |
| prefix 观察参数与 `__instance` | 失败 | 通过（a=7, b=8） |
| prefix 修改原参数 | 失败 | 通过（2+3 → 103） |
| prefix 跳过原方法并设置结果 | 通过 | 通过（返回 `hooked`，原方法未执行） |
| 私有方法 postfix | 失败 | 通过（12 → 120） |
| ref 参数 prefix 改写 | 通过 | 通过（1 → 16） |
| 静态方法 postfix | 失败 | 通过 |
| `GetPatchInfo` 读回 | 通过（prefixes=1 postfixes=1 owners=unitycli.hookprobe） | 同 |
| 撤销后完全还原 | 通过 | 通过 |
| 撤销后可再次安装 | 失败 | 通过 |

汇总：`mechanismVerdict=PASS`；`reflectPassFailures=0`；`directPassFailures=9`。共 50 项通过、9 项失败，9 项失败全部来自 direct 通道。

`Patch` 每次返回替换方法（如 `HookProbe.ProbeTarget.Add_Patch1`），说明补丁确实建立；`UnpatchAll` 后行为完全还原，证明撤销是真实的。

失败的 9 项：`direct.B.postfix.result`、`direct.C.prefix.observed`、`direct.C.prefix.instance`、`direct.C.postfix.value`、`direct.C.combined`、`direct.D.argmutation`、`direct.F.private.postfix`、`direct.H.static.result`、`direct.K.repatch`——涉及 `Add`、`SecretMul`、`StaticTag`。

## 5. 关键结论：补丁的生效条件

补充的时序与内联边界实验：

| 阶段 | 场景 | 结果 |
| --- | --- | --- |
| L | 补丁**之后**才创建委托并首次调用（调用点编译晚于补丁），跨程序集 | 通过（1005） |
| M | 同程序集、体量极小、可被 Mono 内联的目标 `Fresh`，调用点编译晚于补丁 | 通过（1005） |
| N | 同程序集、含 `try/catch` 的目标 `Guarded` | 通过（1005） |
| direct | 调用点在补丁**之前**已经编译 | 失败（9 项） |

由这四组结果合并出的规则：

> **补丁安装之后才首次编译的调用点会被拦截；补丁安装之前已经编译的调用点不会。** 是否内联由 Mono 在编译调用点时决定，因此"目标方法体量小"本身不构成失败，真正的分界是调用点的编译时机。

需要明确的三个后果：

1. 反射调用（始终经过方法入口）与晚于补丁编译的普通调用都能被拦截，机制本身没有问题。
2. `Patch` 成功、替换方法存在、`GetPatchInfo` 可读回，**都不等于**调用点已被拦截；不能用安装成功推断业务命中。
3. 在已经运行起来的目标进程中"事后安装"可能静默无效，且没有任何错误返回。因此命中必须由真实观测验证，"没有命中记录"不能反推"方法未被调用"。

对产品设计的直接影响：

- 计划中 `activation: play` + 进入 Play 时重建域的自动重挂方向是必要的：新域中游戏调用点的首次编译发生在重挂之后，这是补丁能生效的前提。
- 必须在游戏首帧逻辑之前完成安装；中途安装要明确披露可能漏掉已经编译的调用点。
- `debug.hook.install` 的语义保持"证明定义已持久化且补丁已建立"，不得夸大；是否命中由 `events` 或业务副作用单独证明。
- `info`/`events` 需要能表达"已安装但无命中"，并提示可改用更晚编译或体量更大的目标方法。

## 6. 与计划中的判断对照

| 计划中的假设 | 阶段 1 结论 |
| --- | --- |
| Harmony 需要多 DLL 闭包、可能存在 MonoMod 冲突 | 否。`net472` 产物零传递依赖，单文件 |
| 方法在所属磁盘程序集被改写 | 否。只作用于当前域，磁盘 DLL 未变更 |
| Prefix/Postfix 可观察、改参、改结果、跳过原方法 | 是，阶段 1 全部验证通过 |
| 私有、静态、ref 目标 | 是，均验证通过 |
| 停用只撤销自己的效果 | 是，撤销后行为完全还原且可再次安装 |
| 内联可能绕过 detour | 是，但精确条件是"调用点编译时机"，而非仅"方法体量小" |

## 7. 阶段 2：Play 模式、生命周期与共存

在同一 Editor 会话内用状态机驱动（状态落盘，`[InitializeOnLoadMethod]` 跨域恢复），Play 模式实测 19 项断言全部通过：

| 断言 | 结果 |
| --- | --- |
| Edit 模式装补丁后命中 | 通过（1003） |
| 两个 Harmony owner 共存 | 通过 |
| 本 CLI `UnpatchAll` 后第三方 owner 仍生效 | 通过（`foreign-patched`） |
| 本 CLI 撤销后原行为还原 | 通过（3） |
| Play 1（默认设置，进入 Play 触发域重载）覆盖 | 通过：patched=21，unpatched=0 |
| Play 1 的 `Awake` 已覆盖 | 通过：awake result=1300 |
| Play 1 的 `Start` 已覆盖 | 通过：start result=1300 |
| Play 2（关闭进入 Play 的域重载） | 通过：补丁存活，patched=20，unpatched=0 |
| 重载边界前后 | 通过：patched=251，unpatched=0 |
| 重载后单一定义、无重复应用 | 通过：postfixes=1，doubled=0 |
| 全程双重应用次数 | 0 |
| 同一阶段单一补丁 | 通过（多处） |

四个决定性观察：

1. **默认设置下进入 Play 会重载域，Edit 模式安装的补丁不会存活。** 旧域补丁在重载后被确认消失（`postfixesBefore=0`），必须重挂。
2. **重挂必须发生在新域的最早时机，不能等 `EditorApplication.update`。** 实测 update 循环发现 Play 时已晚约 300ms，会错过 `Awake`/`Start` 和首批帧；改到 `[InitializeOnLoadMethod]` 后 `Awake`/`Start` 及全部 Update 都被覆盖。这是本次验证对产品设计影响最大的一条。
3. **关闭进入 Play 的域重载时补丁跨 Play 存活且不重复**（`doubled=0`），但产品不能依赖用户配置，仍需按每次新域重挂。
4. **域重载会清空静态字段且不会重跑 `Awake`。** 探针自身因静态目标引用被清空而抛了 8001 次 NRE——这正是产品 hook host 必须避开的同类假设：重挂逻辑不能依赖跨域静态状态，且必须在 `Update` 等热路径上做空值防御。

## 8. 插件形态与开销

使用与产品计划一致的真实形态单独验证（10 项断言全部通过）：

- `0Harmony.dll` 放在 `Assets` 下作为受管插件，Unity **自动导入并在域启动时加载**（`location` 即 Assets 路径，无需 `Assembly.LoadFrom`）。
- `overrideReferences: true` + `precompiledReferences: ["0Harmony.dll"]` 的 asmdef 能编译期解析 `HarmonyLib`，且 `HarmonyLib.Harmony` 来自同一程序集。
- 编译期 API 安装补丁、`Harmony.GetPatchInfo` 读回、`UnpatchAll` 还原均正常。
- asmdef 程序集**不能引用**预定义程序集 `Assembly-CSharp`；目标必须按 `Type.GetType("..., Assembly-CSharp")` 运行时解析。产品后端不应编译期依赖用户游戏代码，这一点与设计一致。

开销：以委托调用 200 万次对比，baseline 2.2ns/call、patched 2.0ns/call。两者差异在测量噪声内，说明**单次调用的补丁开销相对托管调用本身很小**；本方法无法给出精确增量，不构成严格基准，不得引用为具体数字。

## 9. 仍然未验证

- **Bridge/CLI 集成**：八个 `method-hook.*` Tool、事件缓冲与 cursor、定义持久化上限、并发与准入—全部属于产品实现，尚未编写，因此无从验证。
- **多实例隔离**：机制层面每个 Editor 拥有独立域与宿主，但公开层的 `--instance` 路由隔离需要产品实现；同时需要第二个 Dev Editor，而治理规则要求对准确项目与进程动作单独授权，本轮未取得。
- **Cookies/HMAC、容量上限、临时文件盘点**：依赖产品实现。
- **其他 Unity 版本**：本轮只验证了 minimum 线 2022.3.61f1。
- **稳定运行/开销基准**：未做长时间或多场景的开销与稳定性统计。
- **插件导入后的玩家构建排除**：插件默认对所有平台生效，产品需要正确的 PluginImporter 投影（Editor-only、不 auto-reference）；本轮未验证该投影。

## 10. 本轮产生的临时产物

| 路径 | 用途 |
| --- | --- |
| `Dev/UnityProject/Assets/HookProbe/` | 目标方法、Play 驱动器、插件 DLL |
| `Dev/UnityProject/Assets/Editor/HookProbe/` | 阶段 1/2 验证器、插件 asmdef 测试 |
| `Dev/UnityProject/Library/UnityCli/hook-probe/` | 被测 DLL 与日志 |
| `%LOCALAPPDATA%\UnityCli-Dev\.temp\hook-probe\` | 下载产物、结果与信号文件 |

以上都不属于产品代码，也不进入仓库版本控制；删除属于非临时目标的递归删除，需要单独批准。

## 11. 原始证据

以下为验证器直接输出的结果文件原文（删除临时区前归档）。

### stage1-results.txt

```text
=== HookProbe stage1 ===
passed=50 failed=9
mechanismVerdict=PASS
directPassFailures=9 reflectPassFailures=0
env :: unity=2022.3.61f1 | framework=Mono 6.13.0 (Visual Studio built mono) | dataDir=C:\Users\<user>\AppData\Local\UnityCli-Dev
harmonyDll :: D:\workspace\alps-unity-cli\Dev\UnityProject\Library\UnityCli\hook-probe\0Harmony.dll | bytes=2461696
harmonyAssembly :: 0Harmony, Version=2.4.2.0, Culture=neutral, PublicKeyToken=null | location=D:\workspace\alps-unity-cli\Dev\UnityProject\Library\UnityCli\hook-probe\0Harmony.dll
harmonyInstance :: id=unitycli.hookprobe | type=HarmonyLib.Harmony
resolve-targets :: PASS | add=True describe=True bump=True secretMul(private)=True staticTag=True
direct.baseline.Add :: PASS | Add(2,3)=5
direct.baseline.Describe :: PASS | Describe=orig:x
direct.baseline.Bump :: PASS | ref 1 -> 11
direct.baseline.SecretMul :: PASS | SecretMul(4)=12
direct.baseline.StaticTag :: PASS | StaticTag=static-original
direct.baseline.hits :: PASS | hits=4
patch.direct-postfix-add :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch1 | overloadParams=5
direct.B.patch :: ok
direct.B.postfix.result :: FAIL | Add(2,3) 实际=5 期望 1005
direct.B.postfix.didRunOriginal :: PASS | hits=5 (应+1，证明原方法已执行)
patch.direct-observe :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch3 | overloadParams=5
direct.C.patch :: ok
direct.C.prefix.observed :: FAIL | observed a=-2147483648 b=-2147483648
direct.C.prefix.instance :: FAIL | __instance 是否命中调用对象
direct.C.postfix.value :: FAIL | postfix 读到 __result=-2147483648
direct.C.combined :: FAIL | Add(7,8) 实际=15 期望 1015
patch.direct-mutate-arg :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch6 | overloadParams=5
direct.D.patch :: ok
direct.D.argmutation :: FAIL | Add(2,3) 实际=5 期望 103（a 被改成 100，原方法仍执行 b=3）
patch.direct-skip :: original=ProbeTarget.Describe | replacement=HookProbe.ProbeTarget.Describe_Patch1 | overloadParams=5
direct.E.patch :: ok
direct.E.skip.result :: PASS | Describe 实际=hooked 期望 hooked
direct.E.skip.didNotRunOriginal :: PASS | hits 未增加: 7 -> 7
patch.direct-private :: original=ProbeTarget.SecretMul | replacement=HookProbe.ProbeTarget.SecretMul_Patch1 | overloadParams=5
direct.F.patch :: ok
direct.F.private.postfix :: FAIL | SecretMul(4) 实际=12 期望 120（12*10）
patch.direct-ref-param :: original=ProbeTarget.Bump | replacement=HookProbe.ProbeTarget.Bump_Patch1 | overloadParams=5
direct.G.patch :: ok
direct.G.refparam :: PASS | ref 1 -> 16 期望 16（prefix+5 后原方法+10）
patch.direct-static :: original=ProbeTarget.StaticTag | replacement=HookProbe.ProbeTarget.StaticTag_Patch1 | overloadParams=5
direct.H.patch :: ok
direct.H.static.result :: FAIL | StaticTag 实际=static-original 期望 patched-static
direct.H.static.didRunOriginal :: PASS | staticHits 1 -> 2
patch.direct-patchinfo :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch8 | overloadParams=5
direct.I.patch :: ok
direct.I.patchInfo :: prefixes=1 postfixes=1 owners=unitycli.hookprobe
direct.J.restored.Add :: PASS | Add(2,3) 实际=5 期望 5
direct.J.restored.Describe :: PASS | Describe 实际=orig:x 期望 orig:x
direct.J.restored.SecretMul :: PASS | SecretMul(4) 实际=12 期望 12
direct.J.restored.Bump :: PASS | ref 1 -> 11 期望 11
direct.J.restored.StaticTag :: PASS | StaticTag 实际=static-original 期望 static-original
patch.direct-repatch :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch11 | overloadParams=5
direct.K.patch :: ok
direct.K.repatch :: FAIL | 重新安装后 Add(2,3) 实际=5 期望 1005
direct.K.restored-again :: PASS | 再次撤销后 Add(2,3) 实际=5 期望 5
reflect.baseline.Add :: PASS | Add(2,3)=5
reflect.baseline.Describe :: PASS | Describe=orig:x
reflect.baseline.Bump :: PASS | ref 1 -> 11
reflect.baseline.SecretMul :: PASS | SecretMul(4)=12
reflect.baseline.StaticTag :: PASS | StaticTag=static-original
reflect.baseline.hits :: PASS | hits=4
patch.reflect-postfix-add :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch13 | overloadParams=5
reflect.B.patch :: ok
reflect.B.postfix.result :: PASS | Add(2,3) 实际=1005 期望 1005
reflect.B.postfix.didRunOriginal :: PASS | hits=5 (应+1，证明原方法已执行)
patch.reflect-observe :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch15 | overloadParams=5
reflect.C.patch :: ok
reflect.C.prefix.observed :: PASS | observed a=7 b=8
reflect.C.prefix.instance :: PASS | __instance 是否命中调用对象
reflect.C.postfix.value :: PASS | postfix 读到 __result=15
reflect.C.combined :: PASS | Add(7,8) 实际=1015 期望 1015
patch.reflect-mutate-arg :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch18 | overloadParams=5
reflect.D.patch :: ok
reflect.D.argmutation :: PASS | Add(2,3) 实际=103 期望 103（a 被改成 100，原方法仍执行 b=3）
patch.reflect-skip :: original=ProbeTarget.Describe | replacement=HookProbe.ProbeTarget.Describe_Patch3 | overloadParams=5
reflect.E.patch :: ok
reflect.E.skip.result :: PASS | Describe 实际=hooked 期望 hooked
reflect.E.skip.didNotRunOriginal :: PASS | hits 未增加: 7 -> 7
patch.reflect-private :: original=ProbeTarget.SecretMul | replacement=HookProbe.ProbeTarget.SecretMul_Patch3 | overloadParams=5
reflect.F.patch :: ok
reflect.F.private.postfix :: PASS | SecretMul(4) 实际=120 期望 120（12*10）
patch.reflect-ref-param :: original=ProbeTarget.Bump | replacement=HookProbe.ProbeTarget.Bump_Patch3 | overloadParams=5
reflect.G.patch :: ok
reflect.G.refparam :: PASS | ref 1 -> 16 期望 16（prefix+5 后原方法+10）
patch.reflect-static :: original=ProbeTarget.StaticTag | replacement=HookProbe.ProbeTarget.StaticTag_Patch3 | overloadParams=5
reflect.H.patch :: ok
reflect.H.static.result :: PASS | StaticTag 实际=patched-static 期望 patched-static
reflect.H.static.didRunOriginal :: PASS | staticHits 4 -> 5
patch.reflect-patchinfo :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch20 | overloadParams=5
reflect.I.patch :: ok
reflect.I.patchInfo :: prefixes=1 postfixes=1 owners=unitycli.hookprobe
reflect.J.restored.Add :: PASS | Add(2,3) 实际=5 期望 5
reflect.J.restored.Describe :: PASS | Describe 实际=orig:x 期望 orig:x
reflect.J.restored.SecretMul :: PASS | SecretMul(4) 实际=12 期望 12
reflect.J.restored.Bump :: PASS | ref 1 -> 11 期望 11
reflect.J.restored.StaticTag :: PASS | StaticTag 实际=static-original 期望 static-original
patch.reflect-repatch :: original=ProbeTarget.Add | replacement=HookProbe.ProbeTarget.Add_Patch23 | overloadParams=5
reflect.K.patch :: ok
reflect.K.repatch :: PASS | 重新安装后 Add(2,3) 实际=1005 期望 1005
reflect.K.restored-again :: PASS | 再次撤销后 Add(2,3) 实际=5 期望 5
L.resolve.Fresh :: PASS | fresh=True
patch.fresh-pre-jit :: original=ProbeTarget.Fresh | replacement=HookProbe.ProbeTarget.Fresh_Patch1 | overloadParams=5
L.patch :: ok
L.postPatchCaller.patch-visible :: PASS | 调用点编译晚于补丁: Fresh(2,3) 实际=1005 期望 1005
L.postPatchCaller.didRunOriginal :: PASS | hits=1 期望 1
L.postPatchCaller.restored :: PASS | 撤销后 Fresh(2,3) 实际=5 期望 5
patch.same-asm-inlinable :: original=ProbeTarget.Fresh | replacement=HookProbe.ProbeTarget.Fresh_Patch3 | overloadParams=5
M.patch :: ok
M.sameAssembly.inlinable :: PASS | 同程序集可内联目标: 实际=1005 期望 1005
patch.same-asm-guarded :: original=ProbeTarget.Guarded | replacement=HookProbe.ProbeTarget.Guarded_Patch1 | overloadParams=5
N.patch :: ok
N.sameAssembly.guarded :: PASS | 同程序集不可内联目标: 实际=1005 期望 1005
finding.mechanism :: 反射通道失败数=0 -> Harmony 补丁机制可用
finding.patchRule :: 已编译的调用点看不到补丁；补丁之后才编译的调用点可被拦截（内联与否由 Mono 决定）
finding.preExistingCallers :: 直接调用通道失败数=9 -> 这些调用点在补丁之前已经编译
```

### stage2-results.txt

```text
=== HookProbe stage2 ===
passed=19 failed=0
verdict=PASS
domainGenerations=2
arm.methodsResolved :: PASS | targets add=True staticTag=True
arm.ownPatchedEdit :: PASS | 补丁后结果=1003 期望 1003
arm.foreignPatched :: PASS | foreign 结果=foreign-patched
arm.foreignSurvivesOwnUnpatch :: PASS | own 撤销后 foreign 结果=foreign-patched
arm.ownUnpatchRestores :: PASS | own 撤销后结果=3 期望 3
play1.singlePatch :: PASS | postfixes=1
between.enterPlayModeReloadDisabled :: PASS | 已关闭进入 Play 的域重载
between.singlePatch :: PASS | postfixes=1
play2.singlePatch :: PASS | postfixes=1
reload.detected :: PASS | domainGeneration 0 -> 2
reload.singlePatchAfterReattach :: PASS | 重挂后 postfixes=1
signal.totalPatched :: PASS | totalPatched=312 unpatched=0
signal.play1Coverage :: PASS | play1 patched=21 unpatched=0
signal.play1AwakeCovered :: PASS | play1 awakePatched=1 awakeUnpatched=0
signal.play1StartCovered :: PASS | play1 startPatched=1 startUnpatched=0
signal.play2Coverage :: PASS | play2 patched=20 unpatched=0
signal.reloadBoundaryCoverage :: PASS | 重载前后 patched=251 unpatched=0
signal.afterReloadCoverage :: PASS | afterReload patched=20 unpatched=0
signal.noDoubleApplication :: PASS | doubleApplication=0
```

### plugin-results.txt

```text
=== PluginProbe ===
passed=10 failed=0
verdict=PASS
plugin.loadedBeforeAnyLoadFrom :: PASS | domain 内已加载 0Harmony=True
plugin.locationIsProjectAsset :: PASS | location=D:\workspace\alps-unity-cli\Dev\UnityProject\Assets\HookProbe\Plugins\0Harmony.dll
plugin.assemblyIdentity :: PASS | identity=0Harmony, Version=2.4.2.0, Culture=neutral, PublicKeyToken=null
plugin.compileTimeTypeResolved :: PASS | HarmonyLib.Harmony 来自同一程序集
target.resolvedAtRuntime :: PASS | ProbeTarget=True
patch.installedViaCompileTimeApi :: PASS | replacement=HookProbe.ProbeTarget.Add_Patch1
patch.interceptedViaReflection :: PASS | 结果=1003 期望 1003
patch.infoReadable :: PASS | postfixes=1 owners=unitycli.pluginprobe
patch.unpatchRestores :: PASS | 撤销后结果=3 期望 3
overhead.measured :: PASS | baseline=2.2ns/call patched=2.0ns/call ratio=0.90
```


## 12. 产品实现验证：新增实测结论

本节记录在实现并安装当前 canonical Package 后，于同一 Dev 沙盒（`./Dev/UnityProject`，批处理 Editor + 逐进程 `UNITYCLI_DATA_DIR=%LOCALAPPDATA%\UnityCli-Dev`）真实调用公开 CLI 得到的结论。命令、envelope 与业务 readback 见本节条目；完整的逐项断言记录保存在 Dev 临时区。

### 12.1 已确认可用的公开行为

| 行为 | 证据 |
| --- | --- |
| group/describe 自足 | `tools group` 返回 exact `method-hook` 摘要；`tools group list method-hook` 恰为八个 Tool；`tools describe debug.hook.install` 的公开字段只有 name/description/inputSchema/destructiveHint/sideEffects/playModeSupport/supportsDryRun/examples/target |
| dry-run 无副作用 | `--dry-run` 返回 plannedEffects 且 `Library/UnityCli/code-exec-temp` 为空、定义列表仍为空 |
| 无代码捕获不启动 compiler | 只带 capture 的 install 后 callbacks 两个槽位 present=false，`code-exec-temp` 无文件 |
| 最早时机重挂 | 进入 Play（默认设置会重载域）后，事件页里最早的 prefix 事件 `frame=0`，与探针 `awake` 同帧之前已 armed |
| 真实命中与配对 | 同一 Invocation id 同时出现 prefix 与 postfix 事件；探针业务结果在只观察时保持 300 不变 |
| 改参与类型化回写 | `SetArgument` 在普通参数上生效（`Add(2,3)` 被改成 100+3）；`ref int` 目标的调用方实际看到 `1+5+10=16` |
| 跳过原方法 | `SkipOriginal` 后返回值被替换且原方法 hits 不增加 |
| 静态与私有目标 | 静态方法 postfix 替换结果；私有方法 postfix 正常生效 |
| 私有字段捕获 | `instanceFields` 捕获私有 `hits` 字段并在事件中读出 |
| 回调编译与业务效果 | `replace` 带 postfixCode 后编译成功（新产物），补丁后编译的调用点返回 1300 |
| Emit 与 State | 回调内 `ctx.State` 自增并 `Emit` 出可读 custom 事件；两处事件序列一致 |
| 启停与删除语义 | disable 后行为恢复、enable 后再次生效、remove 后行为恢复且再次 remove 返回 `E_HOOK_NOT_FOUND`；已 armed/disable 的重复调用是 `changed:false` 的 no-op |
| 事件 cursor | 无 cursor 返回尾页；install 返回的 eventCursor 可作为起点续读；换筛选返回 `E_CONTINUATION_STALE`；多 reader 互不消费 |
| 多实例隔离 | A 的定义在 B 不可见；A 的 hookId 在 B 为 `E_HOOK_NOT_FOUND`；A 的 cursor 在 B 为 `E_CONTINUATION_STALE`；B 的 mutation 为 `E_HOOK_SESSION_STALE`；B 无 hook 快照文件；无 `--instance` 时返回 `E_INSTANCE_REQUIRED` |
| 重启作废 | 重启 Editor 后同一 snapshot 文件仍在磁盘，但新 session 定义数为 0，旧变量不恢复 |
| 临时文件有界 | `Library/UnityCli/method-hooks` 只有 definitions.json / events.json 两个固定文件；编译临时目录在每次编译后清零 |

### 12.2 实现期确认的四条硬事实

1. **只靠 `__args` 无法保证 by-ref 参数回写。** 实测 `void Bump(ref int)` 在 prefix 只改 `__args[0]` 时，原方法内部的 `+= 10` 不会回到调用方（观察到 6 而不是 16），postfix 读到的 `__args[0]` 同样是旧值。因此后端按 exact 目标签名 emit 类型化 `ref` 参数并在派发前后做精确回写；普通参数仍经 `__args`，避免要求用户代码能命名私有声明类型。
2. **动态回调的类型标识符不能直接用编译键。** 编译键是十六进制，可能以数字开头，生成的 `namespace ... .5a27...` 会编译失败（CS0116）；现在固定加字母前缀，且源码生成与类型查找共用同一推导。
3. **恢复的定义必须显式进入 host 定义表。** 重挂时只重建 Attachment 而未登记定义，会让 list/info/events 看不到已生效的 hook（安装、启停与故障处理看似正常）。修复后跨 Play 与跨 reload 的定义、revision 与 State 都能读回。
4. **大页读取受框架 read budget 绝对上限约束。** 终端的 member 读取会在同一次请求内被计费多次（实测 3 次），每事件约 66 getters，绝对上限 10000 下实测 150 条可返回、200 条被 getters 预算拒绝；经项目所有者裁定，events 页冻结为默认 100 / 上限 128（而非计划里的建议值），tool 内同时显式声明本动作使用的读取预算。完整上限清单见 `unitypkg/Editor/Infrastructure/MethodHooks/MethodHookLimits.cs`。

### 12.3 本节验证的边界

- 只验证了 2022.3.61f1；其他版本的结论不变，也未被本轮验证。
- 多实例验证使用授权的 `./Dev/UnityProject-HookPeer`（批处理 Editor），不涉及任何真实用户项目。
- 本节未重复阶段 1/2 的机制实验（内联边界、第三方 owner 共存、单次调用开销），那些结论仍以第 4～8 节为准。

### 12.4 陌生 AI 门禁状态

本轮唯一一个定向 `unity-cli-gate` 未能执行：`.pi/agents/unity-cli-gate.md` 固定使用 `ext:windows-pwsh/pwsh`，而本次会话没有 `windows-pwsh` 扩展（只有 `persistent-pwsh`、`pwsh-tools` 等），因此子代理没有可用工具，两次全新会话都只产出无法解析的文本、没有发出任何真实 CLI 调用；Dev Bridge 日志在门禁时间窗内没有任何请求。

按 `unity-cli-validation`，`windows-pwsh` 不可用时这属于环境阻塞，禁止回退到其他 shell 或代理。解除条件：在提供 `windows-pwsh` 扩展的会话中重新运行一次全新门禁（任务与边界不变）。

### 12.5 共享编译与预算：本轮追加核实

针对“统一动态编译与执行预算”逐条核对后确认并修正了两点：

1. **累计 bytes 必须在加载新产物之前用真实值核对。** 原实现只在编译前用 `additionalBytes = 0` 检查，单次编译产物最多可让累计值超出上限一个产物大小；现在编译成功、`Assembly.Load` 之前用真实字节数再次核对，超限则不加载该产物（`E_HOOK_COMPILE_BUDGET_EXHAUSTED`），disable/remove/info 路径完全不引用预算检查。
2. **引用指纹必须按域快照一次。** 原实现每次编译都重算引用集摘要，于是同域内任何后续加载（上一个回调程序集、`debug.code.execute` 的动态程序集、其他功能的惰性加载）都会改变编译键，导致“同域同 key 复用”实际不可达。Dev 实测证据：修复前同源码两次 install 得到两个不同 `key` 与两个产物（`artifacts 1 -> 2`）；改为按域快照后同源码命中 `method_hook_callback_reuse`，`artifacts 1 -> 1`、`bytes` 不变，而不同源码仍正常产生新产物（`1 -> 2`），业务行为不受影响（`Add` 返回 1、`Fresh` 返回 2）。

同域缓存与产物计数在 `BeforeReload`/`Shutdown`/`ResetDomainCaches` 中清理，因此跨域必然重新建立引用；预算只按“当前域曾加载的独特产物与累计 bytes”计数，删除 hook 只释放 host 引用，不声称卸载 Mono 程序集。

### 12.6 自动化真实 patch 回归与期间修复

新增 `unitypkg/Tests/Editor/UnityCliMethodHookIntegrationTests.cs`：在 EditMode 域内安装定义、用反射触发被拦截方法（反射不受“调用点编译早于补丁”限制），覆盖改参、ref 回写、结果替换、跳过原方法、静态/私有目标、无代码捕获与私有字段、第三方 owner 共存与撤销、故障隔离、启停与 revision/CAS 语义；回调编译用测试专用同步 waiter（EditMode 测试在主线程同步运行，不能等中央 scheduler 推进）。两个夹具共 19 项全部通过。

该夹具与随后的定向 Dev 复检又暴露并修复了四个真实缺陷：

1. **remove → install 在同一 target 上叠加补丁**（实测 1003 变 2003）。撤销改为 owner 范围内的 exact-target `Unpatch(..., HarmonyPatchType.All, ownerId)` 并读回确认归零；attach 后新增“恰好一套 Adapter”校验，不满足即 fail closed。
2. **已删除定义仍留在 target 派发列表**：`ReleaseAdapterIfUnused` 在还有其它定义时提前返回，未把该定义移出运行列表，导致删除后仍派发、且最后一个定义删除时判定为“仍有剩余”而不撤销 Adapter。现在先移除再判定。
3. **Postfix 未按发布顺序串联结果**：多个 Postfix 各自基于原结果覆盖，后写者丢弃前一个的替换。现在按公开顺序串联（后一个看到前一个的结果），与 Harmony `ref __result` 语义一致；实测两个定义 `+1000` 与 `+10` 得到 1013。
4. **编译临时文件在子进程句柄未释放时残留**：删除改为有界重试，并在每次编译开工前自愈清理固定临时目录（有界扫描，只清 `.cs/.dll/.pdb/.rsp`）。

同一轮还把 host 的 target 运行表改为按 stable target key 管理、命中热路径单独用 MethodBase 表查找，避免反射实例身份差异导致“两个定义只有一个生效”。

### 12.7 插件导入后置核对（closure/导入投影一致）

补上计划与交接中的开放项“插件被 Unity 导入后是否需要后置校验与回读”：

- 运行后置核对 `MethodHookHost.VerifyPluginImporterSettings`：后端可用性检查在读入后端前，按 Bridge 包身份推导的固定路径读取随包插件的 `PluginImporter`，要求 `GetCompatibleWithAnyPlatform()==false`、`GetCompatibleWithEditor()==true`、`isPreloaded==false`；资产缺失或不是受管插件返回 `E_HOOK_BACKEND_UNAVAILABLE`，导入形态异常返回 `E_HOOK_DEPENDENCY_CONFLICT` 并给出重装包、勿改包缓存的 nextActions。
- 2022.3 的 `PluginImporter` 没有 auto-reference 读取 API（实测方法集合只含 `GetCompatibleWith*`、`isPreloaded`），因此自动引用开关由随包 `.meta` 与离线审计保证：`contracts/package-assembly-closure.json` 新增 `pluginAssetRelativePath` 与 `pluginImporterRequirements`，`scripts/audit-package-assembly-closure.mjs` 解析 `.meta` 断言 `isExplicitlyReferenced: 1`、Any 平台关闭、Editor 平台开启，并要求运行核对使用同一相对路径与同样三个读取点。
- 定向证据：闭包审计 PASS；`tests/method-hook-contract.test.ts` 新增契约/`.meta`/运行核对一致性断言（11/11）；`UnityCliMethodHookIntegrationTests.PackagedPluginImporterProjectionIsVerifiedAtRuntime` 在真实 Dev 项目内验证正例通过、空路径与不存在资产均返回 `E_HOOK_BACKEND_UNAVAILABLE`（C# 夹具 20/20）；Dev 实测 install 触发该核对后正常 armed，日志记录 `method_hook_backend_loaded version=0Harmony 2.4.2.0`。

### 12.8 事件分页上限与 checkpoint 预算（实测与裁定）

- **分页上限实测**：终端在同一次请求的 read budget 内被序列化 3 次，单个事件约计 66 getters，绝对上限 10000。Dev 实测 `limit=100` 返回 100 条、`limit=150` 返回 150 条，`limit=200/250/300/350/400` 均返回 `E_REQUEST_BUDGET_EXCEEDED`（dimension=getters）。经项目所有者裁定，events 冻结为默认 100 / 上限 128；`MethodHookLimits`、schema 范围、describe、plan 第 14 节与本文件同步。
- **checkpoint 预算一致性修复**：事件环上限（16 MiB）高于总快照上限（8 MiB），原实现会让超大事件环的 checkpoint 整体失败。现在 `CheckpointToDisk` 经 `BuildBoundedEventCheckpoint` 从最新端有界保留（预算 `MaxCheckpointEventBytes=6 MiB`），被丢弃区间作为 `checkpoint-truncated` history gap 随 checkpoint 一起持久化并写一条有界日志；新增 C# 用例 `EventCheckpointIsBoundedAndReportsTruncation`（8000 条大负载快照 → 保留尾部、字节在预算内、丢弃区间与尾部连续）通过。

### 12.9 最终状态与验证汇总

实现与针对性验证已收敛，证据汇总如下（逐条命令的输入、前后状态与业务 readback 见下文各节）：

- **自动化与契约**：`npm run build:test` 与 `npm run architecture:validate`（含 `package-closure:validate`/`package-files:validate`、`audit-final-architecture.mjs --strict`、`audit-tool-completion-contract.mjs --strict`）exit=0；定向 TS 切片 `dist/tests/{method-hook-contract,package-assembly-identity,summary-routing,code-exec-contract}.test.js` 共 20 项全通过（node:test，exit=0）；Unity EditMode 夹具 26 项全通过（`UnityCliMethodHookTests` 12 + `UnityCliMethodHookIntegrationTests` 14）。
- **真实 Dev 调用**：单实例矩阵覆盖发现（group/group list/search/describe、tools run 与 command 同契约）、无代码捕获实际 hit、改参/ref 回写/跳过原方法/结果替换、Emit 与私有字段读写、有限 JSON State、多定义优先级与串行结果、启停删除与 replace、故障隔离与 fault 原因、事件尾页与 cursor 语义、正常退出清理快照、重启后不恢复旧定义。
- **多实例隔离**：`Dev/UnityProject` 与 `Dev/UnityProject-HookPeer` 在同一 Dev 数据根下逐进程注入 `UNITYCLI_DATA_DIR` 启动，A 的定义/hookId/session/cursor 在 B 均准确失败，B 无 hook store 目录，A 的观察与干预不改变 B。
- **冻结值**：资源上限集中在 `unitypkg/Editor/Infrastructure/MethodHooks/MethodHookLimits.cs` 单一配置点，按所有者“预算翻倍”的决定冻结（恢复预算 120 秒、list 默认 32/上限 128、info 源码页 32 KiB、events 默认 100/上限 128、State 32 KiB、事件 checkpoint 6 MiB 且有界截断、总快照 8 MiB），与实现、schema、describe、plan 第 14 节一致。
- **仍未完成（不写作已完成）**：第 19 节的陌生 AI 门禁在本轮会话因缺少 `windows-pwsh` 扩展而环境阻塞（子代理未能发出任何真实 CLI 调用，属环境问题而非产品缺陷，仓库规范禁止用其他 shell 代替）；Unity 支持仍只覆盖 minimum 2022.3.61f1，其他受支持版本需要另行授权的固定项目。
- **所有者裁定（本轮）**：接受该门禁为环境阻塞、不阻断本轮完成，依据已收集的定向功能证据结项；报告如实记录门禁未执行及其环境原因。
- **公开 Skill 判定（本轮）**：`skills/unitycli/` 无需修改。依据：该 Skill 只负责稳定的运行时路由、按需发现、跨 Tool 通用规则与 FeedbackMode；method-hook 只是新增一个工具组，发现仍走既有的 group/search/describe，不改变公开路由、envelope、错误恢复或 host 预算，能力细节留在 live descriptor/describe。该判定在未获明确授权前不触动受保护的 Skill 内容。

## 13. 受影响验证范围（AffectedValidationScope）与证据索引

修订基准：`git HEAD = 06646785b8a38d6f55ca75971a1d9aa2b5b2fbad`，工作区共 39 个改动/新增路径（22 个已跟踪文件修改 + 17 个新增），diff 规模 22 files changed / 976 insertions / 282 deletions。本节哈希前 16 位均为该基准下实测。

### 13.1 owners 与直接 callers

| owner（唯一实现点） | 直接 caller / 接入点 |
| --- | --- |
| `unitypkg/Editor/Infrastructure/DynamicCodeCompiler.cs`、`CodeExecSupport.cs` | `debug.code.execute` handler；`MethodHookHost.Operations`（callback 编译）；`UnityCliBridge`（reload/quitting 取消）；唯一 active compiler 与固定临时文件 |
| `unitypkg/Editor/Infrastructure/MethodHooks/*`（Host/Operations/Dispatch/Store/EventBuffer/TargetResolver/Projection/Definition/Abi/Limits） | 八个 `method-hook.*` Tool；`UnityCliBridge` 的 `[InitializeOnLoad]`/BeforeReload/Shutdown；命中热路径 `DispatchTargets` |
| `unitypkg/Hooks/*`（Harmony 后端、IL emitter、asmdef） | `MethodHookHost` 经反射加载后端；asmdef 单向引用 `UnityCli.Editor` |
| `src/package-manager.ts`、`src/assembly-identity.ts` | `init package` 安装与冲突扫描路径 |
| `src/tools/tools/group-summaries.ts` | `tools group` / `group list` / `search` / `describe` 路由摘要 |
| `contracts/package-assembly-closure.json`、`scripts/audit-package-assembly-closure.mjs` | `npm run package-closure:validate` 与 `architecture:validate` |

### 13.2 公开字段与错误

- 公开字段：八个 Tool 的 closed Request/Result DTO（`MethodHookDtos.cs` 与各 Tool 嵌套 DTO），上限来自 `MethodHookLimits.Summary()`。
- 稳定错误闭集（本轮实测或定向用例覆盖）：`E_SCHEMA_INVALID`、`E_HOOK_TARGET_NOT_FOUND`、`E_HOOK_METHOD_UNSUPPORTED`、`E_HOOK_NOT_FOUND`、`E_HOOK_SESSION_STALE`、`E_HOOK_REVISION_STALE`、`E_HOOK_LIMIT_EXCEEDED`、`E_HOOK_COMPILE_BUDGET_EXHAUSTED`、`E_CODE_COMPILE_FAILED`、`E_HOOK_PATCH_FAILED`、`E_HOOK_CALLBACK_FAILED`、`E_HOOK_THREAD_UNSUPPORTED`、`E_HOOK_RESTORE_BUDGET_EXHAUSTED`、`E_HOOK_RECOVERY_SUPPRESSED`、`E_HOOK_BACKEND_UNAVAILABLE`、`E_HOOK_DEPENDENCY_CONFLICT`、`E_HOOK_EVENT_CURSOR_EXPIRED`、`E_CONTINUATION_STALE`、`E_UNITY_BUSY`、`E_REQUEST_BUDGET_EXCEEDED`、`E_EXECUTION_TIMEOUT`。

### 13.3 精确测试选择器与排除项

- TS：`node --test dist/tests/method-hook-contract.test.js dist/tests/package-assembly-identity.test.js dist/tests/summary-routing.test.js dist/tests/code-exec-contract.test.js`（本轮 20/20，exit=0）。
- Unity EditMode：按类名夹具 `UnityCliMethodHookTests`、`UnityCliMethodHookIntegrationTests`（本轮 26/26）。
- 构建/契约：`npm run build:test`、`npm run architecture:validate`（含 `package-closure:validate`/`package-files:validate`/`completion:audit --strict`）、Unity 自带 csc 四程序集编译（exit=0/errors=0）。
- 排除项：无关能力组与工具、无筛选 `npm test`、全 Tool Live、全 EditMode/Player、未授权的其他 Unity 版本 baseline；陌生 AI 门禁因缺 `windows-pwsh` 未执行（所有者裁定为环境阻塞，不阻断本轮）。

### 13.4 状态切片

定义快照（`Library/UnityCli/method-hooks/definitions.json`）、事件环与 checkpoint（内存 + `events.json`）、跨域恢复抑制集合（current-session `SessionState` 键）、事件 cursor（绑定 EditorSessionId + 规范化筛选 + 最后检查 sequence）、编译临时目录（`Library/UnityCli/code-exec-temp`，空闲时 0 文件）、Adapter/产物计数与 foreign owner 摘要（host 内按域统计）。

### 13.5 source/build 哈希（前 16 位）

| 文件 | sha256[:16] | bytes |
| --- | --- | --- |
| `Editor/Infrastructure/DynamicCodeCompiler.cs` | `0731AA332B10255D` | 18547 |
| `Editor/Infrastructure/CodeExecSupport.cs` | `CF0F0741156394CC` | 25239 |
| `Editor/Infrastructure/MethodHooks/MethodHookHost.cs` | `D3F87D332BC17EE2` | 67442 |
| `…/MethodHookHost.Dispatch.cs` | `F78D18239F5F9EF4` | 19136 |
| `…/MethodHookHost.Operations.cs` | `AB794F2BE39A2D95` | 40395 |
| `…/MethodHookLimits.cs` | `8E752EB653043D27` | 8534 |
| `…/MethodHookStore.cs` | `9A76E1D8AF5EC991` | 7597 |
| `…/MethodHookEventBuffer.cs` | `5C76D7901CC53ABE` | 17486 |
| `…/MethodHookTargetResolver.cs` | `2CA4E9C87E4544BA` | 12596 |
| `…/MethodHookProjection.cs` | `748CB3381DFC603C` | 13879 |
| `…/MethodHookDefinition.cs` | `083D56916CAA566F` | 26126 |
| `…/MethodHookAbi.cs` | `9DBF2A98B1C350CF` | 13173 |
| `Hooks/MethodHookHarmonyBackend.cs` | `53E81BB5874D4354` | 10934 |
| `Hooks/MethodHookAdapterEmitter.cs` | `3990D1897557BA9F` | 10383 |
| `Hooks/UnityCli.Hooks.Editor.asmdef` | `12ABD162090070AC` | 450 |
| `Editor/Tools/debug/hook/*.cs`（8 + DTOs） | `82DD10A0E2CB987D`/`9087932DCB5A8D84`/`8618727E8F79519F`/`CD8ED3F6356AFFA6`/`050C1891F905ECAF`/`3640FAB2CF6E8921`/`5232A207BDF7440F`/`5BBD63D5193B19C3`/`B08334EDB3717F4A` | 6267/4077/4119/3177/3261/2949/2479/3517/14575 |
| `src/package-manager.ts` | `B7911539F8F94417` | 130031 |
| `src/assembly-identity.ts` | `5946B54F84AE49AC` | 14362 |
| `src/tools/tools/group-summaries.ts` | `02543E8C1EC3B460` | 4644 |
| `contracts/package-assembly-closure.json` | `8E409D4D33A82354` | 1944 |
| `contracts/tool-completion-contract.json` | `AEE542836B06CCD8` | 504292 |
| `scripts/audit-package-assembly-closure.mjs` | `429DA695F819655B` | 12740 |
| `tests/method-hook-contract.test.ts` | `37C84CCE3FB54F8B` | 9195 |
| `tests/package-assembly-identity.test.ts` | `B28BE5AD9937B82F` | 4158 |
| `Tests/Editor/UnityCliMethodHookTests.cs` | `F3721DE8FFCC232B` | 17742 |
| `Tests/Editor/UnityCliMethodHookIntegrationTests.cs` | `068ED8AEDFD54208` | 32230 |
| `dist/cli.js`（构建产物） | `2D00D8CA91F14F83` | 17259 |
| `dist/tools/.generated-schemas.js` | `D4F3176DEA086899` | 75326 |
| `Editor/Generated/UnityCliToolCompletionRegistry.g.cs` | `238865842BD89C1C` | 173006 |
| `contracts/command-contract.json` | `6B5059A059858600` | 102834 |
| `contracts/package-files.json` | `473DC6249A8DC27C` | 75152 |

本节哈希为验证阶段的修订标识；任何源码改动都会使哈希失效并需要重跑受影响切片，后续修正在本条下方追加新的哈希行。

## 14. 动机案例夹具实测（运动是否早于解锁）

授权沙盒内没有原始动机案例的游戏逻辑（全项目对 `aiMove`/`FuncDelayTime`/`isPlayCG` 零命中），经项目所有者裁定，用一个 Dev 专用夹具完成该条验收。

### 14.1 夹具与业务标记

- 夹具：`Dev/UnityProject/Assets/HookProbe/ProbeMotivationCase.cs`（Dev 沙盒专用，不属于产品代码）。
- 两个由玩家循环真实调用的业务方法：`MotivationActor.BeginMove(int)`（AI 运动开始）与 `MotivationActor.OpenGate(int)`（人类解锁），各带业务标记字段 `aiMove`/`lim`/`FuncDelayTime` 与 `isPlayCG`（与原始案例同名）。
- 固定时间线：驱动在 `frame=30` 调用 `BeginMove`、`frame=120` 调用 `OpenGate`；夹具同时通过 `ProbeSignal` 写出自己的信号线（独立于 hook 的证据）。

### 14.2 观测结果（一次 Play，两个 play 范围 hook，capture 无代码）

- `BeginMove` prefix：`frame=30`、`elapsedMilliseconds=1087.6`、`playSessionId=play-1`、`clockSegment=play-1:seg-1`、捕获参数 `7`。
- `OpenGate` prefix：`frame=120`、`elapsedMilliseconds=1226.0`、同一 `play-1`/`play-1:seg-1`、捕获参数 `1`。
- 同一 Play Session、同一 clock segment 下单调拦截时间 `move < unlock` 成立；install 后定义从 dormant 进入 Play armed，重挂事件 `frame=0`，最早调用在 `frame=30` 被捕获。
- 业务标记读回（同一 Invocation）：prefix 看到调用前状态 `aiMove=false, lim=1, FuncDelayTime=0.5` 与 `isPlayCG=true`；postfix 看到调用后状态 `aiMove=true, lim=0, FuncDelayTime=0`，prefix/postfix `invocationId` 相同。
- 夹具独立信号线（ground truth）：`35|frame=30|...|motivation.begin-move|... aiMove=True` 早于 `126|frame=120|...|motivation.open-gate|... isPlayCG=False`；hook 观测顺序与夹具自己的业务时间线一致。

### 14.3 隔离与诚实边界

- 无代码捕获：整个 Play 期间 `compiledArtifacts=0`、`Library/UnityCli/code-exec-temp` 为 0 文件，未触发项目编译或 reload 请求。
- 未改写磁盘：`Assembly-CSharp.dll` 在同一 Play 周期前后 sha256[:16] 均为 `203C6C2D6790D0FB`；夹具 `.cs` sha256[:16] 均为 `80DE6DC1CAFDB883`。
- 边界声明：拦截时间来自方法调用边界（`elapsedMilliseconds`），不是字段赋值瞬间；夹具信号线是夹具自己的业务事实，不能当作真实游戏结论。本结果证明的是“该能力能在同一 Play/clock segment 下用业务标记与单调时间回答先后问题”，不证明任何真实游戏的“约 4 秒”结论（后者仍属未验证线索）。
- 测量后已移除两个定义（remaining=0）。

## 15. 生命周期四组合实测（domain/scene reload on-off）

脚本：以 `debug.code.execute` 显式切换 `EditorSettings.enterPlayModeOptions`（每次读回生效值），四个组合各跑一轮 Play；每轮前后读 `debug.hook.info`（hitCount、adapterIdentities）、从轮次开始 cursor 读事件页与夹具信号线；最后恢复设置并清理定义。

| 组合 | 读回设置 | calls | 本轮 dispatches | first prefix frame | State 首个值 | play / clock | adapter 数 | prefix:postfix:custom |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A domain on + scene on | `False\|None` | 500 | 94 | 454 | 1 | `play-1` / `play-1:seg-1` | 1 | 42:42:42 |
| B domain off + scene on | `True\|DisableDomainReload` | 500 | 1004 | 0 | 1 | `play-2` / `play-2:seg-52713` | 1 | 42:42:42 |
| C domain on + scene off | `True\|DisableSceneReload` | 500 | 120 | 441 | 1 | `play-1` / `play-1:seg-1` | 1 | 42:42:42 |
| D domain off + scene off | `True\|DisableDomainReload, DisableSceneReload` | 500 | 1004 | 0 | 1 | `play-2` / `play-2:seg-23264` | 1 | 42:42:42 |

解读（不把观察当成超出证据的结论）：

- **无重复 Adapter/hit**：四轮的 `adapterIdentities` 恒为 1，同一事件页内 prefix/postfix/custom 计数逐项相等（42/42/42）→ 每次调用恰好一次前缀与一次后缀派发；`hitCount` 按阶段派发计数，因此“每调用 2”是 prefix+postfix，不是重复挂载（动机案例里单次调用得到 hit=2 且只有 1 条 prefix 与 1 条 postfix，与之一致）。
- **domain reload 开启时的早期调用边界**：组合 A/C 中回调需在新域重新编译，因此在编译完成前的早期调用不被拦截（first prefix frame 454/441），与 `callbackPending` 标记同一恢复窗口；组合 B/D 无需重编译，frame=0 起就 armed 且全部调用命中。这组差异本身就是“不补造丢失调用”的证据：本轮确实少了早期命中，事件页与夹具信号线都不伪装它们存在。
- **State 分局**：四轮 State 首值均为 1，Play 入口重置。
- **Session/clock 语义**：domain reload 组合重新得到 `play-1`（域内计数器随新域重置），无 domain reload 组合继续为 `play-2` 并带新的 clock segment；夹具自身的 `runtime.beforeSceneLoad reload=` 标记与之一致（域重载组合为 false，即静态被清空）。
- **旧对象引用**：各轮 awake 实例哈希不同；同 Play 内再次 reload 会重建实例，而在 domain+scene reload 均关闭的轮次，前一实例身份可以保留（诚实记录，不声称“总是消失”）。
- **产品未擅改用户设置**：Enter Play Mode 选项只由本轮显式 `debug.code.execute` 修改，结束后恢复为原值 `False\|None` 并 `AssetDatabase.SaveAssets()`；`debug.hook.install` 的 describe 也明确声明不改变 Enter Play Mode 设置。

## 16. 安装闭包与资源边界定向实测

### 16.1 不完整安装与冲突（定向功能证据）

| 场景 | 结果 |
| --- | --- |
| 源包缺随包 DLL（临时隐藏 canonical 二进制） | `init package` 与 `--check` 均用 `PACKAGE_SOURCE_INTEGRITY_FAILED` 拒经，消息含确切的源文件绝对路径；恢复二进制后 sha256 回到 `7b9e7563…c55c`，检查重新通过 |
| 已安装副本被破坏（删除已安装投影的 0Harmony.dll） | `init package --check` 报 `status=damaged`、`reasonCode=PACKAGE_INSTALL_DAMAGED`、message “managed files are missing or do not match canonical bytes”，nextActions 给出收敛命令；不修复、不删除其他文件；此时只读 `editor.state` 与 `debug.hook.list` 仍正常返回 |
| Assets 内异名同 identity（植入 `AliasHarmony.dll`，2461696 字节/同 sha256） | `init package` 拒绝并返回 `PACKAGE_ASSEMBLY_CONFLICT`，details 给出准确路径 `Assets/ThirdParty/HookConflictProbe/AliasHarmony.dll`、sha256 与 `0Harmony, Version=2.4.2.0, Culture=neutral, PublicKeyToken=null`；植入文件未被删除；移除后安装恢复 |
| 修复 | `init package` 再次运行收敛为 `status=installed`，已安装 DLL sha256 回到 canonical `7B9E756306FA3D76` |

定义“不完整安装不能启用 hook”由上述三道防线共同保证：源包完整性、已安装副本损坏检测、运行时后端可用性。/ 后端无法加载时的运行时隔离由 `PackagedPluginImporterProjectionIsVerifiedAtRuntime` 与 `VerifyPluginImporterSettings` 覆盖（返回 `E_HOOK_BACKEND_UNAVAILABLE`，不拖垮 catalog）。诚实记录的边界：若已安装投影里的 DLL 被直接删除且未先修复，该项目会因 asmdef 预编译引用缺失而编译失败、Editor 无法启动（实测 `Scripts have compiler errors`）——安装期检查正是用于阻止这种状态，而不是在启动后才尝试隔离。

### 16.2 资源与临时文件现场盘点

- 同 target 定义上限：实测 16 个后第 17 个被拒（`E_HOOK_LIMIT_EXCEEDED`：“This exact target already has 16 hook definitions.”）；达到上限时 `info`/`disable`/`remove` 仍可用，移除后立即又能安装。
- 只读 Tool 纯度：连续 `list`/`events`/`info` 后快照目录文件（名称/大小/mtime）逐项不变，`code-exec-temp` 仍为 0 文件。
- 临时文件：`Library/UnityCli/code-exec-temp` 空闲时 0 文件；`method-hooks` 为固定 1–2 个快照文件；dev 根 `.temp` 顶层今天只新增/修改了 `hook-probe`（夹具信号）与 `requests`（每实例 1 个固定标记文件，今日本会话数百次调用只更新同一文件）；未出现按请求/按版本累积的目录。
- 重复 key 不重复加载：同源码重复 install 命中 `method_hook_callback_reuse`，产物计数 `1 -> 1`（findings 12.5）。

## 17. A/B 多实例隔离与正式根基线

两个授权 Dev 实例均以公开流程安装 canonical 产物、逐进程注入同一 Dev 数据根，且都以 `status=ready` 在线：A=`d:\workspace\alps-unity-cli\dev\unityproject`（instance `unity-da1e42fa…`，session `session-839a9d21…`），B=`…\dev\unityproject-hookpeer`（instance `unity-4a3d82de…`，session `session-3b84242b…`）。

### 17.1 隔离实测（本轮）

| 检查 | 结果 |
| --- | --- |
| 同名 label 两实例各自安装 | A=`hook-af0aa809eec2453f`，B=`hook-92787f97a40a4cd6`（不同 id）；A list total=1 且只见自己的 id，B 同理 |
| 跨实例身份 | A 的 hookId 在 B → `E_HOOK_NOT_FOUND`；A 的 editorSessionId 在 B → `E_HOOK_SESSION_STALE`；A 的 cursor 在 B → `E_CONTINUATION_STALE` |
| 不自动选首个 | 在非 Unity 项目目录下不带 `--instance` → `E_INSTANCE_REQUIRED`（无单实例回退） |
| 同 revision 竞争 | 第一次 disable 成功（rev 1→2），第二次带同一 expectedRevision → `E_HOOK_REVISION_STALE` |
| store 不共享 | 两边各自的 `Library/UnityCli/method-hooks/definitions.json`；A 的快照不含 B 的 hookId，B 的不含 A 的（互相为 false） |
| 跨实例业务互不影响 | 同一 exact target `UnityEngine.Mathf.Max(Int32,Int32)`：基线 A=B=2；仅在 A 装 postfix `+1000` 后 A 得 1002、B 仍为 2；B 自身也能在同 target 装独立定义且结果仍为 2；移除后 A 恢复为 2 |

### 17.2 正式根基线比对

以已记录的 `formal-root-before.json`（5 条目）与当前正式根（175 条目）比对：

- 新增 171 项全部属于正式实例自身：`test-results/unity-ba48a56b…`（170 个 NUnit XML，属于 `d:\workspace\jump-brawlstars\client-jump` 这个正式实例）与另一个正式实例记录。
- 内容变化：`feedback/feedback.jsonl`（反馈日志增长）与 client-jump 的实例记录（其 Editor 周期重新发布）；消失一项为 client-jump 实例的临时资源锁文件。
- 正式根 `instances` 中的 canonicalPath 只有用户项目（client-jump 及其 orca/release 副本），**无任何 Dev 路径或 Dev 根数据**；整个过程正式 Unity 进程保持基线 5 个，Dev Editor 为另外的 pid（A=91616、B=88668）。
- 诚实记录：上述 test-results/feedback 属于正式实例自身的活动，不由 Dev 验证造成；本会话所有 CLI 调用均携带 `--instance <Dev 项目>` 且进程环境为 Dev 数据根。
