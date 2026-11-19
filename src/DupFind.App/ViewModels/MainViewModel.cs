using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using DupFind.App.Mvvm;
using DupFind.Core;
using Microsoft.Win32;

namespace DupFind.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DuplicateFinder _finder = new();
    private CancellationTokenSource? _cancellation;

    public MainViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && Folders.Count > 0);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsBusy);
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => !IsBusy && SelectedFolder is not null);
        KeepNewestCommand = new RelayCommand(KeepNewest, () => Groups.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => Groups.Count > 0);
        MoveSelectedCommand = new RelayCommand(MoveSelected, () => SelectedCount > 0 && !IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Groups.Count > 0);
    }

    public ObservableCollection<string> Folders { get; } = [];

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveFolderCommand { get; }

    public RelayCommand KeepNewestCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand MoveSelectedCommand { get; }

    public RelayCommand ExportCsvCommand { get; }

    private string? _selectedFolder;

    public string? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
                RefreshCommands();
        }
    }

    private string _status = "Add the folders you want to scan, then press Scan.";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private string _selectionText = "Nothing selected.";

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    private double _progress;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommands();
        }
    }

    private int SelectedCount => Groups.Sum(g => g.SelectedCount);

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        if (Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Status = "That folder is already in the list.";
            return;
        }

        Folders.Add(folder);
        Status = $"{Folders.Count} folder(s) ready. Press Scan.";
        RefreshCommands();
    }

    private void RemoveFolder()
    {
        if (SelectedFolder is null)
            return;

        Folders.Remove(SelectedFolder);
        Status = $"{Folders.Count} folder(s) ready. Press Scan.";
        RefreshCommands();
    }

    private async Task ScanAsync()
    {
        Groups.Clear();
        SelectedFolder = null;
        Progress = 0;
        IsBusy = true;
        _cancellation = new CancellationTokenSource();

        // Progress<T> captures the UI context, so this runs on the dispatcher thread.
        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.FilesToExamine == 0 ? 100 : 100.0 * p.FilesExamined / p.FilesToExamine;
            Status = $"Hashing {p.FilesExamined:N0} of {p.FilesToExamine:N0}: {Path.GetFileName(p.CurrentItem)}";
        });

        try
        {
            var result = await _finder.FindAsync(Folders.ToList(), progress, _cancellation.Token);

            foreach (var group in result.Groups)
            {
                var vm = new DuplicateGroupViewModel(group);
                vm.SelectionChanged += OnGroupSelectionChanged;
                Groups.Add(vm);
            }

            var skipped = result.Errors.Count == 0
                ? string.Empty
                : $" {result.Errors.Count:N0} item(s) skipped (locked or unreadable).";

            Status = Groups.Count == 0
                ? "No duplicates found." + skipped
                : $"{Groups.Count:N0} duplicate groups · {Format.Bytes(result.ReclaimableBytes)} reclaimable.{skipped}";
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            // Never let an unexpected failure take the window down.
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            Progress = 0;
            IsBusy = false;
            OnGroupSelectionChanged(this, EventArgs.Empty);
        }
    }

    private void KeepNewest()
    {
        foreach (var group in Groups)
            group.KeepNewest();

        OnGroupSelectionChanged(this, EventArgs.Empty);
    }

    private void ClearSelection()
    {
        foreach (var group in Groups)
            group.ClearSelection();

        OnGroupSelectionChanged(this, EventArgs.Empty);
    }

    private void MoveSelected()
    {
        var selected = Groups.SelectMany(g => g.SelectedFiles).ToList();
        if (selected.Count == 0)
            return;

        var dialog = new OpenFolderDialog { Title = "Choose a folder to move duplicates into" };
        if (dialog.ShowDialog() != true)
            return;

        var target = dialog.FolderName;
        var total = selected.Sum(f => f.Size);

        var confirmed = MessageBox.Show(
            $"Move {selected.Count:N0} file(s), {Format.Bytes(total)}, into:\n\n{target}\n\n" +
            "These files are moved rather than deleted, so the operation can be undone from that folder.",
            "Move duplicates",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.OK)
            return;

        var moved = 0;
        string? firstFailure = null;

        foreach (var file in selected)
        {
            try
            {
                File.Move(file.Path, UniqueDestination(target, file.Path));
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= $"{Path.GetFileName(file.Path)}: {ex.Message}";
            }
        }

        foreach (var group in Groups.ToList())
        {
            group.RemoveMovedFiles();
            if (group.IsDuplicate)
                continue;

            group.SelectionChanged -= OnGroupSelectionChanged;
            Groups.Remove(group);
        }

        Status = $"{moved:N0} file(s) moved to {target}."
                 + (firstFailure is null ? string.Empty : $" Some failed — {firstFailure}");

        OnGroupSelectionChanged(this, EventArgs.Empty);
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export duplicate report",
            Filter = "CSV file|*.csv",
            FileName = "duplicates.csv",
        };

        if (dialog.ShowDialog() != true)
            return;

        var csv = new StringBuilder("group,size_bytes,sha256,path\n");
        for (var i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            foreach (var file in group.Files)
            {
                csv.Append(i + 1).Append(',')
                   .Append(file.Size).Append(',')
                   .Append(group.Hash).Append(',')
                   .Append(CsvEscape(file.Path)).Append('\n');
            }
        }

        try
        {
            File.WriteAllText(dialog.FileName, csv.ToString());
            Status = $"Report written to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not write the report: {ex.Message}";
        }
    }

    /// <summary>Never overwrite a file already sitting in the quarantine folder.</summary>
    private static string UniqueDestination(string folder, string source)
    {
        var name = Path.GetFileName(source);
        var destination = Path.Combine(folder, name);
        var counter = 1;

        while (File.Exists(destination))
        {
            destination = Path.Combine(
                folder,
                $"{Path.GetFileNameWithoutExtension(name)} ({counter++}){Path.GetExtension(name)}");
        }

        return destination;
    }

    private static string CsvEscape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        var count = SelectedCount;
        SelectionText = count == 0
            ? "Nothing selected."
            : $"{count:N0} file(s) selected · {Format.Bytes(Groups.SelectMany(g => g.SelectedFiles).Sum(f => f.Size))}";

        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddFolderCommand.RaiseCanExecuteChanged();
        RemoveFolderCommand.RaiseCanExecuteChanged();
        KeepNewestCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        MoveSelectedCommand.RaiseCanExecuteChanged();
        ExportCsvCommand.RaiseCanExecuteChanged();
    }
}
