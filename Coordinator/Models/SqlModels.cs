namespace Adaptor.Coordinator.Models;

/// <summary>SQL 参数</summary>
public sealed record SqlParameter(string Name, object? Value);

/// <summary>SQL 执行请求（INSERT / UPDATE / DELETE / DDL）</summary>
public sealed record SqlExecuteRequest(
    string Command,
    IReadOnlyList<SqlParameter>? Parameters = null);

/// <summary>SQL 执行结果</summary>
public sealed record SqlExecuteResult(
    int AffectedRows,
    TimeSpan Duration,
    string? ErrorMessage = null);

/// <summary>SQL 查询请求（SELECT）</summary>
public sealed record SqlQueryRequest(
    string Command,
    IReadOnlyList<SqlParameter>? Parameters = null);

/// <summary>SQL 查询结果</summary>
public sealed record SqlQueryResult(
    IReadOnlyList<IDictionary<string, object?>> Rows,
    TimeSpan Duration,
    string? ErrorMessage = null);
