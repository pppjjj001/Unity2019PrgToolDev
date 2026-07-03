// LightProbePlacementUtility.cs
// 动态 LightProbe 自动布置工具（Editor）
//
// 提供三种布置模式：
// 1. 网格布置（Grid）：在指定 Bounds 内生成均匀网格点，可选多层高度
// 2. Renderer 包围盒布置（Renderer Bounds）：从场景 Renderer 自动收集包围盒并填充
// 3. NavMesh 布置（NavMesh）：在导航网格上方生成探针（适合角色行走的区域）
//
// 参考方案：
// - Unity APV（Adaptive Probe Volumes）的自动体积填充思路
// - Light Probe Populator（alexismorin）的空旷区域采样
// - auto-light-probes（ratozumbi）的 NavMesh 采样思路
//
// 使用方式：通过 EnvironmentTimelineEditorWindow 的"探针布置"面板调用
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace BYTools.EnvTimeline
{
    /// <summary>
    /// 探针布置模式
    /// </summary>
    public enum ProbePlacementMode
    {
        /// <summary>手动 Bounds 网格布置</summary>
        GridBounds = 0,
        /// <summary>从场景 Renderer 自动收集包围盒</summary>
        RendererBounds = 1,
        /// <summary>NavMesh 表面上方布置（角色活动区域）</summary>
        NavMeshSurface = 2,
    }

    /// <summary>
    /// 探针布置参数
    /// </summary>
    [System.Serializable]
    public class ProbePlacementSettings
    {
        [Header("布置模式")]
        public ProbePlacementMode mode = ProbePlacementMode.GridBounds;

        [Header("网格参数（GridBounds / RendererBounds 模式）")]
        [Tooltip("X 方向间距（世界单位）")]
        public float spacingX = 4f;
        [Tooltip("Y 方向间距（世界单位）")]
        public float spacingY = 3f;
        [Tooltip("Z 方向间距（世界单位）")]
        public float spacingZ = 4f;

        [Tooltip("Y 方向偏移（从地面抬高）")]
        public float yOffset = 1.5f;

        [Tooltip("多层布置：在地面上方额外添加几层探针")]
        [Range(0, 5)]
        public int extraLayers = 1;
        [Tooltip("每层之间的 Y 间距")]
        public float layerHeight = 3f;

        [Header("手动 Bounds（GridBounds 模式）")]
        public Bounds customBounds = new Bounds(Vector3.zero, new Vector3(40f, 10f, 40f));

        [Header("RendererBounds 模式")]
        [Tooltip("只收集这些层级的 Renderer。留空则收集所有")]
        public int rendererLayerMask = ~0; // int 类型，用位掩码表示
        [Tooltip("包围盒向外扩展的余量")]
        public float boundsMargin = 1f;
        [Tooltip("最大 Renderer 数量（防止超大场景卡顿）")]
        public int maxRendererCount = 500;

        [Header("NavMesh 模式")]
        [Tooltip("NavMesh 采样间距")]
        public float navMeshSpacing = 5f;
        [Tooltip("NavMesh 采样的最大半径")]
        public float navMeshSampleRadius = 100f;

        [Header("边界探针")]
        [Tooltip("在包围盒边缘额外添加探针（防止四面体外推伪影）")]
        public bool addBoundaryProbes = true;
        [Tooltip("边界探针向外延伸的距离")]
        public float boundaryExtend = 2f;

        [Header("优化")]
        [Tooltip("最小间距：两个探针距离小于此值时跳过（去重）")]
        public float minDistance = 0.5f;
        [Tooltip("是否在布置后自动烘焙 LightProbe")]
        public bool autoBake = false;
    }

    /// <summary>
    /// 动态探针布置工具
    /// </summary>
    public static class LightProbePlacementUtility
    {
        /// <summary>
        /// 根据参数生成探针位置并写入 LightProbeGroup
        /// </summary>
        /// <param name="settings">布置参数</param>
        /// <param name="targetGroup">目标 LightProbeGroup（为 null 则在场景中创建）</param>
        /// <param name="origin">布置原点（GridBounds 模式使用 customBounds 的中心）</param>
        /// <returns>写入后的 LightProbeGroup</returns>
        public static LightProbeGroup PlaceProbes(
            ProbePlacementSettings settings,
            LightProbeGroup targetGroup,
            Vector3 origin)
        {
            List<Vector3> positions = GeneratePositions(settings, origin);

            if (positions.Count == 0)
            {
                Debug.LogWarning("[LightProbePlacement] 生成的探针位置为空，请检查参数");
                return targetGroup;
            }

            // 获取或创建 LightProbeGroup
            if (targetGroup == null)
            {
                var go = new GameObject("LightProbeGroup (Auto)");
                targetGroup = go.AddComponent<LightProbeGroup>();
                Undo.RegisterCreatedObjectUndo(go, "Create LightProbeGroup");
            }

            // LightProbeGroup.positions 是局部坐标
            // 如果 targetGroup 没有位移，局部 = 世界
            Transform t = targetGroup.transform;
            Vector3 groupPos = t.position;
            Vector3[] localPositions = new Vector3[positions.Count];
            for (int i = 0; i < positions.Count; i++)
                localPositions[i] = positions[i] - groupPos;

            Undo.RecordObject(targetGroup, "Place Light Probes");
            targetGroup.probePositions = localPositions;
            EditorUtility.SetDirty(targetGroup);

            Debug.Log($"[LightProbePlacement] 已生成 {positions.Count} 个探针位置到 '{targetGroup.name}'");

            if (settings.autoBake)
            {
                LightProbeBaker.BakeLightProbesOnly();
            }

            return targetGroup;
        }

        /// <summary>
        /// 根据模式生成探针世界坐标列表
        /// </summary>
        public static List<Vector3> GeneratePositions(ProbePlacementSettings settings, Vector3 origin)
        {
            List<Vector3> positions = new List<Vector3>();

            switch (settings.mode)
            {
                case ProbePlacementMode.GridBounds:
                    GenerateGridBounds(settings, settings.customBounds, positions);
                    break;

                case ProbePlacementMode.RendererBounds:
                    GenerateFromRenderers(settings, origin, positions);
                    break;

                case ProbePlacementMode.NavMeshSurface:
                    GenerateFromNavMesh(settings, origin, positions);
                    break;
            }

            // 边界探针
            if (settings.addBoundaryProbes)
                AddBoundaryProbes(settings, positions);

            // 去重
            if (settings.minDistance > 0f)
                RemoveDuplicates(positions, settings.minDistance);

            return positions;
        }

        // ============================================================
        // 网格布置
        // ============================================================
        static void GenerateGridBounds(ProbePlacementSettings s, Bounds bounds, List<Vector3> output)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            // 确保 spacing 不为零
            float sx = Mathf.Max(s.spacingX, 0.1f);
            float sy = Mathf.Max(s.spacingY, 0.1f);
            float sz = Mathf.Max(s.spacingZ, 0.1f);

            int nx = Mathf.CeilToInt((max.x - min.x) / sx) + 1;
            int nz = Mathf.CeilToInt((max.z - min.z) / sz) + 1;

            // 底层 + 额外层
            int layers = 1 + s.extraLayers;

            for (int layer = 0; layer < layers; layer++)
            {
                float y = min.y + s.yOffset + layer * s.layerHeight;
                if (y > max.y + s.boundsMargin) break;

                for (int ix = 0; ix < nx; ix++)
                {
                    for (int iz = 0; iz < nz; iz++)
                    {
                        // 交错排列（六边形近似），减少探针数量同时保持覆盖
                        float x = min.x + ix * sx;
                        float z = min.z + iz * sz;
                        if (layer % 2 == 1)
                        {
                            x += sx * 0.5f;
                            z += sz * 0.5f;
                        }

                        output.Add(new Vector3(x, y, z));
                    }
                }
            }
        }

        // ============================================================
        // Renderer 包围盒布置
        // ============================================================
        static void GenerateFromRenderers(ProbePlacementSettings s, Vector3 origin, List<Vector3> output)
        {
            // 收集所有 Renderer
            var allRenderers = Object.FindObjectsOfType<Renderer>();
            int count = 0;
            Bounds combinedBounds = new Bounds();

            foreach (var r in allRenderers)
            {
                if (count >= s.maxRendererCount) break;
                if (r == null) continue;

                // 层级过滤
                if ((s.rendererLayerMask & (1 << r.gameObject.layer)) == 0)
                    continue;

                // 跳过过小的 Renderer
                if (r.bounds.size.magnitude < 0.5f) continue;

                if (count == 0)
                    combinedBounds = r.bounds;
                else
                    combinedBounds.Encapsulate(r.bounds);
                count++;
            }

            if (count == 0)
            {
                Debug.LogWarning("[LightProbePlacement] 未找到任何 Renderer");
                return;
            }

            // 向外扩展
            combinedBounds.Expand(s.boundsMargin);

            GenerateGridBounds(s, combinedBounds, output);
        }

        // ============================================================
        // NavMesh 布置
        // ============================================================
        static void GenerateFromNavMesh(ProbePlacementSettings s, Vector3 origin, List<Vector3> output)
        {
            float spacing = Mathf.Max(s.navMeshSpacing, 0.5f);
            float radius = s.navMeshSampleRadius;

            int range = Mathf.CeilToInt(radius / spacing);

            for (int ix = -range; ix <= range; ix++)
            {
                for (int iz = -range; iz <= range; iz++)
                {
                    float x = origin.x + ix * spacing;
                    float z = origin.z + iz * spacing;

                    // NavMesh 采样
                    if (NavMesh.SamplePosition(
                        new Vector3(x, origin.y, z),
                        out NavMeshHit hit,
                        spacing * 1.5f,
                        NavMesh.AllAreas))
                    {
                        // 在命中点上方布置多层
                        for (int layer = 0; layer <= s.extraLayers; layer++)
                        {
                            float y = hit.position.y + s.yOffset + layer * s.layerHeight;
                            output.Add(new Vector3(hit.position.x, y, hit.position.z));
                        }
                    }
                }
            }
        }

        // ============================================================
        // 边界探针（凸包外延，防止四面体回退伪影）
        // ============================================================
        static void AddBoundaryProbes(ProbePlacementSettings s, List<Vector3> positions)
        {
            if (positions.Count == 0) return;

            // 计算包围盒
            Vector3 min = positions[0], max = positions[0];
            foreach (var p in positions)
            {
                if (p.x < min.x) min.x = p.x;
                if (p.y < min.y) min.y = p.y;
                if (p.z < min.z) min.z = p.z;
                if (p.x > max.x) max.x = p.x;
                if (p.y > max.y) max.y = p.y;
                if (p.z > max.z) max.z = p.z;
            }

            float ext = s.boundaryExtend;
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;

            // 8 个角点 + 6 个面中心点，向外延伸
            Vector3[] corners = new Vector3[]
            {
                new Vector3(min.x - ext, min.y - ext, min.z - ext),
                new Vector3(max.x + ext, min.y - ext, min.z - ext),
                new Vector3(min.x - ext, max.y + ext, min.z - ext),
                new Vector3(max.x + ext, max.y + ext, min.z - ext),
                new Vector3(min.x - ext, min.y - ext, max.z + ext),
                new Vector3(max.x + ext, min.y - ext, max.z + ext),
                new Vector3(min.x - ext, max.y + ext, max.z + ext),
                new Vector3(max.x + ext, max.y + ext, max.z + ext),
            };

            foreach (var c in corners)
                positions.Add(c);

            // 面中心外延
            positions.Add(new Vector3(center.x, min.y - ext, center.z));
            positions.Add(new Vector3(center.x, max.y + ext, center.z));
            positions.Add(new Vector3(min.x - ext, center.y, center.z));
            positions.Add(new Vector3(max.x + ext, center.y, center.z));
            positions.Add(new Vector3(center.x, center.y, min.z - ext));
            positions.Add(new Vector3(center.x, center.y, max.z + ext));
        }

        // ============================================================
        // 去重（距离小于 minDistance 的点只保留一个）
        // ============================================================
        static void RemoveDuplicates(List<Vector3> positions, float minDist)
        {
            float minDistSqr = minDist * minDist;
            for (int i = positions.Count - 1; i >= 1; i--)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    if ((positions[i] - positions[j]).sqrMagnitude < minDistSqr)
                    {
                        positions.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 预览生成的探针位置（不写入 LightProbeGroup）
        /// </summary>
        public static Vector3[] PreviewPositions(ProbePlacementSettings settings, Vector3 origin)
        {
            var list = GeneratePositions(settings, origin);
            return list.ToArray();
        }
    }
}
#endif
