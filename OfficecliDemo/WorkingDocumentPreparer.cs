using Microsoft.Extensions.Logging;

namespace OfficecliDemo;

internal sealed record WorkingDocumentPreparation(
    string DocumentPath,
    string RepairedDocumentPath,
    bool UsedFallbackPath);

internal static class WorkingDocumentPreparer
{
    public static string GetPreferredPath(string sourceDocumentPath, string outputDirectory)
    {
        return Path.Combine(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(sourceDocumentPath)}-大纲待修复.docx");
    }

    public static string GetRepairedPath(string sourceDocumentPath, string outputDirectory)
    {
        return Path.Combine(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(sourceDocumentPath)}-大纲修复后.docx");
    }

    public static async Task<WorkingDocumentPreparation> PrepareAsync(
        string sourceDocumentPath,
        string outputDirectory,
        ILogger logger,
        Func<string, CancellationToken, Task>? releaseDestinationAsync = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var destinationPath = GetPreferredPath(sourceDocumentPath, outputDirectory);
        var repairedDocumentPath = GetRepairedPath(sourceDocumentPath, outputDirectory);
        var outlineResultPath = Path.Combine(outputDirectory, "大纲修复结果.json");
        if (File.Exists(repairedDocumentPath)
            && !File.Exists(outlineResultPath)
            && FilesHaveSameContent(sourceDocumentPath, repairedDocumentPath))
        {
            if (releaseDestinationAsync is not null)
            {
                await releaseDestinationAsync(repairedDocumentPath, cancellationToken)
                    .ConfigureAwait(false);
                if (File.Exists(destinationPath))
                {
                    await releaseDestinationAsync(destinationPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            File.Move(repairedDocumentPath, destinationPath, overwrite: true);
            logger.LogWarning(
                "[Document] 检测到旧版提前生成但尚未修复的正式文件，已改名为待修复工作副本：{Document}",
                destinationPath);
            return new WorkingDocumentPreparation(
                destinationPath,
                repairedDocumentPath,
                UsedFallbackPath: false);
        }

        if (releaseDestinationAsync is not null && File.Exists(destinationPath))
        {
            try
            {
                await releaseDestinationAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogDebug(
                    exception,
                    "[Document] 释放旧工作副本失败，将继续尝试复制：{Document}",
                    destinationPath);
            }
        }

        IOException? lastCopyException = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Copy(sourceDocumentPath, destinationPath, overwrite: true);
                return new WorkingDocumentPreparation(
                    destinationPath,
                    repairedDocumentPath,
                    UsedFallbackPath: false);
            }
            catch (IOException exception)
            {
                lastCopyException = exception;
                if (attempt < 3)
                {
                    logger.LogWarning(
                        "[Document] 工作副本暂时无法覆盖，第 {Attempt}/3 次失败，{DelayMs} ms 后重试：{Document}。原因：{Reason}",
                        attempt,
                        attempt * 200,
                        destinationPath,
                        exception.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(attempt * 200), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var sourceName = Path.GetFileNameWithoutExtension(sourceDocumentPath);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        for (var suffix = 0; suffix < 100; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffixText = suffix == 0 ? string.Empty : $"-{suffix + 1:D2}";
            var fallbackPath = Path.Combine(
                outputDirectory,
                $"{sourceName}-大纲待修复-{timestamp}{suffixText}.docx");
            try
            {
                File.Copy(sourceDocumentPath, fallbackPath, overwrite: false);
                logger.LogWarning(
                    "[Document] 固定工作副本仍被占用，已自动改用新文件：{FallbackDocument}。原文件：{PreferredDocument}",
                    fallbackPath,
                    destinationPath);
                return new WorkingDocumentPreparation(
                    fallbackPath,
                    repairedDocumentPath,
                    UsedFallbackPath: true);
            }
            catch (IOException) when (File.Exists(fallbackPath))
            {
                // 同一秒内多次启动时尝试下一个序号。
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"无法创建招标书工作副本。固定路径被占用，备用路径也无法写入：{fallbackPath}",
                    new AggregateException(lastCopyException!, exception));
            }
        }

        throw new IOException(
            $"无法创建招标书工作副本：一分钟内的备用文件名均已存在于 {outputDirectory}",
            lastCopyException);
    }

    public static async Task<string> PublishRepairedAsync(
        WorkingDocumentPreparation preparation,
        ILogger logger,
        Func<string, CancellationToken, Task>? releaseDestinationAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (releaseDestinationAsync is not null && File.Exists(preparation.RepairedDocumentPath))
        {
            await releaseDestinationAsync(preparation.RepairedDocumentPath, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var publishingPath = preparation.RepairedDocumentPath
            + $".publishing-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(preparation.DocumentPath, publishingPath, overwrite: false);
            File.Move(publishingPath, preparation.RepairedDocumentPath, overwrite: true);
            logger.LogInformation(
                "[Document] 大纲已真正应用，正式发布修复文档：{Document}",
                preparation.RepairedDocumentPath);
            return preparation.RepairedDocumentPath;
        }
        finally
        {
            if (File.Exists(publishingPath))
            {
                File.Delete(publishingPath);
            }
        }
    }

    private static bool FilesHaveSameContent(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        const int bufferSize = 64 * 1024;
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstBuffer = new byte[bufferSize];
        var secondBuffer = new byte[bufferSize];
        while (true)
        {
            var firstRead = first.Read(firstBuffer);
            var secondRead = second.Read(secondBuffer);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }
}
