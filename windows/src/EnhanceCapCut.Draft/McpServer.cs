using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EnhanceCapCut.Draft;

/// <summary>
/// Runs <c>ecc</c> with some arguments. The MCP server does all its work through it, so the tools behave exactly like
/// the command line (and a crash in a render never takes the server down).
/// </summary>
public interface IMcpRunner
{
    /// <summary>Starts <c>ecc arguments</c>: every stdout line goes to <paramref name="line"/>, then
    /// <paramref name="exit"/> gets the exit code and stderr. Returns what stops the run.</summary>
    Action Start(IReadOnlyList<string> arguments, Action<string> line, Action<int, string> exit);
}

/// <summary>The <c>ecc</c> executable in a child process.</summary>
public sealed class McpProcessRunner(string executable) : IMcpRunner
{
    public Action Start(IReadOnlyList<string> arguments, Action<string> line, Action<int, string> exit)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {executable}");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        new Thread(() =>
        {
            while (process.StandardOutput.ReadLine() is { } l) line(l);
            process.WaitForExit();
            exit(process.ExitCode, stderr.Result);
        }) { IsBackground = true }.Start();
        return () =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        };
    }
}

/// <summary>
/// The MCP (Model Context Protocol) server behind <c>ecc mcp</c>, so AI agents can list projects, read what will be
/// rendered, export and add subtitles. JSON-RPC 2.0, one message per line on stdin/stdout.
/// </summary>
/// <remarks>
/// Renders and subtitles take minutes, longer than clients wait for a tool call, so they are jobs: the tool returns a
/// job id at once and <c>job_status</c> reports progress (optionally waiting a while for the end). Jobs run one at a
/// time, in the order they were given, each as <c>ecc render …</c> / <c>ecc subtitles …</c> in a child process.
/// Port of the Swift <c>EnhanceCapCutCore/Automation/MCPServer.swift</c>: same tools, arguments and results, apart
/// from what the platforms differ in (codecs, the speech engine, <c>original: replace</c>).
/// </remarks>
public sealed class McpServer(IMcpRunner runner, string version, Func<List<CapCutProject>>? projects = null)
{
    /// <summary>Protocol revisions this server speaks; the newest one is offered to clients asking for another.</summary>
    internal static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    /// <summary>Longest <c>job_status</c> wait, below the usual client timeout for one tool call.</summary>
    internal const double MaxWaitSeconds = 50;
    /// <summary>How many output lines a job keeps, and how many of the last ones <c>job_status</c> shows.</summary>
    internal const int KeptLines = 200, ShownLines = 20;

    readonly Func<List<CapCutProject>> projects = projects ?? (() => ProjectLocator.AllProjects());
    readonly Lock gate = new();
    readonly List<Job> jobs = [];
    int nextId = 1;

    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>Serves stdin until it closes, then stops the running job: nobody is left to collect it.</summary>
    public void Serve()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var writing = new Lock();
        var pending = new List<Task>();
        while (input.ReadLine() is { } line)
        {
            void Respond()
            {
                if (Handle(line) is not { } response) return;
                lock (writing) output.WriteLine(response);
            }
            // In order, except a job_status that waits: that one mustn't hold up the others.
            if (Waits(line)) pending.Add(Task.Run(Respond));
            else Respond();
        }
        Task.WaitAll(pending);
        CancelAll();
    }

    /// <summary>Whether the message is a job_status call with a wait.</summary>
    internal static bool Waits(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject { } m && m["method"]?.GetValueKind() == JsonValueKind.String
                && (string)m["method"]! == "tools/call" && m["params"] is JsonObject p
                && p["name"]?.GetValueKind() == JsonValueKind.String && (string)p["name"]! == "job_status"
                && p["arguments"] is JsonObject a && Num(a, "wait_seconds") > 0;
        }
        catch (Exception e) when (e is JsonException or McpException) { return false; }
    }

    /// <summary>One JSON-RPC message in, the response line out (none for notifications).</summary>
    public string? Handle(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonObject? message;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { message = null; }
        if (message is null)
            return Encode(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = Error(-32700, "Parse error") });
        if (message["id"] is not { } id) return null;
        var method = message["method"]?.GetValueKind() == JsonValueKind.String ? (string)message["method"]! : "";
        var parameters = message["params"] as JsonObject ?? [];
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        switch (method)
        {
            case "initialize":
                var asked = parameters["protocolVersion"]?.GetValueKind() == JsonValueKind.String ? (string)parameters["protocolVersion"]! : "";
                response["result"] = new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "enhancecapcut", ["title"] = "EnhanceCapCut", ["version"] = version },
                    ["instructions"] = Instructions,
                };
                break;
            case "ping":
                response["result"] = new JsonObject();
                break;
            case "tools/list":
                response["result"] = new JsonObject { ["tools"] = Tools() };
                break;
            case "tools/call":
                var name = parameters["name"]?.GetValueKind() == JsonValueKind.String ? (string)parameters["name"]! : "";
                if (!ToolNames.Contains(name))
                {
                    response["error"] = Error(-32602, $"Unknown tool: {name}");
                    break;
                }
                var arguments = parameters["arguments"] as JsonObject ?? [];
                string text;
                var isError = false;
                try { text = Call(name, arguments); }
                catch (McpException e) { text = e.Message; isError = true; }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    text = e.Message;
                    isError = true;
                }
                response["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                };
                break;
            default:
                response["error"] = Error(-32601, $"Method not found: {method}");
                break;
        }
        return Encode(response);
    }

    static JsonObject Error(int code, string message) => new() { ["code"] = code, ["message"] = message };

    string Call(string tool, JsonObject a)
    {
        switch (tool)
        {
            case "list_projects":
                return Json(new JsonArray(projects().Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.Name,
                    ["open"] = p.IsOpen,
                    ["modified"] = p.Modified.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["timelines"] = new JsonArray(p.Timelines().Select(t => (JsonNode)new JsonObject
                        { ["name"] = t.Name, ["main"] = t.IsMain }).ToArray()),
                }).ToArray()));
            case "project_info":
                return Capture(["info", .. TimelineArguments(a)]);
            case "list_voices":
                return Capture(["voices", Str(a, "language", required: true)!]);
            case "render":
                return Json(Status(Enqueue(tool, RenderArguments(a))));
            case "add_subtitles":
                return Json(Status(Enqueue(tool, SubtitleArguments(a))));
            case "job_status":
            {
                var job = Find(a);
                var wait = Math.Clamp(Num(a, "wait_seconds") ?? 0, 0, MaxWaitSeconds);
                var deadline = DateTime.UtcNow.AddSeconds(wait);
                while (DateTime.UtcNow < deadline && !Locked(() => job.IsFinished)) Thread.Sleep(200);
                return Json(Status(job));
            }
            case "list_jobs":
                return Json(new JsonArray(Locked(() => jobs.ToList()).Select(j => (JsonNode)Status(j, 0)).ToArray()));
            case "cancel_job":
            {
                var job = Find(a);
                var stop = Locked(() =>
                {
                    if (job.State == "queued") { job.State = "cancelled"; job.Finished = DateTime.UtcNow; }
                    if (job.State == "running") { job.CancelRequested = true; return job.Stop; }
                    return null;
                });
                stop?.Invoke();
                return Json(Status(job));
            }
            default:
                throw new McpException($"Unknown tool: {tool}");
        }
    }

    // Arguments → ecc command lines

    internal static List<string> RenderArguments(JsonObject a)
    {
        List<string> args = ["render"];
        if (Str(a, "codec") is { } codec)
        {
            if (!Codecs.Contains(codec)) throw new McpException("codec must be one of " + string.Join(", ", Codecs));
            args.AddRange(["--codec", codec]);
        }
        if (Num(a, "quality") is { } q)
        {
            if (q is < 0 or > 100) throw new McpException("quality must be 0–100");
            args.AddRange(["--quality", Format(q)]);
        }
        if (Num(a, "bitrate_mbps") is { } b) args.AddRange(["--bitrate", Format(b)]);
        if (Num(a, "fps") is { } fps) args.AddRange(["--fps", Format(fps)]);
        if (Str(a, "aspect") is { } aspect)
        {
            if (!Aspects.Contains(aspect)) throw new McpException("aspect must be one of " + string.Join(", ", Aspects));
            args.AddRange(["--aspect", aspect]);
        }
        if (Num(a, "height") is { } h)
        {
            if (!Heights.Contains(h)) throw new McpException("height must be 0 (the project's), 720, 1080, 1440 or 2160");
            args.AddRange(["--height", Format(h)]);
        }
        if (Num(a, "lufs") is { } lufs) args.AddRange(["--lufs", Format(lufs)]);
        if (Bool(a, "subtitles") == false) args.Add("--no-subtitles");
        if (Num(a, "from_seconds") is { } from) args.AddRange(["--from", Format(from)]);
        if (Num(a, "duration_seconds") is { } duration) args.AddRange(["--duration", Format(duration)]);
        if (Str(a, "output") is { } output) args.AddRange(["-o", output]);
        args.AddRange(TimelineArguments(a));
        return args;
    }

    internal static List<string> SubtitleArguments(JsonObject a)
    {
        List<string> args = ["subtitles", "--language", Str(a, "language", required: true)!];
        if (Strings(a, "translate") is { Count: > 0 } translate) args.AddRange(["--translate", string.Join(",", translate)]);
        if (Str(a, "show") is { } show) args.AddRange(["--show", show]);
        if (Strings(a, "voice") is { Count: > 0 } voice)
        {
            // Cloning copies a real person's voice and needs their consent, which only a person can confirm.
            if (voice.Any(v => v.EndsWith("=clone", StringComparison.OrdinalIgnoreCase)))
                throw new McpException("Voice cloning needs the speakers' consent, confirmed by a person: use the app or "
                    + "`ecc subtitles … --consent` instead.");
            args.AddRange(["--voice", string.Join(",", voice)]);
        }
        if (Str(a, "hear") is { } hear) args.AddRange(["--hear", hear]);
        if (Str(a, "original") is { } original)
        {
            if (!Originals.Contains(original)) throw new McpException("original must be one of " + string.Join(", ", Originals));
            args.AddRange(["--original", original]);
        }
        if (Str(a, "srt_folder") is { } srt) args.AddRange(["--srt", srt]);
        if (Bool(a, "include_original") == false) args.Add("--no-original");
        if (Bool(a, "keep_previous") == true) args.Add("--keep-previous");
        if (Bool(a, "captions") == false) args.Add("--no-captions");
        args.AddRange(TimelineArguments(a));
        return args;
    }

    /// <summary><c>--timeline</c> and the project, last: <c>ecc</c> takes the first argument that isn't an option as the project.</summary>
    internal static List<string> TimelineArguments(JsonObject a)
    {
        List<string> args = [];
        if (Str(a, "timeline") is { } timeline) args.AddRange(["--timeline", timeline]);
        if (Str(a, "project") is { } project)
        {
            if (project.StartsWith('-')) throw new McpException("project must be a project name or folder");
            args.Add(project);
        }
        return args;
    }

    internal static string Format(double v) => v == Math.Round(v) && Math.Abs(v) < 1e15
        ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("R", CultureInfo.InvariantCulture);

    static string? Str(JsonObject a, string key, bool required = false)
    {
        var node = a[key];
        switch (node?.GetValueKind())
        {
            case null or JsonValueKind.Null:
                if (required) throw new McpException($"{key} is required");
                return null;
            case JsonValueKind.String:
                var s = (string)node!;
                if (s.Length == 0 && required) throw new McpException($"{key} is required");
                return s.Length == 0 ? null : s;
            case JsonValueKind.Number:
                return Format(Double(node!));
            default:
                throw new McpException($"{key} must be a string");
        }
    }

    // Parsed and built nodes hold numbers differently; their JSON text is the same.
    static double Double(JsonNode n) => double.Parse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static double? Num(JsonObject a, string key)
    {
        var node = a[key];
        return node?.GetValueKind() switch
        {
            null or JsonValueKind.Null => null,
            JsonValueKind.Number => Double(node!),
            JsonValueKind.String when double.TryParse((string)node!, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new McpException($"{key} must be a number"),
        };
    }

    static bool? Bool(JsonObject a, string key)
    {
        var node = a[key];
        return node?.GetValueKind() switch
        {
            null or JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new McpException($"{key} must be true or false"),
        };
    }

    /// <summary>A list of strings, or one comma-separated string.</summary>
    static List<string>? Strings(JsonObject a, string key)
    {
        var node = a[key];
        switch (node?.GetValueKind())
        {
            case null or JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return ((string)node!).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            case JsonValueKind.Array:
                return node!.AsArray().Select(n => n?.GetValueKind() == JsonValueKind.String ? (string)n!
                    : throw new McpException($"{key} must be a list of strings")).ToList();
            default:
                throw new McpException($"{key} must be a list of strings");
        }
    }

    // Jobs

    internal sealed class Job(string id, string tool, List<string> arguments)
    {
        public string Id { get; } = id;
        public string Tool { get; } = tool;
        public List<string> Arguments { get; } = arguments;
        /// <summary>queued, running, done, failed, cancelled</summary>
        public string State { get; set; } = "queued";
        public double? Progress { get; set; }
        public string Phase { get; set; } = "";
        public string? Output { get; set; }
        public List<string> Files { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Lines { get; } = [];
        public string? Error { get; set; }
        public DateTime? Started { get; set; }
        public DateTime? Finished { get; set; }
        public Action? Stop { get; set; }
        public bool CancelRequested { get; set; }
        public bool IsFinished => State is "done" or "failed" or "cancelled";
    }

    T Locked<T>(Func<T> body) { lock (gate) return body(); }

    Job Enqueue(string tool, List<string> arguments)
    {
        Job job;
        lock (gate)
        {
            job = new Job($"job-{nextId++}", tool, arguments);
            jobs.Add(job);
        }
        StartNext();
        return job;
    }

    void StartNext()
    {
        Job? job;
        lock (gate)
        {
            if (jobs.Any(j => j.State == "running")) return;
            job = jobs.FirstOrDefault(j => j.State == "queued");
            if (job is null) return;
            job.State = "running";
            job.Started = DateTime.UtcNow;
        }
        try
        {
            var stop = runner.Start(job.Arguments, line => { lock (gate) Read(line, job); }, (code, stderr) =>
            {
                lock (gate)
                {
                    job.Finished = DateTime.UtcNow;
                    job.Stop = null;
                    if (job.CancelRequested) job.State = "cancelled";
                    else if (code == 0) { job.State = "done"; job.Progress = 1; }
                    else
                    {
                        job.State = "failed";
                        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                        job.Error = lines.Length == 0 ? $"ecc exited with code {code}" : string.Join("\n", lines.TakeLast(5));
                    }
                }
                StartNext();
            });
            lock (gate) if (job.State == "running") job.Stop = stop;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (gate)
            {
                job.State = "failed";
                job.Error = $"Couldn't start ecc: {e.Message}";
                job.Finished = DateTime.UtcNow;
            }
            StartNext();
        }
    }

    static readonly Regex Percent = new(@"(\d+(\.\d+)?)%");

    /// <summary>What a line of <c>ecc render</c> / <c>ecc subtitles</c> output says about the job.</summary>
    internal static void Read(string raw, Job job)
    {
        var line = raw.Trim();
        if (line.Length == 0) return;
        job.Lines.Add(line);
        if (job.Lines.Count > KeptLines) job.Lines.RemoveRange(0, job.Lines.Count - KeptLines);
        if (Percent.Match(line) is { Success: true } m)
            job.Progress = Math.Min(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100, 1);
        int arrow = line.IndexOf(" → ", StringComparison.Ordinal), paren = line.LastIndexOf(" (", StringComparison.Ordinal);
        if (line.StartsWith("Rendering ", StringComparison.Ordinal) && arrow >= 0 && arrow + 3 <= paren)
            job.Output = line[(arrow + 3)..paren];
        else if (line.StartsWith("Done in ", StringComparison.Ordinal) && line.IndexOf(": ", StringComparison.Ordinal) is var colon and >= 0)
            job.Output = line[(colon + 2)..];
        else if (new[] { "Wrote ", "Updated " }.FirstOrDefault(p => line.StartsWith(p, StringComparison.Ordinal)) is { } prefix)
            job.Files.Add(line[prefix.Length..]);
        else if (new[] { "not supported:", "missing:", "warning:" }.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
            job.Warnings.Add(line);
        else
            job.Phase = line;
    }

    Job Find(JsonObject a)
    {
        var id = Str(a, "job_id", required: true)!;
        return Locked(() => jobs.FirstOrDefault(j => j.Id == id)) ?? throw new McpException($"No job {id}");
    }

    internal JsonObject Status(Job job, int lines = ShownLines)
    {
        lock (gate)
        {
            var s = new JsonObject
            {
                ["command"] = string.Join(" ", job.Arguments.Prepend("ecc")),
                ["job_id"] = job.Id,
                ["status"] = job.State,
                ["tool"] = job.Tool,
            };
            if (job.Progress is { } p) s["progress"] = Math.Round(p * 1000) / 1000;
            if (job.Phase.Length > 0 && !job.IsFinished) s["phase"] = job.Phase;
            if (job.Output is { } o) s["output"] = o;
            if (job.Files.Count > 0) s["files"] = new JsonArray(job.Files.Select(f => (JsonNode)f).ToArray());
            if (job.Warnings.Count > 0) s["warnings"] = new JsonArray(job.Warnings.Select(w => (JsonNode)w).ToArray());
            if (job.Error is { } e) s["error"] = e;
            if (job.Started is { } started)
                s["elapsed_seconds"] = Math.Round(((job.Finished ?? DateTime.UtcNow) - started).TotalSeconds * 10) / 10;
            if (job.State == "queued") s["position"] = jobs.Where(j => j.State == "queued").ToList().IndexOf(job) + 1;
            if (lines > 0 && job.Lines.Count > 0) s["log"] = new JsonArray(job.Lines.TakeLast(lines).Select(l => (JsonNode)l).ToArray());
            return s;
        }
    }

    internal void CancelAll()
    {
        List<Action> stops;
        lock (gate)
        {
            foreach (var job in jobs.Where(j => j.State == "queued")) job.State = "cancelled";
            stops = [];
            foreach (var job in jobs.Where(j => j.State == "running"))
            {
                job.CancelRequested = true;
                if (job.Stop is { } stop) stops.Add(stop);
            }
        }
        foreach (var stop in stops) stop();
    }

    /// <summary>Runs a quick <c>ecc</c> command and returns its output.</summary>
    string Capture(List<string> arguments)
    {
        var output = new List<string>();
        var result = (Code: 0, Stderr: "");
        using var done = new ManualResetEventSlim();
        runner.Start(arguments, line => { lock (output) output.Add(line); }, (code, stderr) =>
        {
            result = (code, stderr);
            done.Set();
        });
        done.Wait();
        if (result.Code != 0)
        {
            var error = result.Stderr.Trim();
            throw new McpException(error.Length == 0 ? $"ecc exited with code {result.Code}" : error);
        }
        lock (output) return string.Join("\n", output);
    }

    static string Encode(JsonNode node) => node.ToJsonString(Compact);
    static string Json(JsonNode node) => node.ToJsonString(Pretty);

    // Tool definitions

    internal static readonly string[] Codecs = ["h264", "hevc", "hevc10"];
    internal static readonly string[] Aspects = ["project", "9:16", "1:1", "16:9"];
    internal static readonly double[] Heights = [0, 720, 1080, 1440, 2160];
    internal static readonly string[] Originals = ["keep", "lower", "mute", "remove"];

    internal const string Instructions =
        "EnhanceCapCut renders CapCut projects with its own renderer. Projects are named as in list_projects; "
        + "without a project the one open in CapCut is used. render and add_subtitles start jobs that run one at a "
        + "time: poll job_status (wait_seconds up to 50 to wait for the end) until status is done, failed or "
        + "cancelled. Options a render doesn't give fall back to the app's saved export settings. Check project_info "
        + "first: it lists features that won't be rendered and missing media.";

    internal static readonly string[] ToolNames =
        ["list_projects", "project_info", "render", "add_subtitles", "list_voices", "job_status", "list_jobs", "cancel_job"];

    static JsonObject Project() => new()
    {
        ["type"] = "string",
        ["description"] = "Project name (as in list_projects) or folder; the project open in CapCut when omitted",
    };

    static JsonObject Timeline() => new()
    {
        ["type"] = "string",
        ["description"] = "Timeline name or 1-based number in a project with several; the main one when omitted",
    };

    static JsonObject Prop(string type, string? description = null, JsonNode? extra = null)
    {
        var o = new JsonObject { ["type"] = type };
        if (description is not null) o["description"] = description;
        if (extra is JsonObject e) foreach (var (k, v) in e) o[k] = v?.DeepClone();
        return o;
    }

    static JsonArray Enum(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    internal static JsonArray Tools() =>
    [
        Tool("list_projects", "List CapCut projects: name, whether open in CapCut, last change, timelines.", []),
        Tool("project_info", "What will be rendered from a project: canvas, fps, duration, layers, audio clips, "
            + "CapCut features not supported yet, missing media.",
            new() { ["project"] = Project(), ["timeline"] = Timeline() }),
        Tool("render", "Export a project to a video file. Starts a job and returns its id at once; follow it with "
            + "job_status. Options left out come from the app's saved export settings.",
            new()
            {
                ["project"] = Project(), ["timeline"] = Timeline(),
                ["output"] = Prop("string", "Output file (.mp4); the app's export folder and file name pattern when omitted"),
                ["codec"] = Prop("string", extra: new JsonObject { ["enum"] = Enum(Codecs) }),
                ["quality"] = Prop("number", "Constant quality 0–100", new JsonObject { ["minimum"] = 0, ["maximum"] = 100 }),
                ["bitrate_mbps"] = Prop("number", "Average bitrate instead of quality"),
                ["fps"] = Prop("number", "Frame rate; 0 = the project's"),
                ["aspect"] = Prop("string", "Frame shape; other than the project's crops to fill", new JsonObject { ["enum"] = Enum(Aspects) }),
                ["height"] = Prop("integer", "Output height; 0 = the project's",
                    new JsonObject { ["enum"] = new JsonArray(0, 720, 1080, 1440, 2160) }),
                ["lufs"] = Prop("number", "Normalize loudness to this integrated LUFS, e.g. -14"),
                ["subtitles"] = Prop("boolean", "Burn in the project's subtitles (default from settings)"),
                ["from_seconds"] = Prop("number", "Render a part only: start"),
                ["duration_seconds"] = Prop("number", "Render a part only: length"),
            }),
        Tool("add_subtitles", "Transcribe a timeline and add captions to the CapCut project, optionally translated and "
            + "voiced over. Writes into the project, keeping a backup; voice-over goes to a new timeline. "
            + "Starts a job; follow it with job_status.",
            new()
            {
                ["project"] = Project(), ["timeline"] = Timeline(),
                ["language"] = Prop("string", "Spoken language, e.g. ru-RU"),
                ["translate"] = Prop("array", "Languages to translate into, e.g. [\"en\", \"th\"]",
                    new JsonObject { ["items"] = new JsonObject { ["type"] = "string" } }),
                ["show"] = Prop("string", "Language of the visible caption track"),
                ["include_original"] = Prop("boolean", "Keep a track in the spoken language (default true)"),
                ["captions"] = Prop("boolean", "Add caption tracks (default true); false = voice-over only"),
                ["keep_previous"] = Prop("boolean", "Keep caption tracks added earlier (default: replace them)"),
                ["voice"] = Prop("array", "Voice-over languages, each \"th\" or \"th=<voice id from list_voices>\"",
                    new JsonObject { ["items"] = new JsonObject { ["type"] = "string" } }),
                ["hear"] = Prop("string", "Language of the audible voice-over track"),
                ["original"] = Prop("string", "Original audio under the voice-over (default lower)",
                    new JsonObject { ["enum"] = Enum(Originals) }),
                ["srt_folder"] = Prop("string", "Also write .srt files there"),
            }, ["language"]),
        Tool("list_voices", "Installed voices for a language, with the ids add_subtitles takes.",
            new() { ["language"] = Prop("string", "Language code, e.g. th") }, ["language"]),
        Tool("job_status", "Progress and result of a render or add_subtitles job: status (queued, running, done, "
            + "failed, cancelled), progress 0–1, output file, warnings, error, last log lines.",
            new()
            {
                ["job_id"] = Prop("string"),
                ["wait_seconds"] = Prop("number", "Wait up to this long for the job to finish before answering",
                    new JsonObject { ["minimum"] = 0, ["maximum"] = 50 }),
            }, ["job_id"]),
        Tool("list_jobs", "All jobs of this session with their status.", []),
        Tool("cancel_job", "Cancel a queued or running job.", new() { ["job_id"] = Prop("string") }, ["job_id"]),
    ];

    static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is not null) schema["required"] = Enum(required);
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }
}

sealed class McpException(string message) : Exception(message);
