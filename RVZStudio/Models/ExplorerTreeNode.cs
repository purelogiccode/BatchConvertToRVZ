using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RVZSharp.Models;

namespace RVZStudio.Models;

/// <summary>
/// One row of the Explorer tab's tree: wraps a library <see cref="DiscFileInfo"/> and lazily
/// materializes directory children on first expand (a dummy placeholder keeps the expander
/// visible until then). The FST is already parsed in memory, so expanding performs no I/O.
/// </summary>
public sealed class ExplorerTreeNode : INotifyPropertyChanged
{
    private static readonly ExplorerTreeNode Dummy = new();

    private ObservableCollection<ExplorerTreeNode>? _children;
    private bool _isExpanded;
    private bool _isSelected;

    private ExplorerTreeNode()
    {
        Name = "...";
        FullPath = string.Empty;
        _children = [];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExplorerTreeNode"/> class.
    /// </summary>
    /// <param name="data">The file-system entry to wrap.</param>
    public ExplorerTreeNode(DiscFileInfo data)
    {
        Name = string.IsNullOrEmpty(data.Name) ? "/" : data.Name;
        FullPath = data.Path;
        IsDirectory = data.IsDirectory;
        Size = data.IsDirectory ? 0 : data.Size;
        Offset = data.Offset;
        Data = data;

        if (data.IsDirectory)
        {
            _children = [Dummy];
        }
    }

    /// <summary>Gets the entry name ("/" for the root).</summary>
    public string Name { get; }

    /// <summary>Gets the image-internal path (directories end with '/').</summary>
    public string FullPath { get; }

    /// <summary>Gets whether this node is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the file byte size (0 for directories).</summary>
    public long Size { get; }

    /// <summary>Gets the entry's data offset in the decoded image.</summary>
    public long Offset { get; }

    /// <summary>Gets the wrapped library entry, or null for the expand placeholder.</summary>
    public DiscFileInfo? Data { get; }

    /// <summary>Gets whether this is the expand placeholder rather than a real entry.</summary>
    public bool IsDummy => ReferenceEquals(this, Dummy);

    /// <summary>Gets the human-readable kind/size suffix shown next to the name.</summary>
    public string Suffix => IsDirectory ? "dir" : DisplaySize;

    /// <summary>Gets the formatted file size (e.g. "1.5 MB").</summary>
    public string DisplaySize => FormatSize(Size);

    /// <summary>Gets the child rows (placeholder until first expand).</summary>
    public ObservableCollection<ExplorerTreeNode> Children => _children ??= [];

    /// <summary>
    /// Gets or sets whether the tree row is expanded; expanding a directory for the first
    /// time materializes its children from the parsed FST.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (IsDummy || _isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
            if (value)
            {
                EnsureChildren();
            }
        }
    }

    /// <summary>Gets or sets whether the tree row is selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsDummy || _isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Occurs when a bound property value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void EnsureChildren()
    {
        if (IsDummy || !IsDirectory || Data is null || _children is null)
        {
            return;
        }

        if (_children.Count != 1 || !_children[0].IsDummy)
        {
            return;
        }

        _children.Clear();
        foreach (var child in Data.Children)
        {
            _children.Add(new ExplorerTreeNode(child));
        }
    }

    /// <summary>Formats a byte count for tree/detail display.</summary>
    private static string FormatSize(long size)
    {
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        int i;
        double value = size;
        for (i = 0; i < suffix.Length - 1 && size >= 1024; i++, size /= 1024)
        {
            value = size / 1024.0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {suffix[i]}");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
