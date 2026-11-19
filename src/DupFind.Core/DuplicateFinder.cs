using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DupFind.Core;

/// <summary>
/// Finds byte-identical files. Three passes, none of which is expensive:
/// a size histogram, a second walk that keeps only files whose size is shared,
/// then SHA-256 over those candidates only.
/// </summary>
/// <remarks>
/// Two directory walks instead of one is a deliberate trade: the second walk costs
/// metadata reads (cheap, and usually cached by the OS), and in exchange memory stays
/// proportional to the number of duplicates rather than to the number of files on disk.
/// On a million-file volume the single-pass version held every path in memory.
/// </remarks>
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
        var rootList = roots.ToList();
        var errors = new ConcurrentBag<string>();

        // Pass 1: how many files share each exact size? Only the histogram is retained.
        var sizeCounts = new Dictionary<long, int>();

        foreach (var root in rootList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                errors.Add($"Folder not found: {root}");
                continue;
            }

            foreach (var (_, size) in Enumerate(root, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sizeCounts[size] = sizeCounts.GetValueOrDefault(size) + 1;
            }
        }

        // Pass 2: collect paths whose size is shared by at least one other file.
        // Windows paths are case-insensitive, and overlapping roots must not double-count.
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var seen = new HashSet<string>(pathComparer);
        var candidates = new List<(long Size, string Path)>();

        foreach (var root in rootList)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var (path, size) in Enumerate(root, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Zero-byte files are trivially "identical" and never worth deleting.
                if (size == 0 || sizeCounts.GetValueOrDefault(size) < 2)
                    continue;

                if (seen.Add(path))
                    candidates.Add((size, path));
            }
        }

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
                    var hash = await HashFileAsync(candidate.Path, ct).ConfigureAwait(false);
                    hashed.Add((candidate.Size, hash, candidate.Path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{candidate.Path}: {ex.Message}");
                }

                var done = Interlocked.Increment(ref examined);
                if (done % 25 == 0 || done == candidates.Count)
                    progress?.Report(new ScanProgress(candidate.Path, done, candidates.Count));
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

        return new ScanResult(groups, errors.Distinct().ToList());
    }

    /// <summary>
    /// Walks a root lazily. Enumeration can fail part-way through (a drive unplugged, a
    /// folder deleted mid-scan), so each step is guarded and whatever was already found is kept.
    /// </summary>
    private static IEnumerable<(string Path, long Size)> Enumerate(string root, ConcurrentBag<string> errors)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Reparse points can point back up the tree and loop forever.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        using var enumerator = Directory.EnumerateFiles(root, "*", options).GetEnumerator();

        while (true)
        {
            string current;
            try
            {
                if (!enumerator.MoveNext())
                    yield break;

                current = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{root}: {ex.Message}");
                yield break;
            }

            long size;
            try
            {
                size = new FileInfo(current).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{current}: {ex.Message}");
                continue;
            }

            yield return (current, size);
        }
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
