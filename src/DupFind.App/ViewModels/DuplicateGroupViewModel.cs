using System.Collections.ObjectModel;
using System.ComponentModel;
using DupFind.App.Mvvm;
using DupFind.Core;

namespace DupFind.App.ViewModels;

/// <summary>A set of identical files, with per-file selection state.</summary>
public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Hash = group.Hash;
        Size = group.Size;

        foreach (var path in group.Paths)
        {
            var file = new DuplicateFileViewModel(path, group.Size);
            file.PropertyChanged += OnFilePropertyChanged;
            Files.Add(file);
        }
    }

    public event EventHandler? SelectionChanged;

    public string Hash { get; }

    public long Size { get; }

    public ObservableCollection<DuplicateFileViewModel> Files { get; } = [];

    /// <summary>Fewer than two copies left means the group is resolved.</summary>
    public bool IsDuplicate => Files.Count > 1;

    public int SelectedCount => Files.Count(f => f.IsSelected);

    public IEnumerable<DuplicateFileViewModel> SelectedFiles => Files.Where(f => f.IsSelected);

    public string Header =>
        $"{Files.Count} copies · {Format.Bytes(Size)} each · {Format.Bytes(Size * (Files.Count - 1))} wasted · {Folder}";

    private string Folder =>
        System.IO.Path.GetDirectoryName(Files.FirstOrDefault()?.Path ?? string.Empty) ?? string.Empty;

    /// <summary>Checks every copy except the most recently modified one.</summary>
    public void KeepNewest()
    {
        var newest = Files.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        foreach (var file in Files)
            file.IsSelected = !ReferenceEquals(file, newest);
    }

    public void ClearSelection()
    {
        foreach (var file in Files)
            file.IsSelected = false;
    }

    /// <summary>Drops files that were already moved to the quarantine folder.</summary>
    public void RemoveMovedFiles()
    {
        foreach (var file in Files.Where(f => f.IsSelected).ToList())
        {
            file.PropertyChanged -= OnFilePropertyChanged;
            Files.Remove(file);
        }

        OnPropertyChanged(nameof(IsDuplicate));
        OnPropertyChanged(nameof(Header));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
