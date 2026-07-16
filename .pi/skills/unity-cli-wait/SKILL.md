---
name: unity-cli-wait
description: unity_cli 异步 Tool/Context、跨帧等待、callback 生命周期、中央 scheduler 及 CLI wait 分层硬规范。涉及 ValueTask、Promise、ctx.Wait、EditorApplication.update、timeout/cancel、job/batch wait 或 PlayMode 跨帧行为时必须先完整读取；修改公开能力时同时加载 unity-cli-capability。
compatibility: 适用于 alps-unity-cli 的 TypeScript CLI 与 Unity C# Bridge。
---

# Unity CLI Wait

本项目的 Tool / Context 执行接口默认具备异步能力。调用方只负责发出命令并取得最终结果，不应感知内部同步或异步差异。

涉及公开 Tool / Context 时，先完整读取 `../unity-cli-capability/SKILL.md`。

## 接口层

- C# Tool / Context 使用语言原生异步接口，推荐 `ValueTask<T>`。
- TypeScript local Tool / Context 使用 `Promise<T>`。
- 不得把 deferred 作为特殊工具形态暴露给调用方或工具作者。

## 框架层 Wait

`wait` 是框架能力，不是具体 Tool 的私有执行机制。

- 工具作者不得直接注册 `EditorApplication.update`。
- 工具作者不得自行维护 callback 生命周期。
- 工具作者只能通过框架提供的 `ctx.Wait` 或等价 wait service 申请等待。
- 框架只能有一个中央 scheduler 持有 `EditorApplication.update`。
- timeout、cancel、domain reload、Play Mode exit 和 Unity quitting 时，所有 pending waits 必须由框架统一清理。

## 两层 Wait 语义

必须区分命令层 wait 和框架层 wait。

命令层 wait：

- `--wait`
- `--wait-timeout-ms`
- `waitReady`

这些参数表达 job、batch 或编排的交付模式，不是 Tool 内部执行模型。

框架层 wait：

- `ctx.Wait.NextFrame(...)`
- `ctx.Wait.Delay(...)`
- `ctx.Wait.UntilEditorReady()`
- `ctx.Wait.ForCallback(...)`

这些 API 表达 Tool 内部跨帧或 callback 执行方式。

禁止把两层 wait 混淆实现，也不得让具体 Tool 重新发明 scheduler、轮询或 callback 清理机制。
