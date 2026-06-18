namespace Adaptor.Coordinator.Models;

/// <param name="Name">Parameter name. Include the <c>@</c> prefix as required by the driver.</param>
/// <param name="Value">Parameter value; <c>null</c> is sent as <see cref="DBNull"/>.</param>
public sealed record RelationalParameter(string Name, object? Value);

/// <summary>Non-query SQL command (INSERT / UPDATE / DELETE / DDL) with optional parameters.</summary>
/// <param name="Command">SQL command text with optional <c>@param</c> placeholders.</param>
/// <param name="Parameters">Named parameters referenced in <paramref name="Command"/>; null if none.</param>
public sealed record RelationalExecuteRequest(
    string Command,
    IReadOnlyList<RelationalParameter>? Parameters = null);

/// <param name="AffectedRows">Number of rows affected; may be -1 for DDL statements.</param>
/// <param name="Duration">Server-side execution time (does not include network latency).</param>
/// <param name="ErrorMessage">Null on success; describes the failure otherwise.</param>
public sealed record RelationalExecuteResult(
    int AffectedRows,
    TimeSpan Duration,
    string? ErrorMessage = null);

/// <summary>SQL SELECT query with optional parameters.</summary>
/// <param name="Command">SELECT command text with optional <c>@param</c> placeholders.</param>
/// <param name="Parameters">Named parameters referenced in <paramref name="Command"/>; null if none.</param>
public sealed record RelationalQueryRequest(
    string Command,
    IReadOnlyList<RelationalParameter>? Parameters = null);

/// <param name="Rows">Result rows as column-name→value dictionaries; empty list (not null) when no results.</param>
/// <param name="Duration">Server-side execution time.</param>
/// <param name="ErrorMessage">Null on success; describes the failure otherwise.</param>
public sealed record RelationalQueryResult(
    IReadOnlyList<IDictionary<string, object?>> Rows,
    TimeSpan Duration,
    string? ErrorMessage = null);
