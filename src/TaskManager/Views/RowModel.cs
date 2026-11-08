using TaskManager.Engine;

namespace TaskManager.Views;

public enum RowKind { Header, Group, Child }

/// <summary>
/// One visible line on the Processes page. Reused across ticks (looked up by key) so a row keeps its
/// expanded state and selection while its numbers change.
/// </summary>
public sealed class RowModel
{
    public required string Key { get; init; }
    public required RowKind Kind { get; init; }
    public string Name { get; set; } = "";
    public string IconPath { get; set; } = "";
    public bool Expandable { get; set; }
    public bool Expanded { get; set; }
    public string Status { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string ImageName { get; set; } = "";
    public string Type { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public uint Pid { get; set; }
    public double Cpu { get; set; }
    public long Memory { get; set; }
    public double Disk { get; set; }
    public double? Network { get; set; }
    public double? Gpu { get; set; }
    public string GpuEngine { get; set; } = "";
    public nint Window { get; set; }
    public GroupData? Group { get; set; }
}

/// <summary>
/// A fixed line position in the list. When the sort order changes, slots stay put and only the row they show
/// changes, so the list never removes, moves or re-animates containers (that was the flash on every update).
/// </summary>
public sealed class RowSlot
{
    public RowModel? Row { get; private set; }

    public event Action? Changed;

    public void Show(RowModel row)
    {
        Row = row;
        Changed?.Invoke();
    }
}
