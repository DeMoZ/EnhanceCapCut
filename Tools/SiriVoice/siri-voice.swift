// Speaks EnhanceCapCut voice-over tracks with Siri voices. Ported from SubDub.
//
// macOS only exposes Siri voices to programs signed by Apple, so EnhanceCapCut can't use them itself.
// This script runs through the Swift interpreter (`swift siri-voice.swift …`, needs Xcode), which
// is Apple-signed and sees the Siri voices downloaded in System Settings ▸ Accessibility ▸ Read & Speak.
// The app runs it when a Siri voice is picked in the "Subtitles & Voice-over" window.
//
// usage: swift siri-voice.swift --list                 human-readable list of Siri voices
//        swift siri-voice.swift --list-json            same, as JSON (used by the app)
//        swift siri-voice.swift --render <job.json>    render a voice-over track (used by the app)
import AVFoundation

let sampleRate = 48_000.0
let maxSpeedUp = 1.35

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("error: \(message)\n".utf8))
    exit(1)
}

func siriVoices() -> [AVSpeechSynthesisVoice] {
    AVSpeechSynthesisVoice.speechVoices().filter { $0.identifier.contains("gryphon") || $0.identifier.contains(".siri.") }
}

func genderName(_ v: AVSpeechSynthesisVoice) -> String? {
    v.gender == .male ? "Male" : v.gender == .female ? "Female" : nil
}

struct Cue: Codable { var start: Double; var end: Double; var text: String }

// MARK: Synthesis

let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false)!
let synthesizer = AVSpeechSynthesizer()

func speak(_ text: String, voice: AVSpeechSynthesisVoice, rate: Float) -> AVAudioPCMBuffer {
    let u = AVSpeechUtterance(string: text)
    u.voice = voice
    u.rate = rate
    u.preUtteranceDelay = 0
    u.postUtteranceDelay = 0
    var pieces: [AVAudioPCMBuffer] = []
    var done = false
    synthesizer.write(u) { buffer in
        guard let pcm = buffer as? AVAudioPCMBuffer, pcm.frameLength > 0 else { done = true; return }
        pieces.append(pcm)
    }
    while !done { RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
    return trimSilence(convert(pieces))
}

func convert(_ pieces: [AVAudioPCMBuffer]) -> AVAudioPCMBuffer {
    guard let first = pieces.first, let converter = AVAudioConverter(from: first.format, to: format) else {
        return AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1)!
    }
    let frames = pieces.reduce(0) { $0 + Int($1.frameLength) }
    let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(Double(frames) * sampleRate / first.format.sampleRate) + 4096)!
    var index = 0
    var error: NSError?
    converter.convert(to: out, error: &error) { _, status in
        guard index < pieces.count else { status.pointee = .endOfStream; return nil }
        status.pointee = .haveData
        defer { index += 1 }
        return pieces[index]
    }
    return out
}

func trimSilence(_ b: AVAudioPCMBuffer) -> AVAudioPCMBuffer {
    guard let d = b.floatChannelData?[0], b.frameLength > 0 else { return b }
    let n = Int(b.frameLength), pad = Int(sampleRate * 0.02)
    var s = 0, e = n
    while s < n && abs(d[s]) < 0.003 { s += 1 }
    while e > s && abs(d[e - 1]) < 0.003 { e -= 1 }
    s = max(0, s - pad); e = min(n, e + pad)
    guard e > s else { return b }
    let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(e - s))!
    out.frameLength = AVAudioFrameCount(e - s)
    memcpy(out.floatChannelData![0], d + s, (e - s) * MemoryLayout<Float>.size)
    return out
}

func duration(_ b: AVAudioPCMBuffer) -> Double { Double(b.frameLength) / sampleRate }

/// Writes a 48 kHz mono WAV aligned to the timeline; each line is sped up just enough to fit
/// (binary search on measured length — `rate` maps to speed differently for every voice).
/// - Returns: the time ranges where speech was placed.
func render(_ cues: [Cue], voice: AVSpeechSynthesisVoice, totalFrames: AVAudioFramePosition, to output: URL,
            progress: (Int, Cue, Double) -> Void) -> [[Double]] {
    let temp = output.deletingLastPathComponent().appendingPathComponent(".\(output.lastPathComponent).tmp.wav")
    try? FileManager.default.removeItem(at: temp)
    let file: AVAudioFile
    do {
        file = try AVAudioFile(forWriting: temp, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: sampleRate, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
    } catch {
        fail("can't write \(temp.path): \(error.localizedDescription)")
    }

    func writeSilence(_ frames: AVAudioFramePosition) {
        var left = frames
        while left > 0 {
            let n = AVAudioFrameCount(min(left, 65_536))
            let b = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: n)!
            b.frameLength = n
            memset(b.floatChannelData![0], 0, Int(n) * MemoryLayout<Float>.size)
            try! file.write(from: b)
            left -= AVAudioFramePosition(n)
        }
    }

    var written: AVAudioFramePosition = 0
    var ranges: [[Double]] = []
    for (i, cue) in cues.enumerated() {
        let slotEnd = i + 1 < cues.count ? cues[i + 1].start : max(cue.end, Double(totalFrames) / sampleRate)
        // Measure the slot from where this line can actually start, so lines catch up after an overrun.
        let actualStart = max(cue.start, Double(written) / sampleRate)
        let slot = max(0.3, slotEnd - actualStart)
        // The last line has nothing after it to spill into: allow a bit more speed-up there.
        let limit = i + 1 < cues.count ? maxSpeedUp : 1.6
        var speech = speak(cue.text, voice: voice, rate: AVSpeechUtteranceDefaultSpeechRate)
        if duration(speech) > slot * 1.02 {
            let target = max(slot, duration(speech) / limit)
            var lo = AVSpeechUtteranceDefaultSpeechRate, hi = Float(0.85)
            var best = speech
            for _ in 0..<5 {
                let mid = (lo + hi) / 2
                let attempt = speak(cue.text, voice: voice, rate: mid)
                if duration(attempt) > target * 1.02 {
                    lo = mid
                    if duration(attempt) < duration(best) { best = attempt }
                } else {
                    hi = mid
                    best = attempt
                }
            }
            speech = best
        }
        let start = max(AVAudioFramePosition(cue.start * sampleRate), written)
        if start > written { writeSilence(start - written) }
        try! file.write(from: speech)
        written = start + AVAudioFramePosition(speech.frameLength)
        ranges.append([Double(start) / sampleRate, Double(written) / sampleRate])
        progress(i, cue, Double(start) / sampleRate)
    }
    if totalFrames > written { writeSilence(totalFrames - written) }
    if Double(written - totalFrames) / sampleRate > 0.05 {
        print("note: the dub runs \(String(format: "%.1f", Double(written - totalFrames) / sampleRate)) s past the end")
    }
    file.close() // finalizes the WAV header
    try? FileManager.default.removeItem(at: output)
    try! FileManager.default.moveItem(at: temp, to: output)
    return ranges
}


let args = Array(CommandLine.arguments.dropFirst())

switch args.first {
case "--list-json":
    let list = siriVoices().map { v -> [String: String] in
        var d = ["id": v.identifier, "name": v.name, "language": v.language]
        d["gender"] = genderName(v)
        return d
    }
    let data = try! JSONSerialization.data(withJSONObject: list)
    print(String(decoding: data, as: UTF8.self))

case "--render":
    // job.json: {"voice": id, "output": path, "totalSeconds": n, "cues": [{start, end, text}]}
    struct Job: Codable { var voice: String; var output: String; var totalSeconds: Double; var cues: [Cue] }
    guard args.count >= 2, let data = FileManager.default.contents(atPath: args[1]),
          let job = try? JSONDecoder().decode(Job.self, from: data) else { fail("--render needs a job.json") }
    guard let voice = AVSpeechSynthesisVoice(identifier: job.voice) else { fail("voice \(job.voice) is not installed") }
    let ranges = render(job.cues, voice: voice, totalFrames: AVAudioFramePosition(job.totalSeconds * sampleRate),
                        to: URL(fileURLWithPath: job.output)) { i, _, _ in
        print("PROGRESS \(i + 1) \(job.cues.count)")
        fflush(stdout)
    }
    print("RANGES " + String(decoding: try! JSONSerialization.data(withJSONObject: ranges), as: UTF8.self))

default:
    print("Siri voices available to this tool:")
    for v in siriVoices() {
        print("  \(v.language)\t\(genderName(v) ?? "-")\t\(v.name)\t\(v.identifier)")
    }
}
