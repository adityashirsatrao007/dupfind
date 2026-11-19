using System.IO;

namespace DupFind.ViewModels;

/// <summary>One file within a duplicate group.</summary>
public sealed class DuplicateFileViewModel : ObservableObject
{
    private bool _isSelected;

    public DuplicateFileViewModel(string path, long size)
    {
        Path = path;
        Size = size;
        FileName = System.IO.Path.GetFileName(path);
        LastWriteTimeUtc = ReadLastWrite(path);
        LastModified = LastWriteTimeUtc == DateTime.MinValue
            ? "unknown"
            : LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public string Path { get; }

    public string FileName { get; }

    public long Size { get; }

    public DateTime LastWriteTimeUtc { get; }

    public string LastModified { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static DateTime ReadLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file was removed or locked between scanning and display.
            return DateTime.MinValue;
        }
    }
}
