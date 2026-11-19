namespace DupFind.Core;

/// <summary>Progress for a scan. <see cref="FilesExamined"/> counts hashed candidates only.</summary>
public sealed record ScanProgress(string CurrentItem, int FilesExamined, int FilesToExamine);

/// <summary>A set of two or more byte-identical files.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<string> Paths);

/// <summary>Outcome of a scan. Per-file failures are reported, never thrown.</summary>
public sealed record ScanResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<string> Errors)
{
    /// <summary>Bytes freed if every copy but one in each group were removed.</summary>
    public long ReclaimableBytes => Groups.Sum(g => g.Size * (g.Paths.Count - 1));
}
