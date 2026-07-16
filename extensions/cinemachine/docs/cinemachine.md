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
| `cinemachine.noise` | 添加、配置或移除多通道 Perlin 噪声组件 |
| `cinemachine.brain.ensure` | 确保目标相机挂载 CinemachineBrain |
| `cinemachine.brain.status` | 读取 Brain 及当前活动虚拟相机状态 |
| `cinemachine.force-camera` | 调用 CinemachineBrain.SetCameraOverride 强制切到目标虚拟相机，并返回 overrideId |
| `cinemachine.release-override` | 使用 overrideId 释放上一条强制相机 override |
| `cinemachine.extension.list` | 列出目标上的 CinemachineExtension 派生组件 |
| `cinemachine.extension.add` | 添加 CinemachineExtension 派生组件 |
| `cinemachine.extension.remove` | 移除 CinemachineExtension 派生组件 |

## 前置条件

- Unity 项目需安装 `com.unity.cinemachine` 包
- 缺少依赖时工具返回 `unavailable` + `nextActions`

## 发现与使用

```bash
# 列出 Cinemachine 工具
unity_cli tools group list cinemachine --project <project-path>

# 调用前读取完整 schema、preset 集合和 examples
unity_cli tools describe cinemachine.create --project <project-path>

# 检测 Cinemachine 可用性
unity_cli tools run cinemachine.ping --project <project-path> --input '{}'

# 创建跟随相机；非法 preset 返回 E_CINEMACHINE_PRESET_INVALID，且不会创建对象
unity_cli tools run cinemachine.create --project <project-path> --input '{"preset":"follow","name":"PlayerCam","follow":"Player"}'
```

## 依赖缺失时的行为

当目标 Unity 项目未安装 Cinemachine 包时：
- 工具标记为 `unavailable`
- 返回 `E_CINEMACHINE_PACKAGE_NOT_FOUND` 错误码
- `nextActions` 提示安装 `com.unity.cinemachine`
