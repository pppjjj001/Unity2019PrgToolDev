// EnvironmentTimelineController.cs（扩展版 - 增加 Light Probe 混合）
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimeline
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EnvironmentTimelineData))]
    public class EnvironmentTimelineController : MonoBehaviour
    {
        [Header("时间")]
        public float currentTime = 12f;
        public bool autoPlay = false;
        public float timeSpeed = 1f;

        [Header("写入选项")]
        public bool writeToRenderSettings = false;
        public bool writeToMPB = true;
        public bool writeMainCubemapToMaterial = true;
        public string envCubemapPropName = "_SpecularEnvCubemap0";

        [Header("ReflectionProbe 控制")]
        public bool controlReflectionProbes = true;

        // 🆕 Light Probe 控制
        [Header("Light Probe 控制")]
        [Tooltip("启用 Light Probe 数据混合（运行时写入 LightmapSettings.lightProbes）")]
        public bool blendLightProbes = true;
        [Tooltip("启用自定义 Renderer Light Probe 采样（不依赖 LightmapSettings，使用节点快照做 4 近邻加权并写入 MPB）")]
        public bool useCustomRendererLightProbes = false;
        [Tooltip("自定义 Light Probe 采样邻居数。4=四近邻加权")]
        [Range(1, 4)]
        public int customLightProbeNeighborCount = 4;
        [Tooltip("Probe 插值模式：InverseDistance=4近邻逆距离加权（快），Tetrahedral=Delaunay四面体重心坐标（平滑，与Unity原生一致）")]
        public ProbeInterpolationMode probeInterpolationMode = ProbeInterpolationMode.InverseDistance;
        [Tooltip("缓存每个 Renderer 的邻近 Probe index/weight。静态物体建议开启，可把采样成本从 Renderer×Probe 降到 Renderer×4")]
        public bool cacheCustomProbeWeights = true;
        [Tooltip("更新频率限制（秒）。0=每帧。建议 0.05~0.1 节省性能")]
        public float lightProbeUpdateInterval = 0f;

        [Header("Prefab 变换支持")]
        [Tooltip("Prefab 根节点。指定后，LightProbe 快照中的局部位置会跟随 Prefab 的旋转/缩放/位移自动变换，SH 系数也会相应旋转。")]
        [SerializeField]
        public Transform prefabRoot;

        private EnvironmentTimelineData _timelineData;
        public EnvironmentTimelineData timelineData
        {
            get
            {
                if (_timelineData == null)
                    _timelineData = GetComponent<EnvironmentTimelineData>();
                return _timelineData;
            }
        }

        readonly Dictionary<Renderer, MaterialPropertyBlock> _mpbCache
            = new Dictionary<Renderer, MaterialPropertyBlock>();

        readonly HashSet<ReflectionProbe> _currentActiveProbes = new HashSet<ReflectionProbe>();
        readonly HashSet<ReflectionProbe> _nextActiveProbes = new HashSet<ReflectionProbe>();

        EnvTimeNode _lastFrom, _lastTo;

        // 🆕 Light Probe 混合 - 预分配的 “栈式” 缓冲区（避免每帧 GC）
        SphericalHarmonicsL2[] _shBlendBuffer;       // 输出给 lightProbes.bakedProbes
        int _shBufferCapacity = 0;
        float _lastLightProbeUpdateTime = -999f;
        bool _lightProbeOriginalCached = false;
        SphericalHarmonicsL2[] _originalBakedProbes;  // 还原用
        readonly HashSet<Renderer> _customProbeRenderers = new HashSet<Renderer>();
        readonly Dictionary<Renderer, LightProbeUsage> _originalLightProbeUsages
            = new Dictionary<Renderer, LightProbeUsage>();
        readonly Dictionary<LightProbeSnapshot, Dictionary<Renderer, CustomProbeWeights>> _customProbeWeightCache
            = new Dictionary<LightProbeSnapshot, Dictionary<Renderer, CustomProbeWeights>>();
        readonly SphericalHarmonicsL2[] _singleProbeBuffer = new SphericalHarmonicsL2[1];

        // 四面体插值器缓存（每个 LightProbeSnapshot 对应一个 TetrahedralInterpolator）
        readonly Dictionary<LightProbeSnapshot, TetrahedralInterpolator> _tetraInterpolatorCache
            = new Dictionary<LightProbeSnapshot, TetrahedralInterpolator>();

        /// <summary>
        /// 获取指定快照的四面体插值器（如未构建则构建并缓存）
        /// </summary>
        public TetrahedralInterpolator GetTetraInterpolator(LightProbeSnapshot snap)
        {
            if (snap == null || !snap.IsValid) return null;

            TetrahedralInterpolator interp;
            if (!_tetraInterpolatorCache.TryGetValue(snap, out interp))
            {
                interp = new TetrahedralInterpolator();
                var positions = snap.SamplePositions;
                interp.Build(positions, snap.ProbeCount);
                _tetraInterpolatorCache[snap] = interp;
            }
            return interp;
        }

        /// <summary>
        /// 清除四面体插值器缓存（探针位置变化时调用）
        /// </summary>
        public void InvalidateTetraCache()
        {
            _tetraInterpolatorCache.Clear();
        }

        class CustomProbeWeights
        {
            public Vector3 samplePosition;
            public int neighborCount;
            public int i0 = -1, i1 = -1, i2 = -1, i3 = -1;
            public float w0, w1, w2, w3;
        }

        void Update()
        {
            if (timelineData == null) return;

            if (autoPlay && Application.isPlaying)
            {
                currentTime += Time.deltaTime * timeSpeed;
                if (timelineData.loop)
                    currentTime = Mathf.Repeat(currentTime, timelineData.totalDuration);
                else
                    currentTime = Mathf.Clamp(currentTime, 0f, timelineData.totalDuration);
            }

            ApplyAtCurrentTime();
        }

        public void ApplyAtCurrentTime()
        {
            if (timelineData == null || timelineData.nodes.Count == 0) return;

            EnvTimeNode from, to; float t;
            if (!timelineData.Sample(currentTime, out from, out to, out t)) return;

            SerializedSH lerpedSH = SerializedSH.Lerp(from.customSH, to.customSH, t);

            if (writeToRenderSettings && lerpedSH.IsValid)
            {
                RenderSettings.ambientProbe = lerpedSH.ToSHL2();
            }

            if (writeToMPB)
            {
                Cubemap fromCube = from.GetMainCubemap();
                Cubemap toCube   = to.GetMainCubemap();
                Cubemap mainCube = (t < 0.5f) ? (fromCube ?? toCube) : (toCube ?? fromCube);

                ApplyMPBForNode(from, lerpedSH, mainCube);
                if (to != from) ApplyMPBForNode(to, lerpedSH, mainCube);
            }

            if (controlReflectionProbes)
            {
                UpdateProbeActivation(from, to, t);
            }

            // 🆕 Light Probe 混合
            if (blendLightProbes || useCustomRendererLightProbes)
            {
                if (lightProbeUpdateInterval <= 0f ||
                    Time.realtimeSinceStartup - _lastLightProbeUpdateTime >= lightProbeUpdateInterval)
                {
                    if (blendLightProbes)
                        ApplyLightProbeBlend(from, to, t);
                    if (useCustomRendererLightProbes)
                        ApplyCustomRendererLightProbes(from, to, t);
                    _lastLightProbeUpdateTime = Time.realtimeSinceStartup;
                }
            }

            _lastFrom = from;
            _lastTo = to;
        }

        // ===========================================================
        // 🆕 Light Probe 混合 - 无 GC 实现
        // ===========================================================
        void ApplyLightProbeBlend(EnvTimeNode from, EnvTimeNode to, float t)
        {
            var lp = LightmapSettings.lightProbes;
            if (lp == null || lp.count == 0) return;

            // 缓存原始数据（用于禁用时恢复）
            if (!_lightProbeOriginalCached)
            {
                _originalBakedProbes = (SphericalHarmonicsL2[])lp.bakedProbes.Clone();
                _lightProbeOriginalCached = true;
            }

            var fromData = from.lightProbeData;
            var toData = to.lightProbeData;

            bool fromValid = fromData != null && fromData.IsValid;
            bool toValid = toData != null && toData.IsValid;

            // 都无效，直接跳过
            if (!fromValid && !toValid) return;

            int probeCount = lp.count;

            // 仅当任一节点的 probe 数量与场景匹配时才执行
            if (fromValid && fromData.ProbeCount != probeCount) fromValid = false;
            if (toValid && toData.ProbeCount != probeCount) toValid = false;
            if (!fromValid && !toValid) return;

            EnsureBufferCapacity(probeCount);

            // ★ 关键：直接覆盖 _shBlendBuffer，不分配新数组
            if (fromValid && toValid)
            {
                if (from == to || t <= 0f)
                    FillBufferFromSnapshot(fromData, probeCount);
                else if (t >= 1f)
                    FillBufferFromSnapshot(toData, probeCount);
                else
                    LerpBufferFromSnapshots(fromData, toData, t, probeCount);
            }
            else if (fromValid)
            {
                FillBufferFromSnapshot(fromData, probeCount);
            }
            else
            {
                FillBufferFromSnapshot(toData, probeCount);
            }

            // 提交（Unity 需要赋值数组才会更新）
            lp.bakedProbes = _shBlendBuffer;
        }

        void EnsureBufferCapacity(int count)
        {
            if (_shBlendBuffer == null || _shBufferCapacity < count)
            {
                _shBlendBuffer = new SphericalHarmonicsL2[count];
                _shBufferCapacity = count;
            }
        }

        /// <summary>
        /// 将快照中的 SH 系数填充到 _shBlendBuffer（无分配）
        /// </summary>
        void FillBufferFromSnapshot(LightProbeSnapshot snap, int count)
        {
            var coeffs = snap.shCoefficients;
            for (int i = 0; i < count; i++)
            {
                int baseIdx = i * 27;
                SphericalHarmonicsL2 sh = default;
                for (int c = 0; c < 3; c++)
                {
                    int rowBase = baseIdx + c * 9;
                    sh[c, 0] = coeffs[rowBase + 0];
                    sh[c, 1] = coeffs[rowBase + 1];
                    sh[c, 2] = coeffs[rowBase + 2];
                    sh[c, 3] = coeffs[rowBase + 3];
                    sh[c, 4] = coeffs[rowBase + 4];
                    sh[c, 5] = coeffs[rowBase + 5];
                    sh[c, 6] = coeffs[rowBase + 6];
                    sh[c, 7] = coeffs[rowBase + 7];
                    sh[c, 8] = coeffs[rowBase + 8];
                }
                _shBlendBuffer[i] = sh;
            }
        }

        /// <summary>
        /// 两个快照线性插值，结果写入 _shBlendBuffer（无分配，纯栈式计算）
        /// </summary>
        void LerpBufferFromSnapshots(LightProbeSnapshot a, LightProbeSnapshot b, float t, int count)
        {
            var ca = a.shCoefficients;
            var cb = b.shCoefficients;
            float invT = 1f - t;

            for (int i = 0; i < count; i++)
            {
                int baseIdx = i * 27;
                SphericalHarmonicsL2 sh = default;
                for (int c = 0; c < 3; c++)
                {
                    int rowBase = baseIdx + c * 9;
                    // 9 个系数手动展开（避免内部循环开销）
                    sh[c, 0] = ca[rowBase + 0] * invT + cb[rowBase + 0] * t;
                    sh[c, 1] = ca[rowBase + 1] * invT + cb[rowBase + 1] * t;
                    sh[c, 2] = ca[rowBase + 2] * invT + cb[rowBase + 2] * t;
                    sh[c, 3] = ca[rowBase + 3] * invT + cb[rowBase + 3] * t;
                    sh[c, 4] = ca[rowBase + 4] * invT + cb[rowBase + 4] * t;
                    sh[c, 5] = ca[rowBase + 5] * invT + cb[rowBase + 5] * t;
                    sh[c, 6] = ca[rowBase + 6] * invT + cb[rowBase + 6] * t;
                    sh[c, 7] = ca[rowBase + 7] * invT + cb[rowBase + 7] * t;
                    sh[c, 8] = ca[rowBase + 8] * invT + cb[rowBase + 8] * t;
                }
                _shBlendBuffer[i] = sh;
            }
        }

        /// <summary>
        /// 恢复原始 Light Probe 数据
        /// </summary>
        public void RestoreOriginalLightProbes()
        {
            if (!_lightProbeOriginalCached || _originalBakedProbes == null) return;
            var lp = LightmapSettings.lightProbes;
            if (lp != null && lp.count == _originalBakedProbes.Length)
            {
                lp.bakedProbes = _originalBakedProbes;
            }
        }

        void ApplyCustomRendererLightProbes(EnvTimeNode from, EnvTimeNode to, float t)
        {
            var fromData = from != null ? from.lightProbeData : null;
            var toData = to != null ? to.lightProbeData : null;
            bool fromValid = fromData != null && fromData.IsValid;
            bool toValid = toData != null && toData.IsValid;
            if (!fromValid && !toValid) return;

            _customProbeRenderers.Clear();
            CollectRenderersFromNode(from, _customProbeRenderers);
            CollectRenderersFromNode(to, _customProbeRenderers);

            foreach (var r in _customProbeRenderers)
            {
                if (!r) continue;

                // ⭐ Prefab 空间支持：将 Renderer 世界位置逆变换到快照的采样空间
                Vector3 worldPosition = r.bounds.center;
                Vector3 samplePosition = TransformSamplePosition(worldPosition);

                SphericalHarmonicsL2 fromSH = new SphericalHarmonicsL2();
                SphericalHarmonicsL2 toSH = new SphericalHarmonicsL2();
                bool hasFrom = fromValid && SampleSnapshot(fromData, r, samplePosition, out fromSH);
                bool hasTo = toValid && SampleSnapshot(toData, r, samplePosition, out toSH);
                if (!hasFrom && !hasTo) continue;

                SphericalHarmonicsL2 result;
                if (hasFrom && hasTo)
                    result = LerpSH(fromSH, toSH, t);
                else
                    result = hasFrom ? fromSH : toSH;

                // ⭐ Prefab 空间支持：旋转 SH 系数以匹配当前 Prefab 朝向
                result = RotateSampledSH(result);

                MaterialPropertyBlock mpb;
                if (!_mpbCache.TryGetValue(r, out mpb))
                {
                    mpb = new MaterialPropertyBlock();
                    _mpbCache[r] = mpb;
                }

                if (!_originalLightProbeUsages.ContainsKey(r))
                    _originalLightProbeUsages.Add(r, r.lightProbeUsage);

                if (r.lightProbeUsage != LightProbeUsage.CustomProvided)
                    r.lightProbeUsage = LightProbeUsage.CustomProvided;

                r.GetPropertyBlock(mpb);
                _singleProbeBuffer[0] = result;
                mpb.CopySHCoefficientArraysFrom(_singleProbeBuffer);
                r.SetPropertyBlock(mpb);
            }
        }

        /// <summary>
        /// ⭐ 将世界空间位置变换到快照的采样空间。
        /// - usePrefabSpace=true：逆变换到 Prefab 局部空间，与 localPositions 对齐
        /// - usePrefabSpace=false：直接使用世界空间，与旧版 positions 对齐
        /// </summary>
        Vector3 TransformSamplePosition(Vector3 worldPosition)
        {
            // 检查是否有任何快照使用了 Prefab 空间
            bool anyPrefabSpace = false;
            if (timelineData != null)
            {
                foreach (var node in timelineData.nodes)
                {
                    if (node.lightProbeData != null && node.lightProbeData.usePrefabSpace)
                    {
                        anyPrefabSpace = true;
                        break;
                    }
                }
            }

            if (!anyPrefabSpace || prefabRoot == null)
                return worldPosition;

            return prefabRoot.InverseTransformPoint(worldPosition);
        }

        /// <summary>
        /// ⭐ 旋转采样得到的 SH，使其匹配当前 Prefab 朝向。
        /// 当快照使用 Prefab 空间存储时，SH 系数是在烘焙时 Prefab 的局部朝向下记录的。
        /// 运行时 Prefab 旋转后，需要用 delta = currentRotation * inverse(bakeRotation) 旋转 SH。
        /// </summary>
        SphericalHarmonicsL2 RotateSampledSH(SphericalHarmonicsL2 sh)
        {
            if (prefabRoot == null)
                return sh;

            // 找到第一个使用 Prefab 空间的快照，获取其烘焙旋转
            Quaternion bakeRotation = Quaternion.identity;
            bool foundBakeRotation = false;

            if (timelineData != null)
            {
                foreach (var node in timelineData.nodes)
                {
                    if (node.lightProbeData != null && node.lightProbeData.usePrefabSpace)
                    {
                        bakeRotation = node.lightProbeData.bakeSpaceTransform.rotation;
                        foundBakeRotation = true;
                        break;
                    }
                }
            }

            if (!foundBakeRotation)
                return sh;

            Matrix4x4 deltaRotation = SHRotation.BuildDeltaRotation(bakeRotation, prefabRoot.rotation);
            return SHRotation.RotateSH(sh, deltaRotation);
        }

        bool SampleSnapshot(LightProbeSnapshot snap, Renderer renderer, Vector3 position, out SphericalHarmonicsL2 result)
        {
            if (!cacheCustomProbeWeights)
                return SampleSnapshotNearest(snap, position, out result);

            result = default;
            CustomProbeWeights weights = GetCachedProbeWeights(snap, renderer, position);
            if (weights == null || weights.i0 < 0) return false;

            AccumulateSnapshotSH(snap, weights.i0, weights.w0, ref result);
            if (weights.i1 >= 0 && weights.w1 > 0f) AccumulateSnapshotSH(snap, weights.i1, weights.w1, ref result);
            if (weights.i2 >= 0 && weights.w2 > 0f) AccumulateSnapshotSH(snap, weights.i2, weights.w2, ref result);
            if (weights.i3 >= 0 && weights.w3 > 0f) AccumulateSnapshotSH(snap, weights.i3, weights.w3, ref result);
            return true;
        }

        CustomProbeWeights GetCachedProbeWeights(LightProbeSnapshot snap, Renderer renderer, Vector3 position)
        {
            if (snap == null || !snap.IsValid || renderer == null) return null;

            Dictionary<Renderer, CustomProbeWeights> rendererCache;
            if (!_customProbeWeightCache.TryGetValue(snap, out rendererCache))
            {
                rendererCache = new Dictionary<Renderer, CustomProbeWeights>();
                _customProbeWeightCache.Add(snap, rendererCache);
            }

            int neighborCount = Mathf.Clamp(customLightProbeNeighborCount, 1, 4);
            CustomProbeWeights weights;
            if (!rendererCache.TryGetValue(renderer, out weights))
            {
                weights = new CustomProbeWeights();
                rendererCache.Add(renderer, weights);
                BuildProbeWeights(snap, position, neighborCount, weights);
                return weights;
            }

            if (weights.neighborCount != neighborCount ||
                (weights.samplePosition - position).sqrMagnitude > 0.0001f)
            {
                BuildProbeWeights(snap, position, neighborCount, weights);
            }

            return weights;
        }

        void BuildProbeWeights(LightProbeSnapshot snap, Vector3 position, int neighborCount, CustomProbeWeights weights)
        {
            weights.samplePosition = position;
            weights.neighborCount = neighborCount;
            weights.i0 = weights.i1 = weights.i2 = weights.i3 = -1;
            weights.w0 = weights.w1 = weights.w2 = weights.w3 = 0f;

            // 四面体插值模式
            if (probeInterpolationMode == ProbeInterpolationMode.Tetrahedral)
            {
                var interp = GetTetraInterpolator(snap);
                if (interp != null && interp.IsBuilt)
                {
                    var tetraResult = interp.Sample(position);
                    if (tetraResult.valid)
                    {
                        weights.i0 = tetraResult.i0; weights.w0 = tetraResult.w0;
                        weights.i1 = tetraResult.i1; weights.w1 = tetraResult.w1;
                        weights.i2 = tetraResult.i2; weights.w2 = tetraResult.w2;
                        weights.i3 = tetraResult.i3; weights.w3 = tetraResult.w3;
                        return;
                    }
                }
                // 四面体化失败或点在凸包外回退到 IDW
            }

            int count = snap.ProbeCount;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;
            var positions = snap.SamplePositions;

            for (int i = 0; i < count; i++)
            {
                float d = (positions[i] - position).sqrMagnitude;
                if (d <= 0.000001f)
                {
                    weights.i0 = i;
                    weights.w0 = 1f;
                    return;
                }

                if (d < d0)
                {
                    d3 = d2; weights.i3 = weights.i2;
                    d2 = d1; weights.i2 = weights.i1;
                    d1 = d0; weights.i1 = weights.i0;
                    d0 = d; weights.i0 = i;
                }
                else if (neighborCount > 1 && d < d1)
                {
                    d3 = d2; weights.i3 = weights.i2;
                    d2 = d1; weights.i2 = weights.i1;
                    d1 = d; weights.i1 = i;
                }
                else if (neighborCount > 2 && d < d2)
                {
                    d3 = d2; weights.i3 = weights.i2;
                    d2 = d; weights.i2 = i;
                }
                else if (neighborCount > 3 && d < d3)
                {
                    d3 = d; weights.i3 = i;
                }
            }

            float w0 = weights.i0 >= 0 ? 1f / Mathf.Max(d0, 0.000001f) : 0f;
            float w1 = weights.i1 >= 0 && neighborCount > 1 ? 1f / Mathf.Max(d1, 0.000001f) : 0f;
            float w2 = weights.i2 >= 0 && neighborCount > 2 ? 1f / Mathf.Max(d2, 0.000001f) : 0f;
            float w3 = weights.i3 >= 0 && neighborCount > 3 ? 1f / Mathf.Max(d3, 0.000001f) : 0f;
            float weightSum = w0 + w1 + w2 + w3;
            if (weightSum <= 0f) return;

            weights.w0 = w0 / weightSum;
            weights.w1 = w1 / weightSum;
            weights.w2 = w2 / weightSum;
            weights.w3 = w3 / weightSum;
        }

        bool SampleSnapshotNearest(LightProbeSnapshot snap, Vector3 position, out SphericalHarmonicsL2 result)
        {
            result = default;
            if (snap == null || !snap.IsValid) return false;

            // 四面体插值模式
            if (probeInterpolationMode == ProbeInterpolationMode.Tetrahedral)
            {
                var interp = GetTetraInterpolator(snap);
                if (interp != null && interp.IsBuilt)
                {
                    var tetraResult = interp.Sample(position);
                    if (tetraResult.valid)
                    {
                        AccumulateSnapshotSH(snap, tetraResult.i0, tetraResult.w0, ref result);
                        if (tetraResult.i1 >= 0 && tetraResult.w1 > 0f)
                            AccumulateSnapshotSH(snap, tetraResult.i1, tetraResult.w1, ref result);
                        if (tetraResult.i2 >= 0 && tetraResult.w2 > 0f)
                            AccumulateSnapshotSH(snap, tetraResult.i2, tetraResult.w2, ref result);
                        if (tetraResult.i3 >= 0 && tetraResult.w3 > 0f)
                            AccumulateSnapshotSH(snap, tetraResult.i3, tetraResult.w3, ref result);
                        return true;
                    }
                }
                // 回退到 IDW
            }

            int count = snap.ProbeCount;
            int neighborCount = Mathf.Clamp(customLightProbeNeighborCount, 1, 4);

            int i0 = -1, i1 = -1, i2 = -1, i3 = -1;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;
            var positions = snap.SamplePositions;

            for (int i = 0; i < count; i++)
            {
                float d = (positions[i] - position).sqrMagnitude;
                if (d <= 0.000001f)
                {
                    ReadSnapshotSH(snap, i, out result);
                    return true;
                }

                if (d < d0)
                {
                    d3 = d2; i3 = i2;
                    d2 = d1; i2 = i1;
                    d1 = d0; i1 = i0;
                    d0 = d; i0 = i;
                }
                else if (neighborCount > 1 && d < d1)
                {
                    d3 = d2; i3 = i2;
                    d2 = d1; i2 = i1;
                    d1 = d; i1 = i;
                }
                else if (neighborCount > 2 && d < d2)
                {
                    d3 = d2; i3 = i2;
                    d2 = d; i2 = i;
                }
                else if (neighborCount > 3 && d < d3)
                {
                    d3 = d; i3 = i;
                }
            }

            float w0 = i0 >= 0 ? 1f / Mathf.Max(d0, 0.000001f) : 0f;
            float w1 = i1 >= 0 && neighborCount > 1 ? 1f / Mathf.Max(d1, 0.000001f) : 0f;
            float w2 = i2 >= 0 && neighborCount > 2 ? 1f / Mathf.Max(d2, 0.000001f) : 0f;
            float w3 = i3 >= 0 && neighborCount > 3 ? 1f / Mathf.Max(d3, 0.000001f) : 0f;
            float weightSum = w0 + w1 + w2 + w3;
            if (weightSum <= 0f) return false;

            AccumulateSnapshotSH(snap, i0, w0 / weightSum, ref result);
            if (w1 > 0f) AccumulateSnapshotSH(snap, i1, w1 / weightSum, ref result);
            if (w2 > 0f) AccumulateSnapshotSH(snap, i2, w2 / weightSum, ref result);
            if (w3 > 0f) AccumulateSnapshotSH(snap, i3, w3 / weightSum, ref result);
            return true;
        }

        static void ReadSnapshotSH(LightProbeSnapshot snap, int probeIndex, out SphericalHarmonicsL2 sh)
        {
            sh = default;
            AccumulateSnapshotSH(snap, probeIndex, 1f, ref sh);
        }

        static void AccumulateSnapshotSH(LightProbeSnapshot snap, int probeIndex, float weight, ref SphericalHarmonicsL2 sh)
        {
            if (probeIndex < 0) return;

            var coeffs = snap.shCoefficients;
            int baseIdx = probeIndex * 27;
            for (int c = 0; c < 3; c++)
            {
                int rowBase = baseIdx + c * 9;
                sh[c, 0] += coeffs[rowBase + 0] * weight;
                sh[c, 1] += coeffs[rowBase + 1] * weight;
                sh[c, 2] += coeffs[rowBase + 2] * weight;
                sh[c, 3] += coeffs[rowBase + 3] * weight;
                sh[c, 4] += coeffs[rowBase + 4] * weight;
                sh[c, 5] += coeffs[rowBase + 5] * weight;
                sh[c, 6] += coeffs[rowBase + 6] * weight;
                sh[c, 7] += coeffs[rowBase + 7] * weight;
                sh[c, 8] += coeffs[rowBase + 8] * weight;
            }
        }

        static SphericalHarmonicsL2 LerpSH(SphericalHarmonicsL2 a, SphericalHarmonicsL2 b, float t)
        {
            SphericalHarmonicsL2 result = default;
            float invT = 1f - t;
            for (int c = 0; c < 3; c++)
            {
                result[c, 0] = a[c, 0] * invT + b[c, 0] * t;
                result[c, 1] = a[c, 1] * invT + b[c, 1] * t;
                result[c, 2] = a[c, 2] * invT + b[c, 2] * t;
                result[c, 3] = a[c, 3] * invT + b[c, 3] * t;
                result[c, 4] = a[c, 4] * invT + b[c, 4] * t;
                result[c, 5] = a[c, 5] * invT + b[c, 5] * t;
                result[c, 6] = a[c, 6] * invT + b[c, 6] * t;
                result[c, 7] = a[c, 7] * invT + b[c, 7] * t;
                result[c, 8] = a[c, 8] * invT + b[c, 8] * t;
            }
            return result;
        }

        // ===========================================================
        // 既有逻辑
        // ===========================================================
        void ApplyMPBForNode(EnvTimeNode node, SerializedSH lerpedSH, Cubemap mainCube)
        {
            foreach (var go in node.affectedTargets)
            {
                if (!go) continue;
                Renderer[] renderers = node.includeChildren
                    ? go.GetComponentsInChildren<Renderer>(true)
                    : go.GetComponents<Renderer>();

                foreach (var r in renderers)
                {
                    if (!r) continue;

                    MaterialPropertyBlock mpb;
                    if (!_mpbCache.TryGetValue(r, out mpb))
                    {
                        mpb = new MaterialPropertyBlock();
                        _mpbCache[r] = mpb;
                    }
                    r.GetPropertyBlock(mpb);

                    lerpedSH.ApplyToMPB(mpb, 1f);

                    if (writeMainCubemapToMaterial && mainCube != null
                        && !string.IsNullOrEmpty(envCubemapPropName))
                    {
                        mpb.SetTexture(envCubemapPropName, mainCube);
                    }

                    r.SetPropertyBlock(mpb);
                }
            }
        }

        void UpdateProbeActivation(EnvTimeNode from, EnvTimeNode to, float t)
        {
            // shader 不支持反射球融合，不需要同时打开两边的探针。
            // 过渡中 t < 0.5 时保持 from 的探针，越过 50% 后切换为 to 的探针。
            EnvTimeNode activeNode = (from == to || t < 0.5f) ? from : to;

            _nextActiveProbes.Clear();
            // shader 不支持反射球融合，同一时间只激活一个 mainProbe
            if (activeNode != null && activeNode.mainProbe)
                _nextActiveProbes.Add(activeNode.mainProbe);

            foreach (var p in _currentActiveProbes)
            {
                if (p == null) continue;
                if (!_nextActiveProbes.Contains(p))
                    SetProbeEnabled(p, false);
            }

            foreach (var p in _nextActiveProbes)
            {
                if (p == null) continue;
                SetProbeEnabled(p, true);
            }

            _currentActiveProbes.Clear();
            foreach (var p in _nextActiveProbes) _currentActiveProbes.Add(p);
        }

        static void CollectProbesFromNode(EnvTimeNode node, HashSet<ReflectionProbe> set)
        {
            if (node == null) return;
            if (node.mainProbe) set.Add(node.mainProbe);
            foreach (var p in node.additionalProbes)
                if (p) set.Add(p);
        }

        static void CollectRenderersFromNode(EnvTimeNode node, HashSet<Renderer> set)
        {
            if (node == null) return;
            foreach (var go in node.affectedTargets)
            {
                if (!go) continue;
                Renderer[] renderers = node.includeChildren
                    ? go.GetComponentsInChildren<Renderer>(true)
                    : go.GetComponents<Renderer>();

                foreach (var r in renderers)
                    if (r) set.Add(r);
            }
        }

        static void SetProbeEnabled(ReflectionProbe p, bool enabled)
        {
            if (p == null) return;
            if (p.enabled != enabled) p.enabled = enabled;
            if (p.gameObject.activeSelf != enabled) p.gameObject.SetActive(enabled);
        }

        public void JumpToNode(int index)
        {
            if (timelineData == null || index < 0 || index >= timelineData.nodes.Count) return;
            currentTime = timelineData.nodes[index].time;
            ApplyAtCurrentTime();
        }

        public void JumpToNode(string nodeName)
        {
            if (timelineData == null) return;
            var node = timelineData.nodes.Find(n => n.nodeName == nodeName);
            if (node != null)
            {
                currentTime = node.time;
                ApplyAtCurrentTime();
            }
        }

        public void ClearAllMPB()
        {
            foreach (var kv in _mpbCache)
                if (kv.Key) kv.Key.SetPropertyBlock(null);
            _mpbCache.Clear();

            foreach (var kv in _originalLightProbeUsages)
                if (kv.Key) kv.Key.lightProbeUsage = kv.Value;
            _originalLightProbeUsages.Clear();
            _customProbeWeightCache.Clear();
            _tetraInterpolatorCache.Clear();
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
            {
                ClearAllMPB();
                RestoreOriginalLightProbes();
            }
        }
    }
}
