// EnvTimelineSimpleCore.cs
// 从 EnvTimelineSimpleEditorWindow 抽取的核心功能类
// 包含所有非 UI 的业务逻辑：烘焙、验证、Cubemap 处理、资产管理等
// 可独立编译为 DLL，供 EditorWindow 通过反射调用
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Hotfix.Core.EnvTimelineSimple;

namespace UnityEditor.EnvTimelineSimple
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
                        // 不同尺寸：创建源尺寸 Cube RT，复制源数据，逐面读取后 CPU 缩放
                        RenderTexture srcRT = new RenderTexture(srcSize, srcSize, 0, RenderTextureFormat.ARGBFloat);
                        srcRT.dimension = TextureDimension.Cube;
                        srcRT.useMipMap = false;
                        srcRT.Create();

                        for (int face = 0; face < 6; face++)
                            Graphics.CopyTexture(source, face, 0, srcRT, face, 0);

                        float scale = (float)srcSize / size;
                        for (int face = 0; face < 6; face++)
                        {
                            Graphics.SetRenderTarget(srcRT, 0, (CubemapFace)face);
                            Texture2D srcTex = new Texture2D(srcSize, srcSize, TextureFormat.RGBAFloat, false);
                            srcTex.ReadPixels(new Rect(0, 0, srcSize, srcSize), 0, 0);
                            srcTex.Apply();
                            Color[] srcPx = srcTex.GetPixels();
                            UnityEngine.Object.DestroyImmediate(srcTex);

                            Color[] dstPx = new Color[size * size];
                            for (int y = 0; y < size; y++)
                                for (int x = 0; x < size; x++)
                                {
                                    int sx = Mathf.Clamp(Mathf.FloorToInt(x * scale), 0, srcSize - 1);
                                    int sy = Mathf.Clamp(Mathf.FloorToInt(y * scale), 0, srcSize - 1);
                                    dstPx[y * size + x] = srcPx[sy * srcSize + sx];
                                }
                            result.SetPixels(dstPx, (CubemapFace)face);
                        }
                        result.Apply();

                        UnityEngine.Object.DestroyImmediate(srcRT);
                        RenderTexture.active = null;
                        UnityEngine.Object.DestroyImmediate(rt);
                        return result;
                    }
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(rt);
                    EnvTimeSimpleDebug.LogError("[CubemapSHProjector] 无法读取 Cubemap，请开启 Read/Write");
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

    // ================================================================
    // ReflectionProbeStaticScope — 临时勾选 ReflectionProbeStatic
    // ================================================================
    public struct ReflectionProbeStaticScope : IDisposable
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

    // ================================================================
    // SpecularLightBakeScope — 镜面高光烘焙作用域
    // ================================================================
    public struct SpecularLightBakeScope : IDisposable
    {
        List<GameObject> _proxies;
        bool _enabled;

        public static SpecularLightBakeScope Create(EnvTimeNode node)
        {
            var scope = new SpecularLightBakeScope();
            if (node == null || !node.enableSpecularLightBaking)
                return scope;

            scope._enabled = true;
            scope._proxies = new List<GameObject>();

            List<Light> lights = CollectLights(node);
            if (lights.Count == 0) return scope;

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
                        proxy = CreateEmissiveSphere(
                            light.transform.position,
                            radius,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecProxy");
                        break;

                    case LightType.Spot:
                        proxy = CreateEmissiveSphere(
                            light.transform.position,
                            radius,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecProxy");
                        break;

                    case LightType.Area:
                        proxy = CreateEmissivePanel(
                            light.transform.position,
                            light.transform.rotation,
                            new Vector3(light.areaSize.x, light.areaSize.y, 1f) * areaScale,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecPanel",
                            light.cookie);
                        break;

                    case LightType.Disc:
                        proxy = CreateEmissiveDisc(
                            light.transform.position,
                            light.transform.rotation,
                            radius * 10f * areaScale,
                            light.color * intensityMul * light.intensity,
                            light.gameObject.name + "_SpecDisc");
                        break;
                }

                if (proxy != null)
                {
                    proxy.hideFlags = HideFlags.HideAndDontSave;
                    var flags = GameObjectUtility.GetStaticEditorFlags(proxy);
                    GameObjectUtility.SetStaticEditorFlags(
                        proxy, flags | StaticEditorFlags.ReflectionProbeStatic);
                    scope._proxies.Add(proxy);
                }
            }

            if (scope._proxies.Count > 0)
            {
                EnvTimeSimpleDebug.Log($"<color=#FFD700>[EnvTimeline]</color> SpecularLightBakeScope: 创建了 {scope._proxies.Count} 个自发光代理物体");
            }

            return scope;
        }

        public static List<Light> CollectLights(EnvTimeNode node)
        {
            var result = new List<Light>();
            switch (node.specularLightCollectMode)
            {
                case EnvTimeNode.SpecularLightCollectMode.ManualList:
                    result.AddRange(node.specularLightTargets);
                    break;

                case EnvTimeNode.SpecularLightCollectMode.AutoCollectBaked:
                    foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
                    {
                        if (light == null) continue;
                        if (light.lightmapBakeType == LightmapBakeType.Baked)
                            result.Add(light);
                    }
                    break;

                case EnvTimeNode.SpecularLightCollectMode.AutoCollectAll:
                    foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
                    {
                        if (light == null) continue;
                        if (light.lightmapBakeType == LightmapBakeType.Baked ||
                            light.lightmapBakeType == LightmapBakeType.Mixed)
                            result.Add(light);
                    }
                    break;
            }
            return result;
        }

        public static GameObject CreateEmissiveSphere(Vector3 pos, float radius,
            Color emissiveColor, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (radius * 2f);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();

            var mat = new Material(Shader.Find("Standard"));
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            mat.SetColor("_Color", Color.black);
            mat.SetColor("_EmissionColor", emissiveColor);
            mr.sharedMaterial = mat;

            return go;
        }

        public static GameObject CreateEmissivePanel(Vector3 pos, Quaternion rot,
            Vector3 scale, Color emissiveColor, string name, Texture cookie = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.position = pos;
            go.transform.rotation = rot * Quaternion.Euler(0, 180, 0);
            go.transform.localScale = new Vector3(
                Mathf.Max(0.01f, scale.x),
                Mathf.Max(0.01f, scale.y),
                1f);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();

            var mat = new Material(Shader.Find("Standard"));
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            mat.SetColor("_Color", Color.black);
            mat.SetColor("_EmissionColor", emissiveColor);
            if (cookie != null)
                mat.SetTexture("_EmissionMap", cookie);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
            mat.SetInt("_Cull", 0);
            mr.sharedMaterial = mat;

            return go;
        }

        public static GameObject CreateEmissiveDisc(Vector3 pos, Quaternion rot,
            float radius, Color emissiveColor, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.transform.localScale = new Vector3(radius * 2f, radius * 2f, 0.01f);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();

            var mat = new Material(Shader.Find("Standard"));
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            mat.SetColor("_Color", Color.black);
            mat.SetColor("_EmissionColor", emissiveColor);
            mat.SetFloat("_Mode", 0);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.renderQueue = -1;
            mr.sharedMaterial = mat;

            return go;
        }

        public void Dispose()
        {
            if (!_enabled) return;

            if (_proxies != null)
            {
                foreach (var go in _proxies)
                {
                    if (go != null)
                    {
                        var mr = go.GetComponent<MeshRenderer>();
                        if (mr != null && mr.sharedMaterial != null)
                            Object.DestroyImmediate(mr.sharedMaterial);
                        var col = go.GetComponent<Collider>();
                        if (col != null) Object.DestroyImmediate(col);
                        Object.DestroyImmediate(go);
                    }
                }
                _proxies.Clear();
            }
        }
    }

    // ================================================================
    // EnvTimelineSimpleCore — 核心功能类
    // 包含所有从 EditorWindow 抽取的业务逻辑
    // 可独立编译为 DLL，供 EditorWindow 通过反射调用
    // ================================================================
    public class EnvTimelineSimpleCore
    {
        // ---- 状态属性 ----
        public EnvironmentTimelineData Data { get; set; }
        public int DefaultCubemapSize { get; set; } = 128;
        public string CubemapPrefix { get; set; } = "Baked";

        const string PREF_LAST_BAKE_FOLDER = "BYTools_EnvTimeline_LastBakeFolder";

        // ============================================================
        // 数据操作
        // ============================================================

        /// <summary>
        /// 在场景中创建新的 Timeline 物体，返回创建的 Data
        /// </summary>
        public EnvironmentTimelineData CreateNewTimelineInScene()
        {
            GameObject go = new GameObject("EnvironmentTimeline");
            var newData = go.AddComponent<EnvironmentTimelineData>();
            go.AddComponent<EnvironmentTimelineController>();

            Undo.RegisterCreatedObjectUndo(go, "Create Timeline");
            Selection.activeGameObject = go;

            EnvTimeSimpleDebug.Log($"[EnvTimeline] 已创建新的 Timeline 物体: {go.name}");
            return newData;
        }

        /// <summary>
        /// 在指定时间添加节点，返回新节点的索引
        /// </summary>
        public int AddNodeAtTime(float time)
        {
            if (Data == null) return -1;

            Undo.RecordObject(Data, "Add Time Node");
            var node = new EnvTimeNode
            {
                nodeName = "Node_" + Data.nodes.Count,
                time = time
            };
            Data.nodes.Add(node);
            Data.SortByTime();
            int idx = Data.nodes.IndexOf(node);
            EditorUtility.SetDirty(Data);
            return idx;
        }

        /// <summary>
        /// 应用预览（将预览时间应用到 Controller）
        /// </summary>
        public void ApplyPreview(float previewTime)
        {
            if (Data == null) return;

            var ctrl = Data.GetComponent<EnvironmentTimelineController>();
            if (ctrl != null)
            {
                ctrl.currentTime = previewTime;
                ctrl.ApplyAtCurrentTime();
            }
            else
            {
                EnvTimeSimpleDebug.LogWarning($"物体 '{Data.gameObject.name}' 上未找到 EnvironmentTimelineController 组件");
            }
        }

        // ============================================================
        // Probe 验证工具
        // ============================================================

        public bool IsProbeUsedByOtherNode(ReflectionProbe probe, int excludeIndex, out int usedByIndex)
        {
            usedByIndex = -1;
            if (probe == null || Data == null) return false;

            for (int i = 0; i < Data.nodes.Count; i++)
            {
                if (i == excludeIndex) continue;
                if (Data.nodes[i].mainProbe == probe)
                {
                    usedByIndex = i;
                    return true;
                }
            }
            return false;
        }

        public HashSet<int> GetDuplicateProbeNodeIndices()
        {
            var set = new HashSet<int>();
            if (Data == null) return set;

            var map = new Dictionary<ReflectionProbe, int>();
            for (int i = 0; i < Data.nodes.Count; i++)
            {
                var p = Data.nodes[i].mainProbe;
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
        /// </summary>
        public bool IsProbeSelfContained(ReflectionProbe probe)
        {
            if (probe == null) return false;

            if (probe.mode != ReflectionProbeMode.Custom || probe.customBakedTexture == null)
                return false;

            if (!string.IsNullOrEmpty(CubemapPrefix)
                && probe.customBakedTexture.name.StartsWith(CubemapPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        public bool ValidateNoDuplicateProbes()
        {
            var dupSet = GetDuplicateProbeNodeIndices();
            if (dupSet.Count > 0)
            {
                var names = new List<string>();
                foreach (var idx in dupSet)
                    names.Add($"  • [{idx}] {Data.nodes[idx].nodeName}  →  {Data.nodes[idx].mainProbe.name}");

                EditorUtility.DisplayDialog("⛔ 无法烘焙：检测到重复 Probe",
                    "以下节点使用了重复的主 ReflectionProbe，请先修正：\n\n" +
                    string.Join("\n", names) +
                    "\n\n每个节点必须使用不同的主 ReflectionProbe。",
                    "确定");
                return false;
            }
            return true;
        }

        // ============================================================
        // 资产/文件管理
        // ============================================================

        public bool TryPickAssetsFolder(string title, out string assetRelativeFolder)
        {
            assetRelativeFolder = null;

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

            EditorPrefs.SetString(PREF_LAST_BAKE_FOLDER, assetRelativeFolder);

            return true;
        }

        public Texture CreateDefaultCubeTexture(string folder, string fileNameNoExt)
        {
            int size = DefaultCubemapSize;
            int width = size * 6;
            int height = size;

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

            var importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.borderMipmap = false;
                importer.isReadable = false;
                importer.npotScale = TextureImporterNPOTScale.ToNearest;

                var so = new SerializedObject(importer);
                var convProp = so.FindProperty("m_ConvolutionType");
                if (convProp != null) convProp.intValue = 1;
                var fixupProp = so.FindProperty("m_SeamlessCubemap");
                if (fixupProp != null) fixupProp.boolValue = true;
                so.ApplyModifiedProperties();

                importer.SaveAndReimport();
            }

            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<Texture>(fullPath);
        }

        public bool EnsureProbeCubemap(EnvTimeNode node, string saveFolder, ref int counter)
        {
            if (node.mainProbe == null) return false;

            if (node.mainProbe.mode != ReflectionProbeMode.Custom)
                return true;

            if (node.mainProbe.customBakedTexture != null)
                return true;

            string cubemapName = $"{CubemapPrefix}{node.mainProbe.name}_{counter:D4}";
            counter++;

            Texture tex = CreateDefaultCubeTexture(saveFolder, cubemapName);
            if (tex == null)
            {
                EnvTimeSimpleDebug.LogError($"[EnvTimeline] 无法为 '{node.mainProbe.name}' 创建 Cubemap 图片");
                return false;
            }

            node.mainProbe.customBakedTexture = tex;
            EditorUtility.SetDirty(node.mainProbe);

            EnvTimeSimpleDebug.Log($"[EnvTimeline] 为 Probe '{node.mainProbe.name}' 创建 Cubemap 图片: {AssetDatabase.GetAssetPath(tex)}");
            return true;
        }

        public int GetNextBakedFileIndex(string folderPath)
        {
            if (!AssetDatabase.IsValidFolder(folderPath))
                return 1;

            string[] existingFiles = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith(folderPath + "/") &&
                             path.Contains("Baked_") &&
                             path.EndsWith(".exr"))
                .ToArray();

            if (existingFiles.Length == 0)
                return 1;

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
        // 烘焙
        // ============================================================

        public void BakeNodeSH(EnvTimeNode node)
        {
            if (node.mainProbe == null)
            {
                EditorUtility.DisplayDialog("错误", "节点未指定主 ReflectionProbe", "确定");
                return;
            }

            if (IsProbeUsedByOtherNode(node.mainProbe, Data.nodes.IndexOf(node), out int dupIdx))
            {
                EditorUtility.DisplayDialog("⛔ 无法烘焙",
                    $"该 Probe 与节点 [{dupIdx}] {Data.nodes[dupIdx].nodeName} 重复，请先修正！",
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

            string cubeAssetPath = AssetDatabase.GetAssetPath(cube);
            bool madeReadable = false;
            if (!string.IsNullOrEmpty(cubeAssetPath))
            {
                var cubeImporter = AssetImporter.GetAtPath(cubeAssetPath) as TextureImporter;
                if (cubeImporter != null && !cubeImporter.isReadable)
                {
                    cubeImporter.isReadable = true;
                    cubeImporter.SaveAndReimport();
                    AssetDatabase.Refresh();
                    cube = AssetDatabase.LoadAssetAtPath<Cubemap>(cubeAssetPath);
                    if (cube == null)
                    {
                        EnvTimeSimpleDebug.LogError("[EnvTimeline] 无法重新加载可读 Cubemap: " + cubeAssetPath);
                        return;
                    }
                    madeReadable = true;
                }
            }

            float clamp = node.useHDRClamp ? node.hdrClampMax : 0f;
            float[,] coeffs = CubemapSHProjector.ProjectCubemapToSH(
                cube, node.sampleResolution, node.rotationY, clamp);

            if (madeReadable)
            {
                var restoreImporter = AssetImporter.GetAtPath(cubeAssetPath) as TextureImporter;
                if (restoreImporter != null)
                {
                    restoreImporter.isReadable = false;
                    restoreImporter.SaveAndReimport();
                }
            }

            if (coeffs == null)
            {
                EnvTimeSimpleDebug.LogError("[EnvTimeline] SH 投影失败：" + cube.name);
                return;
            }

            bool allZero = true;
            for (int i = 0; i < 9 && allZero; i++)
                for (int c = 0; c < 3 && allZero; c++)
                    if (Mathf.Abs(coeffs[i, c]) > 1e-8f) allZero = false;
            if (allZero)
            {
                EnvTimeSimpleDebug.LogWarning($"[EnvTimeline] 节点 [{node.nodeName}] SH 烘焙结果全零！Cubemap 可能为纯黑或像素读取失败: {cube.name}");
            }

            if (node.exposure != 1f)
            {
                for (int i = 0; i < 9; i++)
                    for (int c = 0; c < 3; c++) coeffs[i, c] *= node.exposure;
            }

            CubemapSHProjector.ConvertToUnityFormat(coeffs,
                out var ar, out var ag, out var ab,
                out var br, out var bg, out var bb, out var cc);

            Undo.RecordObject(Data, "Bake Node SH");
            node.customSH.SHAr = ar;
            node.customSH.SHAg = ag;
            node.customSH.SHAb = ab;
            node.customSH.SHBr = br;
            node.customSH.SHBg = bg;
            node.customSH.SHBb = bb;
            node.customSH.SHC  = cc;
            EditorUtility.SetDirty(Data);

            EnvTimeSimpleDebug.Log($"<color=#7CFC00>[EnvTimeline]</color> 节点 [{node.nodeName}] SH 烘焙完成 (来自 Probe '{node.mainProbe.name}')");
        }

        public void BakeReflectionProbe(EnvTimeNode node)
        {
            if (node.mainProbe == null)
            {
                EditorUtility.DisplayDialog("错误", "节点未指定主 ReflectionProbe", "确定");
                return;
            }

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

            string existingCustomPath = null;
            if (originalMode == ReflectionProbeMode.Custom && probe.customBakedTexture != null)
            {
                existingCustomPath = AssetDatabase.GetAssetPath(probe.customBakedTexture);
            }

            string filename;
            if (!string.IsNullOrEmpty(existingCustomPath))
            {
                string dir = Path.GetDirectoryName(existingCustomPath)?.Replace('\\', '/');
                string baseName = Path.GetFileNameWithoutExtension(existingCustomPath);
                filename = $"{dir}/{baseName}.exr";
                if (!string.IsNullOrEmpty(dir))
                    EditorPrefs.SetString(PREF_LAST_BAKE_FOLDER, dir);
            }
            else
            {
                if (!TryPickAssetsFolder(
                        $"为 '{probe.name}' ({originalMode}) 选择烘焙保存目录",
                        out string bakeFolder))
                {
                    EnvTimeSimpleDebug.Log("[EnvTimeline] 已取消烘焙");
                    return;
                }
                int bakedFileIndex = GetNextBakedFileIndex(bakeFolder);
                filename = $"{bakeFolder}/Baked_{bakedFileIndex:D3}.exr";
            }

            bool wasCustom = (originalMode == ReflectionProbeMode.Custom);
            Undo.RecordObject(probe, "Bake ReflectionProbe");
            if (wasCustom)
            {
                probe.mode = ReflectionProbeMode.Baked;
            }

            bool bakeSuccess;
            using (ReflectionProbeStaticScope.Create(node.reflectionProbeBakeTargets))
            using (SpecularLightBakeScope.Create(node))
            {
                bakeSuccess = Lightmapping.BakeReflectionProbe(probe, filename);
            }

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
                    probe.mode = ReflectionProbeMode.Custom;
                    probe.customBakedTexture = bakedTex;
                    EditorUtility.SetDirty(probe);
                }

                if (node.enableHemisphereMirror)
                {
                    Cubemap processedCube = ProcessCubemapHemisphereMirror(filename, node.hemisphereAngle);
                    if (processedCube != null)
                    {
                        probe.customBakedTexture = processedCube;
                        EditorUtility.SetDirty(probe);
                        EnvTimeSimpleDebug.Log($"<color=#7CFC00>[EnvTimeline]</color> 半球映射处理完成: {filename} (角度: {node.hemisphereAngle}°)");
                    }
                    else
                    {
                        EnvTimeSimpleDebug.LogWarning($"[EnvTimeline] 半球映射处理失败: {filename}");
                    }
                }

                EnvTimeSimpleDebug.Log($"<color=#FFD700>[EnvTimeline]</color> Probe 烘焙完成: {filename} (最终模式: Custom)");
                EditorUtility.DisplayDialog("✓ Probe 烘焙完成",
                    $"已烘焙 '{probe.name}'\n保存到: {filename}\n已切换为 Custom 模式", "确定");
            }
            else
            {
                probe.mode = originalMode;
                EnvTimeSimpleDebug.LogError("[EnvTimeline] Probe 烘焙失败");
                EditorUtility.DisplayDialog("错误", "Probe 烘焙失败，请查看 Console", "确定");
            }
        }

        public void BakeAllNodes()
        {
            if (Data == null) return;

            if (!ValidateNoDuplicateProbes()) return;

            List<EnvTimeNode> validNodes = new List<EnvTimeNode>();
            List<EnvTimeNode> skippedNodes = new List<EnvTimeNode>();
            foreach (var node in Data.nodes)
            {
                if (node.mainProbe != null && node.GetMainCubemap() != null)
                    validNodes.Add(node);
                else
                    skippedNodes.Add(node);
            }

            if (validNodes.Count == 0)
            {
                EditorUtility.DisplayDialog("提示",
                    "没有可烘焙 SH 的节点（所有节点均无有效的反射球 Cubemap）。\n"
                    + "请先为节点指定 ReflectionProbe 并烘焙或指定 Cubemap。", "确定");
                return;
            }

            if (skippedNodes.Count > 0)
            {
                EnvTimeSimpleDebug.Log($"[EnvTimeline] {skippedNodes.Count} 个节点无有效反射球，跳过 SH 烘焙："
                    + string.Join(", ", skippedNodes.ConvertAll(n => n.nodeName)));
            }

            int ok = 0, fail = 0;
            for (int i = 0; i < validNodes.Count; i++)
            {
                var node = validNodes[i];
                EditorUtility.DisplayProgressBar("批量烘焙 SH",
                    $"{node.nodeName} ({i + 1}/{validNodes.Count})",
                    (float)i / validNodes.Count);

                try
                {
                    BakeNodeSH(node);
                    ok++;
                }
                catch (System.Exception ex)
                {
                    EnvTimeSimpleDebug.LogError($"[EnvTimeline] 节点 [{node.nodeName}] SH 烘焙失败: {ex.Message}");
                    fail++;
                }
            }

            EditorUtility.ClearProgressBar();

            string summary = $"✓ SH 成功 {ok} 个";
            if (fail > 0)
                summary += $"，✗ SH 失败 {fail} 个";
            if (skippedNodes.Count > 0)
                summary += $"\n跳过 {skippedNodes.Count} 个无有效反射球的节点";

            EditorUtility.DisplayDialog("批量烘焙完成", summary, "确定");
        }

        // ============================================================
        // 半球映射
        // ============================================================

        public Cubemap ProcessCubemapHemisphereMirror(string exrPath, float hemisphereAngle)
        {
            if (string.IsNullOrEmpty(exrPath) || !File.Exists(exrPath))
            {
                EnvTimeSimpleDebug.LogError($"[EnvTimeline] EXR 文件不存在: {exrPath}");
                return null;
            }

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

            if (importer != null && !origReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            AssetDatabase.Refresh();
            Cubemap sourceCube = AssetDatabase.LoadAssetAtPath<Cubemap>(exrPath);
            if (sourceCube == null)
            {
                EnvTimeSimpleDebug.LogError($"[EnvTimeline] 无法加载 Cubemap: {exrPath}");
                return null;
            }

            int size = sourceCube.width;
            Color[][] facePixels = new Color[6][];
            for (int face = 0; face < 6; face++)
                facePixels[face] = sourceCube.GetPixels((CubemapFace)face);

            sourceCube = null;

            float rad = hemisphereAngle * Mathf.Deg2Rad;
            Vector3 mirrorNormal = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

            Color[][] processedPixels = new Color[6][];
            for (int face = 0; face < 6; face++)
            {
                processedPixels[face] = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float vRaw = (y + 0.5f) / size * 2f - 1f;
                        float v = (face == 2 || face == 3) ? vRaw : -vRaw;
                        Vector3 dir = CubemapSHProjector.GetCubemapDirection(face, u, v);

                        float dot = Vector3.Dot(dir, mirrorNormal);
                        int idx = y * size + x;

                        if (dot < -0.0001f)
                        {
                            Vector3 mirroredDir = dir - 2f * dot * mirrorNormal;
                            processedPixels[face][idx] = SampleCubemapBilinear(facePixels, size, mirroredDir);
                        }
                        else
                        {
                            processedPixels[face][idx] = facePixels[face][idx];
                        }
                    }
                }
            }

            Texture2D stripTex = new Texture2D(size * 6, size, TextureFormat.RGBA32, false);
            for (int face = 0; face < 6; face++)
            {
                Color[] flipped = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    int srcY = size - 1 - y;
                    System.Array.Copy(processedPixels[face], srcY * size,
                        flipped, y * size, size);
                }
                stripTex.SetPixels(face * size, 0, size, size, flipped);
            }
            stripTex.Apply();

            byte[] exrBytes;
            try
            {
                exrBytes = stripTex.EncodeToEXR();
            }
            catch (System.Exception e)
            {
                EnvTimeSimpleDebug.LogError($"[EnvTimeline] EXR 编码失败: {e.Message}");
                Object.DestroyImmediate(stripTex);
                return null;
            }
            Object.DestroyImmediate(stripTex);

            if (exrBytes == null || exrBytes.Length == 0)
            {
                EnvTimeSimpleDebug.LogError("[EnvTimeline] EXR 编码返回空数据");
                return null;
            }

            File.WriteAllBytes(exrPath, exrBytes);

            if (importer != null)
            {
                importer.isReadable = origReadable;
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.generateCubemap = TextureImporterGenerateCubemap.FullCubemap;
                importer.sRGBTexture = origSRGB;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
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

            return AssetDatabase.LoadAssetAtPath<Cubemap>(exrPath);
        }

        public void ProcessNodeHemisphereMirror(EnvTimeNode node)
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

                EnvTimeSimpleDebug.Log($"<color=#7CFC00>[EnvTimeline]</color> 半球映射处理完成: {cubePath} (角度: {node.hemisphereAngle}°)");
                EditorUtility.DisplayDialog("✓ 处理完成",
                    $"半球映射处理完成\n文件: {cubePath}\n角度: {node.hemisphereAngle}°", "确定");
            }
            else
            {
                EditorUtility.DisplayDialog("错误", "半球映射处理失败，请查看 Console", "确定");
            }
        }

        // ============================================================
        // 静态工具方法
        // ============================================================

        public static string FormatV4(Vector4 v) =>
            $"({v.x:F3}, {v.y:F3}, {v.z:F3}, {v.w:F3})";

        public static void DirectionToFaceUV(Vector3 dir, out int face, out float u, out float v)
        {
            dir.Normalize();
            float ax = Mathf.Abs(dir.x);
            float ay = Mathf.Abs(dir.y);
            float az = Mathf.Abs(dir.z);

            if (ax >= ay && ax >= az)      face = dir.x > 0 ? 0 : 1;
            else if (ay >= ax && ay >= az) face = dir.y > 0 ? 2 : 3;
            else                            face = dir.z > 0 ? 4 : 5;

            Vector3[] Normals = {
                new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
                new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
                new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
            };
            Vector3[] UAxes = {
                new Vector3( 0, 0,-1), new Vector3( 0, 0, 1),
                new Vector3( 1, 0, 0), new Vector3( 1, 0, 0),
                new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
            };
            Vector3[] VAxes = {
                new Vector3(0,-1, 0), new Vector3(0,-1, 0),
                new Vector3(0, 0, 1), new Vector3(0, 0,-1),
                new Vector3(0,-1, 0), new Vector3(0,-1, 0),
            };

            float denom = Vector3.Dot(dir, Normals[face]);
            if (Mathf.Abs(denom) < 1e-6f) denom = (denom < 0f) ? -1e-6f : 1e-6f;
            Vector3 raw = dir / denom;

            float su = Vector3.Dot(raw, UAxes[face]);
            float sv = Vector3.Dot(raw, VAxes[face]);

            u = Mathf.Clamp01((su + 1f) * 0.5f);

            if (face == 2 || face == 3)
                v = Mathf.Clamp01((sv + 1f) * 0.5f);
            else
                v = Mathf.Clamp01((1f - sv) * 0.5f);
        }

        public static Color SampleCubemapBilinear(Color[][] facePixels, int size, Vector3 dir)
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

        // ============================================================
        // 镜面高光代理预览（供 Shell 调用）
        // ============================================================

        public List<Light> CollectSpecularLights(EnvTimeNode node)
        {
            return SpecularLightBakeScope.CollectLights(node);
        }

        public GameObject CreateSpecularSphere(Vector3 pos, float radius,
            Color emissiveColor, string name)
        {
            return SpecularLightBakeScope.CreateEmissiveSphere(pos, radius, emissiveColor, name);
        }

        public GameObject CreateSpecularPanel(Vector3 pos, Quaternion rot,
            Vector3 scale, Color emissiveColor, string name, Texture cookie = null)
        {
            return SpecularLightBakeScope.CreateEmissivePanel(pos, rot, scale, emissiveColor, name, cookie);
        }

        public GameObject CreateSpecularDisc(Vector3 pos, Quaternion rot,
            float radius, Color emissiveColor, string name)
        {
            return SpecularLightBakeScope.CreateEmissiveDisc(pos, rot, radius, emissiveColor, name);
        }
    }
}
