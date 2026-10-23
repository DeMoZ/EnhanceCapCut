using Vortice.MediaFoundation;

namespace EnhanceCapCut.Windows;

/// <summary>Media Foundation start-up and the format constants the renderer uses.</summary>
public static class MF
{
    static readonly Lock gate = new();
    static bool started;

    /// <summary>Starts Media Foundation once per process.</summary>
    public static void Startup()
    {
        lock (gate)
        {
            if (started) return;
            MediaFactory.MFStartup().CheckError();
            started = true;
        }
    }

    /// <summary>A Media Foundation subtype from a FourCC or D3DFORMAT value (<c>XXXXXXXX-0000-0010-8000-00AA00389B71</c>).</summary>
    static Guid Format(uint code) => new(code, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    static uint FourCC(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

    public static readonly Guid Rgb32 = Format(22);   // D3DFMT_X8R8G8B8: BGRA in memory
    public static readonly Guid H264 = Format(FourCC("H264"));
    public static readonly Guid Hevc = Format(FourCC("HEVC"));
    public static readonly Guid Nv12 = Format(FourCC("NV12"));
    public static readonly Guid P010 = Format(FourCC("P010"));
    public static readonly Guid Pcm = Format(1);      // WAVE_FORMAT_PCM
    public static readonly Guid Float = Format(3);    // WAVE_FORMAT_IEEE_FLOAT
    public static readonly Guid Aac = Format(0x1610); // WAVE_FORMAT_MPEG_HEAAC

    // MFVideoPrimaries_BT709, MFVideoTransFunc_709, MFVideoTransferMatrix_BT709, MFNominalRange_16_235.
    public const uint PrimariesBT709 = 2, TransferBT709 = 5, MatrixBT709 = 1, NominalRange16To235 = 2;
    // MFVideoTransFunc_2084 (PQ) and MFVideoTransFunc_HLG.
    public const uint TransferPQ = 15, TransferHLG = 16;
    /// <summary>MFVideoInterlace_Progressive.</summary>
    public const uint Progressive = 2;
    /// <summary>eAVEncH264VProfile_High, eAVEncH265VProfile_Main_420_8, eAVEncH265VProfile_Main_420_10.</summary>
    public const uint H264High = 100, HevcMain = 1, HevcMain10 = 2;

    public static ulong Pack(uint high, uint low) => (ulong)high << 32 | low;
    public static (uint High, uint Low) Unpack(ulong value) => ((uint)(value >> 32), (uint)value);

    /// <summary>MF_SOURCE_READER_FIRST_VIDEO_STREAM / FIRST_AUDIO_STREAM / ALL_STREAMS.</summary>
    public const int FirstVideoStream = unchecked((int)0xFFFFFFFC), FirstAudioStream = unchecked((int)0xFFFFFFFD),
        AllStreams = unchecked((int)0xFFFFFFFE);

    public static long ToTicks(long us) => us * 10;
    public static long ToMicroseconds(long ticks) => (long)Math.Round(ticks / 10.0, MidpointRounding.AwayFromZero);
}
