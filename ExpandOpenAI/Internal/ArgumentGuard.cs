namespace ExpandOpenAI.Internal;

internal static class ArgumentGuard
{
    public static void ThrowIfNull(object? value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void ThrowIfNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
        }
    }

    public static void ThrowIfDisposed(bool disposed, object instance)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
    }
}
