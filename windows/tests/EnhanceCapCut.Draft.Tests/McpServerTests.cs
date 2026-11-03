using System.Text.Json.Nodes;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Mirrors Tests/EnhanceCapCutCoreTests/MCPServerTests.swift.
public class McpServerTests
{
    /// <summary>Prints the scripted lines for a command, then exits; <c>Hold</c> keeps renders running until released.</summary>
    sealed class FakeRunner : IMcpRunner
    {
        public Dictionary<string, (string[] Lines, int Code, string Stderr)> Outputs { get; } = [];
        public List<IReadOnlyList<string>> Started { get; } = [];
        public bool Hold { get; set; }
        public int Stopped { get; private set; }

        public Action Start(IReadOnlyList<string> arguments, Action<string> line, Action<int, string> exit)
        {
            Started.Add(arguments);
            var (lines, code, stderr) = Outputs.TryGetValue(arguments[0], out var o) ? o : ([], 0, "");
            if (Hold && arguments[0] == "render") return () => { Stopped++; exit(15, ""); };
            foreach (var l in lines) line(l);
            exit(code, stderr);
            return () => { };
        }
    }

    static (string Text, bool IsError) Call(McpServer server, string tool, JsonObject? arguments = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] },
        };
        var response = JsonNode.Parse(server.Handle(request.ToJsonString())!)!;
        var result = response["result"]!;
        return ((string)result["content"]![0]!["text"]!, (bool)result["isError"]!);
    }

    static JsonNode Json(string text) => JsonNode.Parse(text)!;

    [Fact]
    public void HandshakeAndToolList()
    {
        var server = new McpServer(new FakeRunner(), "1.2", () => []);
        var initialize = Json(server.Handle("""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")!);
        Assert.Equal(7, (int)initialize["id"]!);
        Assert.Equal("2025-06-18", (string)initialize["result"]!["protocolVersion"]!);
        Assert.Equal("1.2", (string)initialize["result"]!["serverInfo"]!["version"]!);
        var unknown = Json(server.Handle("""{"jsonrpc":"2.0","id":8,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""")!);
        Assert.Equal(McpServer.ProtocolVersions[0], (string)unknown["result"]!["protocolVersion"]!);

        Assert.Null(server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        var list = Json(server.Handle("""{"jsonrpc":"2.0","id":"a","method":"tools/list"}""")!);
        Assert.Equal(["list_projects", "project_info", "render", "add_subtitles", "list_voices", "job_status", "list_jobs", "cancel_job"],
            list["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.Equal("a", (string)list["id"]!);
        Assert.Equal(-32601, (int)Json(server.Handle("""{"jsonrpc":"2.0","id":2,"method":"resources/list"}""")!)["error"]!["code"]!);
        Assert.Equal(-32700, (int)Json(server.Handle("{not json")!)["error"]!["code"]!);
    }

    [Fact]
    public void WaitingStatusCallsAreRecognized()
    {
        Assert.True(McpServer.Waits("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"job_status","arguments":{"job_id":"job-1","wait_seconds":5}}}"""));
        Assert.False(McpServer.Waits("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"job_status","arguments":{"job_id":"job-1"}}}"""));
        Assert.False(McpServer.Waits("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"render","arguments":{"wait_seconds":5}}}"""));
        Assert.False(McpServer.Waits("{not json"));
    }

    [Fact]
    public void RenderArguments()
    {
        Assert.Equal(["render", "--codec", "hevc10", "--quality", "70", "--fps", "29.97", "--aspect", "9:16", "--height", "1080",
                "--lufs", "-14", "--no-subtitles", "--from", "10", "--duration", "5.5", "-o", "/tmp/x.mp4", "--timeline", "2", "Trip"],
            McpServer.RenderArguments(new JsonObject
            {
                ["project"] = "Trip", ["timeline"] = "2", ["codec"] = "hevc10", ["quality"] = 70, ["aspect"] = "9:16",
                ["height"] = 1080, ["fps"] = 29.97, ["lufs"] = -14, ["subtitles"] = false, ["from_seconds"] = 10,
                ["duration_seconds"] = 5.5, ["output"] = "/tmp/x.mp4",
            }));
        Assert.Equal(["render"], McpServer.RenderArguments([]));
        Assert.ThrowsAny<Exception>(() => McpServer.RenderArguments(new JsonObject { ["codec"] = "vp9" }));
        Assert.ThrowsAny<Exception>(() => McpServer.RenderArguments(new JsonObject { ["height"] = 1000 }));
        Assert.ThrowsAny<Exception>(() => McpServer.RenderArguments(new JsonObject { ["quality"] = "high" }));
        Assert.ThrowsAny<Exception>(() => McpServer.RenderArguments(new JsonObject { ["project"] = "--help" }));
    }

    [Fact]
    public void SubtitleArguments()
    {
        Assert.Equal(["subtitles", "--language", "ru-RU", "--translate", "en,th", "--show", "en", "--voice", "en,th=voice-id",
                "--hear", "th", "--original", "mute", "--srt", "/tmp/s", "--no-original", "--keep-previous", "--no-captions", "Trip"],
            McpServer.SubtitleArguments(new JsonObject
            {
                ["language"] = "ru-RU", ["translate"] = new JsonArray("en", "th"), ["show"] = "en", ["voice"] = "en,th=voice-id",
                ["hear"] = "th", ["original"] = "mute", ["captions"] = false, ["include_original"] = false,
                ["keep_previous"] = true, ["srt_folder"] = "/tmp/s", ["project"] = "Trip",
            }));
        Assert.ThrowsAny<Exception>(() => McpServer.SubtitleArguments([]));
        // The speakers' consent to cloning can't come from an agent.
        Assert.ThrowsAny<Exception>(() => McpServer.SubtitleArguments(new JsonObject { ["language"] = "ru", ["voice"] = new JsonArray("en=clone") }));
    }

    [Fact]
    public void RenderJobReportsProgressAndResult()
    {
        var runner = new FakeRunner();
        runner.Outputs["render"] = ([
            "not supported: Color adjustment: color correct ×1",
            "Rendering Trip → /out/Trip 1080p.mp4 (1080x1920 @ 30 fps, H.264)",
            "  frames  42%  100/240 frames",
            "Done in 3.0s: /out/Trip 1080p.mp4",
        ], 0, "");
        var server = new McpServer(runner, "1", () => []);
        Assert.Equal("job-1", (string)Json(Call(server, "render", new JsonObject { ["project"] = "Trip" }).Text)["job_id"]!);
        var status = Json(Call(server, "job_status", new JsonObject { ["job_id"] = "job-1", ["wait_seconds"] = 5 }).Text);
        Assert.Equal("done", (string)status["status"]!);
        Assert.Equal(1.0, (double)status["progress"]!);
        Assert.Equal("/out/Trip 1080p.mp4", (string)status["output"]!);
        Assert.Equal(["not supported: Color adjustment: color correct ×1"], status["warnings"]!.AsArray().Select(w => (string)w!));
        Assert.Equal(["render", "Trip"], runner.Started.Single());
    }

    [Fact]
    public void FailedJobCarriesStderr()
    {
        var runner = new FakeRunner();
        runner.Outputs["render"] = ([], 1, "Project not found: Nope\n");
        var server = new McpServer(runner, "1", () => []);
        Call(server, "render", new JsonObject { ["project"] = "Nope" });
        var status = Json(Call(server, "job_status", new JsonObject { ["job_id"] = "job-1" }).Text);
        Assert.Equal("failed", (string)status["status"]!);
        Assert.Equal("Project not found: Nope", (string)status["error"]!);
        Assert.True(Call(server, "job_status", new JsonObject { ["job_id"] = "job-9" }).IsError);
    }

    [Fact]
    public void JobsRunOneAtATimeAndCancel()
    {
        var runner = new FakeRunner { Hold = true };
        var server = new McpServer(runner, "1", () => []);
        foreach (var p in new[] { "A", "B", "C" }) Call(server, "render", new JsonObject { ["project"] = p });
        Assert.Single(runner.Started);
        var queued = Json(Call(server, "job_status", new JsonObject { ["job_id"] = "job-3" }).Text);
        Assert.Equal("queued", (string)queued["status"]!);
        Assert.Equal(2, (int)queued["position"]!);

        Assert.Equal("cancelled", (string)Json(Call(server, "cancel_job", new JsonObject { ["job_id"] = "job-2" }).Text)["status"]!);
        Assert.Equal("cancelled", (string)Json(Call(server, "cancel_job", new JsonObject { ["job_id"] = "job-1" }).Text)["status"]!);
        Assert.Equal(1, runner.Stopped);
        // Cancelling the running one starts the next that is still queued: C, not the cancelled B.
        Assert.Equal(["A", "C"], runner.Started.Select(a => a[^1]));
    }

    [Fact]
    public void QuickCommandsAndProjects()
    {
        var runner = new FakeRunner();
        runner.Outputs["info"] = (["Project: Trip  canvas 1080x1920", "Visual layers: 3, audio clips: 1"], 0, "");
        runner.Outputs["voices"] = ([], 1, "usage: ecc voices <language code>\n");
        var server = new McpServer(runner, "1", () => []);
        Assert.Equal("Project: Trip  canvas 1080x1920\nVisual layers: 3, audio clips: 1",
            Call(server, "project_info", new JsonObject { ["project"] = "Trip", ["timeline"] = "Cut" }).Text);
        Assert.Equal(["info", "--timeline", "Cut", "Trip"], runner.Started[^1]);
        var voices = Call(server, "list_voices", new JsonObject { ["language"] = "th" });
        Assert.True(voices.IsError);
        Assert.Equal("usage: ecc voices <language code>", voices.Text);
        Assert.Equal("[]", Call(server, "list_projects").Text);
    }
}
