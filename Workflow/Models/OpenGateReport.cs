namespace Workflow.Models;

/// <summary>Every open gate item of one task, in display order: the done file first, then the subtasks in index order.</summary>
/// <param name="Items">The items; read errors included.</param>
public sealed record OpenGateReport(IReadOnlyList<OpenGateItem> Items)
{
    /// <summary>A report without a single item.</summary>
    public static OpenGateReport Empty { get; } = new([]);

    /// <summary>The items; read errors included.</summary>
    public IReadOnlyList<OpenGateItem> Items { get; } = Items ?? throw new ArgumentNullException(nameof(Items));

    /// <summary>Number of items, read errors included.</summary>
    public int Count => Items.Count;

    /// <summary>True when there is no item at all.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>True when at least one source could not be evaluated.</summary>
    public bool HasReadErrors => Items.Any(i => i.IsReadError);
}
