# ecc — the command line

The `ecc` tool (built next to the app, also inside the Mac bundle at `Contents/MacOS/ecc` and in the Windows zip as
`ecc.exe`) uses the same renderer as the app. It always speaks English.

```sh
ecc list                                   # projects, * = open in CapCut
ecc info ChiangMaiMarkets                   # what will be rendered, unsupported features, missing media
ecc render 1014 -o ~/Movies/1014.mp4 --codec hevc10 --quality 70
ecc render KohChangFisherman --timeline 2 # one of several timelines, by number or name
ecc render 1014 --from 10 --duration 5 --codec prores422 -o /tmp/test.mov
ecc voice clip.mov -o voice.wav --keep-voice # audition the voice processing on one file
ecc subtitles DurianDick --language ru-RU --translate en,th --show en --srt ~/Movies   # add captions to the project
ecc subtitles DurianDick --language ru-RU --voice en,th --hear en --no-captions        # voice-over only
ecc subtitles ATV --language ru-RU --translate en --voice en=clone --consent --original replace   # dub in the speaker's voice
ecc clone reference.wav --language en --text "Hello there." -o hello.wav   # audition voice cloning
ecc voices th                              # installed voices for a language, with their ids
```

Options: `--codec h264|hevc|hevc10|prores422|prores422HQ`, `--quality 0-100`, `--bitrate Mbps`,
`--fps N`, `--aspect project|9:16|1:1|16:9`, `--height 720|1080|1440|2160`, `--lufs -14`,
`--no-subtitles`, `--from s`, `--duration s`, `--timeline name|number`. Without a project argument the open project is used;
unspecified options fall back to the app's saved settings. `ecc voice` takes `--keep-voice`,
`--remove-voice`, `--denoise 0.85`, `--enhance 75` (the ambient sound level) and/or
`--filter <id>` with `--strength 1` (a voice filter by its CapCut resource id or cached effect folder).
Voice filters are read from CapCut's own effect cache, so a filter renders only after CapCut has
downloaded it (i.e. it has been used or previewed in CapCut on this Mac).
`ecc subtitles` takes `--language` (spoken, e.g. `ru-RU`), `--translate en,th`, `--no-original`, `--show <code>`
(the visible track), `--keep-previous` (don't replace earlier tracks), `--srt <folder>` and `--draft out.json`
(write the changed draft there instead of into the project), `--no-captions`, `--voice en,th` (voice-over
languages; `th=<voice id>` picks a voice from `ecc voices`, Siri voices as `siri:<id>`, `en=clone` the cloned
original voice, which needs `--consent` to confirm the speakers agreed, and takes `--exaggeration 0.5`),
`--hear <code>` (the audible voice-over track) and `--original keep|lower|mute|replace|remove` (default `lower`).
Translation languages must already be downloaded (the app's window can download them).

The Windows `ecc` has the same commands except the Mac-only ones (Apple voices, Siri, ProRes); see
[windows/README.md](../windows/README.md).
