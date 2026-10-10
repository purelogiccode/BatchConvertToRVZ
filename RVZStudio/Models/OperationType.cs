namespace RVZStudio.Models;

/// <summary>
/// Identifies the batch operation currently running in the main window.
/// </summary>
public enum OperationType
{
    /// <summary>No batch operation is running.</summary>
    None,

    /// <summary>A batch conversion to RVZ is running.</summary>
    Conversion,

    /// <summary>A batch verification is running.</summary>
    Verification,

    /// <summary>A batch extraction is running.</summary>
    Extraction
}
