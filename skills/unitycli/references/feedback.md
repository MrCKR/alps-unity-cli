# UnityCLI 问题反馈

只在 `SKILL.md` 的 `FeedbackMode` 为 `on` 且本次实际出现摩擦时读取。正常成功、常规 discovery 或没有问题的调用不得记录。

## 可记录事件

每个独立问题最多记录一次：

- `failure`：公开 CLI/Tool 调用失败，且失败对完成任务有实际影响。
- `ambiguity`：descriptor、schema、availability、错误或 nextActions 存在多种合理理解。
- `missing`：所需能力、字段、说明或可验证业务状态缺失。
- `misleading`：公开名称、示例或建议把 Agent 引向错误调用。
- `workaround`：确实遇到坑点，但通过额外 discovery、只读核验或替代公开能力成功绕过；必须说明 workaround。

不要记录普通成功、个人偏好、泛化建议、未实际遇到的风险或同一根因的重复错误。

## 允许字段

- `Category`：以上五类之一。
- `Summary`：1-512 字符的单行问题摘要。
- `Workaround`：0-512 字符；`workaround` 类必填。
- `Capability`：可选Tool或控制面名称，只允许字母、数字、点、下划线和连字符。
- `ErrorCode`：可选稳定错误码，如 `E_SCHEMA_INVALID`。
- `Phase`：`discovery`、`describe`、`execute`、`verify`、`install` 或 `other`。

禁止传入原始 prompt、完整输入/output envelope、源码、堆栈、大段日志、绝对路径、URL、endpoint、authorization header、token、password、secret 或 API key。脚本也会拒绝常见路径、URL 和 secret 形态。

## 调用

从已安装 Skill 根目录调用：

```powershell
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts/record-feedback.ps1 `
  -Category ambiguity `
  -Summary "Descriptor did not distinguish two valid target forms." `
  -Workaround "Used a read Tool to identify the canonical target." `
  -Capability "sample.tool" `
  -ErrorCode "E_SCHEMA_INVALID" `
  -Phase describe
```

脚本只接受解析到 fixed local drive 且路径中没有 reparse point 的 `%LOCALAPPDATA%`，并写 `%LOCALAPPDATA%\UnityCli\feedback\`：

- `feedback.jsonl`
- `feedback.1.jsonl`
- `feedback.2.jsonl`
- `feedback.3.jsonl`

每个文件最多 64 KiB，使用 Windows named mutex 串行。脚本不联网。成功 stdout 返回 `ok`、`recordId`、`path`、`rotated`；失败为非零退出且不生成伪记录。

## 回复摘要

脚本成功后，在正常任务回复末尾追加简短段落，例如：

```text
Skill 反馈：ambiguity - descriptor 未区分两种目标形式；workaround：通过只读 readback 确认 canonical target。recordId=fb_xxx，path=%LOCALAPPDATA%\UnityCli\feedback\feedback.jsonl
```

只总结实际写入的记录。没有记录时不加该段。脚本失败时仅说明“反馈未写入”及简短原因，不能把 Unity 任务改判为失败。
