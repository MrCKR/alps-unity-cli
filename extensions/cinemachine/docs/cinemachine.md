# Cinemachine 工具

Unity Cinemachine 智能相机系统的 CLI 工具集。提供可用性检测、虚拟相机创建、目标跟踪、镜头控制、优先级管理、混合配置、Body/Aim 管线组件、Brain 强制 override、噪声和扩展组件管理。

## 工具列表

| 工具 | 说明 |
|------|------|
| `cinemachine.ping` | 只读检测 Cinemachine 包和关键反射 API 可用性 |
| `cinemachine.create` | 创建虚拟相机，支持预设（follow, third_person, freelook, dolly, static, top_down, side_scroller） |
| `cinemachine.target` | 设置 Follow / LookAt 目标 |
| `cinemachine.lens` | 读写镜头属性（FOV、近远裁面、正交尺寸、Dutch 角） |
| `cinemachine.priority` | 读写优先级及启用状态 |
| `cinemachine.blend` | 读写 CinemachineBrain 的 DefaultBlend（风格 + 时长） |
| `cinemachine.body` | 切换或配置 Body 管线组件（CinemachineFollow, CinemachinePositionComposer 等） |
| `cinemachine.aim` | 切换或配置 Aim 管线组件（CinemachineRotationComposer, CinemachineHardLookAt 等） |
| `cinemachine.noise` | 添加/配置/移除多通道 Perlin 噪声组件 |
| `cinemachine.brain` | 确保 Brain 挂载或报告 Brain 混合状态 |
| `cinemachine.force-camera` | 调用 CinemachineBrain.SetCameraOverride 强制切到目标虚拟相机，并返回 overrideId |
| `cinemachine.release-override` | 使用 overrideId 释放上一条强制相机 override |
| `cinemachine.extension` | 列出/添加/移除 CinemachineExtension 派生组件 |

## 前置条件

- Unity 项目需安装 `com.unity.cinemachine` 包
- 缺少依赖时工具返回 `unavailable` + `next_actions`

## 发现与使用

```bash
# 列出 Cinemachine 工具（需要 --project 指向已安装 Cinemachine 的 Unity 项目）
unity-cli tools group list cinemachine

# 检测 Cinemachine 可用性
unity-cli tools run cinemachine.ping --project <project-path>

# 创建跟随相机
unity-cli tools run cinemachine.create follow --name PlayerCam --follow Player

# 设置镜头 FOV
unity-cli tools run cinemachine.lens PlayerCam --field-of-view 60

# 强制 Brain 切到虚拟相机，并记录返回的 overrideId
unity-cli tools run cinemachine.force-camera CloseupCam --brain "Main Camera"

# 释放强制相机 override
unity-cli tools run cinemachine.release-override --input "{\"overrideId\":12,\"brain\":\"Main Camera\"}"

# 切换 Body 组件
unity-cli tools run cinemachine.body PlayerCam --body CinemachineFollow
```

## 依赖缺失时的行为

当目标 Unity 项目未安装 Cinemachine 包时：
- 工具标记为 `unavailable`
- 返回 `E_CINEMACHINE_PACKAGE_NOT_FOUND` 错误码
- `next_actions` 提示安装 `com.unity.cinemachine`
