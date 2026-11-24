using System.Text;
using EnhanceCapCut.Core;

namespace EnhanceCapCut.Render.Tests;

// Test conveniences over the Rust core's records (whose generated constructors take every field), as
// CoreTestHelpers.swift on the Mac.
static class Core
{
    /// <summary>A mask geometry with the defaults the native mask tests use: centred, half the frame, no rotation or
    /// feather.</summary>
    public static MaskGeometry Geometry(double centerX = 0, double centerY = 0, double width = 0.5, double height = 0.5,
        double rotation = 0, double feather = 0, double roundCorner = 0) =>
        new(centerX, centerY, width, height, rotation, feather, roundCorner);

    /// <summary>An audio clip (<paramref name="envelope"/> and <paramref name="voice"/> off by default).</summary>
    public static AudioClip Clip(string path, TimeRange target, TimeMap map, double volume, long fadeIn, long fadeOut,
        bool preservePitch, KeyframeTrack? envelope = null, VoiceEffects? voice = null) =>
        new(path, target, map, volume, fadeIn, fadeOut, preservePitch, envelope, voice ?? Effects());

    /// <summary>Voice effects with every stage off unless given.</summary>
    public static VoiceEffects Effects(VoiceSeparation separation = VoiceSeparation.None, double? denoise = null,
        double? enhance = null, VoiceFilter? filter = null) => new(separation, denoise, enhance, filter);

    /// <summary>A timeline built by the core from draft JSON; media paths resolve against <paramref name="project"/>.</summary>
    public static Timeline Timeline(string json, string? project = null) =>
        EnhanceCapCut.Core.Timeline.FromDraft(Encoding.UTF8.GetBytes(json), project ?? Path.GetTempPath(), CoreEnvironment.Current);

    /// <summary>The whole mix of <paramref name="clips"/> (as long as the last one).</summary>
    public static float[] Mix(IAudioDecoder decoder, params AudioClip[] clips) =>
        AudioMixer.Mix(clips, clips.Max(c => c.Target.End), decoder, []);
}
