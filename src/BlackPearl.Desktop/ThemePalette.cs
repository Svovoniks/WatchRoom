using System.Windows.Media;

namespace BlackPearl.Desktop;

// A colour theme offered in Settings > Appearance. Dark and Light list colours in Keys order.
// The player keeps its own dark neutrals and takes only the dark accent, so video stays the brightest thing on screen.
public sealed record ThemePalette(string Id, string Name, string[] Dark, string[] Light)
{
    public static readonly string[] Keys = ["Canvas", "Surface", "Elevated", "Ink", "Muted", "Line", "Accent", "AccentInk", "Hover", "AccentSoft", "Success", "Danger"];

    public static readonly ThemePalette[] All =
    [
        new("sage", "Sage",
            ["#0F1311", "#151A17", "#1E2420", "#EAF0EB", "#9BA89F", "#28302B", "#9FD4A8", "#0E2A15", "#252C27", "#1F3324", "#6CC3D9", "#F07A6E"],
            ["#E3EAE3", "#EDF2ED", "#D6DFD7", "#17211A", "#526157", "#C9D5CB", "#3E7A4C", "#FFFFFF", "#DAE3DB", "#CFE5D3", "#1C6E8A", "#B23A33"]),
        new("dusk", "Dusk",
            ["#12111A", "#1A1824", "#242131", "#EFECF5", "#A29DB3", "#2E2A3D", "#FFB38A", "#331300", "#2B2839", "#3A2A2E", "#6CCB9A", "#FF6B81"],
            ["#E5E1EB", "#EFECF3", "#D9D4E2", "#1D1828", "#5A5468", "#CEC8D9", "#A64B1E", "#FFFFFF", "#DCD7E4", "#EDD5CB", "#23784F", "#B32D4A"]),
        new("olive-copper", "Olive & Copper",
            ["#10110D", "#171913", "#21231B", "#EEEEE4", "#A0A28F", "#2C2F24", "#E09A5C", "#2A1300", "#282B21", "#362A1C", "#8BC46A", "#EE6B5F"],
            ["#E3E3D5", "#EDEDE2", "#D7D8C6", "#1E2016", "#565946", "#CBCDB8", "#95501A", "#FFFFFF", "#DADBCB", "#E8D6C0", "#3D7A22", "#B23A2E"]),
        new("ember", "Ember",
            ["#14100E", "#1C1714", "#27201C", "#F4ECE6", "#AE9F94", "#332A24", "#FF8A5B", "#2E0E00", "#2F2722", "#3D2318", "#7CC48A", "#FF5D73"],
            ["#E8DED3", "#F2EBE3", "#DED2C4", "#2A1D15", "#64554A", "#D3C5B5", "#A9441A", "#FFF6F0", "#E0D5C8", "#EBCFBF", "#2C7A45", "#B3243F"]),
        new("lagoon", "Lagoon",
            ["#0B1214", "#10191C", "#172327", "#E6F2F1", "#8CA5A5", "#1F2E32", "#3DD6C4", "#002A25", "#1C2B2F", "#123430", "#9ACD5A", "#F2706A"],
            ["#DFEAE8", "#EAF2F0", "#D2E0DD", "#10201F", "#4A6260", "#C5D6D3", "#0B7F73", "#FFFFFF", "#D7E4E1", "#C8E8E2", "#3F7414", "#C83C3C"]),
        new("projector", "Projector",
            ["#12100D", "#1A1714", "#24201B", "#F3EEE6", "#A89E90", "#2E2923", "#F2B544", "#2A1A00", "#2B2620", "#3A2C14", "#6CC08B", "#F06A5E"],
            ["#EDE5D8", "#F5EFE5", "#E2D8C8", "#221D16", "#625748", "#D8CCB9", "#9E5800", "#FFFFFF", "#E5DBCC", "#F0DDBF", "#2E7D4F", "#B53A33"]),
    ];

    public static ThemePalette Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public Color[] Colors(bool light) => (light ? Light : Dark).Select(c => (Color)ColorConverter.ConvertFromString(c)).ToArray();
}
