# 自定义 Runtime Tool 示例

展示如何在 Unity 项目中注册 Runtime Tool，并在 Play Mode 下通过 alps-unity-cli 执行。

## 1. C# 注册代码

```csharp
using UnityCli;
using System.Collections.Generic;

[UnityCliTool("sample.runtime-echo",
    Title = "Runtime Echo",
    Group = "custom",
    Description = "Echoes input during Play Mode. Requires active Play Mode session.",
    ReadOnlyHint = true,
    DestructiveHint = false,
    Target = "runtime")]
public static class SampleRuntimeTool
{
    public static object Execute(Dictionary<string, object> input)
    {
        var message = input.TryGetValue("message", out var msg) ? msg?.ToString() : "ping";
        return new
        {
            echo = message,
            timestamp = System.DateTime.Now.ToString("o"),
            playMode = UnityEngine.Application.isPlaying
        };
    }
}
```

**要点**：
- `Target = "runtime"` 标记为 Runtime Tool
- 仅在 Play Mode 活跃时可用
- 非 Play Mode 时执行返回 `E_RUNTIME_NOT_READY`（transient error）
- `ReadOnlyHint = true` — Runtime Tool 也可以是只读的

## 2. CLI 执行流程

```powershell
# 检查编辑器状态
unity_cli tools run editor.state --input '{}'

# 进入 Play Mode
unity_cli tools run editor.play --input '{}'

# 验证 Play Mode
unity_cli tools run editor.state --input '{}'
# → playMode 应为 "playing"

# 执行 Runtime Tool
unity_cli tools run sample.runtime-echo --input '{"message":"hello runtime"}'

# 完成后退出 Play Mode
unity_cli tools run editor.stop --input '{}'
```

## 3. 错误处理

### 非 Play Mode 执行

```json
{
  "ok": false,
  "error": {
    "code": "E_RUNTIME_NOT_READY",
    "message": "Runtime tools require Play Mode to be active.",
    "transient": true,
    "retry_after_ms": 3000,
    "next_actions": ["Enter Play Mode first: unity_cli tools run editor.play --input '{}'"]
  }
}
```

**恢复**：先 `editor.play`，等 `retry_after_ms` 后重试。

### Fingerprint Mismatch

如果 handler 代码变更但 descriptor 未更新：

```json
{
  "ok": false,
  "error": {
    "code": "E_TOOL_FINGERPRINT_MISMATCH",
    "message": "Tool fingerprint does not match.",
    "next_actions": ["Audit the change, then use --fingerprint-mode warn"]
  }
}
```

**恢复**：审计代码变更，确认安全后使用 `--fingerprint-mode warn`。

## 4. 安全边界

- Runtime Tool 遵守与 Editor Tool 相同的 descriptor/envelope 规则
- `target: "runtime"` 的工具在非 Play Mode 时不可用
- Play Mode 执行可能影响运行时状态，但 `readOnlyHint: true` 保证不修改场景/资产
- 不得在非 Play Mode 时伪装可用
