// CustomProbeSamplingSceneView.cs
// SceneView Gizmo 可视化：在场景视图中绘制采样点 → 4 近邻 Probe 的连线、权重标签和所有 Probe 点云
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BYTools.EnvTimeline
{
    /// <summary>
    /// 静态可视化状态。由 Controller Inspector 和 Editor Window 共同写入，
    /// 通过 [InitializeOnLoad] 自动订阅 SceneView.duringSceneGui。
    /// </summary>
    [InitializeOnLoad]
    public static class CustomProbeSamplingSceneView
    {
        // ---- 可视化状态 ----
        public static bool showInSceneView = true;
        public static bool showAllProbes = false;       // 是否绘制快照中全部 Probe 点云
        public static Renderer debugRenderer;            // 被采样的 Renderer
        public static LightProbeSnapshot snapshot;       // 当前查询的快照
        public static int neighborCount = 4;
        public static Color lineColor = new Color(1f, 0.6f, 0.1f);

        // 🆕 四面体可视化
        public static ProbeInterpolationMode interpolationMode = ProbeInterpolationMode.InverseDistance;
        public static bool showTetrahedra = false;        // 显示全部四面体线框
        public static bool highlightContainingTetra = true; // 高亮采样点所在的四面体

        // 🆕 探针布置预览
        public static Vector3[] placementPreviewPositions;
        public static bool showPlacementPreview = false;

        // 采样结果缓存（避免 OnSceneGUI 每帧重算）
        static CustomProbeSamplingResult s_cachedResult;
        static Vector3 s_cachedPosition;
        static int s_cachedRendererId;
        static int s_cachedSnapshotId;
        static int s_cachedNeighborCount;
        static ProbeInterpolationMode s_cachedMode;  // 🆕 缓存模式
        static int s_cachedContainingTetra = -1;       // 🆕 缓存包含四面体索引

        // 权重对应颜色（从高权重暖色到低权重冷色）
        static readonly Color[] kSlotColors =
        {
            new Color(1.0f, 0.4f, 0.2f),  // slot 0
            new Color(1.0f, 0.8f, 0.2f),  // slot 1
            new Color(0.4f, 0.9f, 0.4f),  // slot 2
            new Color(0.4f, 0.6f, 1.0f),  // slot 3
        };

        static CustomProbeSamplingSceneView()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        /// <summary>使缓存失效，下一帧重新计算</summary>
        public static void InvalidateCache()
        {
            s_cachedRendererId = -1;
        }

        /// <summary>获取或重新计算采样结果</summary>
        public static CustomProbeSamplingResult GetResult()
        {
            if (debugRenderer == null || snapshot == null || !snapshot.IsValid)
                return default;

            Vector3 pos = debugRenderer.bounds.center;
            int rid = debugRenderer.GetInstanceID();
            int sid = snapshot.GetHashCode();

            if (s_cachedRendererId == rid &&
                s_cachedSnapshotId == sid &&
                s_cachedNeighborCount == neighborCount &&
                s_cachedMode == interpolationMode &&
                (s_cachedPosition - pos).sqrMagnitude < 0.0001f)
            {
                return s_cachedResult;
            }

            s_cachedResult = CustomProbeSamplingDebugger.Sample(snapshot, pos, neighborCount, interpolationMode);
            s_cachedPosition = pos;
            s_cachedRendererId = rid;
            s_cachedSnapshotId = sid;
            s_cachedNeighborCount = neighborCount;
            s_cachedMode = interpolationMode;

            // 🆕 查找包含四面体
            s_cachedContainingTetra = -1;
            if (interpolationMode == ProbeInterpolationMode.Tetrahedral)
            {
                var interp = CustomProbeSamplingDebugger.GetTetraInterpolator(snapshot);
                if (interp != null && interp.IsBuilt)
                    s_cachedContainingTetra = interp.FindContainingTetrahedron(pos);
            }

            return s_cachedResult;
        }

        static void OnSceneGUI(SceneView sceneView)
        {
            if (!showInSceneView) return;
            if (debugRenderer == null || snapshot == null || !snapshot.IsValid) return;

            var result = GetResult();
            if (!result.IsValid) return;

            Vector3 samplePos = result.samplePosition;
            var positions = snapshot.SamplePositions != null ? snapshot.SamplePositions : snapshot.positions;

            // 保存 Handles 状态
            Color oldColor = Handles.color;
            float oldSize = HandleUtility.GetHandleSize(samplePos);

            // 🆕 四面体可视化
            if (interpolationMode == ProbeInterpolationMode.Tetrahedral)
            {
                var interp = CustomProbeSamplingDebugger.GetTetraInterpolator(snapshot);
                if (interp != null && interp.IsBuilt && positions != null)
                {
                    // 显示全部四面体线框
                    if (showTetrahedra)
                    {
                        Handles.color = new Color(0.3f, 0.5f, 0.8f, 0.15f);
                        foreach (var tet in interp.Tetrahedra)
                        {
                            DrawTetrahedronWireframe(positions[tet.a], positions[tet.b],
                                positions[tet.c], positions[tet.d], 0.5f);
                        }
                    }

                    // 高亮包含四面体
                    if (highlightContainingTetra && s_cachedContainingTetra >= 0)
                    {
                        var tet = interp.Tetrahedra[s_cachedContainingTetra];
                        Handles.color = new Color(0.2f, 1f, 0.4f, 0.8f);
                        DrawTetrahedronWireframe(positions[tet.a], positions[tet.b],
                            positions[tet.c], positions[tet.d], 2f);

                        // 半透明面
                        Handles.color = new Color(0.2f, 1f, 0.4f, 0.08f);
                        DrawTetrahedronFaces(positions[tet.a], positions[tet.b],
                            positions[tet.c], positions[tet.d]);
                    }
                }
            }

            // ---- 画采样点（菱形标记）----
            DrawDiamond(samplePos, oldSize * 0.15f, Color.white);

            // ---- 可选：画全部 Probe 点云 ----
            if (showAllProbes && positions != null)
            {
                Handles.color = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                float probePointSize = oldSize * 0.05f;
                for (int i = 0; i < positions.Length; i++)
                {
                    // 跳过已选中的 4 个
                    if (i == result.i0 || i == result.i1 || i == result.i2 || i == result.i3)
                        continue;
                    Handles.SphereHandleCap(0, positions[i], Quaternion.identity,
                        probePointSize, EventType.Repaint);
                }
            }

            // ---- 画 4 近邻连线和标签 ----
            for (int slot = 0; slot < result.neighborCount; slot++)
            {
                int idx = result.GetIndex(slot);
                if (idx < 0) break;

                float w = result.GetWeight(slot);
                if (w <= 0f) continue;

                float distSqr = result.GetDistanceSqr(slot);
                float dist = Mathf.Sqrt(Mathf.Max(distSqr, 0f));

                Vector3 probePos = positions[idx];
                Color slotColor = kSlotColors[slot];

                // 连线（粗细按权重）
                Handles.color = slotColor;
                float lineThickness = Mathf.Lerp(0.5f, 4f, w);
                Handles.DrawAAPolyLine(lineThickness, samplePos, probePos);

                // Probe 位置画球
                float sphereSize = HandleUtility.GetHandleSize(probePos) * Mathf.Lerp(0.08f, 0.2f, w);
                Handles.SphereHandleCap(0, probePos, Quaternion.identity,
                    sphereSize, EventType.Repaint);

                // 在连线中点附近标权重和距离
                Vector3 mid = Vector3.Lerp(samplePos, probePos, 0.5f);
                string label = $"#{idx}  w={w:P1}\n  d={dist:F2}m";

                Handles.BeginGUI();
                Vector2 label2D = HandleUtility.WorldToGUIPoint(mid);
                Vector2 labelSize = new Vector2(90, 28);
                Rect labelRect = new Rect(label2D.x - labelSize.x * 0.5f,
                                          label2D.y - labelSize.y, labelSize.x, labelSize.y);

                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(slotColor.r, slotColor.g, slotColor.b, 0.8f);
                GUI.Box(labelRect, label, CreateLabelStyle(slotColor));
                GUI.backgroundColor = oldBg;
                Handles.EndGUI();
            }

            // ---- 在采样点上方标总数 ----
            {
                Handles.BeginGUI();
                Vector2 p2D = HandleUtility.WorldToGUIPoint(samplePos);
                string modeLabel = interpolationMode == ProbeInterpolationMode.Tetrahedral
                    ? "四面体" : "IDW";
                string summary = $"采样点\n{result.ActiveCount} 近邻 ({modeLabel})";
                if (interpolationMode == ProbeInterpolationMode.Tetrahedral)
                {
                    summary += s_cachedContainingTetra >= 0 ? "\n✓在四面体内" : "\n⚠凸包外回退";
                }
                Rect r = new Rect(p2D.x - 50, p2D.y - 52, 100, 40);
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.1f, 0.1f, 0.15f, 0.85f);
                GUI.Box(r, summary, CreateSummaryStyle());
                GUI.backgroundColor = oldBg;
                Handles.EndGUI();
            }

            // 🆕 探针布置预览
            if (showPlacementPreview && placementPreviewPositions != null && placementPreviewPositions.Length > 0)
            {
                Handles.color = new Color(0.4f, 0.9f, 1f, 0.6f);
                foreach (var pos in placementPreviewPositions)
                {
                    float psize = HandleUtility.GetHandleSize(pos) * 0.08f;
                    Handles.SphereHandleCap(0, pos, Quaternion.identity, psize, EventType.Repaint);
                }
            }

            // 恢复
            Handles.color = oldColor;
        }

        // 🆕 绘制四面体线框
        static void DrawTetrahedronWireframe(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float thickness)
        {
            Handles.DrawAAPolyLine(thickness, a, b);
            Handles.DrawAAPolyLine(thickness, a, c);
            Handles.DrawAAPolyLine(thickness, a, d);
            Handles.DrawAAPolyLine(thickness, b, c);
            Handles.DrawAAPolyLine(thickness, b, d);
            Handles.DrawAAPolyLine(thickness, c, d);
        }

        // 🆕 绘制四面体半透明面
        static void DrawTetrahedronFaces(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { a, b, c },
                new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.06f),
                new Color(0, 0, 0, 0));
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { a, b, d },
                new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.06f),
                new Color(0, 0, 0, 0));
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { a, c, d },
                new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.06f),
                new Color(0, 0, 0, 0));
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { b, c, d },
                new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.06f),
                new Color(0, 0, 0, 0));
        }

        static void DrawDiamond(Vector3 pos, float size, Color color)
        {
            Color old = Handles.color;
            Handles.color = color;

            Vector3 right = Vector3.right * size;
            Vector3 up = Vector3.up * size;
            Vector3 fwd = Vector3.forward * size;

            // 三轴菱形
            Handles.DrawAAPolyLine(2f, pos - right, pos + right);
            Handles.DrawAAPolyLine(2f, pos - up, pos + up);
            Handles.DrawAAPolyLine(2f, pos - fwd, pos + fwd);

            Handles.color = old;
        }

        static GUIStyle CreateLabelStyle(Color c)
        {
            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                fontSize = 9,
                alignment = TextAnchor.UpperCenter,
                normal = { textColor = Color.white },
                padding = new RectOffset(2, 2, 1, 1),
            };
            return style;
        }

        static GUIStyle CreateSummaryStyle()
        {
            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.95f, 0.4f) },
            };
            return style;
        }
    }
}
#endif
