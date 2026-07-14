# EnvTimeSimple — 环境时间轴系统

基于 ReflectionProbe + 球谐光照（SH）的环境时间轴工具，支持在多个环境状态节点之间平滑过渡，适用于昼夜变化、场景氛围切换等需求。

> **与 EnvTime（完整版）的区别**：EnvTimeSimple 不包含 Light Probe 混合、Prefab 空间变换、SH 旋转等高级功能，聚焦于 ReflectionProbe + CustomSH 的核心流程，更轻量易用。

---

## 目录

- [核心概念](#核心概念)
- [快速上手](#快速上手)
- [组件说明](#组件说明)
  - [EnvironmentTimelineData](#environmenttimelinedata)
  - [EnvironmentTimelineController](#environmenttimelinecontroller)
  - [Timeline 集成（Playable）](#timeline-集成playable)
- [编辑器窗口](#编辑器窗口)
  - [打开方式](#打开方式)
  - [界面说明](#界面说明)
  - [烘焙流程](#烘焙流程)
  - [镜面高光烘焙（Specular Light Baking）](#镜面高光烘焙specular-light-baking)
- [ReflectionProbe 烘焙参与模型](#reflectionprobe-烘焙参与模型)
- [运行时行为](#运行时行为)
- [Shader 要求](#shader-要求)
- [API 速查](#api-速查)
- [常见问题](#常见问题)

---

## 核心概念

```
时间轴 (0 ───────────────── 24)
         ●──────────●──────────●──────────●
      Node_0     Node_1     Node_2     Node_3
     (正午)     (黄昏)     (深夜)     (黎明)
```

- **节点（EnvTimeNode）**：某个时间点的环境快照，包含一个 ReflectionProbe（已烘焙的 Cubemap）和从中投影出的 SH 系数。
- **插值**：运行时根据当前时间在两个相邻节点之间线性插值 SH 系数，并通过 MPB 写入 Renderer。
- **ReflectionProbe 激活**：同一时间只激活一个节点的 mainProbe，过渡过半时切换。

---

## 快速上手

### 1. 创建 Timeline 物体

打开编辑器窗口：**菜单栏 → Tools → BYTools → Environment Timeline Simple 编辑器**

点击「✚ 在场景中创建新的 Timeline 物体」，会自动创建一个带有 `EnvironmentTimelineData` 和 `EnvironmentTimelineController` 的 GameObject。

### 2. 添加节点

- 在时间轴上**双击空白处**添加节点
- 或点击「✚ 添加节点」按钮

### 3. 为每个节点配置 ReflectionProbe

1. 选中节点
2. 在「主 Probe」字段拖入场景中的 ReflectionProbe 组件
3. 每个节点必须使用**不同的** ReflectionProbe（重复会显示红色警告）

### 4. 烘焙

有两种烘焙方式：

| 方式 | 按钮 | 说明 |
|------|------|------|
| 单节点 | 「🔥 烘焙 Probe」 | 烘焙当前节点的 ReflectionProbe Cubemap |
| 单节点 SH | 「▶ 烘焙此节点 SH」 | 从已烘焙的 Cubemap 投影出 SH 系数 |
| 全部 | 「▶ 一键烘焙所有节点 SH」 | 依次烘焙所有 Probe + SH |

> 通常使用「一键烘焙所有节点 SH」即可完成全部流程。

### 5. 指定影响目标

在节点的「影响的模型」列表中添加需要应用环境 SH 的 GameObject。运行时系统会通过 MaterialPropertyBlock 将 SH 系数写入这些物体的 Renderer。

### 6. 预览

拖动「预览时间」滑块或拖拽预览时间条，实时查看不同时间点的环境效果。

---

## 组件说明

### EnvironmentTimelineData

数据容器，存储时间轴配置和所有节点。

| 字段 | 类型 | 说明 |
|------|------|------|
| `totalDuration` | `float` | 时间轴总长度（如 24 表示 24 小时循环） |
| `loop` | `bool` | 是否循环（首尾节点之间也会插值） |
| `nodes` | `List<EnvTimeNode>` | 环境节点列表 |

#### EnvTimeNode 字段

| 分组 | 字段 | 说明 |
|------|------|------|
| **ReflectionProbe** | `mainProbe` | 主反射探针（必填，每个节点必须不同） |
| | `additionalProbes` | 附加探针（到达此节点时一并启用） |
| **影响目标** | `affectedTargets` | 运行时应用 SH 的 GameObject 列表 |
| | `includeChildren` | 是否包含子物体的 Renderer |
| **RP 烘焙参与模型** | `reflectionProbeBakeTargets` | 烘焙时临时勾选 ReflectionProbeStatic 的 GO 列表 |
| **镜面高光烘焙** | `enableSpecularLightBaking` | 启用后在 Baked 光源位置创建自发光代理物体 |
| | `specularLightCollectMode` | 光源收集模式（AutoBaked/AutoAll/Manual） |
| | `specularLightTargets` | 手动光源列表（ManualList 模式使用） |
| | `specularSphereRadius` | 代理球半径（Point/Spot 光源） |
| | `specularIntensityMultiplier` | 自发光强度倍率 |
| | `specularAreaPanelScale` | 面光源面板尺寸倍率 |
| **烘焙参数** | `sampleResolution` | SH 投影采样分辨率（32/64/128/256） |
| | `rotationY` | Cubemap Y 轴旋转角度（0-360） |
| | `useHDRClamp` | 是否启用 HDR Clamp |
| | `hdrClampMax` | HDR Clamp 上限值 |
| | `exposure` | 曝光倍率 |
| **结果** | `customSH` | 烘焙后的 SH 系数（自动写入） |

### EnvironmentTimelineController

运行时控制器，每帧根据 `currentTime` 采样并应用环境效果。

| 字段 | 说明 |
|------|------|
| `currentTime` | 当前环境时间 |
| `autoPlay` | 运行时自动推进时间 |
| `timeSpeed` | 自动播放速度 |
| `writeToRenderSettings` | 写入 `RenderSettings.ambientProbe` |
| `writeToMPB` | 通过 MPB 写入目标 Renderer |
| `writeMainCubemapToMaterial` | 将主 Cubemap 写入材质属性 |
| `envCubemapPropName` | Cubemap 材质属性名（默认 `_SpecularEnvCubemap0`） |
| `controlReflectionProbes` | 自动激活/关闭 ReflectionProbe |
| `useMaterialInstanceForSkinnedMesh` | 运行时对 SkinnedMeshRenderer 使用材质实例（不破坏 SRP Batcher） |

**工作流程**：
1. `Sample(currentTime)` 找到当前时间所在的两个节点 `from`→`to` 及插值权重 `t`
2. `SerializedSH.Lerp(from.customSH, to.customSH, t)` 线性插值 SH 系数
3. 将插值结果通过 MPB 或材质实例写入目标 Renderer 的 `unity_SHAr` 等属性
4. 根据权重 `t` 切换激活的 ReflectionProbe（t < 0.5 保持 from，t >= 0.5 切换到 to）

### Timeline 集成（Playable）

系统提供 Unity Timeline 集成，可通过 Timeline 控制环境时间。

#### 组件

| 文件 | 说明 |
|------|------|
| `EnvironmentTimelineTrack` | 自定义 Timeline Track，绑定 `EnvironmentTimelineController` |
| `EnvironmentTimelinePlayableAsset` | Clip 资产，配置时间映射 |
| `EnvironmentTimelinePlayableBehaviour` | 运行时行为，每帧映射时间到 Controller |
| `EnvironmentTimelineTrackMixer` | 混音器，支持多 Clip 权重混合 |

#### 时间映射模式（TimeRemapMode）

| 模式 | 说明 |
|------|------|
| `PercentageMap` | Timeline Clip 的 0-100% 映射到环境时间轴的 `startTime`→`endTime` |
| `DirectMap` | Timeline 时间直接作为环境时间 |
| `ScaledMap` | 缩放映射，将 Clip 时长缩放到 `startTime`→`endTime` 范围 |

#### 使用方法

1. 打开 Timeline 窗口（Window → Sequencing → Timeline）
2. 选中带有 Controller 的 GameObject，创建 Director
3. 在 Timeline 中点击「+」添加 `Environment Timeline Track`
4. 将 Controller 拖到 Track 的绑定字段
5. 右键 Track → Add Clip，配置 Clip 的映射模式和参数

---

## 编辑器窗口

### 打开方式

**菜单栏 → Tools → BYTools → Environment Timeline Simple 编辑器**

### 界面说明

```
┌─────────────────────────────────────────┐
│  🌅 Environment Timeline 编辑器          │
├─────────────────────────────────────────┤
│ Timeline Data: [拖入或从选中获取]  [新建] │
│ 时间轴总长: [24]   循环: [✓]             │
├─────────────────────────────────────────┤
│ ⚙ Cubemap 自动创建设置                    │
│   默认尺寸: [128]   文件名前缀: [bake_]   │
├─────────────────────────────────────────┤
│ ⏱ 时间轴 (双击空白添加 / 拖拽修改时间)     │
│  [━━━━━━━━━━━━━━━━━━━━━━━━━━━━]         │
│  ●     ●        ●         ●              │
│  Node0 Node1   Node2    Node3            │
├─────────────────────────────────────────┤
│ ▶ 实时预览 (拖拽时间条预览效果)            │
│  [━━━━━━━━━|━━━━━━━━━━━━━━]             │
│  预览时间: [─────●─────]  [应用到场景]    │
├─────────────────────────────────────────┤
│ 📋 节点列表                               │
│  [✚ 添加] [⇅ 排序]                       │
│  ○ [0.00] Node_0  ✓SH ✓Probe ...        │
│  ● [6.00] Node_1  ✓SH ✓Probe ...        │
├─────────────────────────────────────────┤
│ 🔧 节点详细 [1] Node_1                    │
│  名称: [Node_1]                          │
│  时间: [─────●─────]                     │
│  🔮 ReflectionProbe                      │
│    主 Probe: [拖入]                      │
│    附加 Probe: [...]                     │
│  🔥 烘焙参数                              │
│    采样分辨率: [64]                      │
│    Y旋转: [─────●─────]                  │
│    [▶ 烘焙此节点 SH] [🔥 烘焙 Probe]     │
│  🔄 ReflectionProbe 烘焙参与模型          │
│    [使用选中填充] [追加] [清空]           │
│    [GO Field] [✕]                       │
│    ← 拖拽 GameObject 添加                │
│  💡 镜面高光烘焙 (Specular Light Baking)  │
│    启用: [✓]  收集模式: [AutoBaked]      │
│    代理球半径: [─────●─────] 0.05        │
│    强度倍率: [─────●─────] 1.0           │
│    面光源面板倍率: [─────●─────] 1.0      │
│  🎯 影响的模型                            │
│    包含子物体: [✓]                       │
│    [使用选中填充] [追加] [清空]           │
│    [GO Field] [✕]                       │
│    ← 拖拽 GameObject 添加目标            │
│  ✨ Custom SH 数据 (已烘焙)               │
│    SHAr: (x, y, z, w)                    │
│    SHAg: (x, y, z, w)                    │
│    SHAb: (x, y, z, w)                    │
├─────────────────────────────────────────┤
│  [▶ 一键烘焙所有节点 SH]                  │
└─────────────────────────────────────────┘
```

### 烘焙流程

#### 单节点完整烘焙

1. 选中节点
2. 点击「🔥 烘焙 Probe」— 烘焙 ReflectionProbe 的 Cubemap
3. 点击「▶ 烘焙此节点 SH」— 从 Cubemap 投影 SH 系数

> 也可以直接点击「▶ 烘焙此节点 SH」，如果 Probe 未烘焙会提示先烘焙 Probe。

#### 批量烘焙

点击底部「▶ 一键烘焙所有节点 SH」：
1. 检查是否有重复 Probe（有则阻止）
2. 如需要，弹出目录选择窗口
3. 依次烘焙所有节点的 ReflectionProbe（最终统一保存为 Custom 模式）
4. 依次从 Cubemap 投影 SH 系数
5. 弹出完成摘要

#### 烘焙文件

- 烘焙结果保存为 `.exr` 格式的 Cubemap 文件
- 如果 Probe 已有 Custom 纹理，使用同目录同文件名替换
- 否则使用 `Baked_001.exr`、`Baked_002.exr`... 递增命名
- 所有 Probe 烘焙后统一切换为 **Custom 模式**

### 镜面高光烘焙（Specular Light Baking）

Unity 的 Baked 光源在烘焙 ReflectionProbe 时默认不会在 Cubemap 中产生镜面高光。本功能通过在光源位置创建临时自发光代理物体来解决此问题，使 Baked 光源也能在反射球中呈现高光效果。

**原理**：参考 [SpecularProbes](https://github.com/zulubo/SpecularProbes)（Zulubo 开发，用于 Vertigo 2）。

**支持的光源类型与代理物体**：

| 光源类型 | 代理物体 | 说明 |
|----------|----------|------|
| Point | 自发光小球 | 在光源位置创建小半径自发光球体 |
| Spot | 自发光小球 | 在光源位置创建小半径自发光球体 |
| Area | 自发光半透明面板 | 按光源尺寸创建自发光 Quad 面板 |
| Disc | 自发光圆盘 | 创建扁平自发光圆盘 |

**光源收集模式**：

| 模式 | 说明 |
|------|------|
| `AutoCollectBaked` | 自动收集场景中所有 Baked 模式光源（默认） |
| `AutoCollectAll` | 自动收集所有光源（含 Mixed 模式） |
| `ManualList` | 仅使用手动指定的光源列表 |

**配置参数**：

| 参数 | 说明 |
|------|------|
| 代理球半径 | Point/Spot 光源自发光球体的半径（0.001~1） |
| 强度倍率 | 自发光强度倍率（1=与光源一致，>1=更亮） |
| 面光源面板倍率 | Area 光源面板的尺寸倍率（1=与光源一致） |

**工作流程**：

1. 在节点配置中勾选「启用镜面高光烘焙」
2. 选择光源收集模式（通常使用 AutoCollectBaked）
3. 调整代理球半径和强度倍率
4. 执行烘焙（单节点「🔥 烘焙 Probe」或「▶ 一键烘焙所有节点 SH」）
5. 系统会自动在烘焙前创建代理物体，烘焙后销毁

> 代理物体使用 `HideAndDontSave` 标志，不会出现在场景中也不会被保存。烘焙完成后自动销毁，包括临时材质。

---

### ReflectionProbe 烘焙参与模型

每个节点有一个「ReflectionProbe 烘焙参与模型」列表，用于解决以下场景：

**问题**：某些 GameObject 没有勾选 `ReflectionProbeStatic`，烘焙时不会参与 ReflectionProbe 烘焙，导致反射结果不正确。但你又不想永久修改它们的 Static 标记。

**解决方案**：
1. 将需要参与烘焙的 GameObject 添加到此列表
2. 烘焙时，系统会**自动递归**将这些 GO 及其所有子物体临时勾选 `ReflectionProbeStatic`
3. 烘焙结束后**自动还原**原始 Static 标记

**操作方式**：
- **使用当前选中物体填充**：清空列表，用当前 Hierarchy 选中的 GO 填充
- **追加当前选中物体**：在列表末尾追加当前选中的 GO
- **清空**：清空整个列表
- **拖拽**：将 Hierarchy 中的 GO 拖到列表区域添加
- **删除**：点击列表项右侧的 ✕ 按钮

> 此功能在「单节点烘焙」和「一键批量烘焙」时均生效。使用 `using` 语句确保即使烘焙过程中发生异常也能还原。

---

## 运行时行为

### SH 写入方式

系统通过覆盖 Unity 原生 SH 属性名（`unity_SHAr`、`unity_SHAg`... `unity_SHC`）来写入环境光照。

**写入路径**：

```
当前时间
  │
  ▼
Sample() → from, to, t
  │
  ▼
SH Lerp(from.customSH, to.customSH, t)
  │
  ├──→ writeToRenderSettings → RenderSettings.ambientProbe
  │
  └──→ writeToMPB → 对 affectedTargets 的每个 Renderer:
        │
        ├── Renderer.lightProbeUsage = CustomProvided
        │
        ├── [编辑模式 / 默认]
        │   MaterialPropertyBlock → unity_SHAr 等属性
        │   + envCubemapPropName → 主 Cubemap
        │
        └── [运行模式 + SkinnedMeshRenderer + useMaterialInstanceForSkinnedMesh]
            材质实例 → unity_SHAr 等属性
            + envCubemapPropName → 主 Cubemap
            (不破坏 SRP Batcher)
```

### ReflectionProbe 激活策略

由于 Shader 不支持反射球融合，采用硬切换策略：
- `t < 0.5`：保持 `from` 节点的 mainProbe 激活
- `t >= 0.5`：切换到 `to` 节点的 mainProbe 激活
- 其他非活跃 Probe 被禁用（`enabled = false` + `gameObject.SetActive(false)`）

### 清理

- **编辑模式**：`OnDisable` 时自动调用 `ClearAllMPB()`，清除 MPB、恢复 LightProbeUsage
- **运行模式**：需手动调用 `ClearAllMPB()` 或 `ClearMaterialInstances()` 清理材质实例
- Inspector 中有「清除所有 MPB」按钮可手动清理

---

## Shader 要求

目标材质的 Shader 需要满足以下条件之一：

1. **标准 Shader**（Built-in Pipeline）：已内置 `unity_SHAr` 等属性，无需额外处理
2. **自定义 Shader**：需声明以下属性（Unity 标准 SH 属性名）：

```hlsl
CBUFFER_START(UnityPerMaterial)
    // ... 其他属性 ...
    half4 unity_SHAr;
    half4 unity_SHAg;
    half4 unity_SHAb;
    half4 unity_SHBr;
    half4 unity_SHBg;
    half4 unity_SHBb;
    half4 unity_SHC;
CBUFFER_END
```

如果需要写入 Cubemap（`writeMainCubemapToMaterial = true`），还需声明对应属性：

```hlsl
CUBEMAP(_SpecularEnvCubemap0)
```

属性名通过 Controller 的 `envCubemapPropName` 配置。

---

## API 速查

### EnvironmentTimelineController

```csharp
// 应用当前时间的环境效果
controller.ApplyAtCurrentTime();

// 跳转到指定节点
controller.JumpToNode(int index);
controller.JumpToNode(string nodeName);

// 设置时间并应用
controller.currentTime = 6.0f;
controller.ApplyAtCurrentTime();

// 清理
controller.ClearAllMPB();
controller.ClearMaterialInstances();
```

### EnvironmentTimelineData

```csharp
// 按时间排序节点
data.SortByTime();

// 采样当前时间
bool ok = data.Sample(currentTime, out EnvTimeNode from, out EnvTimeNode to, out float t);
```

### SerializedSH

```csharp
// 线性插值
SerializedSH result = SerializedSH.Lerp(shA, shB, t);

// 写入 MaterialPropertyBlock
sh.ApplyToMPB(mpb);

// 写入材质实例
sh.ApplyToMaterial(material);

// 转换为 SphericalHarmonicsL2
SphericalHarmonicsL2 shL2 = sh.ToSHL2();
```

---

## 常见问题

### Q: 烘焙后 Cubemap 是全黑的？

- 检查场景中是否有光源
- 检查需要参与烘焙的模型是否勾选了 `ReflectionProbeStatic`（或使用「ReflectionProbe 烘焙参与模型」功能自动处理）
- 检查 ReflectionProbe 的 `Size` 和 `Culling Mask` 是否覆盖了目标区域

### Q: 节点上显示红色 ⚠ 警告？

多个节点使用了相同的 `mainProbe`，每个节点必须使用不同的 ReflectionProbe。

### Q: 运行时 SkinnedMeshRenderer 的 SH 不生效？

MPB 对 SkinnedMeshRenderer 在某些 Unity 版本中可能不生效。开启 Controller 的 `useMaterialInstanceForSkinnedMesh` 选项，使用材质实例直接写入（不破坏 SRP Batcher）。

### Q: 编辑模式退出后材质变回来了？

这是正常行为。`OnDisable` 时会自动清除所有 MPB 并恢复原始 LightProbeUsage，避免修改场景状态。

### Q: Timeline 播放时环境不变化？

- 检查 Track 是否绑定了 `EnvironmentTimelineController`
- 检查 Track 是否绑定了带有 `EnvironmentTimelineData` 组件的 `EnvironmentTimelineController`
- 检查 Clip 的 `autoControl` 是否开启
- 环境数据通过 Track Binding 自动获取，无需在 Clip 上手动指定

### Q: 如何让烘焙时不影响场景中的 Static 标记？

使用节点的「ReflectionProbe 烘焙参与模型」功能。添加需要参与的 GO 后，系统会在烘焙时临时勾选 `ReflectionProbeStatic`，烘焙完成后自动还原。

### Q: Baked 光源在反射球中没有高光？

Baked 光源默认不会在 ReflectionProbe Cubemap 中产生镜面高光。启用节点的「镜面高光烘焙」功能，系统会在烘焙时在光源位置创建临时自发光代理物体（Point/Spot → 小球，Area → 面板），烘焙后自动销毁。详见上方「镜面高光烘焙」章节。

---

## 文件结构

```
EnvTimeSimple/
├── EnvironmentTimelineData.cs          # 数据容器 + EnvTimeNode + SerializedSH
├── EnvironmentTimelineController.cs     # 运行时控制器
├── EnvironmentTimelinePlayableAsset.cs  # Timeline Clip 资产
├── EnvironmentTimelinePlayableBehaviour.cs # Timeline 运行时行为
├── EnvironmentTimelineTrack.cs          # Timeline Track 定义
├── EnvironmentTimelineTrackMixer.cs     # Timeline 混音器
├── Editor/
│   ├── EnvironmentTimelineEditorWindow.cs   # 主编辑器窗口 + 烘焙工具
│   ├── EnvironmentTimelineControllerEditor.cs # Controller Inspector 增强
│   └── EnvironmentTimelinePlayableAssetEditor.cs # Clip Inspector
└── README.md                           # 本文档
```
