# 示例索引

alps-unity-cli 操作示例，按类型组织。

## 工具发现与执行

| 文件 | 类型 | 内容 |
|------|------|------|
| [01-editor-tool-echo.md](01-editor-tool-echo.md) | Editor Tool | 发现→描述→执行→验证 `editor.console` |
| [02-editor-tool-camera.md](02-editor-tool-camera.md) | Editor Tool | 相机工具：搜索→描述→列表→截图→验证 |
| [03-runtime-tool-echo.md](03-runtime-tool-echo.md) | Runtime Tool | Play Mode 下 runtime 工具 |
| [04-runtime-tool-verify.md](04-runtime-tool-verify.md) | Runtime Tool | runtime 工具超时与验证 |
| [05-verify-workflow.md](05-verify-workflow.md) | 验证流程 | 完整发现→执行→验证循环 |

## 自定义工具示例

| 文件 | 类型 | 内容 |
|------|------|------|
| [custom-editor-tool.md](custom-editor-tool.md) | Custom Tool | 项目侧 Editor Tool 注册与执行 |
| [custom-runtime-tool.md](custom-runtime-tool.md) | Custom Tool | 项目侧 Runtime Tool 注册与 Play Mode 执行 |
| [custom-context.md](custom-context.md) | Custom Context | 项目侧只读 Context（规划中） |
| [extension-tool.md](extension-tool.md) | Extension | 扩展包工具与缺依赖 unavailable |

## Batch 示例

| 文件 | 类型 | 内容 |
|------|------|------|
| [06-batch-editor-only.json](06-batch-editor-only.json) | Batch | Editor-only 批量操作 |
| [07-batch-runtime-only.json](07-batch-runtime-only.json) | Batch | Runtime-only 批量操作 |
| [08-batch-editor-plus-runtime.json](08-batch-editor-plus-runtime.json) | Batch | Editor + Runtime 混合 |
| [09-batch-parallel-readonly.json](09-batch-parallel-readonly.json) | Batch | 并行只读操作 |

## 核心发现路径

所有示例遵循 group-first 分层发现：

```
tools groups                    → 查看分组
       ↓
tools list --group <group>      → 按组查看
       ↓
tools describe <tool>           → 完整描述（必做）
       ↓
tools run <tool>                → 执行
```
