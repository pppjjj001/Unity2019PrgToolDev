// EnvTimelineSimpleEditorWindow.cs（壳类 - 通过反射调用 EnvTimelineSimpleCore）
// 所有核心业务逻辑已抽取到 EnvTimelineSimpleCore，本类仅保留 UI 绘制和事件处理
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Hotfix.Core.EnvTimelineSimple;

namespace UnityEditor.EnvTimelineSimple
{
    /// <summary>
    /// 环境时间轴编辑器窗口（壳类）
    /// 核心功能通过 EnvTimelineSimpleProvider 反射调用 EnvTimelineSimpleCore
    /// 核心可独立编译为 DLL，本壳无需直接引用核心类型
    /// </summary>
    public class EnvTimelineSimpleEditorWindow : EditorWindow
    {
        // ============================================================
        // 反射桥接器
        // ============================================================
        EnvTimelineSimpleProvider _core;
        EnvTimelineSimpleProvider core
        {
            get
            {
                if (_core == null)
                    _core = new EnvTimelineSimpleProvider();
                return _core;
            }
        }

        /// <summary>同步壳的状态到核心</summary>
        void SyncCoreProperties()
        {
            if (_core != null && _core.IsValid)
            {
                _core.Data = _data;
                _core.DefaultCubemapSize = _defaultCubemapSize;
                _core.CubemapPrefix = _cubemapPrefix;
            }
        }

        // ============================================================
        // 数据与设置（与核心同步）
        // ============================================================
        EnvironmentTimelineData _data;
        EnvironmentTimelineData data
        {
            get => _data;
            set
            {
                _data = value;
                if (_core != null) _core.Data = value;
            }
        }

        [SerializeField] private int _defaultCubemapSize = 128;
        [SerializeField] private string _cubemapPrefix = "Baked";

        int defaultCubemapSize
        {
            get => _defaultCubemapSize;
            set
            {
                _defaultCubemapSize = value;
                if (_core != null) _core.DefaultCubemapSize = value;
            }
        }
        string cubemapPrefix
        {
            get => _cubemapPrefix;
            set
            {
                _cubemapPrefix = value;
                if (_core != null) _core.CubemapPrefix = value;
            }
        }

        // ============================================================
        // UI 状态
        // ============================================================
        Vector2 mainScroll;
        int selectedNodeIndex = -1;

        const float TIMELINE_HEIGHT = 70f;
        Rect timelineRect;
        bool draggingNode = false;
        int draggingIndex = -1;

        // BlendZone 拖拽状态
        enum BlendDragMode { None, Start, End, ProbeSwitch }
        BlendDragMode blendDragMode = BlendDragMode.None;
        int blendDragNodeIndex = -1;

        float previewTime = 0f;
        bool draggingPreview = false;
        const float PREVIEW_HANDLE_HEIGHT = 50f;

        // 镜面高光代理预览
        List<GameObject> _specularPreviewObjects;
        bool _isSpecularPreviewActive = false;
        int _specularPreviewNodeIndex = -1;

        // ============================================================
        // 配色
        // ============================================================
        static readonly Color CLR_TITLE       = new Color(1f, 0.85f, 0.3f);
        static readonly Color CLR_OK          = new Color(0.4f, 1f, 0.5f);
        static readonly Color CLR_WARN        = new Color(1f, 0.6f, 0.2f);
        static readonly Color CLR_ERROR       = new Color(1f, 0.35f, 0.35f);
        static readonly Color CLR_INFO        = new Color(0.5f, 0.85f, 1f);
        static readonly Color CLR_PROBE       = new Color(0.4f, 0.9f, 1f);
        static readonly Color CLR_MUTED       = new Color(0.55f, 0.55f, 0.55f);
        static readonly Color CLR_BG_PANEL    = new Color(0.22f, 0.22f, 0.26f);
        static readonly Color CLR_BG_DUP      = new Color(0.6f, 0.15f, 0.15f);
        static readonly Color CLR_BLEND_ZONE  = new Color(0.8f, 0.5f, 1f, 0.25f);
        static readonly Color CLR_BLEND_EDGE  = new Color(0.8f, 0.5f, 1f, 0.6f);
        static readonly Color CLR_PROBE_SWITCH = new Color(1f, 0.3f, 0.3f, 0.8f);
        static readonly Color CLR_PROBE_SMOOTH = new Color(1f, 0.6f, 0.2f, 0.3f);

        // ============================================================
        // MenuItem / 生命周期
        // ============================================================
        [MenuItem("Tools/BYTools/Environment Timeline Simple 编辑器", false, 110)]
        public static void Open()
        {
            var win = GetWindow<EnvTimelineSimpleEditorWindow>("Env Timeline");
            win.minSize = new Vector2(580, 500);
        }

        void OnDestroy()
        {
            ClearSpecularPreview();
        }

        /// <summary>
        /// 清除镜面高光代理预览物体（销毁所有临时 GO 和材质）
        /// </summary>
        void ClearSpecularPreview()
        {
            if (_specularPreviewObjects != null)
            {
                foreach (var go in _specularPreviewObjects)
                {
                    if (go != null)
                    {
                        var mr = go.GetComponent<MeshRenderer>();
                        if (mr != null && mr.sharedMaterial != null)
                            DestroyImmediate(mr.sharedMaterial);
                        var col = go.GetComponent<Collider>();
                        if (col != null) DestroyImmediate(col);
                        DestroyImmediate(go);
                    }
                }
                _specularPreviewObjects.Clear();
            }
            _isSpecularPreviewActive = false;
            _specularPreviewNodeIndex = -1;
            SceneView.RepaintAll();
        }

        /// <summary>
        /// 创建镜面高光代理预览物体，通过反射调用核心类的代理创建方法。
        /// </summary>
        void CreateSpecularPreview(EnvTimeNode node, int nodeIndex)
        {
            ClearSpecularPreview();

            if (node == null || !node.enableSpecularLightBaking) return;

            var lights = core.CollectSpecularLights(node);
            if (lights == null || lights.Count == 0)
            {
                EnvTimeSimpleDebug.LogWarning("[EnvTimeline] 预览: 未收集到任何光源，请检查光源配置");
                return;
            }

            _specularPreviewObjects = new List<GameObject>();
            float radius = Mathf.Max(0.001f, node.specularSphereRadius);
            float intensityMul = node.specularIntensityMultiplier;
            float areaScale = node.specularAreaPanelScale;

            foreach (var light in lights)
            {
                if (light == null) continue;

                GameObject proxy = null;
                switch (light.type)
                {
                    case LightType.Point:
                        proxy = core.CreateSpecularSphere(
                            light.transform.position, radius,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecProxy");
                        break;

                    case LightType.Spot:
                        proxy = core.CreateSpecularSphere(
                            light.transform.position, radius,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecProxy");
                        break;

                    case LightType.Area:
                        proxy = core.CreateSpecularPanel(
                            light.transform.position, light.transform.rotation,
                            new Vector3(light.areaSize.x, light.areaSize.y, 1f) * areaScale,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecPanel",
                            light.cookie);
                        break;

                    case LightType.Disc:
                        proxy = core.CreateSpecularDisc(
                            light.transform.position, light.transform.rotation,
                            radius * 10f * areaScale,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecDisc");
                        break;
                }

                if (proxy != null)
                {
                    proxy.hideFlags = HideFlags.HideAndDontSave;
                    _specularPreviewObjects.Add(proxy);
                }
            }

            if (_specularPreviewObjects.Count > 0)
            {
                _isSpecularPreviewActive = true;
                _specularPreviewNodeIndex = nodeIndex;
                EnvTimeSimpleDebug.Log($"<color=#FFD700>[EnvTimeline]</color> 预览: 创建了 {_specularPreviewObjects.Count} 个自发光代理物体（HideAndDontSave，关闭预览或编辑器时自动清理）");
                SceneView.RepaintAll();
            }
            else
            {
                EnvTimeSimpleDebug.LogWarning("[EnvTimeline] 预览: 未创建任何代理物体，可能光源类型不支持");
            }
        }

        /// <summary>
        /// 窗口获得焦点时自动检测当前选中物体上的 EnvironmentTimelineData。
        /// </summary>
        void OnFocus()
        {
            if (data == null && Selection.activeGameObject != null)
            {
                var ctrl = Selection.activeGameObject.GetComponent<EnvironmentTimelineController>();
                if (ctrl != null)
                    data = ctrl.timelineData;
                else
                    data = Selection.activeGameObject.GetComponent<EnvironmentTimelineData>();
            }
        }

        void OnGUI()
        {
            SyncCoreProperties();

            DrawHeaderBanner();
            DrawDataSelector();

            if (data == null)
            {
                EditorGUILayout.HelpBox("请在场景中选择包含 EnvironmentTimelineData 的物体，或创建新的", MessageType.Info);
                GUI.backgroundColor = CLR_OK;
                if (GUILayout.Button("✚ 在场景中创建新的 Timeline 物体", GUILayout.Height(30)))
                {
                    CreateNewTimelineInScene();
                }
                GUI.backgroundColor = Color.white;
                return;
            }

            float bottomReserved = 160f;
            float scrollHeight = position.height - GUILayoutUtility.GetLastRect().yMax - bottomReserved - 10f;
            if (scrollHeight < 100f) scrollHeight = 100f;

            mainScroll = EditorGUILayout.BeginScrollView(
                mainScroll,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(scrollHeight));

            EditorGUILayout.Space(4);
            DrawCubemapSettings();
            EditorGUILayout.Space(4);
            DrawTimeline();
            EditorGUILayout.Space(4);
            DrawPreviewTimeline();
            EditorGUILayout.Space(4);
            DrawNodeList();
            EditorGUILayout.Space(8);
            DrawSelectedNodeInspector();
            EditorGUILayout.Space(10);

            EditorGUILayout.EndScrollView();

            DrawBottomActions();

            if (GUI.changed && data != null)
            {
                EditorUtility.SetDirty(data);
                EditorUtility.SetDirty(data.gameObject);
            }
        }

        // ============================================================
        // UI 绘制方法
        // ============================================================
        void DrawHeaderBanner()
        {
            Rect r = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.15f, 0.18f, 0.25f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, 4, r.height), CLR_TITLE);

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                normal = { textColor = CLR_TITLE },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(12, 0, 0, 0)
            };
            GUI.Label(r, "🌅  Environment Timeline 编辑器", style);
        }

        static void DrawSectionHeader(string text, Color color, string emoji = "")
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = color },
                fontSize = 12
            };
            EditorGUILayout.LabelField($"{emoji} {text}", style);
        }

        void DrawDataSelector()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            data = (EnvironmentTimelineData)EditorGUILayout.ObjectField(
                "Timeline Data", data, typeof(EnvironmentTimelineData), true);

            if (EditorGUI.EndChangeCheck() && data != null)
            {
                Selection.activeGameObject = data.gameObject;
            }

            GUI.backgroundColor = CLR_INFO;
            if (GUILayout.Button("从选中物体获取", GUILayout.Width(120)))
            {
                if (Selection.activeGameObject != null)
                {
                    data = Selection.activeGameObject.GetComponent<EnvironmentTimelineData>();
                    if (data == null)
                    {
                        EditorUtility.DisplayDialog("提示",
                            "选中的物体上没有 EnvironmentTimelineData 组件", "确定");
                    }
                }
            }

            GUI.backgroundColor = CLR_OK;
            if (GUILayout.Button("新建", GUILayout.Width(50)))
            {
                CreateNewTimelineInScene();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (data != null)
            {
                EditorGUI.BeginChangeCheck();
                float td = EditorGUILayout.FloatField("时间轴总长", data.totalDuration);
                bool lp = EditorGUILayout.Toggle("循环", data.loop);
                bool he = EditorGUILayout.Toggle(new GUIContent("末尾保持最后节点", "勾选：到达时间轴末尾后保持最后一个节点的环境（默认）。\n取消：循环回到第一个节点继续模拟。"), data.holdAtEnd);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Edit Timeline");
                    data.totalDuration = Mathf.Max(0.01f, td);
                    data.loop = lp;
                    data.holdAtEnd = he;
                    EditorUtility.SetDirty(data);
                }
            }
        }

        // ---- 壳包装方法：通过反射调用核心 ----

        void CreateNewTimelineInScene()
        {
            data = core.CreateNewTimelineInScene();
        }

        void AddNodeAtTime(float time)
        {
            if (data == null) return;
            selectedNodeIndex = core.AddNodeAtTime(time);
        }

        void ApplyPreview()
        {
            if (data == null) return;
            core.ApplyPreview(previewTime);
            SceneView.RepaintAll();
        }

        // ---- Cubemap 设置 ----

        void DrawCubemapSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawSectionHeader("Cubemap 自动创建设置", CLR_INFO, "⚙");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("默认尺寸", GUILayout.Width(80));
            defaultCubemapSize = EditorGUILayout.IntPopup(defaultCubemapSize,
                new[] { "64", "128", "256", "512", "1024" },
                new[] { 64, 128, 256, 512, 1024 });
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("文件名前缀", GUILayout.Width(80));
            cubemapPrefix = EditorGUILayout.TextField(cubemapPrefix);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ---- 时间轴 ----

        void DrawTimeline()
        {
            if (data == null) return;

            DrawSectionHeader("时间轴 (双击空白添加节点 / 拖拽节点修改时间)", CLR_TITLE, "⏱");
            timelineRect = GUILayoutUtility.GetRect(0, TIMELINE_HEIGHT, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(timelineRect, new Color(0.18f, 0.18f, 0.18f));

            int ticks = 12;
            for (int i = 0; i <= ticks; i++)
            {
                float x = timelineRect.x + timelineRect.width * (i / (float)ticks);
                EditorGUI.DrawRect(new Rect(x, timelineRect.y, 1, timelineRect.height),
                    new Color(0.35f, 0.35f, 0.35f));
                GUI.Label(new Rect(x - 14, timelineRect.yMax - 14, 36, 14),
                    (data.totalDuration * i / ticks).ToString("F1"),
                    EditorStyles.miniLabel);
            }

            Event e = Event.current;
            var dupSet = core.GetDuplicateProbeNodeIndices();

            DrawBlendZonesOnTimeline(e);

            if (e.type == EventType.MouseDown &&
                selectedNodeIndex >= 0 && selectedNodeIndex < data.nodes.Count)
            {
                var selNode = data.nodes[selectedNodeIndex];
                float sx = timelineRect.x + timelineRect.width *
                    Mathf.Clamp01(selNode.time / data.totalDuration);
                Rect selRect = new Rect(sx - 8, timelineRect.y + 6, 16, TIMELINE_HEIGHT - 28);
                if (selRect.Contains(e.mousePosition))
                {
                    draggingNode = true;
                    draggingIndex = selectedNodeIndex;
                    GUI.changed = true;
                    e.Use();
                }
            }

            for (int i = 0; i < data.nodes.Count; i++)
            {
                if (i == selectedNodeIndex) continue;
                DrawTimelineNode(i, dupSet, false);
            }

            if (selectedNodeIndex >= 0 && selectedNodeIndex < data.nodes.Count)
            {
                DrawTimelineNode(selectedNodeIndex, dupSet, true);
            }

            if (e.type == EventType.MouseDown)
            {
                for (int i = 0; i < data.nodes.Count; i++)
                {
                    if (i == selectedNodeIndex) continue;

                    var node = data.nodes[i];
                    float nx = timelineRect.x + timelineRect.width *
                        Mathf.Clamp01(node.time / data.totalDuration);
                    Rect nodeRect = new Rect(nx - 8, timelineRect.y + 6, 16, TIMELINE_HEIGHT - 28);

                    if (nodeRect.Contains(e.mousePosition))
                    {
                        selectedNodeIndex = i;
                        draggingNode = true;
                        draggingIndex = i;
                        GUI.changed = true;
                        e.Use();
                        break;
                    }
                }
            }

            if (draggingNode && draggingIndex >= 0 && draggingIndex < data.nodes.Count)
            {
                if (e.type == EventType.MouseDrag)
                {
                    float ratio = Mathf.Clamp01((e.mousePosition.x - timelineRect.x) / timelineRect.width);
                    Undo.RecordObject(data, "Move Node Time");
                    data.nodes[draggingIndex].time = ratio * data.totalDuration;
                    e.Use();
                    GUI.changed = true;
                }

                if (e.type == EventType.MouseUp)
                {
                    draggingNode = false;
                    draggingIndex = -1;
                }
            }

            if (e.type == EventType.MouseDown && e.clickCount == 2 &&
                timelineRect.Contains(e.mousePosition))
            {
                bool onNode = false;
                for (int i = 0; i < data.nodes.Count; i++)
                {
                    float nx = timelineRect.x + timelineRect.width *
                        Mathf.Clamp01(data.nodes[i].time / data.totalDuration);
                    if (Mathf.Abs(e.mousePosition.x - nx) < 10)
                    {
                        onNode = true;
                        break;
                    }
                }

                if (!onNode)
                {
                    float ratio = Mathf.Clamp01((e.mousePosition.x - timelineRect.x) / timelineRect.width);
                    AddNodeAtTime(ratio * data.totalDuration);
                    e.Use();
                }
            }

            EditorGUILayout.BeginHorizontal();
            DrawLegendDot(new Color(1f, 0.85f, 0.2f), "选中");
            DrawLegendDot(new Color(0.4f, 1f, 0.6f), "已烘焙SH");
            DrawLegendDot(new Color(0.3f, 0.7f, 1f), "未烘焙");
            DrawLegendDot(CLR_ERROR, "Probe重复");
            DrawLegendDot(CLR_BLEND_EDGE, "混合区域");
            DrawLegendDot(CLR_PROBE_SWITCH, "Probe瞄点");
            EditorGUILayout.EndHorizontal();
        }

        // ============================================================
        // BlendZone 时间轴可视化
        // ============================================================
        void DrawBlendZonesOnTimeline(Event e)
        {
            if (data == null || data.nodes.Count < 2) return;

            for (int i = 0; i < data.nodes.Count; i++)
            {
                int fromIdx = i - 1;
                int toIdx = i;

                if (fromIdx < 0)
                {
                    if (data.loop && !data.holdAtEnd && data.nodes.Count >= 2)
                    {
                        fromIdx = data.nodes.Count - 1;
                        toIdx = 0;
                        DrawSingleBlendZone(e, fromIdx, toIdx, true);
                    }
                    continue;
                }

                DrawSingleBlendZone(e, fromIdx, toIdx, false);
            }
        }

        void DrawSingleBlendZone(Event e, int fromIdx, int toIdx, bool isWrap)
        {
            var fromNode = data.nodes[fromIdx];
            var toNode = data.nodes[toIdx];
            var bz = toNode.blendZone;
            if (bz == null || !bz.enabled) return;

            float fromX, toX;
            if (isWrap)
            {
                fromX = timelineRect.x + timelineRect.width * Mathf.Clamp01(fromNode.time / data.totalDuration);
                toX = timelineRect.x + timelineRect.width * Mathf.Clamp01(toNode.time / data.totalDuration);
                if (toX <= fromX) toX = timelineRect.x + timelineRect.width;
            }
            else
            {
                fromX = timelineRect.x + timelineRect.width * Mathf.Clamp01(fromNode.time / data.totalDuration);
                toX = timelineRect.x + timelineRect.width * Mathf.Clamp01(toNode.time / data.totalDuration);
            }

            if (toX <= fromX + 2f) return;

            float gap = toX - fromX;
            float s = Mathf.Clamp01(bz.start);
            float en = Mathf.Clamp01(bz.end);
            if (en <= s) en = Mathf.Min(1f, s + 0.001f);

            float blendStartX = fromX + gap * s;
            float blendEndX = fromX + gap * en;
            float blendWidth = blendEndX - blendStartX;

            Rect blendRect = new Rect(blendStartX, timelineRect.y + 4, blendWidth, TIMELINE_HEIGHT - 32);
            EditorGUI.DrawRect(blendRect, CLR_BLEND_ZONE);

            EditorGUI.DrawRect(new Rect(blendStartX, timelineRect.y + 4, 2, TIMELINE_HEIGHT - 32), CLR_BLEND_EDGE);
            EditorGUI.DrawRect(new Rect(blendEndX - 1, timelineRect.y + 4, 2, TIMELINE_HEIGHT - 32), CLR_BLEND_EDGE);

            DrawBlendCurveMini(blendRect, bz.shBlendCurve);

            float switchLocalT = Mathf.Clamp01(bz.probeSwitchPoint);
            float switchRawT = Mathf.Lerp(s, en, switchLocalT);
            float switchX = fromX + gap * switchRawT;

            float smoothW = Mathf.Clamp01(bz.probeSwitchSmoothWidth) * (en - s) * 0.5f;
            if (smoothW > 0.001f)
            {
                float smoothStartX = fromX + gap * (switchRawT - smoothW);
                float smoothEndX = fromX + gap * (switchRawT + smoothW);
                Rect smoothRect = new Rect(smoothStartX, timelineRect.y + 4, smoothEndX - smoothStartX, TIMELINE_HEIGHT - 32);
                EditorGUI.DrawRect(smoothRect, CLR_PROBE_SMOOTH);
            }

            Rect switchRect = new Rect(switchX - 1, timelineRect.y + 2, 3, TIMELINE_HEIGHT - 28);
            EditorGUI.DrawRect(switchRect, CLR_PROBE_SWITCH);

            Rect triRect = new Rect(switchX - 5, timelineRect.y + 2, 10, 6);
            EditorGUI.DrawRect(triRect, CLR_PROBE_SWITCH);

            GUI.Label(new Rect(blendStartX, timelineRect.y + TIMELINE_HEIGHT - 26, blendWidth, 12),
                "⟷", EditorStyles.miniLabel);

            // ===== 拖拽交互 =====
            bool isThisNodeSelected = (toIdx == selectedNodeIndex);

            Rect startHandle = new Rect(blendStartX - 4, timelineRect.y + 4, 8, TIMELINE_HEIGHT - 32);
            EditorGUIUtility.AddCursorRect(startHandle, MouseCursor.ResizeHorizontal);
            if (isThisNodeSelected && e.type == EventType.MouseDown && startHandle.Contains(e.mousePosition))
            {
                blendDragMode = BlendDragMode.Start;
                blendDragNodeIndex = toIdx;
                e.Use();
            }

            Rect endHandle = new Rect(blendEndX - 4, timelineRect.y + 4, 8, TIMELINE_HEIGHT - 32);
            EditorGUIUtility.AddCursorRect(endHandle, MouseCursor.ResizeHorizontal);
            if (isThisNodeSelected && e.type == EventType.MouseDown && endHandle.Contains(e.mousePosition))
            {
                blendDragMode = BlendDragMode.End;
                blendDragNodeIndex = toIdx;
                e.Use();
            }

            Rect switchHandle = new Rect(switchX - 6, timelineRect.y + 2, 12, TIMELINE_HEIGHT - 28);
            EditorGUIUtility.AddCursorRect(switchHandle, MouseCursor.MoveArrow);
            if (isThisNodeSelected && e.type == EventType.MouseDown && switchHandle.Contains(e.mousePosition))
            {
                blendDragMode = BlendDragMode.ProbeSwitch;
                blendDragNodeIndex = toIdx;
                e.Use();
            }

            if (blendDragMode != BlendDragMode.None && blendDragNodeIndex == toIdx)
            {
                if (e.type == EventType.MouseDrag)
                {
                    float rawT = Mathf.Clamp01((e.mousePosition.x - fromX) / gap);
                    Undo.RecordObject(data, "Edit BlendZone");

                    var dragBz = data.nodes[blendDragNodeIndex].blendZone;
                    switch (blendDragMode)
                    {
                        case BlendDragMode.Start:
                            dragBz.start = Mathf.Clamp01(rawT);
                            if (dragBz.start >= dragBz.end) dragBz.start = dragBz.end - 0.01f;
                            break;
                        case BlendDragMode.End:
                            dragBz.end = Mathf.Clamp01(rawT);
                            if (dragBz.end <= dragBz.start) dragBz.end = dragBz.start + 0.01f;
                            break;
                        case BlendDragMode.ProbeSwitch:
                            float localT = (rawT - dragBz.start) / (dragBz.end - dragBz.start);
                            dragBz.probeSwitchPoint = Mathf.Clamp01(localT);
                            break;
                    }
                    e.Use();
                    GUI.changed = true;
                    ApplyPreview();
                }

                if (e.type == EventType.MouseUp)
                {
                    blendDragMode = BlendDragMode.None;
                    blendDragNodeIndex = -1;
                }
            }
        }

        void DrawBlendCurveMini(Rect rect, BlendCurveType curve)
        {
            int steps = 24;
            Color curveColor = new Color(0.8f, 0.5f, 1f, 0.6f);
            for (int i = 0; i < steps; i++)
            {
                float t0 = i / (float)steps;
                float t1 = (i + 1) / (float)steps;
                float y0 = ApplyCurveMini(t0, curve);
                float y1 = ApplyCurveMini(t1, curve);

                float x0 = rect.x + rect.width * t0;
                float x1 = rect.x + rect.width * t1;
                float py0 = rect.y + rect.height * (1f - y0);
                float py1 = rect.y + rect.height * (1f - y1);

                EditorGUI.DrawRect(new Rect(x0, py0, x1 - x0 + 1, Mathf.Max(1, py1 - py0)), curveColor);
            }
        }

        static float ApplyCurveMini(float t, BlendCurveType curve)
        {
            switch (curve)
            {
                case BlendCurveType.Linear: return t;
                case BlendCurveType.SmoothStep: return Mathf.SmoothStep(0f, 1f, t);
                case BlendCurveType.EaseIn: return t * t;
                case BlendCurveType.EaseOut: return 1f - (1f - t) * (1f - t);
                case BlendCurveType.EaseInOut: return t * t * (3f - 2f * t);
                default: return t;
            }
        }

        // ============================================================
        // BlendZone Inspector 面板
        // ============================================================
        void DrawBlendZoneInspector(EnvTimeNode node)
        {
            if (node == null || node.blendZone == null) return;
            var bz = node.blendZone;

            int nodeIdx = data.nodes.IndexOf(node);
            bool hasPrevNode = nodeIdx > 0 || (data.loop && !data.holdAtEnd && data.nodes.Count >= 2);

            DrawSectionHeader("混合区域 (BlendZone)", CLR_BLEND_EDGE, "🔀");

            if (!hasPrevNode)
            {
                EditorGUILayout.HelpBox("此节点是第一个节点且无循环过渡，混合区域不生效。", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bz.enabled = EditorGUILayout.Toggle(
                new GUIContent("启用混合区域", "启用后，从前一个节点过渡到此节点时，只有在混合区域内才开始 SH/LightProbe 混合和 Probe 切换。\n" +
                 "关闭则 SH 使用全段线性混合，Probe 在目标节点位置切换。"),
                bz.enabled);

            if (!bz.enabled)
            {
                EditorGUILayout.HelpBox("混合区域已关闭：SH 全段线性混合，Probe 在目标节点位置切换。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("混合区域范围 (占两节点间距的百分比)", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            bz.start = EditorGUILayout.Slider("起始", bz.start, 0f, 1f);
            bz.end = EditorGUILayout.Slider("结束", bz.end, 0f, 1f);
            EditorGUILayout.EndHorizontal();

            if (bz.start >= bz.end)
            {
                EditorGUILayout.HelpBox("起始位置不能大于等于结束位置！已自动修正。", MessageType.Warning);
                if (bz.start >= bz.end) bz.end = Mathf.Min(1f, bz.start + 0.01f);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("居中 (0.3~0.7)", GUILayout.Width(120)))
            {
                bz.start = 0.3f; bz.end = 0.7f; bz.probeSwitchPoint = 0.5f;
            }
            if (GUILayout.Button("前移 (0.1~0.4)", GUILayout.Width(120)))
            {
                bz.start = 0.1f; bz.end = 0.4f; bz.probeSwitchPoint = 0.5f;
            }
            if (GUILayout.Button("后移 (0.6~0.9)", GUILayout.Width(120)))
            {
                bz.start = 0.6f; bz.end = 0.9f; bz.probeSwitchPoint = 0.5f;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            DrawSectionHeader("ReflectionProbe 切换瞄点", CLR_PROBE_SWITCH, "🎯");
            bz.probeSwitchPoint = EditorGUILayout.Slider(
                new GUIContent("切换位置", "在混合区域 [start, end] 内的归一化位置。\n" +
                 "0 = 混合区域起点切换，1 = 混合区域终点切换，0.5 = 中点切换。"),
                bz.probeSwitchPoint, 0f, 1f);

            bz.probeSwitchSmoothWidth = EditorGUILayout.Slider(
                new GUIContent("平滑宽度", "Probe 切换的平滑过渡半宽（在混合区域内的归一化值）。\n" +
                 "0 = 硬切换（瞬切），>0 = 在瞄点两侧此宽度范围内平滑过渡。\n" +
                 "⚠️ 平滑过渡期间两个 Probe 同时启用，需要 Shader 支持反射球融合。"),
                bz.probeSwitchSmoothWidth, 0f, 0.5f);

            if (bz.probeSwitchSmoothWidth > 0f)
            {
                EditorGUILayout.HelpBox(
                    "⚠️ 平滑宽度 > 0 时，切换期间两个 ReflectionProbe 同时启用。\n" +
                    "需要 Shader 支持反射球融合（Box Projection Blend），否则可能出现渲染异常。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            DrawSectionHeader("SH/LightProbe 混合曲线", CLR_OK, "📈");
            bz.shBlendCurve = (BlendCurveType)EditorGUILayout.EnumPopup(
                new GUIContent("曲线类型", "SH/LightProbe 在混合区域内的插值曲线类型"),
                bz.shBlendCurve);

            Rect curveRect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(curveRect, new Color(0.15f, 0.15f, 0.2f));
            DrawBlendCurveMini(curveRect, bz.shBlendCurve);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("曲线说明:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  Linear = 线性, SmoothStep = S形, EaseIn = 先慢后快, EaseOut = 先快后慢, EaseInOut = 两端慢中间快",
                EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        void DrawTimelineNode(int i, HashSet<int> dupSet, bool isSelected)
        {
            var node = data.nodes[i];
            float nx = timelineRect.x + timelineRect.width *
                Mathf.Clamp01(node.time / data.totalDuration);
            Rect nodeRect = new Rect(nx - 8, timelineRect.y + 6, 16, TIMELINE_HEIGHT - 28);

            bool isDup = dupSet.Contains(i);

            if (isSelected)
            {
                Rect glow = new Rect(nodeRect.x - 3, nodeRect.y - 3,
                    nodeRect.width + 6, nodeRect.height + 6);
                EditorGUI.DrawRect(glow, new Color(1f, 1f, 1f, 0.9f));
                Rect glow2 = new Rect(nodeRect.x - 2, nodeRect.y - 2,
                    nodeRect.width + 4, nodeRect.height + 4);
                EditorGUI.DrawRect(glow2, new Color(0f, 0f, 0f, 1f));
            }

            Color c;
            if (isDup) c = CLR_ERROR;
            else if (isSelected) c = new Color(1f, 0.85f, 0.2f);
            else if (node.customSH.IsValid) c = new Color(0.4f, 1f, 0.6f);
            else c = new Color(0.3f, 0.7f, 1f);
            EditorGUI.DrawRect(nodeRect, c);

            string label = isDup ? node.nodeName + "⚠" : node.nodeName;
            var lblStyle = new GUIStyle(EditorStyles.miniLabel);
            if (isSelected)
            {
                lblStyle.fontStyle = FontStyle.Bold;
                lblStyle.normal.textColor = new Color(1f, 0.95f, 0.4f);
            }

            GUI.Label(new Rect(nx - 35, timelineRect.y + TIMELINE_HEIGHT - 26, 70, 14),
                label, lblStyle);

            EditorGUIUtility.AddCursorRect(nodeRect, MouseCursor.SlideArrow);
        }

        static void DrawLegendDot(Color c, string label)
        {
            GUILayout.Space(4);
            Rect r = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(10));
            r.y += 4;
            EditorGUI.DrawRect(r, c);
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(65));
        }

        void DrawPreviewTimeline()
        {
            if (data == null) return;

            DrawSectionHeader("实时预览 (拖拽时间条预览效果)", CLR_INFO, "▶");

            Rect previewRect = GUILayoutUtility.GetRect(0, PREVIEW_HANDLE_HEIGHT, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.2f));

            int ticks = 12;
            for (int i = 0; i <= ticks; i++)
            {
                float x = previewRect.x + previewRect.width * (i / (float)ticks);
                EditorGUI.DrawRect(new Rect(x, previewRect.y, 1, previewRect.height),
                    new Color(0.3f, 0.3f, 0.35f));
            }

            float handleX = previewRect.x + previewRect.width * Mathf.Clamp01(previewTime / data.totalDuration);
            Rect handleRect = new Rect(handleX - 6, previewRect.y + 5, 12, previewRect.height - 10);
            EditorGUI.DrawRect(handleRect, new Color(1f, 0.5f, 0.2f));

            GUI.Label(new Rect(handleX - 25, previewRect.y + previewRect.height - 18, 50, 16),
                previewTime.ToString("F2"), EditorStyles.whiteMiniLabel);

            Event e = Event.current;
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.SlideArrow);

            if (e.type == EventType.MouseDown && handleRect.Contains(e.mousePosition))
            {
                draggingPreview = true;
                e.Use();
            }

            if (draggingPreview)
            {
                if (e.type == EventType.MouseDrag)
                {
                    float ratio = Mathf.Clamp01((e.mousePosition.x - previewRect.x) / previewRect.width);
                    previewTime = ratio * data.totalDuration;
                    ApplyPreview();
                    e.Use();
                    Repaint();
                }
                if (e.type == EventType.MouseUp)
                {
                    draggingPreview = false;
                }
            }

            if (e.type == EventType.MouseDown && previewRect.Contains(e.mousePosition) && !draggingPreview)
            {
                float ratio = Mathf.Clamp01((e.mousePosition.x - previewRect.x) / previewRect.width);
                previewTime = ratio * data.totalDuration;
                ApplyPreview();
                e.Use();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            previewTime = EditorGUILayout.Slider("预览时间", previewTime, 0f, data.totalDuration);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyPreview();
            }
            GUI.backgroundColor = CLR_INFO;
            if (GUILayout.Button("应用到场景", GUILayout.Width(100)))
            {
                ApplyPreview();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        void DrawNodeList()
        {
            if (data == null) return;

            DrawSectionHeader("节点列表", CLR_TITLE, "📋");

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = CLR_OK;
            if (GUILayout.Button("✚ 添加节点")) AddNodeAtTime(0f);
            GUI.backgroundColor = CLR_INFO;
            if (GUILayout.Button("⇅ 按时间排序")) { data.SortByTime(); EditorUtility.SetDirty(data); }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            var dupSet = core.GetDuplicateProbeNodeIndices();
            if (dupSet.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"⛔ 检测到 {dupSet.Count} 个节点使用了重复的主 ReflectionProbe，请修正后再烘焙！",
                    MessageType.Error);
            }

            int rmIdx = -1;
            for (int i = 0; i < data.nodes.Count; i++)
            {
                var node = data.nodes[i];
                bool isDup = dupSet.Contains(i);
                bool sel = (i == selectedNodeIndex);

                if (isDup) GUI.backgroundColor = CLR_BG_DUP;
                else if (sel) GUI.backgroundColor = new Color(1f, 0.95f, 0.55f);

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUI.backgroundColor = Color.white;

                if (GUILayout.Toggle(sel, "", GUILayout.Width(18)) != sel) selectedNodeIndex = i;

                var nameStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = isDup ? CLR_ERROR : (sel ? CLR_TITLE : Color.white) },
                    fontStyle = (sel || isDup) ? FontStyle.Bold : FontStyle.Normal
                };
                string dupMark = isDup ? "  ⚠" : "";
                EditorGUILayout.LabelField($"[{node.time:F2}] {node.nodeName}{dupMark}",
                    nameStyle, GUILayout.Width(200));

                GUI.color = node.customSH.IsValid ? CLR_OK : CLR_MUTED;
                EditorGUILayout.LabelField(node.customSH.IsValid ? "✓SH" : "·SH", GUILayout.Width(36));

                if (isDup) GUI.color = CLR_ERROR;
                else GUI.color = node.mainProbe ? CLR_PROBE : CLR_MUTED;
                EditorGUILayout.LabelField(node.mainProbe ? "✓Probe" : "·Probe", GUILayout.Width(50));

                GUI.color = Color.white;
                EditorGUILayout.LabelField("targets:" + node.affectedTargets.Count, GUILayout.Width(70));
                EditorGUILayout.LabelField("addProbes:" + node.additionalProbes.Count, GUILayout.Width(80));

                GUI.backgroundColor = CLR_ERROR;
                if (GUILayout.Button("✕", GUILayout.Width(22))) rmIdx = i;
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();
            }
            if (rmIdx >= 0)
            {
                if (EditorUtility.DisplayDialog("删除", "确定删除节点？", "确定", "取消"))
                {
                    Undo.RecordObject(data, "Remove Node");
                    data.nodes.RemoveAt(rmIdx);
                    if (selectedNodeIndex >= data.nodes.Count) selectedNodeIndex = -1;
                    EditorUtility.SetDirty(data);
                }
            }
        }

        void DrawSelectedNodeInspector()
        {
            if (data == null || selectedNodeIndex < 0 || selectedNodeIndex >= data.nodes.Count) return;

            // 切换节点时自动清除上一节点的代理预览
            if (_isSpecularPreviewActive && selectedNodeIndex != _specularPreviewNodeIndex)
                ClearSpecularPreview();

            var node = data.nodes[selectedNodeIndex];

            DrawSectionHeader($"节点详细 [{selectedNodeIndex}] {node.nodeName}", CLR_TITLE, "🔧");

            EditorGUI.BeginChangeCheck();

            node.nodeName = EditorGUILayout.TextField("名称", node.nodeName);
            node.time = EditorGUILayout.Slider("时间", node.time, 0f, data.totalDuration);

            EditorGUILayout.Space(4);
            DrawSectionHeader("ReflectionProbe", CLR_PROBE, "🔮");

            // 启用反射球开关
            EditorGUI.BeginChangeCheck();
            bool newEnableSphere = EditorGUILayout.ToggleLeft(
                new GUIContent("  启用反射球", "勾选时此节点会启用自身的反射球；取消后使用系统默认反射环境（烘焙 SH 不受影响）"),
                node.enableReflectionSphere);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(data, "Toggle Enable Reflection Sphere");
                node.enableReflectionSphere = newEnableSphere;
                EditorUtility.SetDirty(data);
            }
            EditorGUILayout.Space(2);

            EditorGUI.BeginChangeCheck();
            ReflectionProbe newProbe = (ReflectionProbe)EditorGUILayout.ObjectField(
                "主 Probe", node.mainProbe, typeof(ReflectionProbe), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (newProbe != null && core.IsProbeUsedByOtherNode(newProbe, selectedNodeIndex, out int usedBy))
                {
                    EditorUtility.DisplayDialog("⛔ 不允许重复",
                        $"该 ReflectionProbe 已被节点 [{usedBy}] {data.nodes[usedBy].nodeName} 使用。\n\n" +
                        "每个节点必须使用不同的主 ReflectionProbe。", "确定");
                }
                else
                {
                    Undo.RecordObject(data, "Change Main Probe");
                    node.mainProbe = newProbe;
                    EditorUtility.SetDirty(data);
                }
            }

            if (node.mainProbe != null &&
                core.IsProbeUsedByOtherNode(node.mainProbe, selectedNodeIndex, out int dupIdx))
            {
                EditorGUILayout.HelpBox(
                    $"⛔ 此 Probe 与节点 [{dupIdx}] {data.nodes[dupIdx].nodeName} 重复！\n" +
                    "每个节点必须使用不同的主 Probe。",
                    MessageType.Error);
            }

            if (node.mainProbe != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawSectionHeader("Probe 状态", CLR_INFO, "ℹ");

                EditorGUILayout.LabelField("  Mode", node.mainProbe.mode.ToString());
                Texture cube = node.mainProbe.texture;
                if (cube == null)
                {
                    cube = node.mainProbe.mode == ReflectionProbeMode.Custom
                        ? node.mainProbe.customBakedTexture
                        : node.mainProbe.bakedTexture;
                }

                var cubeStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = cube != null ? CLR_OK : CLR_ERROR },
                    fontStyle = FontStyle.Bold
                };
                EditorGUILayout.LabelField("  Cubemap",
                    cube != null ? "✓ " + cube.name : "✗ <未烘焙!>", cubeStyle);

                if (cube == null)
                {
                    if (node.mainProbe.mode == ReflectionProbeMode.Custom)
                        EditorGUILayout.HelpBox("此 Probe 处于 Custom 模式，尚未指定 Cubemap！", MessageType.Warning);
                    else
                        EditorGUILayout.HelpBox("此 Probe 尚未烘焙！", MessageType.Warning);
                }

                // 判断是否自带 cubemap
                bool selfContained = core.IsProbeSelfContained(node.mainProbe);
                if (selfContained)
                {
                    var scStyle = new GUIStyle(EditorStyles.label)
                    {
                        normal = { textColor = CLR_INFO },
                        fontStyle = FontStyle.Bold
                    };
                    EditorGUILayout.LabelField("  自带Cubemap", "✓ 不支持Bake", scStyle);
            EditorGUILayout.HelpBox(
                "此 Probe 自带 Cubemap（Custom 模式且 cubemap 名不含 Baked 前缀），\n"
                + "不支持 Bake 环境球。如需重新烘焙，请先清除 Custom Cubemap。\n"
                + "（cubemap 名含 Baked 前缀的 Probe 允许重新烘焙）",
                MessageType.Info);
                }
                EditorGUILayout.EndVertical();
            }
            else
            {
                EditorGUILayout.HelpBox("请指定主 ReflectionProbe（必填）", MessageType.Warning);
            }

            EditorGUILayout.LabelField("附加 Probe (到达此节点时一并启用)");
            int rm = -1;
            for (int i = 0; i < node.additionalProbes.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                node.additionalProbes[i] = (ReflectionProbe)EditorGUILayout.ObjectField(
                    node.additionalProbes[i], typeof(ReflectionProbe), true);
                GUI.backgroundColor = CLR_ERROR;
                if (GUILayout.Button("✕", GUILayout.Width(22))) rm = i;
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
            }
            if (rm >= 0) node.additionalProbes.RemoveAt(rm);

            Rect pdr = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
            GUI.Box(pdr, "← 拖拽 ReflectionProbe 添加到附加列表");
            HandleProbeDrop(pdr, node);

            EditorGUILayout.Space(4);
            DrawSectionHeader("烘焙参数", CLR_TITLE, "🔥");

            node.sampleResolution = EditorGUILayout.IntPopup("采样分辨率", node.sampleResolution,
                new[] { "32", "64", "128", "256" }, new[] { 32, 64, 128, 256 });
            EditorGUILayout.BeginHorizontal();
            node.rotationY = EditorGUILayout.Slider("Y旋转", node.rotationY, 0f, 360f);
            if (GUILayout.Button("0°", GUILayout.Width(30))) node.rotationY = 0f;
            if (GUILayout.Button("180°", GUILayout.Width(40))) node.rotationY = 180f;
            EditorGUILayout.EndHorizontal();
            node.useHDRClamp = EditorGUILayout.Toggle("HDR Clamp", node.useHDRClamp);
            if (node.useHDRClamp)
                node.hdrClampMax = EditorGUILayout.Slider("Clamp 上限", node.hdrClampMax, 0.1f, 100f);
            node.exposure = EditorGUILayout.Slider("曝光", node.exposure, 0.01f, 10f);

            EditorGUILayout.Space(4);
            DrawSectionHeader("半球映射", CLR_INFO, "🌐");
            node.enableHemisphereMirror = EditorGUILayout.Toggle(
                new GUIContent("启用半球映射", "烘焙后将空半球用实景半球镜像填充"),
                node.enableHemisphereMirror);
            if (node.enableHemisphereMirror)
            {
                EditorGUILayout.BeginHorizontal();
                node.hemisphereAngle = EditorGUILayout.Slider(
                    new GUIContent("与Z轴夹角", "0°=+Z, 90°=+X, 180°=-Z, 270°=-X"),
                    node.hemisphereAngle, 0f, 360f);
                if (GUILayout.Button("0°", GUILayout.Width(30))) node.hemisphereAngle = 0f;
                if (GUILayout.Button("90°", GUILayout.Width(36))) node.hemisphereAngle = 90f;
                if (GUILayout.Button("180°", GUILayout.Width(40))) node.hemisphereAngle = 180f;
                if (GUILayout.Button("270°", GUILayout.Width(40))) node.hemisphereAngle = 270f;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox(
                    "启用后，烘焙 Cubemap 完成时会自动将空半球（与角度相反方向）" +
                    "用实景半球镜像填充。\n适用于场景只有一半有实景的情况。",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4);
            DrawBlendZoneInspector(node);

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = CLR_OK;
            if (GUILayout.Button("▶ 烘焙此节点 SH", GUILayout.Height(28)))
            {
                core.BakeNodeSH(node);
            }

            // 自带 cubemap 的 Probe 不支持 Bake
            bool probeSelfContained = core.IsProbeSelfContained(node.mainProbe);
            GUI.backgroundColor = probeSelfContained ? CLR_MUTED : CLR_WARN;
            GUI.enabled = !probeSelfContained;
            if (GUILayout.Button(probeSelfContained ? "🔥 烘焙 Probe (不支持)" : "🔥 烘焙 Probe", GUILayout.Height(28)))
            {
                core.BakeReflectionProbe(node);
            }
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 半球映射独立处理按钮（已有烘焙结果时可单独处理）
            if (node.enableHemisphereMirror && node.mainProbe != null)
            {
                Cubemap existCube = node.GetMainCubemap();
                GUI.backgroundColor = (existCube != null) ? CLR_INFO : CLR_MUTED;
                GUI.enabled = (existCube != null);
                if (GUILayout.Button("🔄 单独处理环境球（半球映射）", GUILayout.Height(24)))
                {
                    core.ProcessNodeHemisphereMirror(node);
                }
                GUI.enabled = true;
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.Space(6);
            DrawSectionHeader($"ReflectionProbe 烘焙参与模型 ({node.reflectionProbeBakeTargets.Count})", CLR_INFO, "🔄");
            EditorGUILayout.HelpBox(
                "烘焙此节点时，将列表中的 GameObject 及其递归所有子物体临时勾选 ReflectionProbeStatic 以参与烘焙。\n烘焙结束后自动还原原始状态。",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = CLR_INFO;
            if (GUILayout.Button("使用当前选中物体填充"))
            {
                Undo.RecordObject(data, "Fill Bake Targets");
                node.reflectionProbeBakeTargets.Clear();
                foreach (var go in Selection.gameObjects)
                    if (go) node.reflectionProbeBakeTargets.Add(go);
            }
            if (GUILayout.Button("追加当前选中物体"))
            {
                Undo.RecordObject(data, "Append Bake Targets");
                foreach (var go in Selection.gameObjects)
                    if (go && !node.reflectionProbeBakeTargets.Contains(go)) node.reflectionProbeBakeTargets.Add(go);
            }
            // 从上一节点导入烘焙参与模型
            GUI.backgroundColor = CLR_WARN;
            GUI.enabled = selectedNodeIndex > 0;
            if (GUILayout.Button("从上一节点导入", GUILayout.Width(110)))
            {
                var prevNode = data.nodes[selectedNodeIndex - 1];
                Undo.RecordObject(data, "Import Bake Targets from Prev");
                node.reflectionProbeBakeTargets.Clear();
                foreach (var go in prevNode.reflectionProbeBakeTargets)
                    if (go) node.reflectionProbeBakeTargets.Add(go);
                EditorUtility.SetDirty(data);
            }
            GUI.enabled = true;
            GUI.backgroundColor = CLR_ERROR;
            if (GUILayout.Button("清空", GUILayout.Width(50)))
            {
                node.reflectionProbeBakeTargets.Clear();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            int rmBakeTarget = -1;
            for (int i = 0; i < node.reflectionProbeBakeTargets.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                node.reflectionProbeBakeTargets[i] = (GameObject)EditorGUILayout.ObjectField(
                    node.reflectionProbeBakeTargets[i], typeof(GameObject), true);
                GUI.backgroundColor = CLR_ERROR;
                if (GUILayout.Button("✕", GUILayout.Width(22))) rmBakeTarget = i;
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
            }
            if (rmBakeTarget >= 0) node.reflectionProbeBakeTargets.RemoveAt(rmBakeTarget);

            Rect bakeDropRect = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
            GUI.Box(bakeDropRect, "← 拖拽 GameObject 添加");
            HandleGODropToList(bakeDropRect, node.reflectionProbeBakeTargets);

            // ---- 镜面高光烘焙 ----
            EditorGUILayout.Space(6);
            DrawSectionHeader("镜面高光烘焙 (Specular Light Baking)", CLR_WARN, "💡");
            EditorGUILayout.HelpBox(
                "启用后，烘焙 ReflectionProbe 时会在 Baked 光源位置创建临时自发光代理物体（小球/面板），\n" +
                "使 Baked 光源在 Cubemap 中产生镜面高光。\n" +
                "Point/Spot 光源 → 自发光小球，Area 光源 → 自发光半透明面板。\n" ,
                MessageType.Info);

            node.enableSpecularLightBaking = EditorGUILayout.Toggle(
                new GUIContent("启用镜面高光烘焙",
                    "烘焙时在 Baked 光源位置放置自发光代理物体"),
                node.enableSpecularLightBaking);

            if (node.enableSpecularLightBaking)
            {
                EditorGUI.indentLevel++;

                node.specularLightCollectMode = (EnvTimeNode.SpecularLightCollectMode)EditorGUILayout.EnumPopup(
                    new GUIContent("光源收集模式",
                        "AutoCollectBaked = 自动收集 Baked 光源\n" +
                        "AutoCollectAll = 自动收集所有光源（含 Mixed）\n" +
                        "ManualList = 仅使用下方手动指定的光源"),
                    node.specularLightCollectMode);

                node.specularSphereRadius = EditorGUILayout.Slider(
                    new GUIContent("代理球半径",
                        "Point/Spot 光源的自发光代理球体半径（世界单位）"),
                    node.specularSphereRadius, 0.001f, 1f);

                node.specularIntensityMultiplier = EditorGUILayout.Slider(
                    new GUIContent("强度倍率",
                        "自发光强度倍率（1 = 与光源颜色一致，>1 = 更亮的高光）"),
                    node.specularIntensityMultiplier, 0.1f, 10f);

                node.specularAreaPanelScale = EditorGUILayout.Slider(
                    new GUIContent("面光源面板倍率",
                        "Area 光源自发光面板的尺寸倍率（1 = 与光源实际尺寸一致）"),
                    node.specularAreaPanelScale, 0.01f, 5f);

                // 手动光源列表（仅 ManualList 模式显示）
                if (node.specularLightCollectMode == EnvTimeNode.SpecularLightCollectMode.ManualList)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("手动指定光源列表:", EditorStyles.miniLabel);

                    EditorGUILayout.BeginHorizontal();
                    GUI.backgroundColor = CLR_INFO;
                    if (GUILayout.Button("追加当前选中物体"))
                    {
                        Undo.RecordObject(data, "Append Specular Lights");
                        foreach (var go in Selection.gameObjects)
                        {
                            if (go && go.TryGetComponent<Light>(out var l)
                                && !node.specularLightTargets.Contains(l))
                                node.specularLightTargets.Add(l);
                        }
                    }
                    GUI.backgroundColor = CLR_ERROR;
                    if (GUILayout.Button("清空", GUILayout.Width(50)))
                    {
                        node.specularLightTargets.Clear();
                    }
                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();

                    int rmLight = -1;
                    for (int i = 0; i < node.specularLightTargets.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        node.specularLightTargets[i] = (Light)EditorGUILayout.ObjectField(
                            node.specularLightTargets[i], typeof(Light), true);
                        GUI.backgroundColor = CLR_ERROR;
                        if (GUILayout.Button("✕", GUILayout.Width(22))) rmLight = i;
                        GUI.backgroundColor = Color.white;
                        EditorGUILayout.EndHorizontal();
                    }
                    if (rmLight >= 0) node.specularLightTargets.RemoveAt(rmLight);
                }

                // 预览代理物体
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                if (_isSpecularPreviewActive)
                {
                    GUI.backgroundColor = CLR_WARN;
                    if (GUILayout.Button("● 关闭代理预览"))
                        ClearSpecularPreview();
                }
                else
                {
                    GUI.backgroundColor = CLR_OK;
                    if (GUILayout.Button("👁 预览代理物体"))
                        CreateSpecularPreview(node, selectedNodeIndex);
                }
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            DrawSectionHeader($"影响的模型 ({node.affectedTargets.Count})", CLR_INFO, "🎯");
            node.includeChildren = EditorGUILayout.Toggle("包含子物体", node.includeChildren);

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = CLR_INFO;
            if (GUILayout.Button("使用当前选中物体填充"))
            {
                Undo.RecordObject(data, "Fill Targets");
                node.affectedTargets.Clear();
                foreach (var go in Selection.gameObjects)
                    if (go) node.affectedTargets.Add(go);
            }
            if (GUILayout.Button("追加当前选中物体"))
            {
                Undo.RecordObject(data, "Append Targets");
                foreach (var go in Selection.gameObjects)
                    if (go && !node.affectedTargets.Contains(go)) node.affectedTargets.Add(go);
            }
            // 从上一节点导入影响的模型
            GUI.backgroundColor = CLR_WARN;
            GUI.enabled = selectedNodeIndex > 0;
            if (GUILayout.Button("从上一节点导入", GUILayout.Width(110)))
            {
                var prevNode = data.nodes[selectedNodeIndex - 1];
                Undo.RecordObject(data, "Import Targets from Prev");
                node.affectedTargets.Clear();
                foreach (var go in prevNode.affectedTargets)
                    if (go) node.affectedTargets.Add(go);
                EditorUtility.SetDirty(data);
            }
            GUI.enabled = true;
            GUI.backgroundColor = CLR_ERROR;
            if (GUILayout.Button("清空", GUILayout.Width(50)))
            {
                node.affectedTargets.Clear();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            int rmTarget = -1;
            for (int i = 0; i < node.affectedTargets.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                node.affectedTargets[i] = (GameObject)EditorGUILayout.ObjectField(
                    node.affectedTargets[i], typeof(GameObject), true);
                GUI.backgroundColor = CLR_ERROR;
                if (GUILayout.Button("✕", GUILayout.Width(22))) rmTarget = i;
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
            }
            if (rmTarget >= 0) node.affectedTargets.RemoveAt(rmTarget);

            Rect dr = GUILayoutUtility.GetRect(0, 30, GUILayout.ExpandWidth(true));
            GUI.Box(dr, "← 拖拽 GameObject 添加目标");
            HandleGODrop(dr, node);

            if (node.customSH.IsValid)
            {
                EditorGUILayout.Space(4);
                DrawSectionHeader("Custom SH 数据", CLR_OK, "✨");
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("SHAr", EnvTimelineSimpleProvider.FormatV4(node.customSH.SHAr));
                EditorGUILayout.LabelField("SHAg", EnvTimelineSimpleProvider.FormatV4(node.customSH.SHAg));
                EditorGUILayout.LabelField("SHAb", EnvTimelineSimpleProvider.FormatV4(node.customSH.SHAb));
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(data);
            }
        }

        // ============================================================
        // 底部固定操作栏
        // ============================================================
        void DrawBottomActions()
        {
            if (data == null) return;

            EditorGUILayout.Space(4);

            var dupSet = core.GetDuplicateProbeNodeIndices();
            bool hasDup = dupSet.Count > 0;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (hasDup)
            {
                var warnStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = CLR_ERROR }
                };
                EditorGUILayout.LabelField($"⛔ 当前存在 {dupSet.Count} 个 Probe 重复，烘焙已禁用", warnStyle);
            }

            GUI.backgroundColor = hasDup ? CLR_MUTED : CLR_OK;
            if (GUILayout.Button("▶ 一键烘焙所有节点 SH", GUILayout.Height(34)))
            {
                core.BakeAllNodes();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndVertical();
        }

        // ============================================================
        // 拖拽处理
        // ============================================================
        void HandleProbeDrop(Rect r, EnvTimeNode node)
        {
            var e = Event.current;
            if (!r.Contains(e.mousePosition)) return;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        ReflectionProbe rp = null;
                        if (o is ReflectionProbe rp1) rp = rp1;
                        else if (o is GameObject g) rp = g.GetComponent<ReflectionProbe>();
                        if (rp && !node.additionalProbes.Contains(rp) && rp != node.mainProbe)
                            node.additionalProbes.Add(rp);
                    }
                    e.Use();
                }
            }
        }

        void HandleGODrop(Rect r, EnvTimeNode node)
        {
            var e = Event.current;
            if (!r.Contains(e.mousePosition)) return;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                        if (o is GameObject go && !node.affectedTargets.Contains(go))
                            node.affectedTargets.Add(go);
                    e.Use();
                }
            }
        }

        void HandleGODropToList(Rect r, List<GameObject> list)
        {
            var e = Event.current;
            if (!r.Contains(e.mousePosition)) return;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                        if (o is GameObject go && !list.Contains(go))
                            list.Add(go);
                    e.Use();
                }
            }
        }
    }
}
