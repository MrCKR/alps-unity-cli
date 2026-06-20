# ProBuilder 扩展包

ProBuilder 扩展包（`unity-cli-extension-probuilder`）提供 36 个细粒度工具，用于在 Unity 中进行 ProBuilder 网格编辑。

## 前置条件

- Unity 项目已安装 `com.unity.probuilder` 包
- alps-unity-cli 已连接到运行中的 Unity Editor（Bridge 连接）

## 发现与查看工具

### 列出所有 ProBuilder 工具

```bash
unity_cli tools group list probuilder
```

### 查看某个工具的详细参数

```bash
unity_cli tools describe probuilder.mesh-info
unity_cli tools describe probuilder.create-shape
unity_cli tools describe probuilder.extrude-faces
```

### 运行工具

```bash
# 检测 ProBuilder 包是否可用
unity_cli probuilder ping

# 创建形状
unity_cli probuilder create-shape cube --name "MyCube"

# 查看网格信息
unity_cli probuilder mesh-info <target>

# 挤出面
unity_cli probuilder extrude-faces <target> --indices 1,2,3 --distance 0.5

# 以 batch job 模式运行
unity_cli probuilder create-shape sphere --job --wait
```

## 工具分类

| 分类 | 工具 | 数量 |
|------|------|------|
| 工具检测 | `probuilder.ping` | 1 |
| 形状创建 | `probuilder.create-shape`、`probuilder.create-poly-shape` | 2 |
| 面操作 | `probuilder.extrude-faces`、`probuilder.delete-faces`、`probuilder.detach-faces`、`probuilder.merge-faces`、`probuilder.subdivide`、`probuilder.flip-normals`、`probuilder.select-faces`、`probuilder.set-face-material`、`probuilder.set-face-color`、`probuilder.set-face-uvs` | 10 |
| 边操作 | `probuilder.extrude-edges`、`probuilder.bevel-edges`、`probuilder.bridge-edges`、`probuilder.connect-elements` | 4 |
| 网格合并 | `probuilder.combine-meshes`、`probuilder.merge-objects`、`probuilder.duplicate-and-flip`、`probuilder.create-polygon` | 4 |
| 顶点操作 | `probuilder.merge-vertices`、`probuilder.weld-vertices`、`probuilder.split-vertices`、`probuilder.move-vertices`、`probuilder.insert-vertex`、`probuilder.append-vertices-to-edge` | 6 |
| 信息查看 | `probuilder.mesh-info`、`probuilder.validate-mesh` | 2 |
| 转换 | `probuilder.convert-to-probuilder` | 1 |
| 平滑 | `probuilder.set-smoothing`、`probuilder.auto-smooth` | 2 |
| 轴心/变换 | `probuilder.center-pivot`、`probuilder.freeze-transform`、`probuilder.set-pivot` | 3 |
| 修复 | `probuilder.repair-mesh` | 1 |

## 扩展包安装

### 自动安装（init 检测）

当 Unity 项目中已安装 `com.unity.probuilder` 时，`init` 会自动安装 ProBuilder 扩展包：

```bash
unity_cli init --project /path/to/unity/project
```

输出中的 `extensions.autoInstalled` 会包含 `["probuilder"]`。

### 查看扩展状态

```bash
unity_cli doctor --project /path/to/unity/project
```

> 扩展包也可通过 `extensions install/remove/doctor` 命令管理。

### 手动安装

```bash
unity_cli extensions install probuilder --project /path/to/unity/project
```

### 卸载

```bash
unity_cli extensions remove probuilder --project /path/to/unity/project
```

### 查看所有扩展

```bash
unity_cli extensions list --project /path/to/unity/project
```

## 依赖缺失时的行为

当项目中未安装 `com.unity.probuilder` 包时：

- ProBuilder 工具在 `tools group list probuilder` 中标记为 `available=false`
- `tools describe probuilder.*` 会显示 `requiresPackages: ["com.unity.probuilder"]`
- `tools run probuilder.*` 返回 `E_EXTENSION_DEPENDENCY_MISSING` 错误，附带 `next_actions` 提示安装依赖

### 恢复路径

```bash
# 方法 1：通过 CLI 安装 ProBuilder 包
unity_cli package install com.unity.probuilder --project /path/to/project

# 方法 2：手动添加到 Packages/manifest.json
# 在 dependencies 中添加 "com.unity.probuilder": "latest"

# 安装后重新检测
unity_cli doctor --project /path/to/project
```

## 诊断

### doctor 检查

```bash
unity_cli doctor --project /path/to/unity/project
```

doctor 会输出 `extension.probuilder` 检查项，包括：

- 扩展包是否已注册
- 扩展包是否已启用（installed）
- `com.unity.probuilder` 是否存在及版本
- descriptor 数量（期望 36）
- bridge command 数量（期望 36）
- C# handler 代码是否存在

### 扩展包专项诊断

```bash
unity_cli doctor --project /path/to/unity/project
```

### capabilities 查看

```bash
unity_cli capabilities --project /path/to/unity/project
```

在 `extensions` 部分可以看到 ProBuilder 扩展包的可用性状态和工具数量。`extensionGroups.probuilder=36` 只在扩展包安装后出现。

## 常见问题

### Q: 运行 ProBuilder 工具时报 `E_PROBUILDER_PACKAGE_NOT_FOUND`

A: 项目中未安装 `com.unity.probuilder` 包。请按上方"恢复路径"安装。

### Q: `tools group list probuilder` 中看不到 ProBuilder 工具

A: 可能未启用扩展包。运行 `unity_cli init --project <path>` 确保 Bridge 包已安装，然后确认 `com.unity.probuilder` 已安装。

### Q: 运行工具时 Unity 报错

A: 确认 Unity Editor 正在运行，且 Bridge 连接正常。运行 `unity_cli doctor` 检查连接状态。

### Q: init 未自动安装 ProBuilder 扩展包

A: `init` 仅在项目已包含 `com.unity.probuilder` 时自动安装。如果包是 init 之后安装的，可重新运行 `unity_cli init --project <path>` 或手动运行 `unity_cli extensions install probuilder --project <path>`。
