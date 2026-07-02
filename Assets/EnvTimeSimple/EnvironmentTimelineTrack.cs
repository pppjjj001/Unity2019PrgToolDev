//EnvironmentTimelineTrack.cs
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BYTools.EnvTimelineSimple
{
    [TrackColor(0.3f, 0.7f, 0.9f)]
    [TrackClipType(typeof(EnvironmentTimelinePlayableAsset))]
    [TrackBindingType(typeof(EnvironmentTimelineController))]
    public class EnvironmentTimelineTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            var mixer = ScriptPlayable<EnvironmentTimelineTrackMixer>.Create(graph, inputCount);
            return mixer;
        }
    }
}
