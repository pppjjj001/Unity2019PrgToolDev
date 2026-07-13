// EnvironmentTimelineController.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hotfix.Core.EnvTimelineSimple
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

        [Header("运行模式 - SkinnedMeshRenderer 材质覆盖")]
        [Tooltip("运行模式下，对 SkinnedMeshRenderer 使用材质实例直接修改（而非 MPB）。\n" +
                 "✅ 优势：不破坏 SRP Batcher（MPB 会破坏），多材质子网格全部覆盖。\n" +
                 "⚠️ 弊端：会创建材质实例副本，增加少量内存，需手动清理。\n" +
                 "编辑模式始终使用 MPB，不受此选项影响。")]
        public bool useMaterialInstanceForSkinnedMesh = false;

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
        readonly Dictionary<Renderer, LightProbeUsage> _originalLightProbeUsages
            = new Dictionary<Renderer, LightProbeUsage>();

        // 运行模式材质实例缓存（仅 SkinnedMeshRenderer）
        // Value 为材质数组，支持多子网格/多材质
        readonly Dictionary<Renderer, Material[]> _materialInstanceCache
            = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Renderer, Material[]> _originalSharedMaterials
            = new Dictionary<Renderer, Material[]>();

        readonly HashSet<ReflectionProbe> _currentActiveProbes = new HashSet<ReflectionProbe>();
        readonly HashSet<ReflectionProbe> _nextActiveProbes = new HashSet<ReflectionProbe>();

        EnvTimeNode _lastFrom, _lastTo;

        void Update()
        {
            if (timelineData == null) return;

            if (autoPlay && Application.isPlaying)
            {
                currentTime += Time.deltaTime * timeSpeed;
                if (!timelineData.holdAtEnd && timelineData.loop)
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

            // ★ BlendZone：使用 to 节点的混合区域设置来计算 SH 混合权重
            BlendZone bz = to != null ? to.blendZone : null;
            float shT = bz != null ? bz.EvaluateSHBlend(t) : t;
            float probeBlend = bz != null ? bz.EvaluateProbeBlend(t) : (t >= 0.5f ? 1f : 0f);

            SerializedSH lerpedSH = SerializedSH.Lerp(from.customSH, to.customSH, shT);

            if (writeToRenderSettings && lerpedSH.IsValid)
            {
                RenderSettings.ambientProbe = lerpedSH.ToSHL2();
            }

            if (writeToMPB)
            {
                Cubemap fromCube = from.GetMainCubemap();
                Cubemap toCube   = to.GetMainCubemap();
                // ★ Cubemap 跟随 Probe 切换点选择
                Cubemap mainCube = (probeBlend < 0.5f) ? (fromCube ?? toCube) : (toCube ?? fromCube);

                ApplyMPBForNode(from, lerpedSH, mainCube);
                if (to != from) ApplyMPBForNode(to, lerpedSH, mainCube);
            }

            if (controlReflectionProbes)
            {
                UpdateProbeActivation(from, to, t, bz);
            }

            _lastFrom = from;
            _lastTo = to;
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

                    // 缓存原始 LightProbeUsage 并切换为 CustomProvided
                    if (!_originalLightProbeUsages.ContainsKey(r))
                        _originalLightProbeUsages[r] = r.lightProbeUsage;
                    if (r.lightProbeUsage != LightProbeUsage.CustomProvided)
                        r.lightProbeUsage = LightProbeUsage.CustomProvided;

                    // 运行模式下，SkinnedMeshRenderer 可选使用材质实例直接修改
                    if (Application.isPlaying && useMaterialInstanceForSkinnedMesh
                        && r is SkinnedMeshRenderer)
                    {
                        ApplyToMaterialInstance(r, lerpedSH, mainCube);
                    }
                    else
                    {
                        // 默认 MPB 方式（编辑模式始终走此分支）
                        MaterialPropertyBlock mpb;
                        if (!_mpbCache.TryGetValue(r, out mpb))
                        {
                            mpb = new MaterialPropertyBlock();
                            _mpbCache[r] = mpb;
                        }
                        r.GetPropertyBlock(mpb);

                        lerpedSH.ApplyToMPB(mpb);

                        if (writeMainCubemapToMaterial && mainCube != null
                            && !string.IsNullOrEmpty(envCubemapPropName))
                        {
                            mpb.SetTexture(envCubemapPropName, mainCube);
                        }

                        r.SetPropertyBlock(mpb);
                    }
                }
            }
        }

        /// <summary>
        /// 对 SkinnedMeshRenderer 使用材质实例直接写入 SH 和 Cubemap。
        /// 支持多子网格/多材质：会为 sharedMaterials 中的每个材质创建副本。
        /// 首次调用时创建材质副本数组并替换到 Renderer，后续直接修改材质属性。
        /// ⚠️ 材质实例需在 ClearAllMPB / OnDisable 中手动销毁，否则内存泄漏。
        /// ✅ 使用材质实例（同 shader）不会破坏 SRP Batcher，而 MPB 会。
        /// </summary>
        void ApplyToMaterialInstance(Renderer r, SerializedSH lerpedSH, Cubemap mainCube)
        {
            Material[] matInstances;
            if (!_materialInstanceCache.TryGetValue(r, out matInstances))
            {
                // 缓存原始 sharedMaterials（用于恢复）
                Material[] originals = r.sharedMaterials;
                _originalSharedMaterials[r] = (Material[])originals.Clone();

                // 为每个子材质创建实例副本
                matInstances = new Material[originals.Length];
                for (int i = 0; i < originals.Length; i++)
                {
                    if (originals[i] == null) continue;
                    matInstances[i] = new Material(originals[i]);
                    matInstances[i].name = originals[i].name + "_EnvRuntime";
                }
                _materialInstanceCache[r] = matInstances;

                // 将实例数组赋给 Renderer
                r.sharedMaterials = matInstances;
            }

            // 向所有材质实例写入 SH 系数和 Cubemap
            for (int i = 0; i < matInstances.Length; i++)
            {
                if (matInstances[i] == null) continue;
                lerpedSH.ApplyToMaterial(matInstances[i]);

                if (writeMainCubemapToMaterial && mainCube != null
                    && !string.IsNullOrEmpty(envCubemapPropName))
                {
                    matInstances[i].SetTexture(envCubemapPropName, mainCube);
                }
            }
        }

        void UpdateProbeActivation(EnvTimeNode from, EnvTimeNode to, float t, BlendZone bz)
        {
            if (from == to)
            {
                // 同一节点：只激活 from 的 Probe
                _nextActiveProbes.Clear();
                if (from != null && from.mainProbe)
                    _nextActiveProbes.Add(from.mainProbe);
            }
            else
            {
                // ★ BlendZone：根据混合区域设置决定 Probe 切换
                float probeBlend = bz != null ? bz.EvaluateProbeBlend(t) : (t >= 0.5f ? 1f : 0f);
                bool useToProbe = bz != null ? bz.ShouldSwitchProbe(t) : (t >= 0.5f);
                float smoothW = bz != null && bz.enabled
                    ? Mathf.Clamp01(bz.probeSwitchSmoothWidth) : 0f;

                _nextActiveProbes.Clear();

                if (smoothW > 0.001f && probeBlend > 0f && probeBlend < 1f)
                {
                    // 平滑过渡期间：同时激活两个 Probe
                    if (from != null && from.mainProbe)
                        _nextActiveProbes.Add(from.mainProbe);
                    if (to != null && to.mainProbe)
                        _nextActiveProbes.Add(to.mainProbe);
                }
                else
                {
                    // 硬切换：只激活一个 Probe
                    EnvTimeNode activeNode = useToProbe ? to : from;
                    if (activeNode != null && activeNode.mainProbe)
                        _nextActiveProbes.Add(activeNode.mainProbe);
                }
            }

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
            // 清理 MPB
            foreach (var kv in _mpbCache)
                if (kv.Key) kv.Key.SetPropertyBlock(null);
            _mpbCache.Clear();

            // 清理材质实例（恢复原始 sharedMaterials 并销毁实例）
            ClearMaterialInstances();

            // 恢复原始 LightProbeUsage
            foreach (var kv in _originalLightProbeUsages)
                if (kv.Key) kv.Key.lightProbeUsage = kv.Value;
            _originalLightProbeUsages.Clear();
        }

        /// <summary>
        /// 清理所有运行时创建的材质实例，恢复 Renderer 的原始 sharedMaterials。
        /// </summary>
        public void ClearMaterialInstances()
        {
            foreach (var kv in _materialInstanceCache)
            {
                if (kv.Key && _originalSharedMaterials.TryGetValue(kv.Key, out var originals))
                    kv.Key.sharedMaterials = originals;

                if (kv.Value != null)
                {
                    for (int i = 0; i < kv.Value.Length; i++)
                    {
                        if (kv.Value[i] == null) continue;
                        if (Application.isPlaying)
                            Destroy(kv.Value[i]);
                        else
                            DestroyImmediate(kv.Value[i]);
                    }
                }
            }
            _materialInstanceCache.Clear();
            _originalSharedMaterials.Clear();
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
            {
                ClearAllMPB();
            }
        }
    }
}