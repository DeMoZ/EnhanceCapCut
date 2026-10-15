import Accelerate
import AudioToolbox
import AVFoundation

/// Renders CapCut's per-clip "Isolate voice" (keep / remove vocal) and "Reduce noise" into temporary
/// files that the mixer plays instead of the original media.
///
/// CapCut runs its own models; it caches separation results in the project, but obfuscated, so we
/// process the source ourselves:
/// - Keep vocal: Apple's on-device voice isolation (`AUSoundIsolation`, high-quality voice mode).
/// - Remove vocal: the source minus that voice estimate, as a spectral Wiener mask (`VocalRemover`).
/// - Reduce noise: `rate × voice + (1 − rate) × source` — CapCut's denoiser (an ML model) removes
///   wind, water and other ambience, not just stationary hiss, which the voice isolation reproduces.
///   On a clip whose voice is removed, the background gets a stationary-noise spectral gate
///   (`SpectralDenoiser`) instead; on a "Keep vocal" clip there's nothing left to remove.
/// - Enhance voice: the ambience (source minus voice) is turned down by `VoiceEnhancer.ambientGain`,
///   then `VoiceEnhancer` applies CapCut's gain and EQ. With "Reduce noise" also on, the lower of the
///   two ambience levels wins. Not applied to "Remove vocal" clips (no voice left to enhance).
/// - Voice filter: the effect's cached processor graph (`VoiceFilterGraph`) runs last, from the
///   clip's first source sample so reverb tails and compressor state start where CapCut's do.
///
/// Each clip's source range (plus some context) is processed once and cached for the render.
final class VoiceProcessor {
    struct Output {
        let url: URL
        /// Source media time (µs) at time 0 of `url`; may be negative.
        let sourceOffset: Int64
    }

    static let sampleRate = 48_000.0
    /// Extra source audio around the clip so the models have context at its edges.
    static let padding: Int64 = 1_000_000
    /// AUSoundIsolation's delay at 48 kHz (measured; the unit reports 0). Used when the delay can't
    /// be measured from the clip itself (e.g. no voice).
    static let isolationLatency = 6_360

    private let directory: URL
    private var cache: [Key: Output] = [:]
    private var counter = 0

    private struct Key: Hashable {
        var url: URL
        var start: Int64
        var end: Int64
        var effects: VoiceEffects
    }

    init(directory: URL) {
        self.directory = directory
    }

    /// Deletes the processed files; call once the mix has been read.
    func removeFiles() {
        try? FileManager.default.removeItem(at: directory)
        cache = [:]
    }

    /// The processed audio for `clip`, or nil when the clip has no voice effects.
    func process(_ clip: AudioClip, isCancelled: () -> Bool) async throws -> Output? {
        guard !clip.voice.isEmpty else { return nil }
        let start = max(0, clip.timeMap.sourceStart - Self.padding)
        let end = clip.timeMap.sourceStart + clip.timeMap.sourceDuration + Self.padding
        let key = Key(url: clip.url, start: start, end: end, effects: clip.voice)
        if let cached = cache[key] { return cached }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        guard let (sourceURL, sourceStart) = try await decode(clip.url, from: start, to: end) else { return nil }
        var url = sourceURL
        var offset = sourceStart
        switch clip.voice.separation {
        case .none:
            // The share of the ambience (source − voice) that's kept.
            var ambience: Double?
            if let strength = clip.voice.denoise { ambience = 1 - strength }
            if let level = clip.voice.enhance { ambience = min(ambience ?? 1, VoiceEnhancer.ambientGain(level: level)) }
            if let ambience, ambience < 1 {
                let (voice, latency) = try isolateVoice(sourceURL, isCancelled: isCancelled)
                url = newFile("denoised")
                try Self.blend(source: sourceURL, voice: voice, voiceStart: AVAudioFramePosition(latency),
                               voiceAmount: Float(1 - ambience), to: url, isCancelled: isCancelled)
            }
        case .keepVoice:
            let (voice, latency) = try isolateVoice(sourceURL, isCancelled: isCancelled)
            url = voice
            offset = sourceStart - Self.us(frames: latency)
        case .removeVoice:
            let (voice, latency) = try isolateVoice(sourceURL, isCancelled: isCancelled)
            url = newFile("background")
            try VocalRemover.process(mix: try Self.read(sourceURL), voice: try Self.read(voice),
                                     voiceStart: AVAudioFramePosition(latency), to: try Self.write(url),
                                     isCancelled: isCancelled)
        }
        if clip.voice.separation == .removeVoice, let strength = clip.voice.denoise {
            let denoised = newFile("denoised")
            try SpectralDenoiser.process(try Self.read(url), to: try Self.write(denoised), strength: strength,
                                         isCancelled: isCancelled)
            url = denoised
        }
        if clip.voice.enhance != nil, clip.voice.separation != .removeVoice {
            let enhanced = newFile("enhanced")
            try VoiceEnhancer.process(try Self.read(url), to: try Self.write(enhanced), isCancelled: isCancelled)
            url = enhanced
        }
        if let filter = clip.voice.filter {
            if isCancelled() { throw RenderError.cancelled }
            let filtered = newFile("filtered")
            let clipStart = AVAudioFramePosition((Double(clip.timeMap.sourceStart - offset) / 1_000_000 * Self.sampleRate).rounded())
            try Self.applyFilter(filter, to: try Self.read(url), from: max(0, clipStart), output: try Self.write(filtered))
            url = filtered
        }
        let output = Output(url: url, sourceOffset: offset)
        cache[key] = output
        return output
    }

    // MARK: Stages

    /// Decodes `[start, end)` µs of the media's first audio track to a 48 kHz stereo float file.
    /// Returns the file and the source time of its first sample, or nil if there's no audio.
    private func decode(_ url: URL, from start: Int64, to end: Int64) async throws -> (URL, Int64)? {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { return nil }
        let reader = try AVAssetReader(asset: asset)
        reader.timeRange = CMTimeRange(start: AudioRenderer.us(start), end: AudioRenderer.us(end))
        // The mix output converts any channel layout to stereo, like the renderer's reader.
        let output = AVAssetReaderAudioMixOutput(audioTracks: [track], audioSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Self.sampleRate,
            AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: true,
            AVLinearPCMIsBigEndianKey: false,
        ])
        reader.add(output)
        guard reader.startReading() else { throw RenderError.writerFailed(reader.error?.localizedDescription ?? "decode") }

        let fileURL = newFile("source")
        let file = try Self.write(fileURL)
        var first: Int64?
        while let sample = output.copyNextSampleBuffer() {
            let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sample))
            guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames) else { continue }
            buffer.frameLength = frames
            guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sample, at: 0, frameCount: Int32(frames),
                                                               into: buffer.mutableAudioBufferList) == noErr else { continue }
            if first == nil {
                let pts = CMSampleBufferGetPresentationTimeStamp(sample)
                first = pts.isNumeric ? Int64((pts.seconds * 1_000_000).rounded()) : start
            }
            try file.write(from: buffer)
        }
        file.close()
        if reader.status == .failed { throw RenderError.writerFailed(reader.error?.localizedDescription ?? "decode") }
        guard let first else { return nil }
        return (fileURL, first)
    }

    /// Runs the file through AUSoundIsolation. The result is delayed by the returned number of frames.
    private func isolateVoice(_ url: URL, isCancelled: () -> Bool) throws -> (URL, Int) {
        let input = try Self.read(url)
        let format = input.processingFormat
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        let unit = AVAudioUnitEffect(audioComponentDescription: AudioComponentDescription(
            componentType: kAudioUnitType_Effect, componentSubType: kAudioUnitSubType_AUSoundIsolation,
            componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0))
        engine.attach(player)
        engine.attach(unit)
        engine.connect(player, to: unit, format: format)
        engine.connect(unit, to: engine.mainMixerNode, format: format)
        let maxFrames: AVAudioFrameCount = 4096
        try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: maxFrames)
        let au = unit.audioUnit
        AudioUnitSetParameter(au, AudioUnitParameterID(kAUSoundIsolationParam_WetDryMixPercent), kAudioUnitScope_Global, 0, 100, 0)
        AudioUnitSetParameter(au, AudioUnitParameterID(kAUSoundIsolationParam_SoundToIsolate), kAudioUnitScope_Global, 0,
                              AudioUnitParameterValue(kAUSoundIsolationSoundType_HighQualityVoice), 0)
        try engine.start()
        defer { engine.stop() }
        player.scheduleFile(input, at: nil)
        player.play()

        let outURL = newFile("voice")
        let out = try Self.write(outURL)
        let buffer = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: maxFrames)!
        // Render past the end so the delayed tail is complete.
        let total = input.length + AVAudioFramePosition(Self.sampleRate / 2)
        while engine.manualRenderingSampleTime < total {
            if isCancelled() { throw RenderError.cancelled }
            let frames = AVAudioFrameCount(min(AVAudioFramePosition(maxFrames), total - engine.manualRenderingSampleTime))
            guard try engine.renderOffline(frames, to: buffer) == .success else {
                throw RenderError.writerFailed("voice isolation")
            }
            try out.write(from: buffer)
        }
        out.close()
        let latency = (try? Self.measureDelay(reference: url, delayed: outURL)) ?? nil
        return (outURL, latency ?? Self.isolationLatency)
    }

    /// `voiceAmount × voice + (1 − voiceAmount) × source`, with the voice read from `voiceStart`.
    static func blend(source: URL, voice: URL, voiceStart: AVAudioFramePosition, voiceAmount: Float, to url: URL,
                      isCancelled: () -> Bool) throws {
        let a = try read(source), b = try read(voice), out = try write(url)
        b.framePosition = voiceStart
        let format = a.processingFormat
        let chunk: AVAudioFrameCount = 65_536
        let x = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: chunk)!
        let v = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: chunk)!
        var dry = 1 - voiceAmount, wet = voiceAmount
        while a.framePosition < a.length {
            if isCancelled() { throw RenderError.cancelled }
            try a.read(into: x, frameCount: chunk)
            guard x.frameLength > 0 else { break }
            let frames = Int(x.frameLength)
            for c in 0..<Int(format.channelCount) { memset(v.floatChannelData![c], 0, frames * MemoryLayout<Float>.size) }
            if b.framePosition < b.length {
                try b.read(into: v, frameCount: AVAudioFrameCount(min(AVAudioFramePosition(frames), b.length - b.framePosition)))
            }
            for c in 0..<Int(format.channelCount) {
                let xc = x.floatChannelData![c], vc = v.floatChannelData![c]
                vDSP_vsmul(xc, 1, &dry, xc, 1, vDSP_Length(frames))
                vDSP_vsma(vc, 1, &wet, xc, 1, xc, 1, vDSP_Length(frames))
            }
            try out.write(from: x)
        }
        out.close()
    }

    /// Runs a voice filter over `input` from frame `start` on (earlier frames pass through untouched).
    static func applyFilter(_ filter: VoiceFilter, to input: AVAudioFile, from start: AVAudioFramePosition,
                            output: AVAudioFile) throws {
        let graph = try VoiceFilterGraph.load(directory: filter.directory)
        let format = input.processingFormat
        let frames = AVAudioFrameCount(input.length)
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return }
        try input.read(into: buffer)
        let count = Int(buffer.frameLength), first = min(Int(start), count)
        let channels = (0..<Int(format.channelCount)).map { c in
            Array(UnsafeBufferPointer(start: buffer.floatChannelData![c] + first, count: count - first))
        }
        let processed = try graph.process(channels, rate: format.sampleRate, strength: filter.strength)
        for (c, x) in processed.enumerated() where c < channels.count {
            x.withUnsafeBufferPointer { (buffer.floatChannelData![c] + first).update(from: $0.baseAddress!, count: min(x.count, count - first)) }
        }
        try output.write(from: buffer)
        output.close()
    }

    // MARK: Delay measurement

    /// Delay (frames) of `delayed` relative to `reference`, from the cross-correlation of their mono
    /// mixes over the first 30 s; nil when the signals are too dissimilar (e.g. no voice at all).
    static func measureDelay(reference: URL, delayed: URL, maxDelay: Int = 24_000) throws -> Int? {
        let span = Int(sampleRate * 30)
        let a = try monoSamples(reference, count: span), b = try monoSamples(delayed, count: span + maxDelay)
        guard a.count > 4 * 4_800, b.count >= a.count else { return nil }
        // Coarse search on 8× decimated signals, then refine at full rate.
        let factor = 8
        func decimate(_ x: [Float]) -> [Float] {
            stride(from: 0, to: x.count - factor + 1, by: factor).map { i in
                var s: Float = 0
                for j in 0..<factor { s += x[i + j] }
                return s
            }
        }
        let da = decimate(a), db = decimate(b)
        let (coarse, _) = bestLag(da, db, lags: 0...(maxDelay / factor), length: da.count)
        let lo = max(0, coarse * factor - factor * 2), hi = coarse * factor + factor * 2
        let (lag, correlation) = bestLag(a, b, lags: lo...hi, length: a.count)
        return correlation > 0.3 ? lag : nil
    }

    /// The lag in `lags` maximizing the normalized correlation of a[i] and b[i + lag].
    private static func bestLag(_ a: [Float], _ b: [Float], lags: ClosedRange<Int>, length: Int) -> (Int, Float) {
        let n = min(length, b.count - lags.upperBound)
        guard n > 0 else { return (lags.lowerBound, 0) }
        var energyA: Float = 0
        vDSP_svesq(a, 1, &energyA, vDSP_Length(n))
        var best = (lags.lowerBound, Float(-1))
        a.withUnsafeBufferPointer { pa in
            b.withUnsafeBufferPointer { pb in
                for lag in lags {
                    var dot: Float = 0, energyB: Float = 0
                    vDSP_dotpr(pa.baseAddress!, 1, pb.baseAddress! + lag, 1, &dot, vDSP_Length(n))
                    vDSP_svesq(pb.baseAddress! + lag, 1, &energyB, vDSP_Length(n))
                    let c = dot / max(sqrt(energyA * energyB), 1e-20)
                    if c > best.1 { best = (lag, c) }
                }
            }
        }
        return best
    }

    private static func monoSamples(_ url: URL, count: Int) throws -> [Float] {
        let file = try read(url)
        let frames = AVAudioFrameCount(min(AVAudioFramePosition(count), file.length))
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames) else { return [] }
        try file.read(into: buffer, frameCount: frames)
        let channels = Int(file.processingFormat.channelCount)
        var mono = [Float](repeating: 0, count: Int(buffer.frameLength))
        for c in 0..<channels { vDSP_vadd(mono, 1, buffer.floatChannelData![c], 1, &mono, 1, vDSP_Length(mono.count)) }
        return mono
    }

    // MARK: Files

    private func newFile(_ name: String) -> URL {
        counter += 1
        return directory.appendingPathComponent("\(counter)-\(name).caf")
    }

    /// 48 kHz stereo float; planar in memory, interleaved on disk.
    static func write(_ url: URL) throws -> AVAudioFile {
        try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
    }

    static func read(_ url: URL) throws -> AVAudioFile {
        try AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false)
    }

    static func us(frames: Int) -> Int64 { Int64((Double(frames) / sampleRate * 1_000_000).rounded()) }
}

/// CapCut's "Enhance voice", approximated from the author's exports (see docs/FORMAT.md): the voice
/// comes out ~2.8 dB louder with the rumble below ~80 Hz cut and a strong "air" boost around 13 kHz;
/// the ambience is kept at a level-dependent share. The ambience reduction itself happens in
/// `VoiceProcessor` (voice isolation blend); this applies the gain and EQ.
enum VoiceEnhancer {
    /// Broadband gain on voiced parts (+2.8 dB; 2.2–3.0 dB on three clips).
    static let gain: Float = 1.38
    /// Zero-phase high-pass: a 2nd-order Butterworth run forward and backward (exports: −3 dB around
    /// 80 Hz, −8 dB at 63 Hz, −11 dB at 50 Hz). CapCut's low cut doesn't shift phase: a plain
    /// minimum-phase filter drops the waveform correlation with the exports from 0.98 to 0.84.
    static let highPassHz = 68.0
    /// Peaking boost fitted to the exports (+2 dB at 8 kHz, +5 at 10 kHz, +11 at 12.8 kHz, +7 at 16 kHz).
    static let airHz = 13_700.0, airGainDB = 12.9, airQ = 1.42
    /// The ambience share kept at `ambient_sound_level` 75, measured on KoSamet's camera audio as
    /// 0.26–0.37 of the voice level; the other levels are interpolated as `level^k` (1 at 100, 0 at 0)
    /// since no export with another level was available to check against.
    static let ambientAt75 = 0.3

    /// The share of the ambience kept for `level` (0...1).
    static func ambientGain(level: Double) -> Double {
        let k = log(ambientAt75) / log(0.75)
        return pow(min(max(level, 0), 1), k)
    }

    struct Biquad {
        var b0, b1, b2, a1, a2: Double

        static func highPass(_ f: Double, q: Double = 1 / 2.squareRoot(), rate: Double) -> Biquad {
            let w = 2 * Double.pi * f / rate, alpha = sin(w) / (2 * q), cw = cos(w), a0 = 1 + alpha
            return Biquad(b0: (1 + cw) / 2 / a0, b1: -(1 + cw) / a0, b2: (1 + cw) / 2 / a0,
                          a1: -2 * cw / a0, a2: (1 - alpha) / a0)
        }

        static func peak(_ f: Double, gainDB: Double, q: Double, rate: Double) -> Biquad {
            let a = pow(10, gainDB / 40), w = 2 * Double.pi * f / rate, alpha = sin(w) / (2 * q), cw = cos(w)
            let a0 = 1 + alpha / a
            return Biquad(b0: (1 + alpha * a) / a0, b1: -2 * cw / a0, b2: (1 - alpha * a) / a0,
                          a1: -2 * cw / a0, a2: (1 - alpha / a) / a0)
        }

        /// Filters `x` in place, forward or backward.
        func run(_ x: inout [Float], reverse: Bool = false) {
            var x1 = 0.0, x2 = 0.0, y1 = 0.0, y2 = 0.0
            for j in 0..<x.count {
                let i = reverse ? x.count - 1 - j : j
                let input = Double(x[i])
                let y = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
                x2 = x1; x1 = input; y2 = y1; y1 = y
                x[i] = Float(y)
            }
        }
    }

    /// Applies the EQ and gain to one channel of a whole clip.
    static func apply(_ x: inout [Float], rate: Double) {
        let highPass = Biquad.highPass(highPassHz, rate: rate)
        highPass.run(&x)
        highPass.run(&x, reverse: true)
        Biquad.peak(airHz, gainDB: airGainDB, q: airQ, rate: rate).run(&x)
        var g = gain
        vDSP_vsmul(x, 1, &g, &x, 1, vDSP_Length(x.count))
    }

    /// Processes the whole file in memory (the zero-phase filter runs backward over it).
    static func process(_ input: AVAudioFile, to output: AVAudioFile, isCancelled: () -> Bool) throws {
        let format = input.processingFormat
        let frames = AVAudioFrameCount(input.length)
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return }
        try input.read(into: buffer)
        for c in 0..<Int(format.channelCount) {
            if isCancelled() { throw RenderError.cancelled }
            let data = buffer.floatChannelData![c]
            var x = Array(UnsafeBufferPointer(start: data, count: Int(buffer.frameLength)))
            apply(&x, rate: format.sampleRate)
            x.withUnsafeBufferPointer { data.update(from: $0.baseAddress!, count: x.count) }
        }
        try output.write(from: buffer)
        output.close()
    }
}

/// Applies voice effects to a whole media file and writes a 48 kHz stereo float WAV aligned with the
/// source (used by `ecc voice` to audition the processing).
public enum VoiceEffectsRenderer {
    public static func render(_ media: URL, effects: VoiceEffects, to output: URL) async throws {
        let duration = try await AVURLAsset(url: media).load(.duration)
        let length = Int64(duration.seconds * 1_000_000)
        let clip = AudioClip(url: media, target: TimeRange(start: 0, duration: length),
                             timeMap: TimeMap(sourceStart: 0, sourceDuration: length, targetDuration: length),
                             volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: true, voice: effects)
        let processor = VoiceProcessor(directory: FileManager.default.temporaryDirectory
            .appendingPathComponent("EnhanceCapCut-voice-\(UUID().uuidString)"))
        defer { processor.removeFiles() }
        guard let result = try await processor.process(clip, isCancelled: { false }) else {
            throw RenderError.writerFailed("no audio or no effects")
        }
        let input = try VoiceProcessor.read(result.url)
        let skip = AVAudioFramePosition((Double(-result.sourceOffset) / 1_000_000 * VoiceProcessor.sampleRate).rounded())
        input.framePosition = max(0, skip)
        try? FileManager.default.removeItem(at: output)
        let out = try AVAudioFile(forWriting: output, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: VoiceProcessor.sampleRate,
            AVNumberOfChannelsKey: 2, AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
        var left = AVAudioFramePosition(duration.seconds * VoiceProcessor.sampleRate)
        let buffer = AVAudioPCMBuffer(pcmFormat: input.processingFormat, frameCapacity: 65_536)!
        while left > 0, input.framePosition < input.length {
            try input.read(into: buffer, frameCount: AVAudioFrameCount(min(65_536, left)))
            guard buffer.frameLength > 0 else { break }
            try out.write(from: buffer)
            left -= AVAudioFramePosition(buffer.frameLength)
        }
        out.close()
    }
}
