// EnvironmentTimelineEditorWindow.cs（MonoBehaviour 适配版 - 全局可滚动 + 视觉强化版）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Hotfix.Core.EnvTimelineSimple;

namespace BYTools.EnvTimelineSimple
{
    // ================================================================
    // Cubemap → SH L2 (9 系数) CPU 端积分器
    // 完全独立的工具类，可被任何编辑器工具调用
    // ================================================================
    public static class CubemapSHProjector
    {
        static readonly Vector3[] FaceUAxis =
        {
            new Vector3( 0, 0,-1), new Vector3( 0, 0, 1),
            new Vector3( 1, 0, 0), new Vector3( 1, 0, 0),
            new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
        };

        static readonly Vector3[] FaceVAxis =
        {
            new Vector3(0,-1, 0), new Vector3(0,-1, 0),
            new Vector3(0, 0, 1), new Vector3(0, 0,-1),
            new Vector3(0,-1, 0), new Vector3(0,-1, 0),
        };

        static readonly Vector3[] FaceNormal =
        {
            new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
            new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
        };

        /// <summary>
        /// 根据 Cubemap 面索引和 UV 坐标 [-1,1] 计算方向向量（已归一化）
        /// </summary>
        public static Vector3 GetCubemapDirection(int face, float u, float v)
        {
            Vector3 dir = FaceNormal[face] + FaceUAxis[face] * u + FaceVAxis[face] * v;
            dir.Normalize();
            return dir;
        }

        static Vector3 RotateAroundY(Vector3 dir, float angleDeg)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);
            return new Vector3(
                dir.x * cosA + dir.z * sinA,
                dir.y,
                -dir.x * sinA + dir.z * cosA
            );
        }

        /// <summary>
        /// 将 Cubemap 投影到 SH L2（9 个基函数）
        /// 返回 float[9,3]：[i,0]=R [i,1]=G [i,2]=B
        /// 计算结果在 **线性空间**
        /// </summary>
        public static float[,] ProjectCubemapToSH(Cubemap cube, int maxResolution = 64,
            float rotationY = 0f, float hdrClampMax = 0f)
        {
            if (cube == null) return null;

            Cubemap readableCube = MakeReadableCopy(cube, maxResolution);
            if (readableCube == null) return null;

            int size = readableCube.width;
            float[,] coeffs = new float[9, 3];
            double totalWeight = 0;

            bool needGammaToLinear = NeedGammaToLinearConversion(cube);
            bool doClamp = (hdrClampMax > 0f);

            for (int face = 0; face < 6; face++)
            {
                Color[] pixels = readableCube.GetPixels((CubemapFace)face);

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float v = (y + 0.5f) / size * 2f - 1f;

                        Vector3 dir = FaceNormal[face] + FaceUAxis[face] * u + FaceVAxis[face] * v;
                        dir.Normalize();

                        if (Mathf.Abs(rotationY) > 0.001f)
                            dir = RotateAroundY(dir, rotationY);

                        float tmp = 1f + u * u + v * v;
                        float weight = 4f / (Mathf.Sqrt(tmp) * tmp * size * size);

                        Color pixel = pixels[y * size + x];

                        if (needGammaToLinear)
                        {
                            pixel.r = GammaToLinear(pixel.r);
                            pixel.g = GammaToLinear(pixel.g);
                            pixel.b = GammaToLinear(pixel.b);
                        }

                        float r = pixel.r;
                        float g = pixel.g;
                        float b = pixel.b;

                        if (doClamp)
                        {
                            r = Mathf.Min(r, hdrClampMax);
                            g = Mathf.Min(g, hdrClampMax);
                            b = Mathf.Min(b, hdrClampMax);
                        }

                        float[] basis = EvalSHBasis9(dir);
                        for (int i = 0; i < 9; i++)
                        {
                            float bw = basis[i] * weight;
                            coeffs[i, 0] += r * bw;
                            coeffs[i, 1] += g * bw;
                            coeffs[i, 2] += b * bw;
                        }

                        totalWeight += weight;
                    }
                }
            }

            double normFactor = 4.0 * Math.PI / totalWeight;
            for (int i = 0; i < 9; i++)
            {
                coeffs[i, 0] *= (float)normFactor;
                coeffs[i, 1] *= (float)normFactor;
                coeffs[i, 2] *= (float)normFactor;
            }

            if (readableCube != cube)
                UnityEngine.Object.DestroyImmediate(readableCube);

            return coeffs;
        }

        /// <summary>
        /// 将 SH L2 系数转换为 Unity 风格的 7 个 Vector4
        /// 与 unity_SHAr 等完全对应
        /// </summary>
        public static void ConvertToUnityFormat(float[,] coeffs,
            out Vector4 SHAr, out Vector4 SHAg, out Vector4 SHAb,
            out Vector4 SHBr, out Vector4 SHBg, out Vector4 SHBb,
            out Vector4 SHC)
        {
            SHAr = new Vector4(coeffs[3, 0], coeffs[1, 0], coeffs[2, 0], coeffs[0, 0]);
            SHAg = new Vector4(coeffs[3, 1], coeffs[1, 1], coeffs[2, 1], coeffs[0, 1]);
            SHAb = new Vector4(coeffs[3, 2], coeffs[1, 2], coeffs[2, 2], coeffs[0, 2]);

            SHBr = new Vector4(coeffs[4, 0], coeffs[5, 0], coeffs[6, 0], coeffs[7, 0]);
            SHBg = new Vector4(coeffs[4, 1], coeffs[5, 1], coeffs[6, 1], coeffs[7, 1]);
            SHBb = new Vector4(coeffs[4, 2], coeffs[5, 2], coeffs[6, 2], coeffs[7, 2]);

            SHC = new Vector4(coeffs[8, 0], coeffs[8, 1], coeffs[8, 2], 1.0f);
        }

        /// <summary>
        /// 直接从 SphericalHarmonicsL2 转换为 Unity 风格 7 Vector4
        /// </summary>
        public static void ConvertFromSHL2(SphericalHarmonicsL2 sh,
            out Vector4 SHAr, out Vector4 SHAg, out Vector4 SHAb,
            out Vector4 SHBr, out Vector4 SHBg, out Vector4 SHBb,
            out Vector4 SHC)
        {
            SHAr = new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0]);
            SHAg = new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0]);
            SHAb = new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0]);
            SHBr = new Vector4(sh[0, 4], sh[0, 5], sh[0, 6], sh[0, 7]);
            SHBg = new Vector4(sh[1, 4], sh[1, 5], sh[1, 6], sh[1, 7]);
            SHBb = new Vector4(sh[2, 4], sh[2, 5], sh[2, 6], sh[2, 7]);
            SHC  = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1.0f);
        }

        static float[] EvalSHBasis9(Vector3 dir)
        {
            float x = dir.x, y = dir.y, z = dir.z;
            float[] b = new float[9];
            b[0] = 0.2820947917f;
            b[1] = 0.4886025119f * y;
            b[2] = 0.4886025119f * z;
            b[3] = 0.4886025119f * x;
            b[4] = 1.0925484306f * x * y;
            b[5] = 1.0925484306f * y * z;
            b[6] = 0.3153915652f * (3f * z * z - 1f);
            b[7] = 1.0925484306f * x * z;
            b[8] = 0.5462742153f * (x * x - y * y);
            return b;
        }

        static bool NeedGammaToLinearConversion(Cubemap cube)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
            {
                string path = AssetDatabase.GetAssetPath(cube);
                if (!string.IsNullOrEmpty(path))
                {
                    TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null && importer.sRGBTexture)
                        return true;
                }
                if (cube.format == TextureFormat.RGBA32 || cube.format == TextureFormat.RGB24 ||
                    cube.format == TextureFormat.ARGB32 || cube.format == TextureFormat.DXT1 ||
                    cube.format == TextureFormat.DXT5 || cube.format == TextureFormat.BC7)
                    return true;
            }
            return false;
        }

        static float GammaToLinear(float v)
        {
            if (v <= 0.04045f) return v / 12.92f;
            return Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        static Cubemap MakeReadableCopy(Cubemap source, int maxRes)
        {
            int size = Mathf.Min(source.width, maxRes);

            try
            {
                Color[] test = source.GetPixels(CubemapFace.PositiveX);
                if (test != null && test.Length > 0)
                {
                    if (source.width <= maxRes) return source;
                    return DownsampleCubemap(source, size);
                }
            }
            catch { }

            // 不可读 → 通过 RenderTexture 拷贝
            return CopyViaRenderTexture(source, size);
        }

        static Cubemap CopyViaRenderTexture(Cubemap source, int size)
        {
            RenderTexture rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBFloat);
            rt.dimension = TextureDimension.Cube;
            rt.useMipMap = false;
            rt.Create();

            Cubemap result = new Cubemap(size, TextureFormat.RGBAFloat, false);

            // 利用 Graphics.CopyTexture 进行 GPU 拷贝（如果支持）
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0
                && (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0)
            {
                try
                {
                    int srcSize = source.width;
                    if (srcSize == size)
                    {
                        for (int face = 0; face < 6; face++)
                            Graphics.CopyTexture(source, face, 0, rt, face, 0);
                    }
                    else
                    {
                        // 不同尺寸需要 Blit 缩放，先用 Blit 写入 RT
                        for (int face = 0; face < 6; face++)
                        {
                            Graphics.SetRenderTarget(rt, 0, (CubemapFace)face);
                            GL.Clear(true, true, Color.black);
                        }
                    }
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(rt);
                    Debug.LogError("[CubemapSHProjector] 无法读取 Cubemap，请开启 Read/Write");
                    return null;
                }
            }

            // 从 RT 读回 CPU
            for (int face = 0; face < 6; face++)
            {
                RenderTexture.active = rt;
                Graphics.SetRenderTarget(rt, 0, (CubemapFace)face);

                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBAFloat, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                result.SetPixels(tex.GetPixels(), (CubemapFace)face);
                UnityEngine.Object.DestroyImmediate(tex);
            }

            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            result.Apply();
            return result;
        }

        static Cubemap DownsampleCubemap(Cubemap source, int targetSize)
        {
            Cubemap result = new Cubemap(targetSize, TextureFormat.RGBAFloat, false);
            int srcSize = source.width;
            float scale = (float)srcSize / targetSize;

            for (int face = 0; face < 6; face++)
            {
                Color[] srcPixels = source.GetPixels((CubemapFace)face);
                Color[] dstPixels = new Color[targetSize * targetSize];
                for (int y = 0; y < targetSize; y++)
                    for (int x = 0; x < targetSize; x++)
                    {
                        int sx = Mathf.Clamp(Mathf.FloorToInt(x * scale), 0, srcSize - 1);
                        int sy = Mathf.Clamp(Mathf.FloorToInt(y * scale), 0, srcSize - 1);
                        dstPixels[y * targetSize + x] = srcPixels[sy * srcSize + sx];
                    }
                result.SetPixels(dstPixels, (CubemapFace)face);
            }
            result.Apply();
            return result;
        }

        public static Color EvaluateSHAtDirection(float[,] coeffs, Vector3 dir)
        {
            float[] basis = EvalSHBasis9(dir);
            float r = 0, g = 0, b = 0;
            for (int i = 0; i < 9; i++)
            {
                r += coeffs[i, 0] * basis[i];
                g += coeffs[i, 1] * basis[i];
                b += coeffs[i, 2] * basis[i];
            }
            return new Color(Mathf.Max(0, r), Mathf.Max(0, g), Mathf.Max(0, b), 1);
        }

        public static Color EvaluateSHAtDirection(SphericalHarmonicsL2 sh, Vector3 dir)
        {
            Vector3[] dirs = new[] { dir };
            Color[] colors = new Color[1];
            sh.Evaluate(dirs, colors);
            return colors[0];
        }
    }
    public class EnvTimelineSimpleEditorWindow : EditorWindow
    {
        /// <summary>
        /// 临时勾选 ReflectionProbeStatic 的作用域。
        /// Create 时记录原始 StaticEditorFlags 并对未勾选的 GO 及其递归子物体设置 ReflectionProbeStatic，
        /// Dispose 时还原全部原始状态。配合 using 语句确保异常 / 取消时也能还原。
        /// </summary>
        struct ReflectionProbeStaticScope : IDisposable
        {
            Dictionary<GameObject, StaticEditorFlags> _originalFlags;

            public static ReflectionProbeStaticScope Create(List<GameObject> targets)
            {
                var scope = new ReflectionProbeStaticScope();
                if (targets == null || targets.Count == 0) return scope;

                scope._originalFlags = new Dictionary<GameObject, StaticEditorFlags>();
                var visited = new HashSet<GameObject>();
                foreach (var go in targets)
                {
                    if (go == null) continue;
                    CollectAndSetStatic(go, scope._originalFlags, visited);
                }
                return scope;
            }

            static void CollectAndSetStatic(GameObject go,
                Dictionary<GameObject, StaticEditorFlags> dict,
                HashSet<GameObject> visited)
            {
                if (go == null || visited.Contains(go)) return;
                visited.Add(go);

                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                dict[go] = flags;

                if ((flags & StaticEditorFlags.ReflectionProbeStatic) == 0)
                    GameObjectUtility.SetStaticEditorFlags(
                        go, flags | StaticEditorFlags.ReflectionProbeStatic);

                foreach (Transform child in go.transform)
                    CollectAndSetStatic(child.gameObject, dict, visited);
            }

            public void Dispose()
            {
                if (_originalFlags == null) return;
                foreach (var kv in _originalFlags)
                {
                    if (kv.Key != null)
                        GameObjectUtility.SetStaticEditorFlags(kv.Key, kv.Value);
                }
                _originalFlags.Clear();
            }
        }

        EnvironmentTimelineData data;
        Vector2 mainScroll;
        int selectedNodeIndex = -1;

        const float TIMELINE_HEIGHT = 70f;
        Rect timelineRect;
        bool draggingNode = false;
        int draggingIndex = -1;

        float previewTime = 0f;
        bool draggingPreview = false;
        const float PREVIEW_HANDLE_HEIGHT = 50f;

        [SerializeField] private int defaultCubemapSize = 128;
        [SerializeField] private string cubemapPrefix = "Baked";

        static readonly Color CLR_TITLE       = new Color(1f, 0.85f, 0.3f);
        static readonly Color CLR_OK          = new Color(0.4f, 1f, 0.5f);
        static readonly Color CLR_WARN        = new Color(1f, 0.6f, 0.2f);
        static readonly Color CLR_ERROR       = new Color(1f, 0.35f, 0.35f);
        static readonly Color CLR_INFO        = new Color(0.5f, 0.85f, 1f);
        static readonly Color CLR_PROBE       = new Color(0.4f, 0.9f, 1f);
        static readonly Color CLR_MUTED       = new Color(0.55f, 0.55f, 0.55f);
        static readonly Color CLR_BG_PANEL    = new Color(0.22f, 0.22f, 0.26f);
        static readonly Color CLR_BG_DUP      = new Color(0.6f, 0.15f, 0.15f);

        [MenuItem("Tools/BYTools/Environment Timeline Simple 编辑器", false, 110)]
        public static void Open()
        {
            var win = GetWindow<EnvTimelineSimpleEditorWindow>("Env Timeline");
            win.minSize = new Vector2(580, 500);
        }

        void OnGUI()
        {
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

            float bottomReserved = 130f;
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

        void CreateNewTimelineInScene()
        {
            GameObject go = new GameObject("EnvironmentTimeline");
            data = go.AddComponent<EnvironmentTimelineData>();
            var controller = go.AddComponent<EnvironmentTimelineController>();

            Undo.RegisterCreatedObjectUndo(go, "Create Timeline");
            Selection.activeGameObject = go;

            Debug.Log($"[EnvTimeline] 已创建新的 Timeline 物体: {go.name}");
        }

        bool IsProbeUsedByOtherNode(ReflectionProbe probe, int excludeIndex, out int usedByIndex)
        {
            usedByIndex = -1;
            if (probe == null || data == null) return false;

            for (int i = 0; i < data.nodes.Count; i++)
            {
                if (i == excludeIndex) continue;
                if (data.nodes[i].mainProbe == probe)
                {
                    usedByIndex = i;
                    return true;
                }
            }
            return false;
        }

        HashSet<int> GetDuplicateProbeNodeIndices()
        {
            var set = new HashSet<int>();
            if (data == null) return set;

            var map = new Dictionary<ReflectionProbe, int>();
            for (int i = 0; i < data.nodes.Count; i++)
            {
                var p = data.nodes[i].mainProbe;
                if (p == null) continue;
                if (map.TryGetValue(p, out int first))
                {
                    set.Add(first);
                    set.Add(i);
                }
                else
                {
                    map[p] = i;
                }
            }
            return set;
        }

        /// <summary>
        /// 判断 Probe 是否自带 Cubemap（Custom 模式且 cubemap 不为 null 且 cubemap 名不含 Baked 前缀）。
        /// 自带 Cubemap 的 Probe 不支持 Bake 环境球。
        /// 工具烘焙的 cubemap（名含 Baked 前缀）允许重新烘焙。
        /// </summary>
        bool IsProbeSelfContained(ReflectionProbe probe)
        {
            if (probe == null) return false;

            // 仅 Custom 模式 + cubemap 不为 null 时检查
            if (probe.mode != ReflectionProbeMode.Custom || probe.customBakedTexture == null)
                return false;

            // cubemap 名含 Baked 前缀 → 工具烘焙的，允许重新烘焙
            if (!string.IsNullOrEmpty(cubemapPrefix)
                && probe.customBakedTexture.name.StartsWith(cubemapPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            // Custom 模式 + cubemap 不为 null + 名不含 Baked 前缀 → 用户自带的，不支持 Bake
            return true;
        }

        bool ValidateNoDuplicateProbes()
        {
            var dupSet = GetDuplicateProbeNodeIndices();
            if (dupSet.Count > 0)
            {
                var names = new List<string>();
                foreach (var idx in dupSet)
                    names.Add($"  • [{idx}] {data.nodes[idx].nodeName}  →  {data.nodes[idx].mainProbe.name}");

                EditorUtility.DisplayDialog("⛔ 无法烘焙：检测到重复 Probe",
                    "以下节点使用了重复的主 ReflectionProbe，请先修正：\n\n" +
                    string.Join("\n", names) +
                    "\n\n每个节点必须使用不同的主 ReflectionProbe。",
                    "确定");
                return false;
            }
            return true;
        }

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
            var dupSet = GetDuplicateProbeNodeIndices();

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
            EditorGUILayout.EndHorizontal();
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

        void ApplyPreview()
        {
            if (data == null) return;

            var ctrl = data.GetComponent<EnvironmentTimelineController>();
            if (ctrl != null)
            {
                ctrl.currentTime = previewTime;
                ctrl.ApplyAtCurrentTime();
                SceneView.RepaintAll();
            }
            else
            {
                Debug.LogWarning($"物体 '{data.gameObject.name}' 上未找到 EnvironmentTimelineController 组件");
            }
        }

        void AddNodeAtTime(float time)
        {
            if (data == null) return;

            Undo.RecordObject(data, "Add Time Node");
            var node = new EnvTimeNode
            {
                nodeName = "Node_" + data.nodes.Count,
                time = time
            };
            data.nodes.Add(node);
            data.SortByTime();
            selectedNodeIndex = data.nodes.IndexOf(node);
            EditorUtility.SetDirty(data);
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

            var dupSet = GetDuplicateProbeNodeIndices();
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
            var node = data.nodes[selectedNodeIndex];

            DrawSectionHeader($"节点详细 [{selectedNodeIndex}] {node.nodeName}", CLR_TITLE, "🔧");

            EditorGUI.BeginChangeCheck();

            node.nodeName = EditorGUILayout.TextField("名称", node.nodeName);
            node.time = EditorGUILayout.Slider("时间", node.time, 0f, data.totalDuration);

            EditorGUILayout.Space(4);
            DrawSectionHeader("ReflectionProbe", CLR_PROBE, "🔮");

            EditorGUI.BeginChangeCheck();
            ReflectionProbe newProbe = (ReflectionProbe)EditorGUILayout.ObjectField(
                "主 Probe", node.mainProbe, typeof(ReflectionProbe), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (newProbe != null && IsProbeUsedByOtherNode(newProbe, selectedNodeIndex, out int usedBy))
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
                IsProbeUsedByOtherNode(node.mainProbe, selectedNodeIndex, out int dupIdx))
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
                bool selfContained = IsProbeSelfContained(node.mainProbe);
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

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = CLR_OK;
            if (GUILayout.Button("▶ 烘焙此节点 SH", GUILayout.Height(28)))
            {
                BakeNodeSH(node);
            }

            // 自带 cubemap 的 Probe 不支持 Bake
            bool probeSelfContained = IsProbeSelfContained(node.mainProbe);
            GUI.backgroundColor = probeSelfContained ? CLR_MUTED : CLR_WARN;
            GUI.enabled = !probeSelfContained;
            if (GUILayout.Button(probeSelfContained ? "🔥 烘焙 Probe (不支持)" : "🔥 烘焙 Probe", GUILayout.Height(28)))
            {
                BakeReflectionProbe(node);
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
                    ProcessNodeHemisphereMirror(node);
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
                EditorGUILayout.LabelField("SHAr", FormatV4(node.customSH.SHAr));
                EditorGUILayout.LabelField("SHAg", FormatV4(node.customSH.SHAg));
                EditorGUILayout.LabelField("SHAb", FormatV4(node.customSH.SHAb));
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(data);
            }
        }

        // ============================================================
        // 🎨 底部固定操作栏（已移除"烘焙所有 Probe"按钮）
        // ============================================================
        void DrawBottomActions()
        {
            if (data == null) return;

            EditorGUILayout.Space(4);

            var dupSet = GetDuplicateProbeNodeIndices();
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
                BakeAllNodes();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndVertical();
        }

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

        // ============================================================
        // 🆕 让用户选择保存目录（统一工具方法）
        // ============================================================
        const string PREF_LAST_BAKE_FOLDER = "BYTools_EnvTimeline_LastBakeFolder";

        bool TryPickAssetsFolder(string title, out string assetRelativeFolder)
        {
            assetRelativeFolder = null;

            // 读取上次使用的烘焙目录，优先打开该位置以减少重复操作
            string lastRelFolder = EditorPrefs.GetString(PREF_LAST_BAKE_FOLDER, "Assets");
            string startDir = lastRelFolder;
            if (lastRelFolder.StartsWith("Assets"))
                startDir = Application.dataPath + lastRelFolder.Substring("Assets".Length);
            if (!Directory.Exists(startDir))
                startDir = Application.dataPath;

            string abs = EditorUtility.OpenFolderPanel(title, startDir, "");
            if (string.IsNullOrEmpty(abs))
                return false;

            if (!abs.StartsWith(Application.dataPath))
            {
                EditorUtility.DisplayDialog("错误", "请选择 Assets 文件夹内的路径", "确定");
                return false;
            }
            assetRelativeFolder = "Assets" + abs.Substring(Application.dataPath.Length);
            if (!AssetDatabase.IsValidFolder(assetRelativeFolder))
            {
                Directory.CreateDirectory(assetRelativeFolder);
                AssetDatabase.Refresh();
            }

            // 记忆本次选择的目录，下次烘焙时优先打开
            EditorPrefs.SetString(PREF_LAST_BAKE_FOLDER, assetRelativeFolder);

            return true;
        }

        // ============================================================
        // 🆕 创建一张默认的 Cube 类型 Texture（图片资源）
        // 6 面横向布局: width = size*6, height = size
        // 导入设置: TextureType=Default, Shape=Cube, Mapping=Auto,
        //           ConvolutionType=Specular, sRGB=true, GenerateMipMaps=true
        // ============================================================
        Texture CreateDefaultCubeTexture(string folder, string fileNameNoExt)
        {
            int size = defaultCubemapSize;
            int width = size * 6;
            int height = size;

            // 用 PNG 创建默认灰图（sRGB）
            Texture2D tmp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color baseCol = new Color(0.2f, 0.2f, 0.2f, 1f);
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = baseCol;
            tmp.SetPixels(pixels);
            tmp.Apply();

            byte[] png = tmp.EncodeToPNG();
            Object.DestroyImmediate(tmp);

            string fullPath = $"{folder}/{fileNameNoExt}.png";
            File.WriteAllBytes(fullPath, png);
            AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceSynchronousImport);

            // 设置导入参数：Cube + Default + Specular Glossy
            var importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled = true;
                importer.borderMipmap = false;
                importer.isReadable = false;
                importer.npotScale = TextureImporterNPOTScale.ToNearest;

                // ConvolutionType = Specular(Glossy)
                var so = new SerializedObject(importer);
                var convProp = so.FindProperty("m_ConvolutionType");
                if (convProp != null) convProp.intValue = 1; // 0=None,1=Specular,2=Diffuse
                var fixupProp = so.FindProperty("m_SeamlessCubemap");
                if (fixupProp != null) fixupProp.boolValue = true;
                so.ApplyModifiedProperties();

                importer.SaveAndReimport();
            }

            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<Texture>(fullPath);
        }

        // ============================================================
        // 确保 Custom 模式 Probe 拥有 Cubemap（缺失则自动创建占位图片）
        // 注意：新烘焙流程中 Custom 模式已支持直接烘焙（见 BakeReflectionProbe），
        //       此方法保留用于需要创建占位 Cubemap 图片的场景。
        // ============================================================
        bool EnsureProbeCubemap(EnvTimeNode node, string saveFolder, ref int counter)
        {
            if (node.mainProbe == null) return false;

            // 非 Custom 模式不在此处理（由烘焙流程负责）
            if (node.mainProbe.mode != ReflectionProbeMode.Custom)
                return true;

            // Custom 模式已有 Cubemap
            if (node.mainProbe.customBakedTexture != null)
                return true;

            string cubemapName = $"{cubemapPrefix}{node.mainProbe.name}_{counter:D4}";
            counter++;

            Texture tex = CreateDefaultCubeTexture(saveFolder, cubemapName);
            if (tex == null)
            {
                Debug.LogError($"[EnvTimeline] 无法为 '{node.mainProbe.name}' 创建 Cubemap 图片");
                return false;
            }

            node.mainProbe.customBakedTexture = tex;
            EditorUtility.SetDirty(node.mainProbe);

            Debug.Log($"[EnvTimeline] 为 Probe '{node.mainProbe.name}' 创建 Cubemap 图片: {AssetDatabase.GetAssetPath(tex)}");
            return true;
        }

        // ============================================================
        // 获取下一个Baked文件序号（按目录中现有Baked_*.exr文件自动递增）
        // ============================================================
        int GetNextBakedFileIndex(string folderPath)
        {
            if (!AssetDatabase.IsValidFolder(folderPath))
                return 1;

            // 获取目录下所有文件
            string[] existingFiles = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith(folderPath + "/") && 
                             path.Contains("Baked_") && 
                             path.EndsWith(".exr"))
                .ToArray();

            if (existingFiles.Length == 0)
                return 1;

            // 找出最大的序号
            int maxIndex = 0;
            foreach (string filePath in existingFiles)
            {
                string fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                if (fileName.StartsWith("Baked_"))
                {
                    string numberPart = fileName.Substring("Baked_".Length);
                    if (int.TryParse(numberPart, out int index))
                    {
                        maxIndex = Mathf.Max(maxIndex, index);
                    }
                }
            }

            return maxIndex + 1;
        }

        // ============================================================
        // 单节点 SH 烘焙
        // ============================================================
        void BakeNodeSH(EnvTimeNode node)
        {
            if (node.mainProbe == null)
            {
                EditorUtility.DisplayDialog("错误", "节点未指定主 ReflectionProbe", "确定");
                return;
            }

            if (IsProbeUsedByOtherNode(node.mainProbe, data.nodes.IndexOf(node), out int dupIdx))
            {
                EditorUtility.DisplayDialog("⛔ 无法烘焙",
                    $"该 Probe 与节点 [{dupIdx}] {data.nodes[dupIdx].nodeName} 重复，请先修正！",
                    "确定");
                return;
            }

            Cubemap cube = node.GetMainCubemap();
            if (cube == null)
            {
                if (EditorUtility.DisplayDialog("Probe 未烘焙",
                    "主 Probe 尚未烘焙，是否立即烘焙？", "立即烘焙", "取消"))
                {
                    BakeReflectionProbe(node);
                    cube = node.GetMainCubemap();
                }
                if (cube == null) return;
            }

            float clamp = node.useHDRClamp ? node.hdrClampMax : 0f;
            float[,] coeffs = CubemapSHProjector.ProjectCubemapToSH(
                cube, node.sampleResolution, node.rotationY, clamp);
            if (coeffs == null)
            {
                Debug.LogError("[EnvTimeline] SH 投影失败：" + cube.name);
                return;
            }

            if (node.exposure != 1f)
            {
                for (int i = 0; i < 9; i++)
                    for (int c = 0; c < 3; c++) coeffs[i, c] *= node.exposure;
            }

            CubemapSHProjector.ConvertToUnityFormat(coeffs,
                out var ar, out var ag, out var ab,
                out var br, out var bg, out var bb, out var cc);

            Undo.RecordObject(data, "Bake Node SH");
            node.customSH.SHAr = ar;
            node.customSH.SHAg = ag;
            node.customSH.SHAb = ab;
            node.customSH.SHBr = br;
            node.customSH.SHBg = bg;
            node.customSH.SHBb = bb;
            node.customSH.SHC  = cc;
            EditorUtility.SetDirty(data);

            Debug.Log($"<color=#7CFC00>[EnvTimeline]</color> 节点 [{node.nodeName}] SH 烘焙完成 (来自 Probe '{node.mainProbe.name}')");
        }

        // ============================================================
        // 单 Probe 烘焙（最终都保存为 Custom 模式）
        //   • Custom  模式：临时切换到 Baked 模式烘焙，再切回 Custom，赋值 customBakedTexture
        //   • Baked   模式：按现有流程烘焙，最后切换到 Custom 并赋值 customBakedTexture
        //   • Realtime 模式：同 Baked
        // ============================================================
        void BakeReflectionProbe(EnvTimeNode node)
        {
            if (node.mainProbe == null)
            {
                EditorUtility.DisplayDialog("错误", "节点未指定主 ReflectionProbe", "确定");
                return;
            }

            // 自带 cubemap 的 Probe 不支持 Bake
            if (IsProbeSelfContained(node.mainProbe))
            {
                EditorUtility.DisplayDialog("不支持 Bake",
                    $"Probe '{node.mainProbe.name}' 自带 Cubemap（Custom 模式且 cubemap 名不含 Baked 前缀）。\n"
                    + "不支持 Bake 环境球。如需重新烘焙，请先清除 Custom Cubemap。\n"
                    + "（cubemap 名含 Baked 前缀的 Probe 允许重新烘焙）",
                    "确定");
                return;
            }

            var probe = node.mainProbe;
            var originalMode = probe.mode;

            // ---- 记录 Custom 模式现有纹理路径（用于同目录同名称替换）----
            string existingCustomPath = null;
            if (originalMode == ReflectionProbeMode.Custom && probe.customBakedTexture != null)
            {
                existingCustomPath = AssetDatabase.GetAssetPath(probe.customBakedTexture);
            }

            // ---- 确定烘焙文件路径 ----
            string filename;
            if (!string.IsNullOrEmpty(existingCustomPath))
            {
                // Custom 模式有现有纹理：使用相同目录和基础名称生成 .exr 替换
                string dir = Path.GetDirectoryName(existingCustomPath)?.Replace('\\', '/');
                string baseName = Path.GetFileNameWithoutExtension(existingCustomPath);
                filename = $"{dir}/{baseName}.exr";
                // 记忆该目录，便于后续烘焙优先打开
                if (!string.IsNullOrEmpty(dir))
                    EditorPrefs.SetString(PREF_LAST_BAKE_FOLDER, dir);
            }
            else
            {
                // 需要用户选择目录
                if (!TryPickAssetsFolder(
                        $"为 '{probe.name}' ({originalMode}) 选择烘焙保存目录",
                        out string bakeFolder))
                {
                    Debug.Log("[EnvTimeline] 已取消烘焙");
                    return;
                }
                int bakedFileIndex = GetNextBakedFileIndex(bakeFolder);
                filename = $"{bakeFolder}/Baked_{bakedFileIndex:D3}.exr";
            }

            // ---- Custom 模式：临时切换到 Baked 模式进行烘焙 ----
            bool wasCustom = (originalMode == ReflectionProbeMode.Custom);
            Undo.RecordObject(probe, "Bake ReflectionProbe");
            if (wasCustom)
            {
                probe.mode = ReflectionProbeMode.Baked;
            }

            bool bakeSuccess;
            using (ReflectionProbeStaticScope.Create(node.reflectionProbeBakeTargets))
            {
                bakeSuccess = Lightmapping.BakeReflectionProbe(probe, filename);
            }

            // Custom 模式：切回 Custom
            if (wasCustom)
            {
                probe.mode = ReflectionProbeMode.Custom;
            }

            if (bakeSuccess)
            {
                AssetDatabase.Refresh();
                var bakedTex = AssetDatabase.LoadAssetAtPath<Cubemap>(filename);
                if (bakedTex != null)
                {
                    // 最终都保存为 Custom 模式
                    probe.mode = ReflectionProbeMode.Custom;
                    probe.customBakedTexture = bakedTex;
                    EditorUtility.SetDirty(probe);
                }

                // 半球映射后处理
                if (node.enableHemisphereMirror)
                {
                    Cubemap processedCube = ProcessCubemapHemisphereMirror(filename, node.hemisphereAngle);
                    if (processedCube != null)
                    {
                        probe.customBakedTexture = processedCube;
                        EditorUtility.SetDirty(probe);
                        Debug.Log($"<color=#7CFC00>[EnvTimeline]</color> 半球映射处理完成: {filename} (角度: {node.hemisphereAngle}°)");
                    }
                    else
                    {
                        Debug.LogWarning($"[EnvTimeline] 半球映射处理失败: {filename}");
                    }
                }

                Debug.Log($"<color=#FFD700>[EnvTimeline]</color> Probe 烘焙完成: {filename} (最终模式: Custom)");
                EditorUtility.DisplayDialog("✓ Probe 烘焙完成",
                    $"已烘焙 '{probe.name}'\n保存到: {filename}\n已切换为 Custom 模式", "确定");
            }
            else
            {
                // 烘焙失败：恢复原始模式
                probe.mode = originalMode;
                Debug.LogError("[EnvTimeline] Probe 烘焙失败");
                EditorUtility.DisplayDialog("错误", "Probe 烘焙失败，请查看 Console", "确定");
            }
        }

        // ============================================================
        // 一键烘焙所有节点 SH
        //   • 所有模式 Probe 都会烘焙，最终统一保存为 Custom 模式
        //   • Custom 模式：临时切换到 Baked 烘焙，再切回 Custom
        //   • Baked/Realtime 模式：烘焙后切换到 Custom
        //   • 最后统一烘焙所有节点 SH
        // ============================================================
        void BakeAllNodes()
        {
            if (data == null) return;

            if (!ValidateNoDuplicateProbes()) return;

            // 收集所有需要烘焙的节点（自带 cubemap 的 Probe 跳过烘焙）
            List<EnvTimeNode> needsBake = new List<EnvTimeNode>();
            List<EnvTimeNode> selfContainedNodes = new List<EnvTimeNode>();
            foreach (var node in data.nodes)
            {
                if (node.mainProbe != null)
                {
                    if (IsProbeSelfContained(node.mainProbe))
                        selfContainedNodes.Add(node);
                    else
                        needsBake.Add(node);
                }
            }

            if (needsBake.Count == 0 && selfContainedNodes.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "没有可烘焙的节点（未指定主 Probe）", "确定");
                return;
            }

            if (needsBake.Count == 0)
            {
                EditorUtility.DisplayDialog("提示",
                    $"所有 {selfContainedNodes.Count} 个节点的 Probe 均自带 Cubemap，无需烘焙。\n"
                    + "将直接进行 SH 投影。", "确定");
            }
            else if (selfContainedNodes.Count > 0)
            {
                Debug.Log($"[EnvTimeline] {selfContainedNodes.Count} 个节点的 Probe 自带 Cubemap，跳过 Probe 烘焙，仅烘焙 SH");
            }

            // 检查是否有需要用户选择目录的节点
            //（Custom 模式且无现有纹理，或 Baked/Realtime 模式）
            bool needFolderPick = false;
            foreach (var node in needsBake)
            {
                var probe = node.mainProbe;
                if (probe.mode == ReflectionProbeMode.Custom && probe.customBakedTexture != null)
                    continue; // Custom 有现有纹理，使用同目录替换
                needFolderPick = true;
                break;
            }

            string defaultFolder = null;
            if (needFolderPick)
            {
                if (!TryPickAssetsFolder(
                        $"选择烘焙保存目录 (需烘焙 {needsBake.Count} 个 Probe，最终保存为 Custom 模式)",
                        out defaultFolder))
                {
                    Debug.Log("[EnvTimeline] 用户取消了操作");
                    return;
                }
            }

            // ---- 烘焙所有 Probe（最终统一保存为 Custom 模式）----
            EditorUtility.DisplayProgressBar("烘焙 Probe", "正在烘焙 ReflectionProbe...", 0f);
            int bakeOk = 0, bakeFail = 0;
            for (int i = 0; i < needsBake.Count; i++)
            {
                var node = needsBake[i];
                EditorUtility.DisplayProgressBar("烘焙 Probe",
                    $"烘焙 {node.nodeName} 的 Probe ({i + 1}/{needsBake.Count})",
                    (float)i / needsBake.Count);

                var probe = node.mainProbe;
                var originalMode = probe.mode;

                // 记录 Custom 模式现有纹理路径（用于同目录同名称替换）
                string existingCustomPath = null;
                if (originalMode == ReflectionProbeMode.Custom && probe.customBakedTexture != null)
                    existingCustomPath = AssetDatabase.GetAssetPath(probe.customBakedTexture);

                // 确定烘焙文件路径
                string filename;
                if (!string.IsNullOrEmpty(existingCustomPath))
                {
                    // Custom 模式有现有纹理：使用相同目录和基础名称生成 .exr 替换
                    string dir = Path.GetDirectoryName(existingCustomPath)?.Replace('\\', '/');
                    string baseName = Path.GetFileNameWithoutExtension(existingCustomPath);
                    filename = $"{dir}/{baseName}.exr";
                    // 记忆该目录，便于后续烘焙优先打开
                    if (!string.IsNullOrEmpty(dir))
                        EditorPrefs.SetString(PREF_LAST_BAKE_FOLDER, dir);
                }
                else
                {
                    int bakedFileIndex = GetNextBakedFileIndex(defaultFolder);
                    filename = $"{defaultFolder}/Baked_{bakedFileIndex:D3}.exr";
                }

                // Custom 模式：临时切换到 Baked 模式进行烘焙
                bool wasCustom = (originalMode == ReflectionProbeMode.Custom);
                if (wasCustom)
                    probe.mode = ReflectionProbeMode.Baked;

                bool nodeBakeOk;
                using (ReflectionProbeStaticScope.Create(node.reflectionProbeBakeTargets))
                {
                    nodeBakeOk = Lightmapping.BakeReflectionProbe(probe, filename);
                }
                if (nodeBakeOk)
                {
                    // 最终都保存为 Custom 模式
                    probe.mode = ReflectionProbeMode.Custom;

                    AssetDatabase.Refresh();
                    var bakedTex = AssetDatabase.LoadAssetAtPath<Cubemap>(filename);
                    if (bakedTex != null)
                    {
                        probe.customBakedTexture = bakedTex;
                        EditorUtility.SetDirty(probe);
                    }
                    // 半球映射后处理
                    if (node.enableHemisphereMirror)
                    {
                        Cubemap processedCube = ProcessCubemapHemisphereMirror(filename, node.hemisphereAngle);
                        if (processedCube != null)
                        {
                            probe.customBakedTexture = processedCube;
                            EditorUtility.SetDirty(probe);
                            Debug.Log($"<color=#7CFC00>[EnvTimeline]</color> 半球映射处理完成: {filename} (角度: {node.hemisphereAngle}°)");
                        }
                    }
                    bakeOk++;
                    Debug.Log($"<color=#FFD700>[EnvTimeline]</color> Probe 烘焙完成: {filename} (最终模式: Custom)");
                }
                else
                {
                    // 烘焙失败：恢复原始模式
                    probe.mode = originalMode;
                    bakeFail++;
                    Debug.LogError($"[EnvTimeline] Probe 烘焙失败: {probe.name}");
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            Debug.Log($"[EnvTimeline] 已烘焙 {bakeOk} 个 Probe (失败 {bakeFail})，所有 Probe 已切换为 Custom 模式");

            // ---- 烘焙所有节点 SH ----
            int ok = 0, fail = 0;
            for (int i = 0; i < data.nodes.Count; i++)
            {
                var node = data.nodes[i];
                EditorUtility.DisplayProgressBar("批量烘焙 SH",
                    $"{node.nodeName} ({i + 1}/{data.nodes.Count})",
                    (float)i / data.nodes.Count);

                if (node.mainProbe == null || node.GetMainCubemap() == null)
                {
                    fail++;
                    continue;
                }

                BakeNodeSH(node);
                ok++;
            }

            EditorUtility.ClearProgressBar();

            string summary = $"✓ SH 成功 {ok} 个，✗ SH 失败 {fail} 个";
            if (needsBake.Count > 0)
            {
                summary += $"\n已烘焙 {bakeOk} 个 Probe (失败 {bakeFail})";
                summary += $"\n所有 Probe 已切换为 Custom 模式";
            }
            if (selfContainedNodes.Count > 0)
            {
                summary += $"\n跳过 {selfContainedNodes.Count} 个自带 Cubemap 的 Probe（仅烘焙 SH）";
            }

            EditorUtility.DisplayDialog("批量烘焙完成", summary, "确定");
        }

        static string FormatV4(Vector4 v) =>
            $"({v.x:F3}, {v.y:F3}, {v.z:F3}, {v.w:F3})";

        // ============================================================
        // 🌐 半球映射：将空半球用实景半球镜像填充
        // ============================================================

        /// <summary>
        /// 对已烘焙的 Cubemap (.exr) 进行半球镜像后处理。
        /// 将空半球（与 hemisphereAngle 相反方向）的像素用实景半球镜像填充，
        /// 保存为同格式 EXR，保持原有导入设置。
        /// </summary>
        Cubemap ProcessCubemapHemisphereMirror(string exrPath, float hemisphereAngle)
        {
            if (string.IsNullOrEmpty(exrPath) || !File.Exists(exrPath))
            {
                Debug.LogError($"[EnvTimeline] EXR 文件不存在: {exrPath}");
                return null;
            }

            // 1. 读取原始导入设置
            var importer = AssetImporter.GetAtPath(exrPath) as TextureImporter;
            bool origReadable = importer?.isReadable ?? false;
            bool origSRGB = importer?.sRGBTexture ?? true;
            bool origMipMaps = importer?.mipmapEnabled ?? true;
            int origConvolution = 0;
            bool origSeamless = false;
            if (importer != null)
            {
                var so0 = new SerializedObject(importer);
                var convProp0 = so0.FindProperty("m_ConvolutionType");
                if (convProp0 != null) origConvolution = convProp0.intValue;
                var seamProp0 = so0.FindProperty("m_SeamlessCubemap");
                if (seamProp0 != null) origSeamless = seamProp0.boolValue;
            }

            // 2. 临时设为可读
            if (importer != null && !origReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            // 3. 加载并读取像素
            AssetDatabase.Refresh();
            Cubemap sourceCube = AssetDatabase.LoadAssetAtPath<Cubemap>(exrPath);
            if (sourceCube == null)
            {
                Debug.LogError($"[EnvTimeline] 无法加载 Cubemap: {exrPath}");
                return null;
            }

            int size = sourceCube.width;
            Color[][] facePixels = new Color[6][];
            for (int face = 0; face < 6; face++)
                facePixels[face] = sourceCube.GetPixels((CubemapFace)face);

            sourceCube = null;

            // 4. 计算镜像平面法线（过 Y 轴的垂直平面）
            float rad = hemisphereAngle * Mathf.Deg2Rad;
            Vector3 mirrorNormal = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

            // 5. 逐像素处理
            Color[][] processedPixels = new Color[6][];
            for (int face = 0; face < 6; face++)
            {
                processedPixels[face] = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float v = (y + 0.5f) / size * 2f - 1f;
                        Vector3 dir = CubemapSHProjector.GetCubemapDirection(face, u, v);

                        float dot = Vector3.Dot(dir, mirrorNormal);
                        int idx = y * size + x;

                        if (dot < -0.0001f)
                        {
                            // 空半球：镜像方向并从实景半球采样
                            Vector3 mirroredDir = dir - 2f * dot * mirrorNormal;
                            processedPixels[face][idx] = SampleCubemapBilinear(facePixels, size, mirroredDir);
                        }
                        else
                        {
                            // 实景半球：保持原样
                            processedPixels[face][idx] = facePixels[face][idx];
                        }
                    }
                }
            }

            // 6. 创建横向条带 Texture2D 并编码为 EXR
            Texture2D stripTex = new Texture2D(size * 6, size, TextureFormat.RGBAFloat, false);
            for (int face = 0; face < 6; face++)
                stripTex.SetPixels(face * size, 0, size, size, processedPixels[face]);
            stripTex.Apply();

            byte[] exrBytes;
            try
            {
                exrBytes = stripTex.EncodeToEXR();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EnvTimeline] EXR 编码失败: {e.Message}");
                Object.DestroyImmediate(stripTex);
                return null;
            }
            Object.DestroyImmediate(stripTex);

            if (exrBytes == null || exrBytes.Length == 0)
            {
                Debug.LogError("[EnvTimeline] EXR 编码返回空数据");
                return null;
            }

            // 7. 写入文件（覆盖原 EXR）
            File.WriteAllBytes(exrPath, exrBytes);

            // 8. 恢复导入设置
            if (importer != null)
            {
                importer.isReadable = origReadable;
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
                importer.sRGBTexture = origSRGB;
                importer.alphaSource = TextureImporterAlphaSource.None;
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled = origMipMaps;
                importer.borderMipmap = false;
                importer.npotScale = TextureImporterNPOTScale.ToNearest;

                var so = new SerializedObject(importer);
                var convProp = so.FindProperty("m_ConvolutionType");
                if (convProp != null) convProp.intValue = origConvolution;
                var seamProp = so.FindProperty("m_SeamlessCubemap");
                if (seamProp != null) seamProp.boolValue = origSeamless;
                so.ApplyModifiedProperties();

                importer.SaveAndReimport();
            }

            AssetDatabase.Refresh();

            // 9. 加载处理后的 Cubemap
            return AssetDatabase.LoadAssetAtPath<Cubemap>(exrPath);
        }

        /// <summary>
        /// 将方向向量转换为 Cubemap 面索引和 [0,1] UV 坐标
        /// </summary>
        static void DirectionToFaceUV(Vector3 dir, out int face, out float u, out float v)
        {
            dir.Normalize();
            float ax = Mathf.Abs(dir.x);
            float ay = Mathf.Abs(dir.y);
            float az = Mathf.Abs(dir.z);

            if (ax >= ay && ax >= az)
            {
                if (dir.x > 0) { face = 0; u = -dir.z / ax; v = -dir.y / ax; }
                else            { face = 1; u =  dir.z / ax; v = -dir.y / ax; }
            }
            else if (ay >= ax && ay >= az)
            {
                if (dir.y > 0) { face = 2; u =  dir.x / ay; v =  dir.z / ay; }
                else            { face = 3; u =  dir.x / ay; v = -dir.z / ay; }
            }
            else
            {
                if (dir.z > 0) { face = 4; u =  dir.x / az; v = -dir.y / az; }
                else            { face = 5; u = -dir.x / az; v = -dir.y / az; }
            }

            u = Mathf.Clamp01((u + 1f) * 0.5f);
            v = Mathf.Clamp01((v + 1f) * 0.5f);
        }

        /// <summary>
        /// 从 Cubemap 像素数组中双线性采样
        /// </summary>
        static Color SampleCubemapBilinear(Color[][] facePixels, int size, Vector3 dir)
        {
            DirectionToFaceUV(dir, out int face, out float u, out float v);

            float px = u * size - 0.5f;
            float py = v * size - 0.5f;

            int x0 = Mathf.FloorToInt(px);
            int y0 = Mathf.FloorToInt(py);
            float fx = px - x0;
            float fy = py - y0;

            int x1 = x0 + 1;
            int y1 = y0 + 1;

            x0 = Mathf.Clamp(x0, 0, size - 1);
            x1 = Mathf.Clamp(x1, 0, size - 1);
            y0 = Mathf.Clamp(y0, 0, size - 1);
            y1 = Mathf.Clamp(y1, 0, size - 1);

            Color c00 = facePixels[face][y0 * size + x0];
            Color c01 = facePixels[face][y1 * size + x0];
            Color c10 = facePixels[face][y0 * size + x1];
            Color c11 = facePixels[face][y1 * size + x1];

            Color c0 = Color.Lerp(c00, c01, fy);
            Color c1 = Color.Lerp(c10, c11, fy);
            return Color.Lerp(c0, c1, fx);
        }

        /// <summary>
        /// 对节点的 Cubemap 进行半球映射独立处理（烘焙后单独处理环境球）
        /// </summary>
        void ProcessNodeHemisphereMirror(EnvTimeNode node)
        {
            Cubemap cube = node.GetMainCubemap();
            if (cube == null)
            {
                EditorUtility.DisplayDialog("错误", "节点未烘焙 Cubemap，请先烘焙", "确定");
                return;
            }

            string cubePath = AssetDatabase.GetAssetPath(cube);
            if (string.IsNullOrEmpty(cubePath))
            {
                EditorUtility.DisplayDialog("错误", "无法获取 Cubemap 资源路径", "确定");
                return;
            }

            EditorUtility.DisplayProgressBar("半球映射", "正在处理 Cubemap...", 0f);

            Cubemap processed = ProcessCubemapHemisphereMirror(cubePath, node.hemisphereAngle);

            EditorUtility.ClearProgressBar();

            if (processed != null)
            {
                if (node.mainProbe.mode == ReflectionProbeMode.Custom)
                    node.mainProbe.customBakedTexture = processed;
                else
                    node.mainProbe.bakedTexture = processed;
                EditorUtility.SetDirty(node.mainProbe);

                Debug.Log($"<color=#7CFC00>[EnvTimeline]</color> 半球映射处理完成: {cubePath} (角度: {node.hemisphereAngle}°)");
                EditorUtility.DisplayDialog("✓ 处理完成",
                    $"半球映射处理完成\n文件: {cubePath}\n角度: {node.hemisphereAngle}°", "确定");
            }
            else
            {
                EditorUtility.DisplayDialog("错误", "半球映射处理失败，请查看 Console", "确定");
            }
        }
    }
}
#endif