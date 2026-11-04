/// `ecc help [command]`, `ecc --help`, `ecc <command> --help` / `-h`: usage, printed without doing anything else.
enum Help {
    static let overview = """
    usage: ecc <command> [options]     (ecc <command> --help for its options)
      list                         CapCut projects, * = open in CapCut
      info [project]               what the renderer makes of a project
      render [project]             export a project (the open one by default)
      voice <media> -o out.wav     run the voice processing on one file
      subtitles [project]          captions, translations and voice-over into the project
      voices <language>            system voices for a language
      clone <reference audio>      speak a text in the reference's voice
      mcp                          an MCP server on stdin/stdout for AI agents
    """

    static let commands: [String: String] = [
        "list": "usage: ecc list\n  CapCut projects, newest first; * marks the one open in CapCut.",
        "info": """
        usage: ecc info [project] [--timeline name|number]
          The timeline's layers and audio clips and the CapCut features that aren't rendered yet.
        """,
        "render": """
        usage: ecc render [project] [options]
          -o file                          output file (default: the app's export folder and file name)
          --timeline name|number           one of the project's timelines (default: the main one)
          --codec h264|hevc|hevc10|prores422|prores422HQ
          --quality 0-100 | --bitrate Mbps
          --fps N                          --aspect project|9:16|1:1|16:9
          --height 0|720|1080|1440|2160    --lufs -14 (normalize loudness)
          --no-subtitles                   don't burn in the captions
          --from s  --duration s           render only part of the timeline
          Without a project the open one is used; unspecified options fall back to the app's saved settings.
        """,
        "voice": """
        usage: ecc voice <media> -o out.wav [--keep-voice | --remove-voice] [--denoise 0.85] [--enhance 75]
                         [--filter <cached effect dir | resource id>] [--strength 1]
        """,
        "subtitles": """
        usage: ecc subtitles [project] [--timeline name|number] --language ru-RU [--engine apple|whisper]
                             [--translate en,th] [--no-original] [--show en] [--keep-previous] [--srt folder]
                             [--draft out.json] [--no-captions] [--voice en,th | en=<voice id>,th | en=clone]
                             [--hear th] [--original keep|lower|mute|replace|remove] [--exaggeration 0.5] [--consent]
          macOS 26+. Writes into the project unless --draft is given; voice-over goes into a new timeline.
          `clone` speaks in the original speaker's voice — only with their consent, which --consent confirms.
        """,
        "voices": "usage: ecc voices <language>\n  The system voices for a language code (e.g. en, th).",
        "clone": """
        usage: ecc clone <reference audio> --language en --text "…" -o out.wav [--exaggeration 0.5] [--seed 1]
          Downloads the voice cloning model on first use (1.55 GB).
        """,
        "mcp": "usage: ecc mcp\n  An MCP server on stdin/stdout: list, info, render and subtitles as tools (see docs/CLI.md).",
    ]

    static func text(for command: String?) -> String {
        command.flatMap { commands[$0] } ?? overview
    }

    static func isRequest(_ arg: String) -> Bool { arg == "--help" || arg == "-h" || arg == "help" }
}
