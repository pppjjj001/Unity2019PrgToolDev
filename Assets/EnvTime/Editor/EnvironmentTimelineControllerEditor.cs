// EnvironmentTimelineControllerEditor.cs
// 重写版：分组折叠 Inspector + 自定义 LightProbe 采样调试面板 + OnSceneGUI 联动
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimeline
{
    [CustomEditor(typeof(EnvironmentTimelineController))]
    public class EnvironmentTimelineControllerEditor : Editor
    {
        // 折叠状态
        bool foldTime = true;
        bool foldWrite = true;
        bool foldReflProbe = true;
        bool foldLightProbe = true;
        bool foldCustomSampling = false;   // 默认收起
        bool foldDebug = false;
        bool foldTimelineJump = false;

        // 采样调试状态（Inspector 局部，与 SceneView 静态状态联动）
        Renderer debugSampleRenderer;
        bool debugShowInScene = true;
        bool debugShowAllProbes = false;
        bool debugAutoPickFromSelected = true;
        CustomProbeSamplingResult debugResult;

        // 颜色与样式
        static readonly Color CLR_HEADER_LIGHT = new Color(0.4f, 0.9f, 1f);
        static readonly Color CLR_HEADER_PROBE = new Color(0.4f, 0.9f, 0.6f);
        static readonly Color CLR_HEADER_CUSTOM = new Color(1f, 0.8f, 0.3f);
        static readonly Color CLR_HEADER_DEBUG = new Color(1f, 0.5f, 0.3f);
        static readonly Color CLR_HEADER_TIME = new Color(1f, 0.85f, 0.3f);
        static readonly Color CLR_MUTED = new Color(0.6f, 0.6f, 0.6f);
        static readonly Color CLR_OK = new Color(0.4f, 1f, 0.5f);
        static readonly Color CLR_WARN = new Color(1f, 0.6f, 0.2f);

        // 权重条颜色（与 SceneView 一致）
        static readonly Color[] kSlotColors =
        {
            new Color(1.0f, 0.4f, 0.2f),
            new Color(1.0f, 0.8f, 0.2f),
            new Color(0.4f, 0.9f, 0.4f),
            new Color(0.4f, 0.6f, 1.0f),
        };

        public override void OnInspectorGUI()
        {
            var ctrl = (EnvironmentTimelineController)target;

            DrawTimeSection(ctrl);
            DrawWriteSection(ctrl);
            DrawReflectionProbeSection(ctrl);
            DrawLightProbeSection(ctrl);
            DrawCustomSamplingSection(ctrl);
            DrawDebugSection(ctrl);
            DrawTimelineJumpSection(ctrl);
        }

        // ============================================================
        // 1. 时间
        // ============================================================
        void DrawTimeSection(EnvironmentTimelineController ctrl)
        {
            foldTime = DrawFoldHeader(foldTime, "⏱ 时间设置", CLR_HEADER_TIME);
            if (!foldTime) return;

            EditorGUI.BeginChangeCheck();
            var sp = serializedObject.FindProperty("currentTime");
            EditorGUILayout.PropertyField(sp);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoPlay"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("timeSpeed"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                if (!Application.isPlaying)
                {
                    ctrl.ApplyAtCurrentTime();
                    SceneView.RepaintAll();
                }
            }
        }

        // ============================================================
        // 2. 写入选项
        // ============================================================
        void DrawWriteSection(EnvironmentTimelineController ctrl)
        {
            foldWrite = DrawFoldHeader(foldWrite, "✍ 写入选项", CLR_HEADER_LIGHT);
            if (!foldWrite) return;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("writeToRenderSettings"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("writeToMPB"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("writeMainCubemapToMaterial"));
            if (ctrl.writeMainCubemapToMaterial)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("envCubemapPropName"));
            serializedObject.ApplyModifiedProperties();
        }

        // ============================================================
        // 3. ReflectionProbe 控制
        // ============================================================
        void DrawReflectionProbeSection(EnvironmentTimelineController ctrl)
        {
            foldReflProbe = DrawFoldHeader(foldReflProbe, "🔮 ReflectionProbe 控制", CLR_HEADER_PROBE);
            if (!foldReflProbe) return;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("controlReflectionProbes"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("blendProbeIntensity"));
            serializedObject.ApplyModifiedProperties();
        }

        // ============================================================
        // 4. Light Probe 控制（全局混合）
        // ============================================================
        void DrawLightProbeSection(EnvironmentTimelineController ctrl)
        {
            foldLightProbe = DrawFoldHeader(foldLightProbe, "💡 Light Probe 全局混合", CLR_HEADER_PROBE);
            if (!foldLightProbe) return;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("blendLightProbes"));

            if (ctrl.blendLightProbes)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("lightProbeUpdateInterval"));
                EditorGUILayout.HelpBox(
                    "全局混合：将所有节点快照的 SH 系数线性插值后直接写入 LightmapSettings.lightProbes.bakedProbes。\n" +
                    "要求节点快照的 ProbeCount 与当前场景一致。",
                    MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ============================================================
        // 5. ★ 自定义 Renderer Light Probe 采样（核心）
        // ============================================================
        void DrawCustomSamplingSection(EnvironmentTimelineController ctrl)
        {
            foldCustomSampling = DrawFoldHeader(foldCustomSampling,
                "🎯 自定义 Renderer Light Probe 采样（绕过场景依赖）", CLR_HEADER_CUSTOM);
            if (!foldCustomSampling) return;

            EditorGUILayout.HelpBox(
                "此模式不依赖 LightmapSettings.lightProbes，而是用每个节点存储的 LightProbeSnapshot " +
                "自行做 4 近邻逆距离加权（IDW），结果通过 MaterialPropertyBlock 写入 Renderer，" +
                "并将 lightProbeUsage 改为 CustomProvided。\n\n" +
                "• 适用于节点间 LightProbe 数量/位置不一致的场景\n" +
                "• 静态物体建议开启 cacheCustomProbeWeights\n" +
                "• cost 从 Renderer×Probe 降到 Renderer×4",
                MessageType.Info);

            EditorGUILayout.Space(4);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("useCustomRendererLightProbes"));

            if (ctrl.useCustomRendererLightProbes)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("customLightProbeNeighborCount"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cacheCustomProbeWeights"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("lightProbeUpdateInterval"));

                EditorGUILayout.Space(4);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("prefabRoot"));
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ============================================================
        // 6. ★ 采样调试面板
        // ============================================================
        void DrawDebugSection(EnvironmentTimelineController ctrl)
        {
            foldDebug = DrawFoldHeader(foldDebug, "🔬 采样调试", CLR_HEADER_DEBUG);
            if (!foldDebug) return;

            if (ctrl.timelineData == null || ctrl.timelineData.nodes.Count == 0)
            {
                EditorGUILayout.HelpBox("没有 Timeline 节点数据", MessageType.Warning);
                return;
            }

            // 确定当前 from/to 节点
            ctrl.timelineData.Sample(ctrl.currentTime, out var fromNode, out var toNode, out float t);

            EditorGUILayout.LabelField($"当前时间: {ctrl.currentTime:F2}  插值 t={t:F2}");
            EditorGUILayout.LabelField($"From: {(fromNode != null ? fromNode.nodeName : "null")}  →  " +
                                       $"To: {(toNode != null ? toNode.nodeName : "null")}");

            EditorGUILayout.Space(4);

            // 选择快照来源
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("采样快照来源", GUILayout.Width(90));
            var snap = fromNode != null ? fromNode.lightProbeData : null;
            if (snap != null && snap.IsValid)
                EditorGUILayout.LabelField($"From 节点 ({snap.ProbeCount} probes)", GUILayout.Width(180));
            else
            {
                var snap2 = toNode != null ? toNode.lightProbeData : null;
                if (snap2 != null && snap2.IsValid)
                {
                    EditorGUILayout.LabelField($"To 节点 ({snap2.ProbeCount} probes)", GUILayout.Width(180));
                    snap = snap2;
                }
                else
                {
                    EditorGUILayout.LabelField("⚠ 无有效快照", GUILayout.Width(180));
                }
            }
            EditorGUILayout.EndHorizontal();

            if (snap == null || !snap.IsValid)
            {
                EditorGUILayout.HelpBox("当前 from/to 节点都没有有效的 LightProbeSnapshot。\n请先在 Env Timeline 编辑器中捕获 LightProbe 数据。",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(4);

            // Renderer 选择
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("采样 Renderer", GUILayout.Width(90));
            debugSampleRenderer = (Renderer)EditorGUILayout.ObjectField(
                debugSampleRenderer, typeof(Renderer), true);
            EditorGUILayout.EndHorizontal();

            // 自动从选中物体获取
            debugAutoPickFromSelected = EditorGUILayout.Toggle("自动从选中物体获取", debugAutoPickFromSelected);

            if (debugAutoPickFromSelected && debugSampleRenderer == null)
            {
                var sel = Selection.activeGameObject;
                if (sel != null)
                    debugSampleRenderer = sel.GetComponent<Renderer>();
            }

            // 邻居数
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("邻居数", GUILayout.Width(90));
            int nc = EditorGUILayout.IntSlider(CustomProbeSamplingSceneView.neighborCount, 1, 4);
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                CustomProbeSamplingSceneView.neighborCount = nc;
                CustomProbeSamplingSceneView.InvalidateCache();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(4);

            // SceneView 可视化开关
            EditorGUI.BeginChangeCheck();
            debugShowInScene = EditorGUILayout.Toggle("SceneView 可视化", debugShowInScene);
            debugShowAllProbes = EditorGUILayout.Toggle("显示全部 Probe 点云", debugShowAllProbes);
            if (EditorGUI.EndChangeCheck())
            {
                CustomProbeSamplingSceneView.showInSceneView = debugShowInScene;
                CustomProbeSamplingSceneView.showAllProbes = debugShowAllProbes;
                SceneView.RepaintAll();
            }

            // 执行采样
            if (debugSampleRenderer != null)
            {
                EditorGUILayout.Space(6);

                Vector3 samplePos = debugSampleRenderer.bounds.center;
                debugResult = CustomProbeSamplingDebugger.Sample(snap, samplePos, nc);

                // 联动 SceneView
                CustomProbeSamplingSceneView.debugRenderer = debugSampleRenderer;
                CustomProbeSamplingSceneView.snapshot = snap;
                CustomProbeSamplingSceneView.neighborCount = nc;

                if (!debugResult.IsValid)
                {
                    EditorGUILayout.HelpBox("采样失败：快照无效或 Probe 数为 0", MessageType.Error);
                    return;
                }

                // 采样结果摘要
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("采样结果", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  采样位置: {samplePos}");
                EditorGUILayout.LabelField($"  有效邻居: {debugResult.ActiveCount} / {nc}");

                // SH 颜色预览
                Color shColor = CustomProbeSamplingDebugger.EvaluateSHColor(debugResult.blendedSH, Vector3.up);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("  SH→↑方向色:", GUILayout.Width(90));
                Rect colorRect = GUILayoutUtility.GetRect(60, 18, GUILayout.Width(60));
                EditorGUI.DrawRect(colorRect, shColor.gamma);
                EditorGUILayout.LabelField($"({shColor.r:F2}, {shColor.g:F2}, {shColor.b:F2})");
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();

                EditorGUILayout.Space(4);

                // 权重条
                EditorGUILayout.LabelField("权重分布", EditorStyles.boldLabel);
                for (int slot = 0; slot < nc; slot++)
                {
                    int idx = debugResult.GetIndex(slot);
                    float w = debugResult.GetWeight(slot);
                    float distSqr = debugResult.GetDistanceSqr(slot);
                    float dist = idx >= 0 ? Mathf.Sqrt(Mathf.Max(distSqr, 0f)) : -1f;

                    if (idx < 0)
                    {
                        EditorGUILayout.LabelField($"  Slot {slot}: (无)");
                        continue;
                    }

                    // 权重条
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"#{idx}", GUILayout.Width(40));
                    Color barColor = kSlotColors[slot];

                    Rect barBg = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(barBg, new Color(0.2f, 0.2f, 0.2f));
                    if (w > 0f)
                    {
                        Rect barFill = new Rect(barBg.x, barBg.y, barBg.width * w, barBg.height);
                        EditorGUI.DrawRect(barFill, barColor);
                    }

                    EditorGUILayout.LabelField($"{w:P1}  d={dist:F2}m", GUILayout.Width(110));
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space(4);

                if (GUILayout.Button("应用到此 Renderer（写入 MPB）", GUILayout.Height(24)))
                {
                    ApplyDebugSHToRenderer(debugSampleRenderer, debugResult.blendedSH);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("请选择一个 Renderer 进行采样调试", MessageType.Info);
            }
        }

        void ApplyDebugSHToRenderer(Renderer r, SphericalHarmonicsL2 sh)
        {
            Undo.RecordObject(r, "Apply Debug SH");

            var originalUsage = r.lightProbeUsage;
            if (originalUsage != LightProbeUsage.CustomProvided)
            {
                Undo.RecordObject(r, "Change LightProbeUsage");
                r.lightProbeUsage = LightProbeUsage.CustomProvided;
            }

            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);

            var buffer = new SphericalHarmonicsL2[1];
            buffer[0] = sh;
            mpb.CopySHCoefficientArraysFrom(buffer);
            r.SetPropertyBlock(mpb);

            SceneView.RepaintAll();
            Debug.Log($"[EnvTimeline] 已将采样 SH 写入 Renderer '{r.name}' 的 MPB");
        }

        // ============================================================
        // 7. Timeline 快捷跳转
        // ============================================================
        void DrawTimelineJumpSection(EnvironmentTimelineController ctrl)
        {
            foldTimelineJump = DrawFoldHeader(foldTimelineJump, "🎬 Timeline 集成 & 快捷跳转", CLR_HEADER_LIGHT);
            if (!foldTimelineJump) return;

            EditorGUILayout.HelpBox(
                "在 Timeline 窗口中添加 'Environment Timeline Track'，" +
                "绑定此 Controller，即可通过 Timeline 控制环境时间。",
                MessageType.Info);

            if (ctrl.timelineData == null) return;

            EditorGUILayout.Space(4);

            int columns = 3;
            int n = ctrl.timelineData.nodes.Count;
            for (int i = 0; i < n; i += columns)
            {
                EditorGUILayout.BeginHorizontal();
                for (int j = 0; j < columns && i + j < n; j++)
                {
                    var node = ctrl.timelineData.nodes[i + j];
                    if (GUILayout.Button($"{node.nodeName}\n[{node.time:F2}]", GUILayout.Height(36)))
                    {
                        ctrl.JumpToNode(i + j);
                        SceneView.RepaintAll();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("立即应用当前时间", GUILayout.Height(26)))
            {
                ctrl.ApplyAtCurrentTime();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("清除所有 MPB", GUILayout.Height(22)))
            {
                ctrl.ClearAllMPB();
                SceneView.RepaintAll();
            }
        }

        // ============================================================
        // 辅助：折叠标题
        // ============================================================
        bool DrawFoldHeader(bool foldout, string title, Color color)
        {
            Rect r = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(color.r * 0.3f, color.g * 0.3f, color.b * 0.3f, 0.5f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, 3, r.height), color);

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = color },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 0, 0, 0),
            };

            string arrow = foldout ? "▼" : "▶";
            GUI.Label(r, $"{arrow}  {title}", style);

            // 点击区域
            var e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                foldout = !foldout;
                e.Use();
                GUI.changed = true;
            }
            return foldout;
        }
    }
}
#endif
