namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

public class UnitTest1
{
    [Fact]
    public async Task RequestRoundTripsWithDesignJsonNamesAndStringEnums()
    {
        var path = CreateTemporaryFilePath();
        try
        {
            var request = new RunRequestDocument
            {
                RunId = "2026-10-10T03-00-00Z",
                Revision = 2,
                RequestedAt = DateTimeOffset.Parse("2026-10-10T03:00:00Z"),
                RequestedBy = "scheduler",
                Mode = ScanMode.RetryFailed,
                Tools = new Dictionary<string, ToolRequestOverride>
                {
                    ["Semgrep"] = new() { Enabled = true, MaxParallel = 4 }
                },
                Scope = new RequestScope { IncludeRepos = ["gh-123"] }
            };

            await AtomicJsonFile.WriteAsync(path, request);
            var json = await File.ReadAllTextAsync(path);
            var readRequest = await AtomicJsonFile.ReadAsync<RunRequestDocument>(path);

            Assert.Contains("\"schemaVersion\": 3", json);
            Assert.Contains("\"mode\": \"RetryFailed\"", json);
            Assert.Equal(request.RunId, readRequest.RunId);
            Assert.Equal(request.Revision, readRequest.Revision);
            Assert.Equal(request.RequestedAt, readRequest.RequestedAt);
            Assert.Equal(request.Mode, readRequest.Mode);
            Assert.Equal(request.Tools["Semgrep"], readRequest.Tools["Semgrep"]);
            Assert.Equal(request.Scope.IncludeRepos, readRequest.Scope.IncludeRepos);
        }
        finally
        {
            DeleteTemporaryDirectory(path);
        }
    }

    [Fact]
    public async Task WriteReplacesExistingFileAndLeavesNoTemporaryFile()
    {
        var path = CreateTemporaryFilePath();
        try
        {
            await AtomicJsonFile.WriteAsync(path, new ToolFailedMarker { Message = "first" });
            await AtomicJsonFile.WriteAsync(path, new ToolFailedMarker { Message = "second" });

            var marker = await AtomicJsonFile.ReadAsync<ToolFailedMarker>(path);

            Assert.Equal("second", marker.Message);
            Assert.Equal(3, marker.SchemaVersion);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteTemporaryDirectory(path);
        }
    }

    [Fact]
    public async Task StatusUsesDesignPhaseName()
    {
        var path = CreateTemporaryFilePath();
        try
        {
            await AtomicJsonFile.WriteAsync(path, new StatusDocument { Phase = RunPhase.ImportAndClone });

            var json = await File.ReadAllTextAsync(path);
            var status = await AtomicJsonFile.ReadAsync<StatusDocument>(path);

            Assert.Equal("Import+Clone", JsonDocument.Parse(json).RootElement.GetProperty("phase").GetString());
            Assert.Equal(RunPhase.ImportAndClone, status.Phase);
        }
        finally
        {
            DeleteTemporaryDirectory(path);
        }
    }

    private static string CreateTemporaryFilePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        return Path.Combine(directory, "document.json");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

    }
}
