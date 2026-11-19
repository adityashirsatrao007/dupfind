using DupFind.Core;
using DupFind.ViewModels;

namespace DupFind.ViewModels.Tests;

/// <summary>
/// The presentation rules, tested without a window. These run on any OS, which is the point
/// of keeping the view models in a plain net8.0 library instead of inside the WPF project.
/// </summary>
public sealed class DuplicateGroupViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dupfind-vm-" + Guid.NewGuid().ToString("N"));

    public DuplicateGroupViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }

    [Fact]
    public void KeepNewest_selects_every_copy_except_the_most_recently_written()
    {
        var oldest = Touch("old.txt", DateTime.UtcNow.AddDays(-2));
        var middle = Touch("middle.txt", DateTime.UtcNow.AddDays(-1));
        var newest = Touch("newest.txt", DateTime.UtcNow);

        var group = Group(oldest, middle, newest);

        group.KeepNewest();

        Assert.False(Selected(group, newest));
        Assert.True(Selected(group, oldest));
        Assert.True(Selected(group, middle));
    }

    [Fact]
    public void KeepNewest_never_selects_the_only_newest_file_when_timestamps_tie()
    {
        var stamp = DateTime.UtcNow;
        var group = Group(Touch("a.txt", stamp), Touch("b.txt", stamp));

        group.KeepNewest();

        Assert.Equal(1, group.SelectedCount);
    }

    [Fact]
    public void ClearSelection_deselects_everything()
    {
        var group = Group(Touch("a.txt", DateTime.UtcNow), Touch("b.txt", DateTime.UtcNow));

        group.KeepNewest();
        group.ClearSelection();

        Assert.Equal(0, group.SelectedCount);
    }

    [Fact]
    public void Selecting_a_copy_raises_selection_changed()
    {
        var group = Group(Touch("a.txt", DateTime.UtcNow), Touch("b.txt", DateTime.UtcNow));
        var raised = 0;
        group.SelectionChanged += (_, _) => raised++;

        group.Files[0].IsSelected = true;

        Assert.Equal(1, raised);
    }

    [Fact]
    public void RemoveFiles_reports_the_group_as_resolved_below_two_copies()
    {
        var group = Group(Touch("a.txt", DateTime.UtcNow), Touch("b.txt", DateTime.UtcNow));
        Assert.True(group.IsDuplicate);

        group.RemoveFiles([group.Files[1]]);

        Assert.False(group.IsDuplicate);
        Assert.Single(group.Files);
    }

    [Fact]
    public void RemoveFiles_leaves_a_still_duplicated_group_flagged()
    {
        var group = Group(Touch("a.txt", DateTime.UtcNow), Touch("b.txt", DateTime.UtcNow), Touch("c.txt", DateTime.UtcNow));

        group.RemoveFiles([group.Files[0]]);

        Assert.True(group.IsDuplicate);
        Assert.Equal(2, group.Files.Count);
    }

    [Fact]
    public void Header_reports_copies_reclaimed_bytes_and_folder()
    {
        var group = Group(Touch("a.txt", DateTime.UtcNow), Touch("b.txt", DateTime.UtcNow));

        // Two copies of a small file: one copy's worth is reclaimable.
        Assert.Contains("2 copies", group.Header);
        Assert.Contains(_folder, group.Header);
    }

    [Fact]
    public void SelectedFiles_returns_only_the_ticked_copies()
    {
        var group = Group(Touch("a.txt", DateTime.UtcNow), Touch("b.txt", DateTime.UtcNow));
        group.Files[0].IsSelected = true;

        var selected = group.SelectedFiles.ToList();

        Assert.Single(selected);
        Assert.Equal(group.Files[0].Path, selected[0].Path);
    }

    private DuplicateGroupViewModel Group(params string[] paths) =>
        new(new DuplicateGroup("hash", new FileInfo(paths[0]).Length, paths));

    private static bool Selected(DuplicateGroupViewModel group, string path) =>
        group.Files.Single(f => f.Path == path).IsSelected;

    /// <summary>Creates a real file so LastWriteTime is real rather than the 1601 default.</summary>
    private string Touch(string name, DateTime writtenUtc)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
