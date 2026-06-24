internal static class MyQdrantGuard
{
    public static void ThrowIfNull(object? argument, string paramName)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void ThrowIfNullOrWhiteSpace(string? argument, string paramName)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentException("The value cannot be empty or consist only of white-space characters.", paramName);
        }
    }

    public static void ThrowIfDisposed(bool condition, object instance)
    {
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
    }
}
