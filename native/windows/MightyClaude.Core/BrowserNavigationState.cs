namespace MightyClaude.Core;

public sealed record BrowserNavigationState(bool CanGoBack = false, bool CanGoForward = false, bool IsLoading = false, Uri? Url = null);
