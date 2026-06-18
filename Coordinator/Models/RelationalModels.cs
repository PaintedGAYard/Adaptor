namespace Adaptor.Coordinator.Models;

/// <summary>关系数据库参数</summary>
public sealed record RelationalParameter(string Name, object? Value);

/// <summary>关系数据库执行请求（INSERT / UPDATE / DELETE / DDL）</summary>
public sealed record RelationalExecuteRequest(
    string Command,
    IReadOnlyList<RelationalParameter>? Parameters = null);

/// <summary>关系数据库执行结果</summary>
public sealed record RelationalExecuteResult(
    int AffectedRows,
    TimeSpan Duration,
    string? ErrorMessage = null);

/// <summary>关系数据库查询请求（SELECT）</summary>
public sealed record RelationalQueryRequest(
    string Command,
    IReadOnlyList<RelationalParameter>? Parameters = null);

/// <summary>关系数据库查询结果</summary>
public sealed record RelationalQueryResult(
    IReadOnlyList<IDictionary<string, object?>> Rows,
    TimeSpan Duration,
    string? ErrorMessage = null);
