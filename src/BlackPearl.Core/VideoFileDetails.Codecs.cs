using System.Globalization;

namespace BlackPearl.Core;

// Codec configuration records, shared by Matroska (CodecPrivate) and MP4 (the sample entry's child boxes): the profile,
// level, chroma format and bit depth of H.264, HEVC, AV1 and VP9 video, and AAC's object type. Colour and HDR
// descriptions use the ITU-T H.273 code points both containers carry.
public static partial class VideoFileDetails
{
    /// <summary>Config: a Matroska codec ID, or an MP4 configuration box type (avcC, hvcC, av1C, vpcC).</summary>
    private static void AddCodecProfile(Section track, string config, byte[] data)
    {
        switch (config)
        {
            case "V_MPEG4/ISO/AVC" or "avcC" when data.Length >= 4:
            {
                var profile = data[1] switch
                {
                    66 => (data[2] & 0x40) != 0 ? "Constrained Baseline" : "Baseline", 77 => "Main", 88 => "Extended", 100 => "High", 110 => "High 10",
                    122 => "High 4:2:2", 244 => "High 4:4:4 Predictive", 44 => "CAVLC 4:4:4", 118 or 128 => "Multiview High", _ => "Profile " + data[1]
                };
                track.Add("Profile", profile + ", level " + (data[3] == 9 ? "1b" : (data[3] / 10.0).ToString("0.#", CultureInfo.InvariantCulture)));
                // The High profiles' extension: chroma_format, then bit depths less 8.
                if (data[1] is 100 or 110 or 122 or 144 or 244 && AvcExtension(data) is { } extension)
                {
                    track.Add("Chroma subsampling", Chroma(extension.Chroma));
                    AddOnce(track, "Bit depth", $"{extension.Depth} bits");
                }
                break;
            }
            case "V_MPEGH/ISO/HEVC" or "hvcC" when data.Length >= 23:
            {
                var profile = (data[1] & 0x1f) switch
                {
                    1 => "Main", 2 => "Main 10", 3 => "Main Still Picture", 4 => "Format range extensions", 5 => "High throughput", 6 => "Multiview Main",
                    7 => "Scalable Main", 8 => "3D Main", 9 => "Screen content coding", 10 => "Scalable range extensions", var other => "Profile " + other
                };
                var tier = (data[1] & 0x20) != 0 ? ", high tier" : "";
                track.Add("Profile", profile + ", level " + (data[12] / 30.0).ToString("0.#", CultureInfo.InvariantCulture) + tier);
                track.Add("Chroma subsampling", Chroma(data[16] & 3));
                AddOnce(track, "Bit depth", $"{(data[17] & 7) + 8} bits");
                break;
            }
            case "V_AV1" or "av1C" when data.Length >= 3 && (data[0] & 0x80) != 0:
            {
                var profile = (data[1] >> 5) switch { 0 => "Main", 1 => "High", 2 => "Professional", var other => "Profile " + other };
                var level = data[1] & 0x1f;
                track.Add("Profile", profile + (level == 31 ? "" : $", level {2 + (level >> 2)}.{level & 3}") + ((data[2] & 0x80) != 0 ? ", high tier" : ""));
                var depth = (data[2] & 0x40) == 0 ? 8 : (data[2] & 0x20) != 0 ? 12 : 10;
                AddOnce(track, "Bit depth", $"{depth} bits");
                track.Add("Chroma subsampling", (data[2] & 0x10) != 0 ? "Monochrome" : ((data[2] >> 3 & 1) == 1, (data[2] >> 2 & 1) == 1) switch
                {
                    (true, true) => "4:2:0", (true, false) => "4:2:2", _ => "4:4:4"
                });
                break;
            }
            case "vpcC" when data.Length >= 7:
                track.Add("Profile", $"Profile {data[4]}, level {(data[5] / 10.0).ToString("0.#", CultureInfo.InvariantCulture)}");
                AddOnce(track, "Bit depth", $"{data[6] >> 4} bits");
                track.Add("Chroma subsampling", (data[6] >> 1 & 7) switch { 0 or 1 => "4:2:0", 2 => "4:2:2", 3 => "4:4:4", _ => null });
                break;
            case "V_VP9" when data.Length >= 3:
                // CodecPrivate: features as id, length, value: 1 profile, 2 level, 3 bit depth, 4 chroma subsampling.
                for (var i = 0; i + 2 < data.Length; i += 2 + data[i + 1])
                    if (data[i + 1] == 1)
                        switch (data[i])
                        {
                            case 1: track.Add("Profile", "Profile " + data[i + 2]); break;
                            case 3: AddOnce(track, "Bit depth", $"{data[i + 2]} bits"); break;
                        }
                break;
        }
    }
    private static (int Chroma, int Depth)? AvcExtension(byte[] data)
    {
        // version, profile, compatibility, level, lengthSizeMinusOne, then the SPS and PPS lists.
        var position = 5;
        if (data.Length <= position) return null;
        var sps = data[position++] & 0x1f;
        for (var i = 0; i < sps; i++) { if (position + 2 > data.Length) return null; position += 2 + (data[position] << 8 | data[position + 1]); }
        if (position >= data.Length) return null;
        var pps = data[position++];
        for (var i = 0; i < pps; i++) { if (position + 2 > data.Length) return null; position += 2 + (data[position] << 8 | data[position + 1]); }
        if (position + 3 > data.Length) return null;
        return (data[position] & 3, (data[position + 1] & 7) + 8);
    }
    private static string? Chroma(int format) => format switch { 0 => "Monochrome", 1 => "4:2:0", 2 => "4:2:2", 3 => "4:4:4", _ => null };
    private static void AddOnce(Section track, string label, string? value)
    {
        if (!track.Rows.Any(row => row.Label == label)) track.Add(label, value);
    }

    /// <summary>Range: 1 limited, 2 full (Matroska's values).</summary>
    private static void AddColor(Section track, ulong? primaries, ulong? transfer, ulong? matrix, ulong? range, ulong? maxCll, ulong? maxFall, bool mastering, bool dolbyVision)
    {
        var hdr = transfer switch { 16 => "HDR10 (PQ)", 18 => "HLG", _ => null };
        if (dolbyVision) hdr = hdr is null ? "Dolby Vision" : "Dolby Vision, " + hdr + " compatible";
        track.Add("HDR", hdr);
        track.Add("Color primaries", primaries switch
        {
            1 => "BT.709", 4 => "BT.470 M", 5 => "BT.601 (PAL)", 6 => "BT.601 (NTSC)", 7 => "SMPTE 240M", 8 => "Generic film", 9 => "BT.2020",
            10 => "SMPTE ST 428 (XYZ)", 11 => "DCI-P3", 12 => "Display P3", 22 => "EBU 3213", _ => null
        });
        track.Add("Transfer characteristics", transfer switch
        {
            1 => "BT.709", 4 => "Gamma 2.2", 5 => "Gamma 2.8", 6 => "BT.601", 7 => "SMPTE 240M", 8 => "Linear", 11 => "IEC 61966-2-4", 13 => "sRGB",
            14 => "BT.2020 (10-bit)", 15 => "BT.2020 (12-bit)", 16 => "PQ (SMPTE ST 2084)", 17 => "SMPTE ST 428", 18 => "HLG (ARIB STD-B67)", _ => null
        });
        track.Add("Matrix coefficients", matrix switch
        {
            0 => "Identity (RGB)", 1 => "BT.709", 4 => "FCC", 5 => "BT.601 (PAL)", 6 => "BT.601 (NTSC)", 7 => "SMPTE 240M", 8 => "YCgCo",
            9 => "BT.2020 non-constant", 10 => "BT.2020 constant", 14 => "ICtCp", _ => null
        });
        track.Add("Color range", range switch { 1 => "Limited", 2 => "Full", _ => null });
        if (maxCll is > 0) track.Add("Maximum content light level", $"{maxCll} cd/m²");
        if (maxFall is > 0) track.Add("Maximum frame-average light level", $"{maxFall} cd/m²");
        if (mastering) track.Add("Mastering display metadata", true);
    }

    // AudioSpecificConfig: object type (5 bits, or 31 and 6 more), sampling frequency index (4 bits, or 15 and 24 bits of
    // rate), channel configuration (4 bits). Configuration 0 is described elsewhere in the stream.
    private static int? AacChannels(byte[] config)
    {
        if (config.Length < 2) return null;
        var bits = new BitReader(config);
        if (bits.Read(5) == 31) bits.Read(6);
        if (bits.Read(4) == 15) bits.Read(24);
        if (bits.Remaining < 4) return null;
        return bits.Read(4) switch { var c and >= 1 and <= 6 => c, 7 => 8, 11 => 7, 12 or 14 => 8, 13 => 24, _ => null };
    }

    // AudioSpecificConfig's object type.
    private static string? AacProfile(byte[] config)
    {
        if (config.Length < 1) return null;
        var type = config[0] >> 3;
        if (type == 31 && config.Length >= 2) type = 32 + ((config[0] & 7) << 3 | config[1] >> 5);
        return type switch
        {
            1 => "Main", 2 => "LC", 3 => "SSR", 4 => "LTP", 5 => "HE-AAC", 29 => "HE-AACv2", 23 => "LD", 39 => "ELD", 42 => "xHE-AAC", _ => null
        };
    }
}
