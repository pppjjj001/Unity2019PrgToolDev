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

        // 采样结果缓存（避免 OnSceneGUI 每帧重算）
        static CustomProbeSamplingResult s_cachedResult;
        static Vector3 s_cachedPosition;
        static int s_cachedRendererId;
        static int s_cachedSnapshotId;
        static int s_cachedNeighborCount;

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
                (s_cachedPosition - pos).sqrMagnitude < 0.0001f)
            {
                return s_cachedResult;
            }

            s_cachedResult = CustomProbeSamplingDebugger.Sample(snapshot, pos, neighborCount);
            s_cachedPosition = pos;
            s_cachedRendererId = rid;
            s_cachedSnapshotId = sid;
            s_cachedNeighborCount = neighborCount;
            return s_cachedResult;
        }

        static void OnSceneGUI(SceneView sceneView)
        {
            if (!showInSceneView) return;
            if (debugRenderer == null || snapshot == null || !snapshot.IsValid) return;

            var result = GetResult();
            if (!result.IsValid) return;

            Vector3 samplePos = result.samplePosition;
            var positions = snapshot.positions;

            // 保存 Handles 状态
            Color oldColor = Handles.color;
            float oldSize = HandleUtility.GetHandleSize(samplePos);

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
                string summary = $"采样点\n{result.ActiveCount} 近邻";
                Rect r = new Rect(p2D.x - 40, p2D.y - 40, 80, 28);
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.1f, 0.1f, 0.15f, 0.85f);
                GUI.Box(r, summary, CreateSummaryStyle());
                GUI.backgroundColor = oldBg;
                Handles.EndGUI();
            }

            // 恢复
            Handles.color = oldColor;
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
