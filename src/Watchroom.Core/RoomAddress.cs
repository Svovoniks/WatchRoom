namespace Watchroom.Core;

public sealed record RoomAddress(string Server, string Code)
{
    public static string ValidateServer(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter an HTTPS server address, or an HTTP localhost address for testing. Do not include credentials, a query or an invitation code.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static RoomAddress Parse(string invitation, string defaultServer)
    {
        var value = invitation.Trim();
        var separator = value.LastIndexOf('#');
        var server = ValidateServer(separator >= 0 ? value[..separator] : defaultServer);
        var code = (separator >= 0 ? value[(separator + 1)..] : value).Trim().ToUpperInvariant();
        if (code.Length != 24 || code.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Paste the full invitation link or its 24-character room code. Ask the host for a fresh invitation if it has expired.");
        return new(server, code);
    }
}
