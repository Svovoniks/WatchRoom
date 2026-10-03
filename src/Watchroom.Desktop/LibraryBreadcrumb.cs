namespace Watchroom.Desktop;

public sealed record LibraryBreadcrumb(string Label, string Level, int Category = 0, string? Series = null,
    string? Kind = null, int? Season = null, string? MediaId = null, bool IsCurrent = false, string Separator = "/");
