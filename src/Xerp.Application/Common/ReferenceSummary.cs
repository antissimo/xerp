namespace Xerp.Application.Common;

/// <summary>How a representation shows a referenced record (ADR-0008, decision 2): read at response time.</summary>
public sealed record ReferenceSummary(Guid Id, string Code, string Name);
