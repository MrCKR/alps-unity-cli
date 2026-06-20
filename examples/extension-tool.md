# 扩展包工具示例

展示扩展包工具如何与 alps-unity-cli 集成，以及缺依赖时的 unavailable + next_actions 行为。

## 1. 扩展包工具示例：ProBuilder

ProBuilder 扩展包已实现（P7），需要 `com.unity.probuilder` 包。

### 安装扩展

```powershell
# 安装 Bridge 包（包含 ProBuilder 支持）
unity_cli init --project C:\path\MyProject

# 确保 ProBuilder 包已安装
unity_cli package install com.unity.probuilder --project C:\path\MyProject --wait
```

### 发现与执行

```powershell
# 查看分组（包含 probuilder）
unity_cli tools groups

# 查看 ProBuilder 工具
unity_cli tools list --group probuilder

# 描述工具
unity_cli tools describe probuilder.shape

# 执行
unity_cli tools run probuilder.shape --input '{"type":"Cube","positionX":0,"positionY":1,"positionZ":0}'
```

### 缺依赖行为

当项目未安装 `com.unity.probuilder` 时：

```powershell
unity_cli tools describe probuilder.shape
```

输出中：
```json
{
  "name": "probuilder.shape",
  "requiresPackages": ["com.unity.probuilder"],
  "available": false
}
```

执行时返回：
```json
{
  "ok": false,
  "error": {
    "code": "E_PROBUILDER_PACKAGE_NOT_FOUND",
    "message": "ProBuilder package is not installed in the target project.",
    "next_actions": [
      "Install the com.unity.probuilder package in the Unity project.",
      "Use unity_cli package install com.unity.probuilder --project <path>."
    ]
  }
}
```

**恢复**：安装所需 Package 后重试。

## 2. 扩展包组成

一个完整的 CLI 扩展包包含：

| 组件 | 说明 |
|------|------|
| C# Bridge 实现 | `[UnityCliTool]` 标记的 handler |
| Descriptor | 由特性自动生成的 inputSchema/outputSchema |
| Manifest | 扩展元数据（名称、版本、依赖） |
| 测试 | 单元测试和集成测试 |
| 文档片段 | 扩展使用说明 |

## 3. 规划中的扩展命令

独立的 `extensions` 命令组正在规划中（P5）：

```powershell
# 规划中（P5）的 API，实现后生效
unity_cli extensions list                    # 列出已安装扩展（P5）
unity_cli extensions install <name>          # 安装扩展（P5）
unity_cli extensions remove <name>           # 卸载扩展（P5）
unity_cli extensions doctor                  # 扩展诊断（P5）

# init 自动检测
unity_cli init --project <path>              # 检测项目 Package 并安装匹配扩展
```

## 4. 安全边界

| 规则 | 说明 |
|------|------|
| 扩展不能绕过 descriptor/envelope | 必须通过统一注册和执行流程 |
| 扩展不能提供任意反射 | 不允许 escape hatch |
| Fingerprint 校验 | handler 代码变更时默认阻止 |
| 缺依赖不可用 | unavailable + next_actions，不静默失败 |

## 5. 当前扩展状态

| 扩展 | 状态 | 说明 |
|------|------|------|
| ProBuilder | ✅ 已完成 | shape/create/edit/csg |
| Cinemachine | 📋 Backlog | 待实现（P8） |
| VFX Graph | 📋 Backlog | 基础 vfx 命令已有，扩展包待实现（P8） |
| URP/HDRP | 📋 Backlog | 待实现（P8） |
