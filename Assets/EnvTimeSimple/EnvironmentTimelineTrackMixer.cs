//EnvironmentTimelineTrackMixer.cs
using UnityEngine;
using UnityEngine.Playables;

namespace BYTools.EnvTimelineSimple
{
    public class EnvironmentTimelineTrackMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, UnityEngine.Playables.FrameData info, object playerData)
        {
            var controller = playerData as EnvironmentTimelineController;
            if (controller == null) return;

            int inputCount = playable.GetInputCount();
            
            // 支持多个 Clip 混合（可选）
            float totalWeight = 0f;
            float blendedTime = 0f;

            for (int i = 0; i < inputCount; i++)
            {
                float weight = playable.GetInputWeight(i);
                if (weight > 0.0001f)
                {
                    var inputPlayable = (ScriptPlayable<EnvironmentTimelinePlayableBehaviour>)playable.GetInput(i);
                    var behaviour = inputPlayable.GetBehaviour();

                    if (behaviour != null && behaviour.timelineAsset != null)
                    {
                        float clipTime = (float)inputPlayable.GetTime();
                        float clipDuration = (float)inputPlayable.GetDuration();
                        float envTime = RemapTime(behaviour, clipTime, clipDuration);

                        blendedTime += envTime * weight;
                        totalWeight += weight;
                    }
                }
            }

            if (totalWeight > 0.0001f)
            {
                controller.currentTime = blendedTime / totalWeight;
                // ProcessFrame 会在每个 Clip 的 Behaviour 中调用 ApplyAtCurrentTime
            }
        }

        private float RemapTime(EnvironmentTimelinePlayableBehaviour behaviour, float clipTime, float clipDuration)
        {
            switch (behaviour.remapMode)
            {
                case TimeRemapMode.PercentageMap:
                    float percent = clipDuration > 0 ? Mathf.Clamp01(clipTime / clipDuration) : 0f;
                    return Mathf.Lerp(behaviour.startTime, behaviour.endTime, percent);

                case TimeRemapMode.DirectMap:
                    return clipTime;

                case TimeRemapMode.ScaledMap:
                    float scale = clipDuration > 0 ? (behaviour.endTime - behaviour.startTime) / clipDuration : 1f;
                    return behaviour.startTime + clipTime * scale;

                default:
                    return clipTime;
            }
        }
    }
}
