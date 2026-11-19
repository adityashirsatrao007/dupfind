using DupFind.Core;

namespace DupFind.Core.Tests;

/// <summary>
/// Exercises the finder against a real temporary directory tree. The point of the
/// size-first strategy is that it behaves correctly on real files, so the tests use them.
/// </summary>
public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dupfind-tests-" + Guid.NewGuid().ToString("N"));

    public DuplicateFinderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task FindsIdenticalFilesAcrossNestedFolders()
    {
        var first = Write("a/report.txt", "quarterly numbers");
        var second = Write("b/copy of report.txt", "quarterly numbers");
        var third = Write("c/nested/deep/report.txt", "quarterly numbers");
        Write("b/unrelated.txt", "something else entirely");

        var result = await new DuplicateFinder().FindAsync([_root]);

        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Paths.Count);
        Assert.Contains(first, group.Paths);
        Assert.Contains(second, group.Paths);
        Assert.Contains(third, group.Paths);
        Assert.Empty(result.Errors);

        // Two of the three copies are redundant.
        Assert.Equal(2 * new FileInfo(first).Length, result.ReclaimableBytes);
    }

    [Fact]
    public async Task SameSizeDifferentContentIsNotADuplicate()
    {
        Write("left.bin", "AAAA");
        Write("right.bin", "BBBB");

        var result = await new DuplicateFinder().FindAsync([_root]);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.ReclaimableBytes);
    }

    [Fact]
    public async Task ZeroByteFilesAreIgnored()
    {
        Write("one/empty.dat", string.Empty);
        Write("two/empty.dat", string.Empty);

        var result = await new DuplicateFinder().FindAsync([_root]);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task OverlappingRootsDoNotDoubleCountAFile()
    {
        Write("photos/holiday.jpg", "pretend this is a jpeg");
        Write("backup/holiday.jpg", "pretend this is a jpeg");

        var result = await new DuplicateFinder().FindAsync([_root, Path.Combine(_root, "photos")]);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Paths.Count);
    }

    [Fact]
    public async Task MissingFolderIsReportedAndTheRestOfTheScanContinues()
    {
        Write("one/notes.txt", "shared body");
        Write("two/notes.txt", "shared body");

        var result = await new DuplicateFinder().FindAsync(
            [Path.Combine(_root, "does-not-exist"), _root]);

        Assert.Single(result.Groups);
        var error = Assert.Single(result.Errors);
        Assert.Contains("does-not-exist", error);
    }

    [Fact]
    public async Task EmptyFolderProducesAnEmptyResult()
    {
        var result = await new DuplicateFinder().FindAsync([_root]);

        Assert.Empty(result.Groups);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CancellationStopsTheScan()
    {
        Write("one/big.txt", new string('x', 4096));
        Write("two/big.txt", new string('x', 4096));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DuplicateFinder().FindAsync([_root], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ProgressEndsAtOneHundredPercent()
    {
        Write("one/a.txt", "alpha content");
        Write("two/a.txt", "alpha content");
        Write("one/b.txt", "beta content!");
        Write("two/b.txt", "beta content!");

        var progress = new RecordingProgress();
        await new DuplicateFinder().FindAsync([_root], progress);

        var last = Assert.Single(progress.Reports.Where(r => r.FilesExamined == r.FilesToExamine));
        Assert.Equal(4, last.FilesToExamine);
        Assert.Equal(4, last.FilesExamined);
    }

    [Fact]
    public void ScanResultReclaimableBytesIgnoresTheLastCopy()
    {
        var result = new ScanResult(
            [new DuplicateGroup("HASH", 100, ["a", "b", "c", "d"])],
            []);

        Assert.Equal(300, result.ReclaimableBytes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A lingering handle on Windows just leaves temp files behind; harmless for a test run.
        }
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        private readonly object _gate = new();
        private readonly List<ScanProgress> _reports = [];

        public IReadOnlyList<ScanProgress> Reports
        {
            get
            {
                lock (_gate)
                    return _reports.ToList();
            }
        }

        public void Report(ScanProgress value)
        {
            lock (_gate)
                _reports.Add(value);
        }
    }
}
