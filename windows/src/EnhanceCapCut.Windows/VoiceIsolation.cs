using EnhanceCapCut.Draft;
using EnhanceCapCut.Subtitles;

namespace EnhanceCapCut.Windows;

/// <summary>Loads the voice isolation model for an export, downloading it (8.6 MB) the first time a clip needs it.</summary>
public static class VoiceIsolation
{
    /// <summary>The isolator, or null when no clip needs one or the model can't be had (then a warning is added).</summary>
    public static VoiceIsolator? Load(Timeline timeline, List<string> warnings, CancellationToken cancel = default)
    {
        if (!timeline.AudioClips.Any(c => Render.VoiceProcessor.NeedsIsolation(c.Voice))) return null;
        try
        {
            ModelStore.EnsureAsync([ModelStore.VoiceIsolation], cancel: cancel).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            warnings.Add($"Couldn't download the voice isolation model: {e.Message}");
            return null;
        }
        return VoiceIsolator.Load();
    }
}
