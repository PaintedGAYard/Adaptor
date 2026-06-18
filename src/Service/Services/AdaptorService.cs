using System.Transactions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Models;
using Adaptor.Coordinator.Services;

namespace Adaptor.Service.Services;

#region Shared helper methods

/// <summary>
/// Shared infrastructure for all gRPC service implementations.
/// </summary>
/// <remarks>gRPC transaction_id reuses <see cref="TransactionInformation.LocalIdentifier"/> directly.</remarks>
public sealed class AdaptorServiceContext
{
    public TransactionCoordinator Coordinator { get; }
    public ILogger Logger { get; }
    public CoordinatorOptions Options { get; }

    public AdaptorServiceContext(
        TransactionCoordinator coordinator,
        IOptions<CoordinatorOptions> options,
        ILogger<AdaptorServiceContext> logger)
    {
        Coordinator = coordinator;
        Options = options.Value;
        Logger = logger;
    }

    /// <summary>
    /// Map internal <see cref="Coordinator.Models.CommitStatus"/> to the protobuf enum.
    /// </summary>
    public static global::Adaptor.Service.CommitStatus ConvertStatus(
        Adaptor.Coordinator.Models.CommitStatus status) => status switch
    {
        Adaptor.Coordinator.Models.CommitStatus.Committed => global::Adaptor.Service.CommitStatus.Committed,
        Adaptor.Coordinator.Models.CommitStatus.Partial => global::Adaptor.Service.CommitStatus.Partial,
        Adaptor.Coordinator.Models.CommitStatus.RolledBack => global::Adaptor.Service.CommitStatus.RolledBack,
        Adaptor.Coordinator.Models.CommitStatus.Timeout => global::Adaptor.Service.CommitStatus.Timeout,
        _ => global::Adaptor.Service.CommitStatus.Unspecified,
    };

    /// <summary>
    /// Map <see cref="System.Transactions.TransactionStatus"/> to the protobuf TransactionState.
    /// </summary>
    public static global::Adaptor.Service.TransactionState ConvertTransactionStatus(
        System.Transactions.TransactionStatus status) => status switch
    {
        System.Transactions.TransactionStatus.Active => global::Adaptor.Service.TransactionState.Active,
        System.Transactions.TransactionStatus.Committed => global::Adaptor.Service.TransactionState.Committed,
        System.Transactions.TransactionStatus.Aborted => global::Adaptor.Service.TransactionState.RolledBack,
        System.Transactions.TransactionStatus.InDoubt => global::Adaptor.Service.TransactionState.InDoubt,
        _ => global::Adaptor.Service.TransactionState.Unspecified,
    };

    /// <summary>
    /// Convert a CLR object to a protobuf <see cref="Value"/>.
    /// </summary>
    /// <remarks>
    /// Google.Protobuf has no built-in FromObject. Dictionaries become <see cref="Struct"/>,
    /// lists become <see cref="ListValue"/>. Uses if/else chain to avoid type-inference issues.
    /// </remarks>
    public static Value ObjectToValue(object? value)
    {
        if (value == null) return Value.ForNull();
        if (value is string s) return Value.ForString(s);
        if (value is int i) return Value.ForNumber(i);
        if (value is long l) return Value.ForNumber(l);
        if (value is float f) return Value.ForNumber(f);
        if (value is double d) return Value.ForNumber(d);
        if (value is bool b) return Value.ForBool(b);

        // Dictionary → Struct
        if (value is IEnumerable<KeyValuePair<string, object?>> dict)
        {
            var structValue = new Struct();
            foreach (var kvp in dict)
            {
                structValue.Fields[kvp.Key] = ObjectToValue(kvp.Value);
            }
            return Value.ForStruct(structValue);
        }

        // List → ListValue
        if (value is IEnumerable<object?> list)
        {
            var listValue = new ListValue();
            foreach (var item in list)
            {
                listValue.Values.Add(ObjectToValue(item));
            }
            return new Value { ListValue = listValue };
        }

        return Value.ForString(value.ToString() ?? string.Empty);
    }
}

#endregion

#region Transaction service

/// <summary>
/// gRPC Transaction service implementation.
/// Manages Begin / Commit / Rollback lifecycle.
/// Status queries map from <see cref="System.Transactions.TransactionStatus"/>.
/// </summary>
public sealed class TransactionServiceImpl : Transaction.TransactionBase
{
    private readonly AdaptorServiceContext _ctx;

    public TransactionServiceImpl(AdaptorServiceContext ctx)
    {
        _ctx = ctx;
    }

    public override async Task<BeginTransactionResponse> BeginTransaction(
        BeginTransactionRequest request, ServerCallContext context)
    {
        var timeout = request.Timeout?.ToTimeSpan();
        var connectionId = context.GetHttpContext().Connection.Id;

        var result = await _ctx.Coordinator.BeginTransactionAsync(
            timeout, connectionId, context.CancellationToken);

        var transactionId = result.Transaction.TransactionInformation.LocalIdentifier;

        _ctx.Logger.LogInformation(
            "BeginTransaction: transaction_id={TxId}, expires_at={Expires}",
            transactionId, result.ExpiresAt);

        return new BeginTransactionResponse
        {
            TransactionId = transactionId,
            ExpiresAt = Timestamp.FromDateTime(result.ExpiresAt.ToUniversalTime()),
        };
    }

    public override async Task<CommitTransactionResponse> CommitTransaction(
        CommitTransactionRequest request, ServerCallContext context)
    {
        var commitResult = await _ctx.Coordinator.CommitTransactionAsync(
            request.TransactionId, context.CancellationToken);

        var response = new CommitTransactionResponse
        {
            Status = AdaptorServiceContext.ConvertStatus(commitResult.Status),
            ErrorMessage = commitResult.ErrorMessage ?? string.Empty,
        };

        foreach (var dr in commitResult.DriverResults)
        {
            response.DriverResults.Add(new DriverCommitResult
            {
                DriverName = dr.DriverName,
                Success = dr.Success,
                RetryCount = dr.RetryCount,
                ErrorMessage = dr.ErrorMessage ?? string.Empty,
            });
        }

        return response;
    }

    public override async Task<RollbackTransactionResponse> RollbackTransaction(
        RollbackTransactionRequest request, ServerCallContext context)
    {
        await _ctx.Coordinator.RollbackTransactionAsync(
            request.TransactionId, context.CancellationToken);

        return new RollbackTransactionResponse
        {
            Success = true,
        };
    }

    public override Task<GetTransactionStatusResponse> GetTransactionStatus(
        GetTransactionStatusRequest request, ServerCallContext context)
    {
        var transactionId = request.TransactionId;
        var tx = _ctx.Coordinator.FindTransaction(transactionId);

        if (tx == null)
        {
            return Task.FromResult(new GetTransactionStatusResponse
            {
                State = global::Adaptor.Service.TransactionState.Unspecified,
            });
        }

        var state = AdaptorServiceContext.ConvertTransactionStatus(
            tx.TransactionInformation.Status);

        var response = new GetTransactionStatusResponse
        {
            State = state,
        };

        var expiry = tx.TransactionInformation.CreationTime + _ctx.Options.DefaultTransactionTimeout;
        response.ExpiresAt = Timestamp.FromDateTime(expiry.ToUniversalTime());

        return Task.FromResult(response);
    }
}

#endregion

#region DBRelational service

/// <summary>
/// gRPC DBRelational service implementation.
/// Provides Execute / Query / ExecuteBatch capabilities.
/// </summary>
public sealed class RelationalServiceImpl : DBRelational.DBRelationalBase
{
    private readonly AdaptorServiceContext _ctx;

    public RelationalServiceImpl(AdaptorServiceContext ctx)
    {
        _ctx = ctx;
    }

    public override async Task<RelationalExecuteResponse> Execute(
        RelationalExecuteRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
            txId,
            async (driver, transaction) =>
            {
                var internalRequest = new Coordinator.Models.RelationalExecuteRequest(
                    request.Command,
                    ConvertParameters(request.Parameters));

                return await driver.ExecuteAsync(internalRequest, transaction, context.CancellationToken);
            },
            context.CancellationToken);

        return new RelationalExecuteResponse
        {
            AffectedRows = result.AffectedRows,
            DurationMs = result.Duration.TotalMilliseconds,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };
    }

    public override async Task<RelationalQueryResponse> Query(
        RelationalQueryRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IRelationalQueryCapability, RelationalQueryResult>(
            txId,
            async (driver, transaction) =>
            {
                var internalRequest = new Coordinator.Models.RelationalQueryRequest(
                    request.Command,
                    ConvertParameters(request.Parameters));

                return await driver.QueryAsync(internalRequest, transaction, context.CancellationToken);
            },
            context.CancellationToken);

        var response = new RelationalQueryResponse
        {
            DurationMs = result.Duration.TotalMilliseconds,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };

        foreach (var row in result.Rows)
        {
            var protoRow = new Row();
            foreach (var kvp in row)
            {
                protoRow.Columns[kvp.Key] = AdaptorServiceContext.ObjectToValue(kvp.Value);
            }
            response.Rows.Add(protoRow);
        }

        return response;
    }

    public override async Task<RelationalExecuteBatchResponse> ExecuteBatch(
        RelationalExecuteBatchRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        var totalAffected = 0;
        var start = DateTime.UtcNow;

        foreach (var cmd in request.Commands)
        {
            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.RelationalExecuteRequest(
                        cmd.Command,
                        ConvertParameters(cmd.Parameters));
                    return await driver.ExecuteAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

            totalAffected += result.AffectedRows;
        }

        var duration = DateTime.UtcNow - start;

        return new RelationalExecuteBatchResponse
        {
            TotalAffectedRows = totalAffected,
            DurationMs = duration.TotalMilliseconds,
        };
    }

    private static IReadOnlyList<Coordinator.Models.RelationalParameter> ConvertParameters(
        IReadOnlyList<RelationalParameter>? protoParams)
    {
        if (protoParams == null || protoParams.Count == 0)
            return Array.Empty<Coordinator.Models.RelationalParameter>();

        return protoParams
            .Select(p => new Coordinator.Models.RelationalParameter(
                p.Name,
                ProtoValueToObject(p.Value)))
            .ToList();
    }

    private static object? ProtoValueToObject(Value? value)
    {
        if (value == null) return null;
        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => value.NumberValue,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(f => f.Key, f => ProtoValueToObject(f.Value)),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ProtoValueToObject).ToList(),
            _ => null,
        };
    }
}

#endregion

#region DBRelationalVector service

/// <summary>
/// gRPC DBRelationalVector service implementation.
/// Provides vector similarity search only; CRUD is handled via DBRelational native SQL.
/// </summary>
public sealed class RelationalVectorServiceImpl : DBRelationalVector.DBRelationalVectorBase
{
    private readonly AdaptorServiceContext _ctx;

    public RelationalVectorServiceImpl(AdaptorServiceContext ctx)
    {
        _ctx = ctx;
    }

    public override async Task<VectorSearchResponse> Search(
        RelationalVectorSearchRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IRelationalVectorSearchCapability, VectorSearchResult>(
            txId,
            async (driver, transaction) =>
            {
                var sparseVector = request.SparseVector == null ? null
                    : new Coordinator.Models.SparseVector(
                        request.SparseVector.Indices.ToArray(),
                        request.SparseVector.Values.ToArray());
                var internalRequest = new Coordinator.Models.RelationalVectorSearchRequest(
                    Table: request.Table,
                    VectorColumn: request.VectorColumn,
                    DenseVector: request.DenseVector?.ToArray(),
                    SparseVector: sparseVector,
                    TopK: request.TopK > 0 ? request.TopK : 10,
                    WhereClause: string.IsNullOrEmpty(request.WhereClause) ? null : request.WhereClause,
                    Parameters: request.Parameters?.Count > 0
                        ? request.Parameters.Select(p => new Coordinator.Models.RelationalParameter(
                            p.Name, ProtoValueToObject(p.Value))).ToList().AsReadOnly()
                        : null);

                return await driver.SearchAsync(internalRequest, transaction, context.CancellationToken);
            },
            context.CancellationToken);

        var response = new VectorSearchResponse
        {
            DurationMs = result.Duration.TotalMilliseconds,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };

        foreach (var hit in result.Hits)
        {
            var protoHit = new VectorSearchHit
            {
                Id = hit.Id,
                Score = hit.Score,
            };

            if (hit.Metadata != null)
            {
                foreach (var kvp in hit.Metadata)
                    protoHit.Metadata[kvp.Key] = AdaptorServiceContext.ObjectToValue(kvp.Value);
            }

            response.Hits.Add(protoHit);
        }

        return response;
    }

    private static IReadOnlyDictionary<string, object?>? ConvertMetadata(
        MapField<string, Value>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return null;

        return metadata.ToDictionary(
            kvp => kvp.Key,
            kvp => ProtoValueToObject(kvp.Value));
    }

    private static object? ProtoValueToObject(Value? value)
    {
        if (value == null) return null;
        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => value.NumberValue,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(f => f.Key, f => ProtoValueToObject(f.Value)),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ProtoValueToObject).ToList(),
            _ => null,
        };
    }
}

#endregion

#region DBBLOB service

/// <summary>
/// gRPC DBBLOB service implementation.
/// Provides BlobUpload / BlobDownload / BlobDelete / BlobList capabilities.
/// </summary>
public sealed class BlobServiceImpl : DBBLOB.DBBLOBBase
{
    private readonly AdaptorServiceContext _ctx;

    public BlobServiceImpl(AdaptorServiceContext ctx)
    {
        _ctx = ctx;
    }

    public override async Task<BlobUploadResponse> BlobUpload(
        BlobUploadRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IBlobUploadCapability, BlobUploadResult>(
            txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.BlobUploadRequest(
                        request.Key,
                        request.Data.ToByteArray(),
                        string.IsNullOrEmpty(request.ContentType) ? null : request.ContentType,
                        request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

                    return await driver.UploadAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

        return new BlobUploadResponse
        {
            Key = result.Key,
            Size = result.Size,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };
    }

    public override async Task<BlobDownloadResponse> BlobDownload(
        BlobDownloadRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IBlobDownloadCapability, BlobDownloadResult>(
            txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.BlobDownloadRequest(request.Key);
                    return await driver.DownloadAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

        return new BlobDownloadResponse
        {
            Key = result.Key,
            Data = Google.Protobuf.ByteString.CopyFrom(result.Data),
            ContentType = result.ContentType ?? string.Empty,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };
    }

    public override async Task<BlobDeleteResponse> BlobDelete(
        BlobDeleteRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        try
        {
            // PostgresBlobDriver has DeleteAsync but it is not part of IBlobUploadCapability.
            // Delete the mapping record via SQL; lo_unlink is handled separately.
            var sql = $"DELETE FROM adaptor_blob_store WHERE key = @key";
            await _ctx.Coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.RelationalExecuteRequest(
                        sql,
                        new List<Coordinator.Models.RelationalParameter>
                        {
                            new("key", request.Key),
                        });
                    return await driver.ExecuteAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

            _ctx.Logger.LogDebug("BlobDelete: key={Key} record removed (LO unlink is manual)", request.Key);
            return new BlobDeleteResponse { Success = true };
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "BlobDelete failed for key={Key}", request.Key);
            return new BlobDeleteResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<BlobListResponse> BlobList(
        BlobListRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        try
        {
            var sql = string.IsNullOrEmpty(request.Prefix)
                ? "SELECT key FROM adaptor_blob_store ORDER BY key"
                : "SELECT key FROM adaptor_blob_store WHERE key LIKE @prefix ORDER BY key";

            var parameters = string.IsNullOrEmpty(request.Prefix)
                ? null
                : new List<Coordinator.Models.RelationalParameter>
                {
                    new("prefix", request.Prefix + "%"),
                };

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IRelationalQueryCapability, RelationalQueryResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.RelationalQueryRequest(sql, parameters);
                    return await driver.QueryAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

            var response = new BlobListResponse();
            foreach (var row in result.Rows)
            {
                if (row.TryGetValue("key", out var key) && key is string s)
                {
                    response.Keys.Add(s);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "BlobList failed");
            return new BlobListResponse { ErrorMessage = ex.Message };
        }
    }

    #endregion
}
