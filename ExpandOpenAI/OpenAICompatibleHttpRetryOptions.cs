namespace ExpandOpenAI;

/// <summary>
/// OpenAI Compatible HTTP 请求的重试配置。
/// </summary>
public sealed class OpenAICompatibleHttpRetryOptions
{
    /// <summary>
    /// 首次请求失败后的最大重试次数。默认重试 2 次；设为 0 可关闭内部重试。
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 2;

    /// <summary>
    /// 第一次重试前的等待时间。后续重试使用指数退避。
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 单次重试等待时间上限，也用于限制服务端 Retry-After。
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate(string parameterName)
    {
        if (MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "MaxRetryAttempts 不能小于 0。");
        }

        if (InitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "InitialDelay 不能小于 0。");
        }

        if (MaxDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "MaxDelay 不能小于 0。");
        }
    }
}
