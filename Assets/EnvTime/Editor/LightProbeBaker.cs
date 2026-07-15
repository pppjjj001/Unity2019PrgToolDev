﻿// LightProbeBaker.cs - 仅烘焙 LightProbe (Unity 2019 兼容)
#if UNITY_EDITOR
using System.Collections;
using UnityEditor;
using UnityEngine;

namespace BYTools.EnvTimeline
{
    public static class LightProbeBaker
    {
        /// <summary>
        /// 仅烘焙 LightProbe（不烘焙 Lightmap / ReflectionProbe）
        /// 通过临时修改 LightmapEditorSettings 实现
        /// </summary>
        public static bool BakeLightProbesOnly()
        {
            if (Lightmapping.isRunning)
            {
                EnvTimeDebug.LogWarning("[LightProbeBaker] 已有烘焙正在进行，请先取消");
                return false;
            }

            // 检查场景中是否有 LightProbeGroup
            var groups = Object.FindObjectsOfType<LightProbeGroup>();
            if (groups == null || groups.Length == 0)
            {
                EditorUtility.DisplayDialog("无 LightProbeGroup",
                    "场景中没有 LightProbeGroup，无法烘焙 LightProbe。\n" +
                    "请先添加 LightProbeGroup。", "确定");
                return false;
            }

            // 备份当前烘焙设置
            var backup = new LightmapBakeSettingsBackup();
            backup.Capture();

            try
            {
                // 关闭 Lightmap 烘焙：把 Lightmap 大小压到最小、关闭 AO 等
                // 在 Unity 2019 中，没有"仅 LightProbe"开关，只能通过最小化 Lightmap 工作量
                LightmapEditorSettings.maxAtlasSize = 32;     // 极小 atlas
                LightmapEditorSettings.realtimeResolution = 1f;
                LightmapEditorSettings.bakeResolution = 1f;
                LightmapEditorSettings.textureCompression = false;

                // 关闭 ReflectionProbe 自动烘焙影响
                Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;

                EnvTimeDebug.Log("[LightProbeBaker] 开始烘焙 LightProbe...");

                // 同步烘焙
                bool success = Lightmapping.Bake();

                if (success)
                {
                    EnvTimeDebug.LogColor($"LightProbe 烘焙完成，共 {(LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0)} 个 Probe", "#7CFC00");
                }
                else
                {
                    EnvTimeDebug.LogError("[LightProbeBaker] 烘焙失败");
                }

                return success;
            }
            finally
            {
                backup.Restore();
            }
        }

        /// <summary>
        /// 备份/恢复 Lightmap 烘焙设置
        /// </summary>
        class LightmapBakeSettingsBackup
        {
            int maxAtlasSize;
            float realtimeRes;
            float bakeRes;
            bool textureCompression;
            Lightmapping.GIWorkflowMode workflow;

            public void Capture()
            {
                maxAtlasSize = LightmapEditorSettings.maxAtlasSize;
                realtimeRes = LightmapEditorSettings.realtimeResolution;
                bakeRes = LightmapEditorSettings.bakeResolution;
                textureCompression = LightmapEditorSettings.textureCompression;
                workflow = Lightmapping.giWorkflowMode;
            }

            public void Restore()
            {
                LightmapEditorSettings.maxAtlasSize = maxAtlasSize;
                LightmapEditorSettings.realtimeResolution = realtimeRes;
                LightmapEditorSettings.bakeResolution = bakeRes;
                LightmapEditorSettings.textureCompression = textureCompression;
                Lightmapping.giWorkflowMode = workflow;
            }
        }
    }
}
#endif