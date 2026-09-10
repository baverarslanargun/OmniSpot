namespace SmartFileLauncher.UI.Services;

public static class ThumbnailKinds
{
    public static bool HasPreview(string? kind) =>
        kind is not ("text" or "code" or "archive" or "app" or "link" or "web");
}
