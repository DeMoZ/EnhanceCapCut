import AVFoundation
import CoreGraphics
import Foundation

public struct ExportSettings: Codable, Equatable {
    public enum Aspect: String, Codable, CaseIterable, Identifiable {
        case project, portrait = "9:16", square = "1:1", landscape = "16:9"
        public var id: String { rawValue }
        public var title: String { self == .project ? "From project" : rawValue }
    }

    public enum Resolution: Int, Codable, CaseIterable, Identifiable {
        /// Keep the project's canvas size.
        case project = 0
        case p720 = 720, p1080 = 1080, p1440 = 1440, p2160 = 2160
        public var id: Int { rawValue }
        public var title: String {
            switch self {
            case .project: return "From project"
            case .p2160: return "4K (2160p)"
            default: return "\(rawValue)p"
            }
        }
    }

    public enum Codec: String, Codable, CaseIterable, Identifiable {
        case h264, hevc, hevc10, prores422, prores422HQ
        public var id: String { rawValue }
        public var title: String {
            switch self {
            case .h264: return "H.264"
            case .hevc: return "HEVC (8-bit)"
            case .hevc10: return "HEVC (10-bit)"
            case .prores422: return "ProRes 422"
            case .prores422HQ: return "ProRes 422 HQ"
            }
        }
        public var isProRes: Bool { self == .prores422 || self == .prores422HQ }
        public var fileExtension: String { isProRes ? "mov" : "mp4" }
        public var fileType: AVFileType { isProRes ? .mov : .mp4 }
        var avCodec: AVVideoCodecType {
            switch self {
            case .h264: return .h264
            case .hevc, .hevc10: return .hevc
            case .prores422: return .proRes422
            case .prores422HQ: return .proRes422HQ
            }
        }
    }

    public enum RateControl: String, Codable, CaseIterable, Identifiable {
        case quality, bitrate
        public var id: String { rawValue }
    }

    public var aspect: Aspect = .project
    public var resolution: Resolution = .project
    public var codec: Codec = .hevc10
    public var rateControl: RateControl = .quality
    /// 0...100, mapped to VideoToolbox's quality setting (CRF-like: constant quality, variable size).
    public var quality: Double = 65
    /// Megabits per second, used when `rateControl == .bitrate`.
    public var bitrateMbps: Double = 16
    /// 0 = from project.
    public var frameRate: Double = 0
    /// Burn CapCut captions (auto-subtitles) into the picture. Plain text clips are always rendered.
    public var burnInSubtitles = true
    public var normalizeLoudness = false
    public var targetLUFS: Double = -14
    public var outputFolder: String = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Movies").path
    /// Tokens: {project} {date} {time} {res} {codec} {fps}.
    public var fileNameTemplate = "{project}_{date}_{time}"

    public init() {}

    // Settings saved by an older version may lack newer keys: fall back to defaults per key.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = ExportSettings()
        aspect = (try? c.decode(Aspect.self, forKey: .aspect)) ?? d.aspect
        resolution = (try? c.decode(Resolution.self, forKey: .resolution)) ?? d.resolution
        codec = (try? c.decode(Codec.self, forKey: .codec)) ?? d.codec
        rateControl = (try? c.decode(RateControl.self, forKey: .rateControl)) ?? d.rateControl
        quality = (try? c.decode(Double.self, forKey: .quality)) ?? d.quality
        bitrateMbps = (try? c.decode(Double.self, forKey: .bitrateMbps)) ?? d.bitrateMbps
        frameRate = (try? c.decode(Double.self, forKey: .frameRate)) ?? d.frameRate
        burnInSubtitles = (try? c.decode(Bool.self, forKey: .burnInSubtitles)) ?? d.burnInSubtitles
        normalizeLoudness = (try? c.decode(Bool.self, forKey: .normalizeLoudness)) ?? d.normalizeLoudness
        targetLUFS = (try? c.decode(Double.self, forKey: .targetLUFS)) ?? d.targetLUFS
        outputFolder = (try? c.decode(String.self, forKey: .outputFolder)) ?? d.outputFolder
        fileNameTemplate = (try? c.decode(String.self, forKey: .fileNameTemplate)) ?? d.fileNameTemplate
    }

    // MARK: Derived values

    /// Output size in pixels (even numbers, as encoders require).
    public func outputSize(canvas: CGSize) -> CGSize {
        let ratio: CGFloat
        switch aspect {
        case .project: ratio = canvas.width / max(canvas.height, 1)
        case .portrait: ratio = 9.0 / 16.0
        case .square: ratio = 1
        case .landscape: ratio = 16.0 / 9.0
        }
        var w: CGFloat, h: CGFloat
        if resolution == .project {
            // Keep the canvas's short side.
            let short = min(canvas.width, canvas.height)
            if ratio >= 1 { h = short; w = short * ratio } else { w = short; h = short / ratio }
        } else {
            let short = CGFloat(resolution.rawValue)
            if ratio >= 1 { h = short; w = short * ratio } else { w = short; h = short / ratio }
        }
        func even(_ v: CGFloat) -> CGFloat { max(2, (v / 2).rounded() * 2) }
        return CGSize(width: even(w), height: even(h))
    }

    public func fps(project: Double) -> Double { frameRate > 0 ? frameRate : (project > 0 ? project : 30) }

    public func fileName(project: String, size: CGSize, fps: Double, date: Date = Date()) -> String {
        let day = DateFormatter(), time = DateFormatter()
        for f in [day, time] {
            // Fixed Gregorian dates regardless of the system calendar (e.g. Buddhist in Thailand).
            f.calendar = Calendar(identifier: .gregorian)
            f.locale = Locale(identifier: "en_US_POSIX")
        }
        day.dateFormat = "yyyy-MM-dd"
        time.dateFormat = "HHmmss"
        var name = fileNameTemplate
            .replacingOccurrences(of: "{project}", with: project)
            .replacingOccurrences(of: "{date}", with: day.string(from: date))
            .replacingOccurrences(of: "{time}", with: time.string(from: date))
            .replacingOccurrences(of: "{res}", with: "\(Int(size.width))x\(Int(size.height))")
            .replacingOccurrences(of: "{codec}", with: codec.rawValue)
            .replacingOccurrences(of: "{fps}", with: fps == fps.rounded() ? String(Int(fps)) : String(format: "%.2f", fps))
        name = name.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        if name.trimmingCharacters(in: .whitespaces).isEmpty { name = project }
        return name + "." + codec.fileExtension
    }

    // MARK: Persistence

    private static let defaultsKey = "ExportSettings.v1"

    public static func load(from defaults: UserDefaults = .standard) -> ExportSettings {
        guard let data = defaults.data(forKey: defaultsKey),
              let settings = try? JSONDecoder().decode(ExportSettings.self, from: data) else { return ExportSettings() }
        return settings
    }

    public func save(to defaults: UserDefaults = .standard) {
        if let data = try? JSONEncoder().encode(self) { defaults.set(data, forKey: Self.defaultsKey) }
    }
}
