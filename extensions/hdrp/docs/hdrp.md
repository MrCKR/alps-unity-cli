# HDRP 工具

High Definition Render Pipeline 扩展工具集。提供 HDRP Volume 配置和管线设置。

## 工具列表

| 工具 | 说明 |
|------|------|
| `hdrp.volume` | 列出/创建/配置 HDRP Volume 组件及其 Profile 效果 |
| `hdrp.settings` | 读写 HDRP 管线资产设置 |

## 前置条件

- Unity 项目需使用 HDRP 作为当前渲染管线
- 需安装 `com.unity.render-pipelines.high-definition` 包

## 发现与使用

```bash
# 列出 HDRP 工具
unity-cli tools group list hdrp

# 列出 HDRP Volume
unity-cli hdrp volume --action list

# 创建全局 HDRP Volume
unity-cli hdrp volume --action create --is-global true --name GlobalVolume

# 读取管线设置
unity-cli hdrp settings
```

## 依赖缺失时的行为

当目标 Unity 项目未使用 HDRP 时：
- 工具标记为 `unavailable`
- 返回 `E_HDRP_PACKAGE_NOT_FOUND` 错误码
- `next_actions` 提示安装 `com.unity.render-pipelines.high-definition`
