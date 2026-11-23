using System.Globalization;
using System.Text;
using EnhanceCapCut.Draft;

// Hidden `ecc dump-timeline` / `ecc dump-writers` (not in help): the canonical dumps the golden-parity runner compares
// with the Rust core's and the macOS CLI's (openspec/changes/rust-core, design decision 11; the format is in
// docs/DEVELOPMENT.md › Golden parity and in core/ecc-golden/src/dump.rs). Removed once the apps use the Rust core.
static class Golden
{
    abstract record J
    {
        public sealed record Null : J;
        public sealed record Bool(bool Value) : J;
        public sealed record Int(long Value) : J;
        public sealed record Num(double Value) : J;
        public sealed record Str(string Value) : J;
        public sealed record Arr(IReadOnlyList<J> Items) : J;

        public void Write(StringBuilder o)
        {
            switch (this)
            {
                case Null: o.Append("null"); break;
                case Bool b: o.Append(b.Value ? "true" : "false"); break;
                case Int i: o.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
                case Num n: o.Append(n.Value.ToString("F6", CultureInfo.InvariantCulture)); break;
                case Str s: o.Append(Quoted(s.Value)); break;
                case Arr a:
                    o.Append('[');
                    for (var k = 0; k < a.Items.Count; k++)
                    {
                        if (k > 0) o.Append(',');
                        a.Items[k].Write(o);
                    }
                    o.Append(']');
                    break;
            }
        }
    }

    static string Quoted(string s)
    {
        var o = new StringBuilder("\"");
        foreach (var r in s.EnumerateRunes())
        {
            switch (r.Value)
            {
                case '"': o.Append("\\\""); break;
                case '\\': o.Append("\\\\"); break;
                case '\n': o.Append("\\n"); break;
                case '\r': o.Append("\\r"); break;
                case '\t': o.Append("\\t"); break;
                case < 0x20: o.Append("\\u").Append(r.Value.ToString("x4", CultureInfo.InvariantCulture)); break;
                default: o.Append(r.ToString()); break;
            }
        }
        return o.Append('"').ToString();
    }

    static readonly J Null = new J.Null();
    static J B(bool v) => new J.Bool(v);
    static J I(long v) => new J.Int(v);
    static J N(double v) => new J.Num(v);
    static J S(string v) => new J.Str(v);
    static J A(params J[] items) => new J.Arr(items);
    static J Nums(IEnumerable<double> v) => new J.Arr(v.Select(N).ToList());
    static J Ints(IEnumerable<long> v) => new J.Arr(v.Select(I).ToList());
    static J Opt<T>(T? v, Func<T, J> f) where T : class => v is null ? Null : f(v);
    static J Opt(double? v) => v is { } d ? N(d) : Null;

    /// <summary>Swift's case name: the C# enum name with a lower-case first letter.</summary>
    static string Swift(Enum e)
    {
        var name = e.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    static string MaskRaw(MaskProperty p) => p switch
    {
        MaskProperty.Width => "SizeWidth",
        MaskProperty.Height => "SizeHeight",
        _ => p.ToString(),
    };

    sealed class Dump(string project)
    {
        public readonly StringBuilder Out = new();

        public void Line(params (string Key, J Value)[] fields)
        {
            Out.Append('{');
            for (var k = 0; k < fields.Length; k++)
            {
                if (k > 0) Out.Append(',');
                Out.Append(Quoted(fields[k].Key)).Append(':');
                fields[k].Value.Write(Out);
            }
            Out.Append("}\n");
        }

        public J Path(string path)
        {
            var text = path.Replace('\\', '/');
            var prefix = project.Replace('\\', '/').TrimEnd('/') + "/";
            return S(text.StartsWith(prefix, StringComparison.Ordinal) ? "<project>/" + text[prefix.Length..] : text);
        }
    }

    static J Map(TimeMap m) => A(I(m.SourceStart), I(m.SourceDuration), I(m.TargetDuration), B(m.IsConstant));
    static J Range(TimeRange r) => Ints([r.Start, r.Duration]);
    static J Transform(LayerTransform t) =>
        A(N(t.ScaleX), N(t.ScaleY), N(t.Rotation), N(t.PositionX), N(t.PositionY), B(t.FlipH), B(t.FlipV), N(t.Alpha));
    static J Geometry(MaskGeometry g) => Nums([g.CenterX, g.CenterY, g.Width, g.Height, g.Rotation, g.Feather, g.RoundCorner]);
    static string F6(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    static J TextAnimationJ(TextAnimation? a)
    {
        if (a is null) return Null;
        var effect = a.Effect switch
        {
            TextAnimationEffect.Zoom z => $"zoom {F6(z.Scale)} {(z.Fades ? "true" : "false")}",
            TextAnimationEffect.Slide s => $"slide {F6(s.Dx)} {F6(s.Dy)}",
            _ => Swift2(a.Effect.GetType().Name),
        };
        return A(S(effect), I(a.Duration));
    }

    static string Swift2(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>
    /// The frame times: 0, every clip edge ±1 µs, every keyframe ±1 µs (on the timeline), each transition's start, cut
    /// and last microsecond, and 16 even steps; within 0…duration.
    /// </summary>
    static List<long> SampleTimes(Timeline t)
    {
        var times = new List<long> { 0 };
        foreach (var l in t.Layers)
        {
            foreach (var edge in new[] { l.Target.Start, l.Target.End }) times.AddRange([edge - 1, edge, edge + 1]);
            foreach (var track in l.Keyframes.Values)
            {
                foreach (var time in track.Times)
                {
                    var at = l.Target.Start + l.TimeMap.Offset(time);
                    times.AddRange([at - 1, at, at + 1]);
                }
            }
            if (l.TransitionOut is { } tr)
                times.AddRange([tr.Range.Start, tr.Range.Start + tr.Range.Duration / 2, tr.Range.End - 1]);
        }
        for (var k = 0; k <= 16; k++) times.Add(t.Duration * k / 16);
        return times.Where(x => x >= 0 && x <= t.Duration).Distinct().Order().ToList();
    }

    public static string DumpTimeline(Timeline timeline, string project)
    {
        var d = new Dump(project);
        Write(timeline, d);
        return d.Out.ToString();
    }

    static void Write(Timeline t, Dump d)
    {
        d.Line(("timeline", S(t.Name)), ("canvas", Ints([t.CanvasWidth, t.CanvasHeight])), ("fps", N(t.Fps)),
            ("duration", I(t.Duration)));
        foreach (var item in t.Report.Unsupported) d.Line(("unsupported", A(S(item.Category), S(item.Name), I(item.Count))));
        foreach (var file in t.Report.MissingFiles) d.Line(("missing", S(file)));
        for (var index = 0; index < t.Layers.Count; index++)
        {
            var l = t.Layers[index];
            var content = l.Content switch
            {
                VideoContent v => A(S("video"), d.Path(v.Path)),
                PhotoContent p => A(S("photo"), d.Path(p.Path)),
                TextContentLayer => A(S("text")),
                CompoundContent cc => A(S("compound"), I(cc.Timeline.Layers.Count)),
                StickerContent st => A(S("sticker"), S(st.Name), S(Swift(st.Clock.Style))),
                _ => Null,
            };
            var c = l.Clip;
            d.Line(("layer", I(index)), ("id", S(l.Id)), ("content", content), ("track", I(l.TrackIndex)),
                ("render", I(l.RenderIndex)), ("main", B(l.IsMainTrack)), ("target", Range(l.Target)), ("map", Map(l.TimeMap)),
                ("crop", Nums([l.Crop.UpperLeft.X, l.Crop.UpperLeft.Y, l.Crop.LowerRight.X, l.Crop.LowerRight.Y])),
                ("clip", A(N(c.Scale.X), N(c.Scale.Y), N(c.Rotation), N(c.Transform.X), N(c.Transform.Y), B(c.FlipHorizontal),
                    B(c.FlipVertical), N(c.Alpha))),
                ("uniform", B(l.UniformScale)), ("tone_maps_hdr", B(l.ToneMapsHdr)), ("retouch", B(l.RetouchesSkin)),
                ("enhance", B(l.EnhancesQuality)), ("stabilization", I(l.Stabilization)),
                ("blend", l.BlendMode is { } blend ? S(Swift(blend)) : Null),
                ("canvas", Opt(l.Canvas, cv => A(S(cv.Type), S(cv.Color), N(cv.Blur), S(cv.Image)))),
                ("cutout", Opt(l.Cutout, cu => A(d.Path(cu.Directory), B(cu.Inverted)))),
                ("chroma", Opt(l.ChromaKey, k => Nums([k.Red, k.Green, k.Blue, k.Intensity, k.Shadow, k.EdgeSmooth, k.Spill]))));
            foreach (var property in Enum.GetValues<LayerProperty>())
            {
                if (l.Keyframes.TryGetValue(property, out var track))
                {
                    d.Line(("keyframes", I(index)), ("property", S("KFType" + property)), ("times", Ints(track.Times)),
                        ("values", Nums(track.Values)), ("curved", B(track.Handles is not null)));
                }
            }
            foreach (var f in l.Filters)
                d.Line(("layer_filter", I(index)), ("name", S(f.Name)), ("luts", new J.Arr(f.Luts.Select(d.Path).ToList())),
                    ("intensity", N(f.Intensity)));
            foreach (var e in l.Effects)
                d.Line(("effect", I(index)), ("kind", S(Swift(e.Kind))), ("start", I(e.Start)), ("speed", N(e.Speed)));
            if (l.TransitionOut is { } tr)
            {
                var next = IndexOf(t.Layers, tr.Next);
                d.Line(("transition", I(index)), ("name", S(tr.Name)), ("kind", S(Swift(tr.Kind))), ("next", I(next)),
                    ("range", Range(tr.Range)));
            }
            foreach (var m in l.Masks)
            {
                var keyframed = Enum.GetValues<MaskProperty>().Where(m.Keyframes.ContainsKey).Select(p => S(MaskRaw(p))).ToList();
                d.Line(("mask", I(index)), ("kind", S(m.Shape is { } shape ? Swift(shape) : "brush")),
                    ("brush", m.BrushFolder is { } folder ? d.Path(folder) : Null), ("inverted", B(m.Inverted)),
                    ("geometry", Geometry(m.Geometry)), ("keyframed", new J.Arr(keyframed)));
            }
            foreach (var a in l.Animations)
                d.Line(("animation", I(index)), ("kind", S(Swift(a.Kind))), ("preset", S(Swift(a.Preset))),
                    ("range", Ints([a.Start, a.End])));
            if (l.Content is TextContentLayer text)
            {
                var spec = text.Spec;
                var m = spec.Material;
                var styles = spec.Content.Styles.Select(st =>
                {
                    var r = spec.Content.Utf16Range(st);
                    return A(I(r.Start), I(r.End), Opt(st.Size), st.FontPath is { } fp ? S(fp) : Null,
                        st.FillColor is { } fill ? Nums(fill) : Null, Opt(st.FillAlpha), I(st.Strokes.Count), B(st.Bold),
                        B(st.Italic), B(st.Underline));
                }).ToList();
                d.Line(("text", I(index)), ("type", S(m.Type)), ("content", S(spec.Content.Text)), ("font", S(m.FontPath)),
                    ("size", N(m.FontSize)), ("color", S(m.TextColor)), ("alignment", I(m.Alignment)),
                    ("spacing", Nums([m.LineSpacing, m.LetterSpacing, m.LineMaxWidth])),
                    ("border", A(S(m.BorderColor), N(m.BorderWidth))), ("background", A(S(m.BackgroundColor), N(m.BackgroundAlpha))),
                    ("shadow", B(m.HasShadow)), ("typesetting", I(m.Typesetting)), ("styles", new J.Arr(styles)),
                    ("intro", TextAnimationJ(spec.Animations.Intro)), ("outro", TextAnimationJ(spec.Animations.Outro)),
                    ("loop", TextAnimationJ(spec.Animations.Loop)),
                    ("glow", Opt(spec.Glow, g => new J.Arr(g.Color.Select(N).Append(N(g.Radius)).ToList()))));
            }
            if (l.Content is CompoundContent compound)
            {
                d.Line(("compound", I(index)));
                Write(compound.Timeline, d);
                d.Line(("end", I(index)));
            }
        }
        for (var k = 0; k < t.AudioClips.Count; k++)
        {
            var c = t.AudioClips[k];
            var v = c.Voice;
            d.Line(("audio", I(k)), ("path", d.Path(c.Path)), ("target", Range(c.Target)), ("map", Map(c.TimeMap)),
                ("volume", N(c.Volume)), ("fade", Ints([c.FadeIn, c.FadeOut])), ("pitch", B(c.PreservePitch)),
                ("voice", A(S(Swift(v.Separation)), Opt(v.Denoise), Opt(v.Enhance), Opt(v.Filter, f => S(f.Name)),
                    Opt(v.Filter, f => d.Path(f.Directory)), Opt(v.Filter, f => N(f.Strength)))));
            if (c.VolumeEnvelope is { } e)
                d.Line(("envelope", I(k)), ("times", Ints(e.Times)), ("values", Nums(e.Values)), ("curved", B(e.Handles is not null)));
            // The audio renderer samples the time map every 50 ms; at most 200 samples per clip here.
            var duration = Math.Max(c.Target.Duration, 0);
            var step = Math.Max(50_000, (duration + 199) / 200);
            var source = new List<long>();
            for (long o = 0; o <= duration; o += step) source.Add(c.TimeMap.SourceTime(o));
            d.Line(("samples", I(k)), ("step", I(step)), ("source", Ints(source)));
        }
        for (var j = 0; j < t.Filters.Count; j++)
        {
            var f = t.Filters[j];
            d.Line(("filter", I(j)), ("id", S(f.Id)), ("target", Range(f.Target)), ("track", I(f.TrackIndex)),
                ("render", I(f.RenderIndex)), ("name", S(f.Filter.Name)), ("luts", new J.Arr(f.Filter.Luts.Select(d.Path).ToList())),
                ("intensity", N(f.Filter.Intensity)));
        }
        foreach (var time in SampleTimes(t))
        {
            // As in the compositor: active layers, plus both clips of a running transition.
            var mixing = new HashSet<VisualLayer>(ReferenceEqualityComparer.Instance);
            foreach (var l in t.Layers)
            {
                if (l.TransitionOut is { } tr && tr.Contains(time))
                {
                    mixing.Add(l);
                    mixing.Add(tr.Next);
                }
            }
            var filters = Enumerable.Range(0, t.Filters.Count).Where(j => t.Filters[j].IsActive(time)).Select(j => I(j)).ToList();
            d.Line(("t", I(time)), ("filters", new J.Arr(filters)));
            for (var index = 0; index < t.Layers.Count; index++)
            {
                var l = t.Layers[index];
                if (!l.IsActive(time) && !mixing.Contains(l)) continue;
                var offset = time - l.Target.Start;
                var source = l.TimeMap.SourceTime(offset);
                var text = Null;
                if (l.Content is TextContentLayer tl)
                {
                    var st = tl.Spec.Animations.State(offset, l.Target.Duration);
                    text = Nums([st.Alpha, st.Scale, st.OffsetX, st.OffsetY, st.Reveal]);
                }
                var fx = Null;
                if (l.Effects.Count > 0)
                {
                    var e = VideoEffectState.Combined(l.Effects.Select(x => x.State(offset)));
                    fx = Nums([e.Zoom, e.Rotation, e.Blur, e.Flash]);
                }
                var tx = Null;
                if (l.TransitionOut is { } tr && tr.Contains(time))
                    tx = A(I(IndexOf(t.Layers, tr.Next)), S(Swift(tr.Kind)), N(tr.Progress(time)));
                var clock = Null;
                if (l.Content is StickerContent)
                {
                    var h = ClockSticker.HandAngles(source);
                    clock = Nums([h.Minute, h.Hour]);
                }
                d.Line(("f", I(index)), ("offset", I(offset)), ("source", I(source)), ("tr", Transform(l.Transform(time))),
                    ("text", text), ("fx", fx), ("masks", new J.Arr(l.Masks.Select(m => Geometry(m.GeometryAt(source))).ToList())),
                    ("tx", tx), ("clock", clock));
            }
        }
    }

    static int IndexOf(IReadOnlyList<VisualLayer> layers, VisualLayer layer)
    {
        for (var k = 0; k < layers.Count; k++) if (ReferenceEquals(layers[k], layer)) return k;
        return -1;
    }

    // MARK: Writers

    static SubtitleLanguage Language(string code) => SubtitleLanguage.Find(code) ?? throw new ArgumentException(code);

    static List<SubtitleTrack> CaptionTracks() =>
    [
        new(Language("ru"), [new(0.5, 2, "Привет, мир"), new(2, 3.01, "Вторая\nстрока 😳")], true, false),
        new(Language("en"), [new(0.5, 2, "Hello, world")], false, true),
        new(Language("th"), [new(1.234, 4.5, "สวัสดีครับ ทุกคน")], false, true),
        new(Language("zh-Hans"), [new(5, 6, "你好")], false, false),
    ];

    static List<VoiceOverTrack> VoiceTracks(string project)
    {
        var folder = project.Replace('\\', '/').TrimEnd('/');
        return
        [
            new(Language("en"), folder + "/EnhanceCapCut/voiceover 1/en.wav", 10, [(1, 2.5), (3, 4), (7, 9.25)], true),
            new(Language("th"), "/elsewhere/th.wav", 10, [(1, 2)], false),
            new(Language("en"), folder + "/bg.wav", 10, [(0.75, 4.4)], true, IsBackground: true),
        ];
    }

    static bool IsUuid(byte[] b, int at)
    {
        if (at + 36 > b.Length) return false;
        for (var k = 0; k < 36; k++)
        {
            var c = b[at + k];
            if (k is 8 or 13 or 18 or 23) { if (c != '-') return false; }
            else if (!(c is >= (byte)'0' and <= (byte)'9' || c is >= (byte)'A' and <= (byte)'F')) return false;
        }
        return true;
    }

    static HashSet<string> Uuids(byte[] bytes)
    {
        var ids = new HashSet<string>();
        for (var k = 0; k + 36 <= bytes.Length;)
        {
            if (IsUuid(bytes, k)) { ids.Add(Encoding.ASCII.GetString(bytes, k, 36)); k += 36; } else k++;
        }
        return ids;
    }

    /// <summary>
    /// UUIDs replaced by <c>&lt;id1&gt;</c>, <c>&lt;id2&gt;</c>… in order of first appearance; ids already in the input stay.
    /// The project folder becomes <c>&lt;project&gt;</c>.
    /// </summary>
    static string Normalize(byte[] data, HashSet<string> keep, string project)
    {
        var seen = new Dictionary<string, int>();
        var o = new List<byte>(data.Length);
        for (var k = 0; k < data.Length;)
        {
            if (IsUuid(data, k))
            {
                var id = Encoding.ASCII.GetString(data, k, 36);
                if (keep.Contains(id)) o.AddRange(Encoding.ASCII.GetBytes(id));
                else
                {
                    if (!seen.TryGetValue(id, out var n)) seen[id] = n = seen.Count + 1;
                    o.AddRange(Encoding.ASCII.GetBytes($"<id{n}>"));
                }
                k += 36;
            }
            else o.Add(data[k++]);
        }
        return Encoding.UTF8.GetString(o.ToArray()).Replace(project.Replace('\\', '/').TrimEnd('/'), "<project>");
    }

    public static string DumpWriters(CapCutProject project, ProjectTimeline timeline)
    {
        var draft = File.ReadAllBytes(timeline.DraftPath);
        var keep = Uuids(draft);
        var o = new StringBuilder();
        void Section(string title, byte[] data) => o.Append("== ").Append(title).Append('\n')
            .Append(Normalize(data, keep, project.Folder)).Append('\n');
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500).UtcDateTime;
        var captions = CaptionTracks();
        var once = DraftSubtitleWriter.Apply(captions, draft, replacePrevious: true, now);
        Section("captions", once);
        Section("captions again", DraftSubtitleWriter.Apply(captions, once, replacePrevious: true, now));
        var voice = VoiceTracks(project.Folder);
        var last = draft;
        foreach (var mode in Enum.GetValues<OriginalAudio>())
        {
            last = DraftVoiceOverWriter.Apply(voice, draft, mode, replacePrevious: true, project.Folder);
            Section("voice-over " + mode switch
            {
                OriginalAudio.Keep => "keep",
                OriginalAudio.Lower => "lower",
                OriginalAudio.Mute => "muteall",
                OriginalAudio.Replace => "replacevoice",
                _ => "removevocal",
            }, last);
        }
        Section("without voice-over", DraftVoiceOverWriter.WithoutVoiceOver(last));
        return o.ToString();
    }
}
