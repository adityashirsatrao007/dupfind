using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using DupFind.App.Mvvm;
using DupFind.App.Services;
using DupFind.Core;
using DupFind.ViewModels;
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
        MoveSelectedCommand = new AsyncRelayCommand(MoveSelectedAsync, () => SelectedCount > 0 && !IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Groups.Count > 0);
        ShowSkippedCommand = new RelayCommand(ShowSkipped, () => SkippedFiles.Count > 0);
    }

    public ObservableCollection<string> Folders { get; } = [];

    private ObservableCollection<DuplicateGroupViewModel> _groups = [];

    /// <summary>
    /// Assigned once per scan rather than appended to. Adding 20 000 groups one at a time
    /// raised 20 000 collection-changed events, and the view rebuilt its layout after each.
    /// </summary>
    public ObservableCollection<DuplicateGroupViewModel> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    /// <summary>Files and folders the scan could not read, so the user can act on them.</summary>
    public ObservableCollection<string> SkippedFiles { get; } = [];

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveFolderCommand { get; }

    public RelayCommand KeepNewestCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public AsyncRelayCommand MoveSelectedCommand { get; }

    public RelayCommand ExportCsvCommand { get; }

    public RelayCommand ShowSkippedCommand { get; }

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
        SkippedFiles.Clear();
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

            // Built detached, then published in one assignment: the view binds to the old
            // collection until the scan finishes, so it lays out once instead of per group.
            var groups = new List<DuplicateGroupViewModel>(result.Groups.Count);

            foreach (var group in result.Groups)
            {
                var vm = new DuplicateGroupViewModel(group);
                vm.SelectionChanged += OnGroupSelectionChanged;
                groups.Add(vm);
            }

            Groups = new ObservableCollection<DuplicateGroupViewModel>(groups);

            foreach (var error in result.Errors)
                SkippedFiles.Add(error);

            var skipped = result.Errors.Count == 0
                ? string.Empty
                : $" {result.Errors.Count:N0} item(s) skipped (locked or unreadable) — see Skipped files.";

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
            ErrorLog.Write(ex, "scan");
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

    private void ShowSkipped()
    {
        const int shown = 50;
        var lines = SkippedFiles.Take(shown).ToList();
        var more = SkippedFiles.Count > shown ? $"\n\n…and {SkippedFiles.Count - shown:N0} more." : string.Empty;

        MessageBox.Show(
            string.Join('\n', lines) + more,
            $"{SkippedFiles.Count:N0} skipped item(s)",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

    private async Task MoveSelectedAsync()
    {
        var selected = Groups.SelectMany(g => g.SelectedFiles).ToList();
        if (selected.Count == 0)
            return;

        var picker = new OpenFolderDialog { Title = "Choose a folder to move duplicates into" };
        if (picker.ShowDialog() != true)
            return;

        var target = picker.FolderName;

        var confirmed = MessageBox.Show(
            $"Move {selected.Count:N0} file(s), {Format.Bytes(selected.Sum(f => f.Size))}, into:\n\n{target}\n\n" +
            "These files are moved rather than deleted, so the operation can be undone from that folder.",
            "Move duplicates",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.OK)
            return;

        IsBusy = true;
        Progress = 0;
        _cancellation = new CancellationTokenSource();

        // Written only by the worker loop; read after it finishes. await supplies the barrier.
        var movedFiles = new List<DuplicateFileViewModel>();
        var failures = new List<string>();

        var progress = new Progress<int>(done =>
        {
            Progress = 100.0 * done / selected.Count;
            Status = $"Moving {done:N0} of {selected.Count:N0} file(s)…";
        });

        try
        {
            await Task.Run(
                () => MoveFiles(selected, target, progress, _cancellation.Token, movedFiles, failures),
                _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Move cancelled.";
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "move");
            failures.Add(ex.Message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            Progress = 0;
            IsBusy = false;
        }

        // Files already moved must leave the grid even if the run was cancelled.
        if (movedFiles.Count > 0)
            RemoveMoved(movedFiles);

        if (movedFiles.Count > 0 || failures.Count > 0)
        {
            Status = $"{movedFiles.Count:N0} file(s) moved to {target}."
                     + (failures.Count == 0 ? string.Empty : $" {failures.Count} failed — first: {failures[0]}");
        }

        OnGroupSelectionChanged(this, EventArgs.Empty);
    }

    private static void MoveFiles(
        List<DuplicateFileViewModel> files,
        string target,
        IProgress<int> progress,
        CancellationToken token,
        List<DuplicateFileViewModel> movedFiles,
        List<string> failures)
    {
        var moved = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                File.Move(file.Path, UniqueDestination(target, file.Path));
                movedFiles.Add(file);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(file.Path)}: {ex.Message}");
            }

            progress.Report(moved);
        }
    }

    private void RemoveMoved(List<DuplicateFileViewModel> movedFiles)
    {
        foreach (var group in Groups.ToList())
        {
            group.RemoveFiles(movedFiles);
            if (group.IsDuplicate)
                continue;

            group.SelectionChanged -= OnGroupSelectionChanged;
            Groups.Remove(group);
        }
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
        ShowSkippedCommand.RaiseCanExecuteChanged();
    }
}
