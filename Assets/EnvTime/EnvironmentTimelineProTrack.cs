//EnvironmentTimelineTrack.cs
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BYTools.EnvTimeline
{
    [TrackColor(0.3f, 0.7f, 0.9f)]
    [TrackClipType(typeof(EnvironmentTimelineProPlayableAsset))]
    [TrackBindingType(typeof(EnvironmentTimelineProController))]
    public class EnvironmentTimelineProTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            var mixer = ScriptPlayable<EnvironmentTimelineProTrackMixer>.Create(graph, inputCount);
            return mixer;
        }
    }
}
