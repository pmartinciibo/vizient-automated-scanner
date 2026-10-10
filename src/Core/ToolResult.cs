namespace Vizient.AutomatedScanner.Core;

public sealed record ToolResult(string Tool, bool Succeeded, string? Error = null);
