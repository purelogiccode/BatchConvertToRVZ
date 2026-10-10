namespace RVZStudio.Models;

/// <summary>
/// Specifies which button the user clicked in a message box.
/// </summary>
public enum MessageBoxResult
{
    /// <summary>No result (the dialog was not shown modally or was dismissed).</summary>
    None,

    /// <summary>The user clicked OK.</summary>
    Ok,

    /// <summary>The user clicked Cancel.</summary>
    Cancel,

    /// <summary>The user clicked Yes.</summary>
    Yes,

    /// <summary>The user clicked No.</summary>
    No
}
