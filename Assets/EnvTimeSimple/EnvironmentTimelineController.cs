// EnvironmentTimelineController.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimelineSimple
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

        readonly HashSet<ReflectionProbe> _currentActiveProbes = new HashSet<ReflectionProbe>();
        readonly HashSet<ReflectionProbe> _nextActiveProbes = new HashSet<ReflectionProbe>();

        EnvTimeNode _lastFrom, _lastTo;

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

            // 恢复原始 LightProbeUsage
            foreach (var kv in _originalLightProbeUsages)
                if (kv.Key) kv.Key.lightProbeUsage = kv.Value;
            _originalLightProbeUsages.Clear();
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