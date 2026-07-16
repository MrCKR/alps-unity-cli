---
name: unity-cli-extension
description: unity_cli Extension 的目录结构、manifest、Unity package 依赖、Bridge descriptor、安装投影、availability 与同步更新硬规范。新增、修改、删除任何 extensions/<id> 内容或扩展贡献的 Tool/Context 时必须先完整读取；同时必须加载 unity-cli-capability。
compatibility: 适用于 alps-unity-cli 仓库的 extensions 目录和 Unity 安装投影。
---

# Unity CLI Extension

修改任何 Extension 前，先完整读取 `../unity-cli-capability/SKILL.md`。Extension 贡献的 Tool / Context 仍受所有能力、PlayMode、descriptor、安全与输出规则约束。

## 标准结构

扩展包必须使用：

```text
extensions/<id>/
  extension.json
  bridge/domains/<domain>.ts
  docs/<id>.md
  unitypkg/Editor/<Domain>/<tool>.cs
```

Unity 项目中的安装形态必须是：

```text
Packages/com.unity_cli/Extensions/<id>/
```

不得创建 `Packages/com.unity_cli.<id>` 一类独立 Unity package。

## Manifest 与依赖

- manifest 必须声明 Unity package 依赖、tools、contexts 和 csharpEntries。
- 必须通过 `unityInstall.include` 声明需要投影的 `unitypkg` 与 docs 文件。
- Core 不得对可选 Unity package 建立编译期依赖。
- 扩展可用性必须根据目标项目实际安装的 Unity package 判断。
- 扩展工具不可用时，必须返回明确的缺失 package 和可执行的下一步操作。

## Descriptor 真相来源

- Extension Tool 的完整 descriptor 和 schema 必须在运行时从 Bridge 获取。
- TypeScript 不承担 Extension Tool 级 descriptor 真相。
- Extension manifest 只负责 extension 级离线信息，如 `providesTools`、`requiresUnityPackages`、title 和 description。
- manifest 不得承载 Tool 级 schema 或 describe。
- 离线 extension 名单和依赖信息只更新 manifest / registry-data，不得复制运行时 Tool descriptor。

## 同步修改

新增、修改或删除扩展能力时，必须按实际影响同步维护：

- `extension.json`
- Bridge spec
- Unity C# 实现与注册
- docs
- contract
- tests
- `unityInstall.include` 投影
- 离线 registry-data（仅在离线名单或依赖信息变化时）

不得让 manifest、Bridge、Unity 安装内容、公开 discovery 和文档处于不同语义版本。
