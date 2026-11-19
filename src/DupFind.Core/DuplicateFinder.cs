using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DupFind.Core;

/// <summary>
/// Finds byte-identical files. Two passes: group by exact size, then hash only the
/// candidates that share a size. Hashing every file would be far slower on a real disk.
/// </summary>
public sealed class DuplicateFinder
{
    private readonly int _parallelism;

    /// <param name="parallelism">Degree of parallelism for hashing; defaults to the core count.</param>
    public DuplicateFinder(int parallelism = 0)
        => _parallelism = parallelism > 0 ? parallelism : Environment.ProcessorCount;

    public async Task<ScanResult> FindAsync(
        IEnumerable<string> roots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Windows paths are case-insensitive; overlapping roots must not double-count a file.
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var errors = new ConcurrentBag<string>();
        var sizes = new Dictionary<string, long>(pathComparer);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                errors.Add($"Folder not found: {root}");
                continue;
            }

            foreach (var path in EnumerateFiles(root, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    sizes.TryAdd(path, new FileInfo(path).Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{path}: {ex.Message}");
                }
            }
        }

        // Zero-byte files are trivially "identical" and never worth deleting.
        var candidates = sizes
            .Where(f => f.Value > 0)
            .GroupBy(f => f.Value)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(f => (Size: g.Key, f.Key)))
            .ToList();

        var hashed = new ConcurrentBag<(long Size, string Hash, string Path)>();
        var examined = 0;

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _parallelism,
                CancellationToken = cancellationToken,
            },
            async (candidate, ct) =>
            {
                try
                {
                    var hash = await HashFileAsync(candidate.Key, ct).ConfigureAwait(false);
                    hashed.Add((candidate.Size, hash, candidate.Key));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{candidate.Key}: {ex.Message}");
                }

                var done = Interlocked.Increment(ref examined);
                if (done % 25 == 0 || done == candidates.Count)
                    progress?.Report(new ScanProgress(candidate.Key, done, candidates.Count));
            }).ConfigureAwait(false);

        var groups = hashed
            .GroupBy(h => (h.Size, h.Hash))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(
                g.Key.Hash,
                g.Key.Size,
                g.Select(x => x.Path)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderByDescending(g => g.Size * (g.Paths.Count - 1))
            .ToList();

        return new ScanResult(groups, errors.ToList());
    }

    private static List<string> EnumerateFiles(string root, ConcurrentBag<string> errors)
    {
        var paths = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Reparse points can point back up the tree and loop forever.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            paths.AddRange(Directory.EnumerateFiles(root, "*", options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-walk failure (e.g. a drive unplugged) keeps what was already found.
            errors.Add($"{root}: {ex.Message}");
        }

        return paths;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1 << 20,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
