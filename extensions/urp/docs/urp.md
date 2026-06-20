# URP 工具

Universal Render Pipeline 扩展工具集。提供渲染器特性管理、Volume 配置和管线设置。

## 工具列表

| 工具 | 说明 |
|------|------|
| `urp.feature` | 列出/添加/移除/切换/配置 URP 渲染器特性 |
| `urp.volume` | 列出/创建/配置 URP Volume 组件及其 Profile 效果 |
| `urp.settings` | 读写 URP 管线资产设置（renderScale、HDR、MSAA、阴影距离等） |

## 前置条件

- Unity 项目需使用 URP 作为当前渲染管线
- 需安装 `com.unity.render-pipelines.universal` 包

## 发现与使用

```bash
# 列出 URP 工具
unity-cli tools group list urp

# 列出渲染器特性
unity-cli urp feature --action list

# 创建全局 URP Volume
unity-cli urp volume --action create --is-global --name GlobalVolume

# 读取管线设置
unity-cli urp settings
```

## 依赖缺失时的行为

当目标 Unity 项目未使用 URP 时：
- 工具标记为 `unavailable`
- 返回 `E_URP_PACKAGE_NOT_FOUND` 错误码
- `next_actions` 提示安装 `com.unity.render-pipelines.universal`
