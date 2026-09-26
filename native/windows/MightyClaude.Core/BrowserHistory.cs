namespace MightyClaude.Core;

// Back/forward history of one browser pane.
// Mirrors macOS BrowserHistory: same-URL visit is a reload, forward entries
// are dropped on a new visit (Chromium semantics).
public sealed class BrowserHistory
{
    private readonly List<Uri> entries = [];
    private int index = -1;

    public Uri? Current => (index >= 0 && index < entries.Count) ? entries[index] : null;
    public bool CanGoBack => index > 0;
    public bool CanGoForward => index >= 0 && index < entries.Count - 1;

    public void Visit(Uri url)
    {
        if (Current == url) return;
        if (index >= 0 && index < entries.Count - 1)
            entries.RemoveRange(index + 1, entries.Count - index - 1);
        entries.Add(url);
        index = entries.Count - 1;
    }

    public Uri? GoBack()
    {
        if (!CanGoBack) return null;
        index--;
        return Current;
    }

    public Uri? GoForward()
    {
        if (!CanGoForward) return null;
        index++;
        return Current;
    }

    public BrowserNavigationState State(bool isLoading = false) =>
        new(CanGoBack, CanGoForward, isLoading, Current);
}
