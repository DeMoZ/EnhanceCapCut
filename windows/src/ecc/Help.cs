/// <summary><c>ecc help [command]</c>, <c>ecc --help</c>, <c>ecc &lt;command&gt; --help</c> / <c>-h</c>: usage, printed
/// without doing anything else (the Windows counterpart of the Swift CLI's Help).</summary>
static class Help
{
    public const string Overview = """
        usage: ecc <command> [options]     (ecc <command> --help for its options)
          list                         CapCut projects, * = open in CapCut
          info [project]               what the renderer makes of a project
          render [project]             export a project (the open one by default; Windows only)
          subtitles [project]          captions, translations and voice-over into the project
          voices <language>            Windows voices for a language (Windows only)
          clone <reference.wav>        speak a text in the reference's voice
          mcp                          an MCP server on stdin/stdout for AI agents
        """;

    static readonly Dictionary<string, string> Commands = new()
    {
        ["list"] = "usage: ecc list\n  CapCut projects, newest first; * marks the one open in CapCut.",
        ["info"] = """
            usage: ecc info [project] [--timeline name|number]
              The timeline's layers and audio clips and the CapCut features that aren't rendered yet.
            """,
        ["render"] = """
            usage: ecc render [project] [options]
              -o file                          output file (default: the app's export folder and file name)
              --timeline name|number           one of the project's timelines (default: the main one)
              --codec h264|hevc|hevc10
              --quality 0-100 | --bitrate Mbps
              --fps N                          --aspect project|9:16|1:1|16:9
              --height 0|720|1080|1440|2160    --lufs -14 (normalize loudness)
              --no-subtitles                   don't burn in the captions
              --from s  --duration s           render only part of the timeline
              Without a project the open one is used; unspecified options fall back to the app's saved settings.
            """,
        ["subtitles"] = """
            usage: ecc subtitles [project] [--timeline name|number] --language ru-RU [--translate en,th] [--no-original]
                                 [--show en] [--keep-previous] [--srt folder] [--draft out.json] [--audio mix.wav]
                                 [--no-captions] [--voice en,th | en=<voice id>,th | en=clone] [--hear th]
                                 [--original keep|lower|mute|remove] [--exaggeration 0.5] [--consent]
              Writes into the project unless --draft is given; voice-over goes into a new timeline. Models are
              downloaded on first use. `clone` speaks in the original speaker's voice — only with their consent,
              which --consent confirms.
            """,
        ["voices"] = "usage: ecc voices <language>\n  The Windows voices for a language code (e.g. en, th).",
        ["clone"] = """
            usage: ecc clone <reference.wav> --language en --text "…" -o out.wav [--exaggeration 0.5] [--seed 1]
              Downloads the voice cloning model on first use (1.55 GB).
            """,
        ["mcp"] = "usage: ecc mcp\n  An MCP server on stdin/stdout: list, info, render and subtitles as tools (see docs/CLI.md).",
    };

    public static string Text(string? command) =>
        command is not null && Commands.TryGetValue(command, out var text) ? text : Overview;

    public static bool IsRequest(string arg) => arg is "--help" or "-h" or "help";
}
