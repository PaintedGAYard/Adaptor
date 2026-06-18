using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Adaptor.Coordinator.Services;

namespace Adaptor.Coordinator.Plugins;

/// <summary>
/// SK Plugin — 关系数据库通用操作。
/// 包装 <see cref="IRelationalExecuteCapability"/> 和 <see cref="IRelationalQueryCapability"/>。
/// </summary>
public sealed class RelationalPlugin
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ILogger<RelationalPlugin> _logger;

    public RelationalPlugin(TransactionCoordinator coordinator, ILogger<RelationalPlugin> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    [KernelFunction("execute")]
    [Description("Execute a SQL command (INSERT / UPDATE / DELETE / DDL) within a transaction")]
    public async Task<RelationalExecuteResult> ExecuteAsync(
        [Description("Active transaction ID")] string transactionId,
        [Description("SQL command text with @params")] string command,
        [Description("SQL parameters")] RelationalParameter[]? parameters = null,
        CancellationToken ct = default)
    {
        return await _coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
            transactionId,
            (driver, tx) => driver.ExecuteAsync(
                new RelationalExecuteRequest(command, parameters?.AsReadOnly()), tx, ct),
            ct);
    }

    [KernelFunction("query")]
    [Description("Execute a SQL query (SELECT) within a transaction")]
    public async Task<RelationalQueryResult> QueryAsync(
        [Description("Active transaction ID")] string transactionId,
        [Description("SQL query text with @params")] string command,
        [Description("SQL parameters")] RelationalParameter[]? parameters = null,
        CancellationToken ct = default)
    {
        return await _coordinator.ExecuteOnCapabilityAsync<IRelationalQueryCapability, RelationalQueryResult>(
            transactionId,
            (driver, tx) => driver.QueryAsync(
                new RelationalQueryRequest(command, parameters?.AsReadOnly()), tx, ct),
            ct);
    }

    /// <summary>
    /// Represents a batch item: a SQL command with optional parameters.
    /// </summary>
    public sealed record BatchItem(string Command, IReadOnlyList<RelationalParameter>? Parameters = null);

    [KernelFunction("execute_batch")]
    [Description("Execute multiple SQL commands (INSERT / UPDATE / DELETE / DDL) in batch within a transaction")]
    public async Task<int> ExecuteBatchAsync(
        [Description("Active transaction ID")] string transactionId,
        [Description("Array of batch items, each with Command and optional Parameters")] BatchItem[] commands,
        CancellationToken ct = default)
    {
        var totalAffected = 0;

        foreach (var cmd in commands)
        {
            var result = await _coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
                transactionId,
                (driver, tx) => driver.ExecuteAsync(
                    new RelationalExecuteRequest(cmd.Command, cmd.Parameters), tx, ct),
                ct);

            totalAffected += result.AffectedRows;
        }

        return totalAffected;
    }
}
