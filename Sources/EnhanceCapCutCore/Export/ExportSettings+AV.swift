import AVFoundation

extension ExportSettings.Codec {
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
