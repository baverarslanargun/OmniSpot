using System.Diagnostics.CodeAnalysis;

namespace SmartFileLauncher.Core.Application.Files;

public sealed class NavigationHistory<T>
{
    private readonly List<T> _back = [];
    private readonly List<T> _forward = [];

    public NavigationHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public IReadOnlyList<T> BackEntries => _back;

    public IReadOnlyList<T> ForwardEntries => _forward;

    public void Record(T current)
    {
        _forward.Clear();
        Push(_back, current);
    }

    public bool TryPeekBack([MaybeNullWhen(false)] out T entry) => TryPeek(_back, out entry);

    public bool TryPeekForward([MaybeNullWhen(false)] out T entry) => TryPeek(_forward, out entry);

    public void CompleteBack(T current)
    {
        Pop(_back);
        Push(_forward, current);
    }

    public void CompleteForward(T current)
    {
        Pop(_forward);
        Push(_back, current);
    }

    public void DiscardBack() => Pop(_back);

    public void DiscardForward() => Pop(_forward);

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    private void Push(List<T> stack, T entry)
    {
        stack.Add(entry);
        if (stack.Count > Capacity)
        {
            stack.RemoveAt(0);
        }
    }

    private static bool TryPeek(List<T> stack, [MaybeNullWhen(false)] out T entry)
    {
        if (stack.Count == 0)
        {
            entry = default;
            return false;
        }

        entry = stack[^1];
        return true;
    }

    private static void Pop(List<T> stack)
    {
        if (stack.Count == 0)
        {
            throw new InvalidOperationException("Gezinme geçmişi boş.");
        }

        stack.RemoveAt(stack.Count - 1);
    }
}
