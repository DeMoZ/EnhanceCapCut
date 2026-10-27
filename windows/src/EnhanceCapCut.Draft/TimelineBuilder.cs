using System.Globalization;
using static EnhanceCapCut.Draft.Localization;

namespace EnhanceCapCut.Draft;

public static class TimelineBuilder
{
    /// <summary>Builds <paramref name="timeline"/> of the project, or the root draft (the main timeline) when it's null.</summary>
    public static Timeline Build(CapCutProject project, ProjectTimeline? timeline = null) =>
        Build(project.LoadDraft(timeline), project.Folder);

    public static Timeline Build(Draft draft, string projectFolder) => new Builder(projectFolder).Build(draft, 0);

    sealed class Builder(string projectFolder)
    {
        readonly Dictionary<string, Dictionary<string, int>> unsupported = [];
        readonly HashSet<string> missing = [];
        /// <summary>Nested drafts of compound clips, built once per combination material.</summary>
        readonly Dictionary<string, Timeline> nestedTimelines = [];
        readonly Dictionary<string, CombinationMaterial> combos = [];
        /// <summary>Voice filter preset directories by material path and resource id (null: not usable).</summary>
        readonly Dictionary<string, string?> filterDirectories = [];

        static Dictionary<string, T> ById<T>(IEnumerable<T> items, Func<T, string> id)
        {
            var result = new Dictionary<string, T>();
            foreach (var item in items) result.TryAdd(id(item), item);
            return result;
        }

        static T? First<T>(IEnumerable<string> refs, Dictionary<string, T> table) where T : class =>
            refs.Select(r => table.GetValueOrDefault(r)).FirstOrDefault(m => m is not null);

        public Timeline Build(Draft draft, int depth)
        {
            var m = draft.Materials;
            var videos = ById(m.Videos, v => v.Id);
            var audios = ById(m.Audios, a => a.Id);
            var texts = ById(m.Texts, t => t.Id);
            var canvases = ById(m.Canvases, c => c.Id);
            var speeds = ById(m.Speeds, s => s.Id);
            var fades = ById(m.AudioFades, f => f.Id);
            // Nested drafts may reference combinations stored in an outer draft.
            foreach (var combo in m.Drafts) combos.TryAdd(combo.Id, combo);
            var loudness = ById(m.Loudnesses, l => l.Id);
            var separations = ById(m.VocalSeparations, s => s.Id);
            var denoises = ById(m.RealtimeDenoises, d => d.Id);
            var enhancements = ById(m.VocalBeautifys, e => e.Id);
            var audioEffects = ById(m.AudioEffects, e => e.Id);
            var generic = new Dictionary<string, (string Key, GenericMaterial Item)>();
            foreach (var (key, items) in m.All)
            {
                foreach (var item in items) generic[item.Id] = (key, item);
            }

            var mainTrack = draft.Tracks.FindIndex(t => t.Type == "video");
            var layers = new List<(VisualLayer Layer, int Order)>();
            var clips = new List<AudioClip>();
            var order = 0;

            for (var trackIndex = 0; trackIndex < draft.Tracks.Count; trackIndex++)
            {
                var track = draft.Tracks[trackIndex];
                foreach (var segment in track.Segments)
                {
                    order++;
                    var refs = segment.ExtraMaterialRefs;
                    var speed = First(refs, speeds);
                    var fade = First(refs, fades);
                    var gain = Math.Pow(10, (First(refs, loudness)?.GainDB ?? 0) / 20);
                    var timeMap = MakeTimeMap(segment, speed);
                    var voice = new VoiceEffects();
                    if (First(refs, separations) is { } separation)
                    {
                        var mode = Enum.IsDefined(typeof(VoiceEffects.SeparationMode), separation.Choice)
                            ? (VoiceEffects.SeparationMode)separation.Choice : VoiceEffects.SeparationMode.None;
                        voice = voice with { Separation = mode };
                        if (mode == VoiceEffects.SeparationMode.None && separation.Choice != 0)
                            Note(L("Audio"), $"vocal separation mode {separation.Choice}");
                    }
                    if (First(refs, denoises) is { IsDenoise: true } denoise)
                        voice = voice with { Denoise = Math.Clamp(denoise.Rate, 0, 1) };
                    if (First(refs, enhancements) is { Enable: true } enhance)
                        voice = voice with { Enhance = Math.Clamp(enhance.AmbientSoundLevel / 100, 0, 1) };
                    // A voice filter we can't run (preset not cached, unknown processor) is reported instead.
                    string? filterIssue = null;
                    if (First(refs, audioEffects) is { } effect)
                    {
                        if (VoiceFilterDirectory(effect) is { } directory)
                            voice = voice with { Filter = new VoiceFilter(effect.Name, directory, Math.Clamp(effect.Strength, 0, 1)) };
                        else
                            filterIssue = effect.Name.Length == 0 ? effect.ResourceID : effect.Name;
                    }
                    void ReportAll()
                    {
                        ReportRefs(refs, generic);
                        if (filterIssue is not null) Note(L("Audio effect"), filterIssue);
                    }
                    if (track.Type != "audio") ReportAll();

                    switch (track.Type)
                    {
                        case "video":
                        {
                            if (track.IsHidden || !segment.Visible) continue;
                            var isMain = trackIndex == mainTrack;
                            var video = videos.GetValueOrDefault(segment.MaterialId);
                            VisualContent? content = null;
                            string? audioPath = null;
                            IReadOnlyList<AudioClip> compoundAudio = [];

                            if (refs.Select(r => combos.GetValueOrDefault(r)).FirstOrDefault(c => c is not null) is { } combo
                                && NestedTimeline(combo, depth) is { } nested)
                            {
                                content = new CompoundContent(nested);
                                compoundAudio = nested.AudioClips;
                            }
                            else if (video is not null)
                            {
                                var path = video.Path;
                                if (segment.Reverse && video.Type != "photo")
                                {
                                    // CapCut renders a fully reversed copy into the project folder; the segment's
                                    // source range is in that copy's time base.
                                    if (DraftLoader.ResolvePath(video.ReversePath, projectFolder) is { } reversed && File.Exists(reversed))
                                    {
                                        path = video.ReversePath;
                                    }
                                    else
                                    {
                                        Note(L("Reverse playback"), L("reversed clip without CapCut's reversed copy (rendered forward)"));
                                        timeMap = Mirrored(timeMap, video.Duration);
                                    }
                                }
                                if (DraftLoader.ResolvePath(path, projectFolder) is { } file && File.Exists(file))
                                {
                                    content = video.Type == "photo" ? new PhotoContent(file) : new VideoContent(file);
                                    if (video.Type != "photo" && video.HasAudio && !video.HasSoundSeparated) audioPath = file;
                                }
                                else
                                {
                                    missing.Add(video.Path.Length == 0 ? video.Name : video.Path);
                                }
                            }
                            if (content is null) continue;
                            ReportKeyframes(segment);
                            if (segment.IsLoop) Note(L("Playback"), L("loop"));
                            if (video is not null) ReportVideo(video);
                            if (isMain && First(refs, canvases) is { } canvas
                                && canvas.Type is not ("canvas_color" or "canvas_blur" or "canvas_image"))
                            {
                                Note(L("Canvas"), canvas.Type);
                            }

                            var layer = new VisualLayer(segment.Id, content, trackIndex, segment.RenderIndex, isMain,
                                segment.TargetTimerange, timeMap, video?.Crop ?? new Crop(), segment.Clip ?? new Clip(),
                                segment.UniformScale, Keyframes(segment), isMain ? First(refs, canvases) : null);
                            layers.Add((layer, order));

                            var muted = track.IsMuted || (isMain && draft.Config.VideoMute) || segment.Volume <= 0;
                            if (!muted && audioPath is not null)
                            {
                                clips.Add(new AudioClip(audioPath, segment.TargetTimerange, timeMap, segment.Volume * gain,
                                    fade?.FadeIn ?? 0, fade?.FadeOut ?? 0, !segment.IsToneModify)
                                    { Voice = voice, VolumeEnvelope = VolumeEnvelope(segment) });
                            }
                            // A compound whose sound was detached plays it from the audio track instead.
                            if (!muted && compoundAudio.Count > 0 && video?.HasSoundSeparated != true)
                                clips.AddRange(NestAudio(compoundAudio, segment, timeMap, segment.Volume * gain,
                                    VolumeEnvelope(segment), voice));
                            break;
                        }
                        case "audio":
                        {
                            if (track.IsMuted || segment.Volume <= 0) continue;
                            ReportAll();
                            ReportKeyframes(segment);
                            if (refs.Select(r => combos.GetValueOrDefault(r)).FirstOrDefault(c => c is not null) is { } combo
                                && NestedTimeline(combo, depth) is { } nested)
                            {
                                // Audio detached from a compound clip.
                                clips.AddRange(NestAudio(nested.AudioClips, segment, timeMap, segment.Volume * gain,
                                    VolumeEnvelope(segment), voice));
                                continue;
                            }
                            if (audios.GetValueOrDefault(segment.MaterialId) is not { } audio) continue;
                            if (DraftLoader.ResolvePath(audio.Path, projectFolder) is not { } file || !File.Exists(file))
                            {
                                missing.Add(audio.Path.Length == 0 ? audio.Name : audio.Path);
                                continue;
                            }
                            clips.Add(new AudioClip(file, segment.TargetTimerange, timeMap, segment.Volume * gain,
                                fade?.FadeIn ?? 0, fade?.FadeOut ?? 0, !segment.IsToneModify)
                                { Voice = voice, VolumeEnvelope = VolumeEnvelope(segment) });
                            break;
                        }
                        case "text":
                        {
                            if (track.IsHidden || !segment.Visible || texts.GetValueOrDefault(segment.MaterialId) is not { } text
                                || TextContent.Parse(text.Content) is not { } parsed || string.IsNullOrWhiteSpace(parsed.Text)) continue;
                            if (text.Typesetting != 0) Note(L("Text"), L("vertical typesetting (rendered horizontally)"));
                            if (text.CurveEnabled) Note(L("Text"), L("curved text (rendered straight)"));
                            if (parsed.Styles.Any(s => s.Strokes.Count > 1)) Note(L("Text"), L("multiple outlines (first one rendered)"));
                            if (parsed.Styles.Any(s => !string.IsNullOrEmpty(s.EffectStylePath))
                                && !refs.Any(r => generic.TryGetValue(r, out var g) && g.Item.Type == "text_effect"))
                            {
                                Note(L("Text effect"), L("word art"));
                            }
                            ReportKeyframes(segment);
                            var layer = new VisualLayer(segment.Id, new TextContentLayer(new TextLayerSpec(text, parsed)),
                                trackIndex, segment.RenderIndex, false, segment.TargetTimerange, timeMap, new Crop(),
                                segment.Clip ?? new Clip(), segment.UniformScale, Keyframes(segment), null);
                            layers.Add((layer, order));
                            break;
                        }
                        default:
                            // Stickers, effects and filters on their own tracks.
                            if (track.IsHidden) continue;
                            if (generic.TryGetValue(segment.MaterialId, out var g))
                                Report(g.Key, g.Item);
                            else
                                Note(Capitalized(track.Type), "segment");
                            break;
                    }
                }
            }

            var sorted = layers.OrderBy(l => l.Layer.RenderIndex).ThenBy(l => l.Layer.TrackIndex).ThenBy(l => l.Order)
                .Select(l => l.Layer).ToList();

            var end = Math.Max(draft.Duration, Math.Max(sorted.Select(l => l.Target.End).DefaultIfEmpty(0).Max(),
                clips.Select(c => c.Target.End).DefaultIfEmpty(0).Max()));
            var report = new RenderReport
            {
                Unsupported = unsupported
                    .SelectMany(category => category.Value.Select(n => new RenderReport.Item(category.Key, n.Key, n.Value)))
                    .OrderBy(i => i.Category, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)
                    .ToList(),
                MissingFiles = missing.Order(StringComparer.Ordinal).ToList(),
            };

            return new Timeline(draft.Name, draft.Canvas.Width, draft.Canvas.Height, end, draft.Fps > 0 ? draft.Fps : 30,
                sorted, clips, report);
        }

        Timeline? NestedTimeline(CombinationMaterial combo, int depth)
        {
            if (nestedTimelines.TryGetValue(combo.Id, out var cached)) return cached;
            if (combo.Draft is not { } draft || depth >= 8) return null;
            var timeline = Build(draft, depth + 1);
            nestedTimelines[combo.Id] = timeline;
            return timeline;
        }

        static TimeMap MakeTimeMap(Segment segment, SpeedMaterial? speed)
        {
            var target = segment.TargetTimerange;
            var speedValue = speed?.Speed ?? segment.Speed;
            var source = segment.SourceTimerange ?? new TimeRange(0, Lenient.Round(target.Duration * speedValue));
            IReadOnlyList<Point2> curve = speed is { Mode: 1 } s ? s.CurvePoints : [];
            return new TimeMap(source.Start, source.Duration, target.Duration, curve);
        }

        /// <summary>Maps a range given in a reversed file's time base back onto the original media (played forward).</summary>
        static TimeMap Mirrored(TimeMap map, long mediaDuration)
        {
            if (mediaDuration <= 0) return map;
            var start = Math.Max(mediaDuration - map.SourceStart - map.SourceDuration, 0);
            return new TimeMap(start, map.SourceDuration, map.TargetDuration);
        }

        static Dictionary<LayerProperty, KeyframeTrack> Keyframes(Segment segment)
        {
            var result = new Dictionary<LayerProperty, KeyframeTrack>();
            foreach (var group in segment.Keyframes)
            {
                if (LayerProperties.FromType(group.PropertyType) is { } property && KeyframeTrack.From(group) is { } track)
                    result[property] = track;
            }
            return result;
        }

        /// <summary><c>KFTypeVolume</c> keyframes hold the segment's volume over time; as a multiplier of <c>segment.volume</c>.</summary>
        static KeyframeTrack? VolumeEnvelope(Segment segment)
        {
            if (segment.Volume <= 0) return null;
            var group = segment.Keyframes.FirstOrDefault(g => g.PropertyType == "KFTypeVolume");
            return group is null ? null : KeyframeTrack.From(group)?.Scaled(1 / segment.Volume);
        }

        /// <summary>
        /// Re-times a compound clip's audio into the parent timeline, following the parent's speed and curve.
        /// <paramref name="envelope"/> (the compound segment's volume keyframes) and <paramref name="voice"/> (its voice
        /// effects) apply on top of the nested clips' own settings.
        /// </summary>
        static List<AudioClip> NestAudio(IReadOnlyList<AudioClip> nested, Segment segment, TimeMap timeMap, double volume,
            KeyframeTrack? envelope, VoiceEffects voice)
        {
            long windowStart = timeMap.SourceStart, windowEnd = timeMap.SourceStart + timeMap.SourceDuration;
            var result = new List<AudioClip>();
            foreach (var clip in nested)
            {
                long start = Math.Max(clip.Target.Start, windowStart), end = Math.Min(clip.Target.End, windowEnd);
                if (end <= start) continue;
                long o0 = timeMap.Offset(start), o1 = timeMap.Offset(end);
                if (o1 <= o0) continue;
                TimeMap map;
                if (timeMap.IsConstant && clip.TimeMap.IsConstant)
                {
                    map = clip.TimeMap.Slice(start - clip.Target.Start, end - clip.Target.Start) with { TargetDuration = o1 - o0 };
                }
                else
                {
                    map = new TimeMap(o1 - o0, u =>
                    {
                        var nestedTime = timeMap.PreciseSourceTime(o0 + u * (o1 - o0));
                        return clip.TimeMap.PreciseSourceTime(nestedTime - clip.Target.Start);
                    });
                }
                var v = clip.Voice;
                if (voice.Separation != VoiceEffects.SeparationMode.None) v = v with { Separation = voice.Separation };
                if (voice.Denoise is { } denoise) v = v with { Denoise = denoise };
                if (voice.Enhance is { } enhance) v = v with { Enhance = enhance };
                if (voice.Filter is { } filter) v = v with { Filter = filter };
                KeyframeTrack? volumeEnvelope;
                if (clip.VolumeEnvelope is { } inner)
                {
                    // The nested curve follows the parent's speed: sample both every 50 ms.
                    volumeEnvelope = KeyframeTrack.Sampled(o1 - o0, 50_000, u =>
                        inner.Value(timeMap.SourceTime(o0 + u) - clip.Target.Start) * (envelope?.Value(o0 + u) ?? 1));
                }
                else
                {
                    volumeEnvelope = envelope?.Shifted(o0);
                }
                result.Add(clip with
                {
                    VolumeEnvelope = volumeEnvelope,
                    Target = new TimeRange(segment.TargetTimerange.Start + o0, o1 - o0),
                    TimeMap = map,
                    Volume = clip.Volume * volume,
                    Voice = v,
                    FadeIn = start == clip.Target.Start ? timeMap.Offset(start + clip.FadeIn) - o0 : 0,
                    FadeOut = end == clip.Target.End ? o1 - timeMap.Offset(end - clip.FadeOut) : 0,
                });
            }
            return result;
        }

        /// <summary>
        /// The cached preset directory for a voice filter, or null when it's missing or uses a processor
        /// <see cref="VoiceFilterGraph"/> doesn't implement.
        /// </summary>
        string? VoiceFilterDirectory(AudioEffectMaterial effect)
        {
            var key = effect.Path + "|" + effect.ResourceID;
            if (filterDirectories.TryGetValue(key, out var cached)) return cached;
            var directory = VoiceFilter.Locate(DraftLoader.ResolvePath(effect.Path, projectFolder), effect.ResourceID);
            if (directory is not null)
            {
                try { VoiceFilterGraph.Load(directory); }
                catch (VoiceFilterGraph.LoadException) { directory = null; }
            }
            filterDirectories[key] = directory;
            return directory;
        }

        // MARK: Report

        void Note(string category, string name)
        {
            if (!unsupported.TryGetValue(category, out var names)) unsupported[category] = names = [];
            names[name] = names.GetValueOrDefault(name) + 1;
        }

        void ReportRefs(IEnumerable<string> refs, Dictionary<string, (string Key, GenericMaterial Item)> generic)
        {
            foreach (var r in refs)
            {
                if (generic.TryGetValue(r, out var g)) Report(g.Key, g.Item);
            }
        }

        /// <summary>
        /// Keyframed properties the renderer doesn't animate. Mask keyframes are one entry: the mask itself
        /// is reported from its material.
        /// </summary>
        void ReportKeyframes(Segment segment)
        {
            var names = new HashSet<string>();
            foreach (var group in segment.Keyframes.Where(g => g.Keyframes.Count > 0))
            {
                var type = group.PropertyType;
                if (LayerProperties.FromType(type) is not null) continue;
                if (type.StartsWith("KFTypeCommonMask", StringComparison.Ordinal) || type.StartsWith("KFTypeMask", StringComparison.Ordinal))
                    names.Add("mask animation");
                else
                    names.Add(type.StartsWith("KFType", StringComparison.Ordinal) ? type[6..] : type);
            }
            foreach (var name in names.Order(StringComparer.Ordinal)) Note(L("Keyframes"), name);
        }

        /// <summary>Per-clip processing stored on the video material itself.</summary>
        void ReportVideo(VideoMaterial video)
        {
            if (video.Type != "video" && video.Type != "photo") Note(L("Media"), $"{video.Type} clip");
            if (video.StableLevel > 0) Note(L("Stabilization"), $"level {video.StableLevel}");
            if (video.MattingFlag != 0) Note(L("Remove background"), $"cutout mode {video.MattingFlag}");
            if (video.BeautyMode) Note(L("Beauty / makeup"), L("retouch"));
            // AI-generated images are already baked into the material's file.
            foreach (var algorithm in video.Algorithms.Distinct().Order(StringComparer.Ordinal))
            {
                if (!algorithm.StartsWith("AigcTypeGen", StringComparison.Ordinal)) Note(L("AI processing"), algorithm);
            }
        }

        static readonly HashSet<string> Ignored =
        [
            "speeds", "placeholder_infos", "sound_channel_mappings", "material_colors", "canvases", "beats",
            "loudnesses", "audio_fades", "drafts", "videos", "audios", "texts", "ai_translates", "smart_crops",
            "realtime_denoises_placeholder",
            // Rendered by the voice processing (voice filters whose preset can't be run are noted where they're read).
            "vocal_separations", "realtime_denoises", "vocal_beautifys", "audio_effects",
        ];

        void Report(string key, GenericMaterial item)
        {
            if (Ignored.Contains(key)) return;
            var name = item.Name.Length == 0 ? item.Type : item.Name;
            switch (key)
            {
                case "transitions": Note(L("Transition"), name); break;
                case "video_effects": Note(L("Video effect"), name); break;
                case "stickers": Note(L("Sticker"), name); break;
                case "common_mask" or "masks": Note(L("Mask"), name); break;
                case "hsl": Note(L("HSL color"), L("color adjustment")); break;
                case "manual_deformations": Note(L("Manual deformation"), L("body/face reshape")); break;
                case "chromas": Note(L("Chroma key"), L("chroma key")); break;
                case "material_animations":
                    foreach (var animation in item.AnimationNames) Note(L("Animation"), animation);
                    break;
                case "effects":
                    var t = item.Type;
                    if (t == "filter") Note(L("Filter"), name);
                    else if (t == "text_effect") Note(L("Text effect"), name);
                    else if (t == "text_shape") { if (item.Name.Length > 0) Note(L("Text shape"), item.Name); }
                    else if (t is "color_correct" or "smart_color_adjust") Note(L("Color adjustment"), t.Replace('_', ' '));
                    else if (t.Contains("makeup") || t.Contains("beauty") || t == "figure" || t.Contains("face"))
                        Note(L("Beauty / makeup"), name);
                    else Note(L("Effect"), name);
                    break;
                default:
                    Note(L("Other"), key + (item.Type.Length == 0 ? "" : $" ({item.Type})"));
                    break;
            }
        }

        /// <summary>Like Swift's <c>String.capitalized</c>: each word starts upper-case, the rest is lower-case.</summary>
        static string Capitalized(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }
}
