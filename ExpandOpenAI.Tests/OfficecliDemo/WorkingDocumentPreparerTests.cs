using Microsoft.Extensions.Logging.Abstractions;
using OfficecliDemo;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class WorkingDocumentPreparerTests
{
    [Fact]
    public async Task PrepareAsync_WhenPreferredDocumentIsLocked_UsesUniqueFallbackPath()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(WorkingDocumentPreparerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        FileStream? lockedFile = null;

        try
        {
            var sourcePath = Path.Combine(testDirectory, "招标书.docx");
            await File.WriteAllTextAsync(sourcePath, "test document");
            var preferredPath = WorkingDocumentPreparer.GetPreferredPath(sourcePath, testDirectory);
            await File.WriteAllTextAsync(preferredPath, "old document");
            lockedFile = File.Open(
                preferredPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var result = await WorkingDocumentPreparer.PrepareAsync(
                sourcePath,
                testDirectory,
                NullLogger.Instance,
                static (_, _) => Task.CompletedTask);

            Assert.True(result.UsedFallbackPath);
            Assert.NotEqual(preferredPath, result.DocumentPath);
            Assert.EndsWith("-大纲修复后.docx", result.RepairedDocumentPath);
            Assert.Equal("test document", await File.ReadAllTextAsync(result.DocumentPath));
        }
        finally
        {
            lockedFile?.Dispose();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RepairedDocument_IsPublishedOnlyAfterExplicitPublish()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(WorkingDocumentPreparerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var sourcePath = Path.Combine(testDirectory, "招标书.docx");
            await File.WriteAllTextAsync(sourcePath, "source");

            var preparation = await WorkingDocumentPreparer.PrepareAsync(
                sourcePath,
                testDirectory,
                NullLogger.Instance);

            Assert.EndsWith("-大纲待修复.docx", preparation.DocumentPath);
            Assert.False(File.Exists(preparation.RepairedDocumentPath));

            await File.WriteAllTextAsync(preparation.DocumentPath, "repaired");
            var publishedPath = await WorkingDocumentPreparer.PublishRepairedAsync(
                preparation,
                NullLogger.Instance);

            Assert.Equal(preparation.RepairedDocumentPath, publishedPath);
            Assert.Equal("repaired", await File.ReadAllTextAsync(publishedPath));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_MigratesLegacyUnrepairedFinalToPendingName()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(WorkingDocumentPreparerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var sourcePath = Path.Combine(testDirectory, "招标书.docx");
            await File.WriteAllTextAsync(sourcePath, "same source content");
            var repairedPath = WorkingDocumentPreparer.GetRepairedPath(sourcePath, testDirectory);
            await File.WriteAllTextAsync(repairedPath, "same source content");

            var preparation = await WorkingDocumentPreparer.PrepareAsync(
                sourcePath,
                testDirectory,
                NullLogger.Instance);

            Assert.False(File.Exists(repairedPath));
            Assert.True(File.Exists(preparation.DocumentPath));
            Assert.EndsWith("-大纲待修复.docx", preparation.DocumentPath);
            Assert.Equal("same source content", await File.ReadAllTextAsync(preparation.DocumentPath));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void FindProjectConfigurationDirectory_WhenStartedFromBin_ReturnsProjectDirectory()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(WorkingDocumentPreparerTests),
            Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(testDirectory, "OfficecliDemo");
        var binDirectory = Path.Combine(projectDirectory, "bin", "Debug", "net10.0");

        try
        {
            Directory.CreateDirectory(binDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "OfficecliDemo.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectDirectory, "appsettings.json"), "{}");

            var result = DemoOptions.FindProjectConfigurationDirectory(binDirectory);

            Assert.Equal(projectDirectory, result);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
