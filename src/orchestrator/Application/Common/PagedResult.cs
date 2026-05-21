namespace Orchestrator.Application.Common;

public sealed class PagedResult<T> {
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
}
