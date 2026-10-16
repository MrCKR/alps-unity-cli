# ProBuilder 扩展包

ProBuilder 扩展包提供 36 个细粒度 Tool，用于在已打开的 Unity Editor 中创建、读取和编辑 ProBuilder 网格。扩展精确支持 `com.unity.probuilder@5.2.4`；Unity patch 版本不参与能力判定。

## 前置条件

- Unity 项目安装精确版本 `com.unity.probuilder@5.2.4`
- Unity Editor 已打开目标项目并完成编译
- 已运行 `unitycli extensions install probuilder --project <project>`，或由 `init` 在检测到精确依赖后复制 extension bundle
- `unitycli extensions doctor --project <project>` 显示 ProBuilder extension 已安装、可用且健康

## 发现与执行

```bash
# 列出 ProBuilder Tool
unitycli tools group list probuilder --instance <name|alias|projectPath>

# 获取完整输入 schema、边界、错误与示例
unitycli tools describe probuilder.create-shape --instance <name|alias|projectPath>
unitycli tools describe probuilder.extrude-faces --instance <name|alias|projectPath>

# 证明 Package 与 extension 可用
unitycli tools run probuilder.ping --instance <name|alias|projectPath>

# 按 descriptor 创建形状
unitycli tools run probuilder.create-shape --input '{"shapeType":"Cube","name":"MyCube"}' --instance <name|alias|projectPath>
```

不要根据本页猜测 face、edge 或 vertex index。先读取当前对象和 Live descriptor，再提交 mutation。

## 工具分类

| 分类 | Tool | 数量 |
|------|------|------|
| 检测 | `probuilder.ping` | 1 |
| 形状创建 | `probuilder.create-shape`、`probuilder.create-poly-shape` | 2 |
| 面操作 | `probuilder.extrude-faces`、`probuilder.delete-faces`、`probuilder.detach-faces`、`probuilder.merge-faces`、`probuilder.subdivide`、`probuilder.flip-normals`、`probuilder.select-faces`、`probuilder.set-face-material`、`probuilder.set-face-color`、`probuilder.set-face-uvs` | 10 |
| 边操作 | `probuilder.extrude-edges`、`probuilder.bevel-edges`、`probuilder.bridge-edges`、`probuilder.connect-elements` | 4 |
| 网格合并 | `probuilder.combine-meshes`、`probuilder.merge-objects`、`probuilder.duplicate-and-flip`、`probuilder.create-polygon` | 4 |
| 顶点操作 | `probuilder.merge-vertices`、`probuilder.weld-vertices`、`probuilder.split-vertices`、`probuilder.move-vertices`、`probuilder.insert-vertex`、`probuilder.append-vertices-to-edge` | 6 |
| 读取 | `probuilder.mesh-info`、`probuilder.validate-mesh` | 2 |
| 转换 | `probuilder.convert-to-probuilder` | 1 |
| 平滑 | `probuilder.set-smoothing`、`probuilder.auto-smooth` | 2 |
| 轴心与变换 | `probuilder.center-pivot`、`probuilder.freeze-transform`、`probuilder.set-pivot` | 3 |
| 修复 | `probuilder.repair-mesh` | 1 |

## 完成语义

所有 mutating Tool 都在 `ctx.CommitEffect()` 前完成 typed input、target 和 selection 校验。跨过 effect barrier 后，Tool 通过 Undo API 记录实际 serialized owner，执行一次 Package mutation，刷新所需数据，并从冻结 identity 做 fresh readback。effect 后的 invoke、refresh、identity 或 postcondition 不确定性统一返回 `E_REQUEST_OUTCOME_UNKNOWN`；此时应先检查 request journal 和 fresh state，不能盲目重放 mutation。

以下十五项使用 request-specific terminal proof：

- `probuilder.append-vertices-to-edge`
- `probuilder.auto-smooth`
- `probuilder.bevel-edges`
- `probuilder.bridge-edges`
- `probuilder.connect-elements`
- `probuilder.create-shape`
- `probuilder.duplicate-and-flip`
- `probuilder.extrude-edges`
- `probuilder.extrude-faces`
- `probuilder.flip-normals`
- `probuilder.insert-vertex`
- `probuilder.merge-objects`
- `probuilder.merge-vertices`
- `probuilder.subdivide`
- `probuilder.weld-vertices`

其余十七项 scene mutation 使用各 Tool 的最小 typed business/effect readback，不建立完整 canonical plan：

- `probuilder.center-pivot`
- `probuilder.combine-meshes`
- `probuilder.convert-to-probuilder`
- `probuilder.create-poly-shape`
- `probuilder.create-polygon`
- `probuilder.delete-faces`
- `probuilder.detach-faces`
- `probuilder.freeze-transform`
- `probuilder.merge-faces`
- `probuilder.move-vertices`
- `probuilder.repair-mesh`
- `probuilder.set-face-color`
- `probuilder.set-face-material`
- `probuilder.set-face-uvs`
- `probuilder.set-pivot`
- `probuilder.set-smoothing`
- `probuilder.split-vertices`

`probuilder.create-shape` 是十五项中的独立边界。它只冻结 fresh object identity、name、position、center pivot、requested world bounds，以及 ProBuilder 5.2.4 的 ShapeType topology record。record 仅包含 default local bounds、raw vertex count、directed face index streams 和 shared-vertex partitions；material、UV、compiled Mesh channels、unwrap、selection 与 Unity patch 均不是完成 predicate。

## 关键边界

`probuilder.create-shape` 支持 Cube、Stair、CurvedStair、Prism、Cylinder、Plane、Door、Pipe、Cone、Sprite、Arch、Sphere 和 Torus。Plane 与 Sprite 的 Y 轴尺寸为零，因此不能显式传 `height`。其他 Package 版本会在 scene mutation 前拒绝。

`probuilder.create-poly-shape` 接受至少三个 finite ordered points、可选 `extrudeHeight` 和 `flipNormals`。fresh readback 验证输入点、共同 extrusion offset 和 Package 使用的 normal orientation。

`probuilder.combine-meshes` 接受至少两个唯一 ProBuilder target，将几何合并到第一个 target，并通过 Undo 删除其余 source object。总 raw vertex 数必须低于 ProBuilder 5.2.4 的单 mesh 上限。

`probuilder.convert-to-probuilder` 接受带 readable vertices 和 triangle indices 的 `MeshFilter`。fresh readback 以有向 triangle 几何多重集验证 import 结果，允许 importer 重排或复制 raw vertices。

`probuilder.create-polygon` 接受至少三个唯一有效 raw vertex index；`unordered` 默认为 `true`。fresh readback 验证新增 face 的 distinct raw vertex set。

`probuilder.split-vertices` 将请求 raw index 从 shared partition 拆为 singleton；请求项本来全部是 singleton 时返回无 effect 的成功结果。

`probuilder.set-smoothing` 的 `smoothingGroup` 范围为 `0..65535`。

`probuilder.append-vertices-to-edge`、`probuilder.bevel-edges`、`probuilder.bridge-edges`、`probuilder.connect-elements`、`probuilder.extrude-edges`、`probuilder.extrude-faces`、`probuilder.insert-vertex`、`probuilder.merge-objects`、`probuilder.merge-vertices`、`probuilder.weld-vertices` 等 dedicated proof Tool 的具体拓扑、UV、material 和有限算术边界由各自 Live descriptor 自足声明。

## 安装与诊断

```bash
# 安装精确 Package 后复制 extension bundle
unitycli extensions install probuilder --project <project>

# 检查 Package version、安装投影与 Live availability
unitycli extensions doctor --project <project>

# 查看 extension 状态
unitycli extensions list --project <project>

# 删除 Unity 侧 extension 投影
unitycli extensions remove probuilder --project <project>
```

依赖缺失或版本不是 `5.2.4` 时，extension 状态为 unavailable，`nextActions` 会要求安装 `com.unity.probuilder@5.2.4` 后重新安装 extension。CLI 不会自动选择其他版本。
