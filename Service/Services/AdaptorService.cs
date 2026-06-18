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

// ═══════════════════════════════════════════════════════════════════════════
// 共享辅助方法
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 为四个 gRPC service 实现提供共享的基础设施。
///
/// gRPC 层的 transaction_id 直接复用 <see cref="TransactionInformation.LocalIdentifier"/>，
/// 不自定义 ID。Coordinator 提供 <see cref="TransactionCoordinator.FindTransaction"/>
/// 进行反向查找。
/// </summary>
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
    /// 将内部 CommitStatus 转换为 gRPC 枚举。
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
    /// 将 <see cref="System.Transactions.TransactionStatus"/> 映射为 gRPC TransactionState。
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
    /// 将 CLR object 转换为 protobuf <see cref="Value"/>。
    /// Google.Protobuf 没有内置 FromObject 方法，手动映射类型。
    /// 注意：字典用 <see cref="Struct"/> 表示，列表用 <see cref="ListValue"/> 表示。
    /// </summary>
    public static Value ObjectToValue(object? value)
    {
        // 这个方法使用 if/else 链而非 switch 表达式，
        // 以避免 Value.ForList 的类型推断问题和字典/列表的模式匹配冲突。
        if (value == null) return Value.ForNull();
        if (value is string s) return Value.ForString(s);
        if (value is int i) return Value.ForNumber(i);
        if (value is long l) return Value.ForNumber(l);
        if (value is float f) return Value.ForNumber(f);
        if (value is double d) return Value.ForNumber(d);
        if (value is bool b) return Value.ForBool(b);

        // 字典 → Struct
        if (value is IEnumerable<KeyValuePair<string, object?>> dict)
        {
            var structValue = new Struct();
            foreach (var kvp in dict)
            {
                structValue.Fields[kvp.Key] = ObjectToValue(kvp.Value);
            }
            return Value.ForStruct(structValue);
        }

        // 列表 → ListValue
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

// ═══════════════════════════════════════════════════════════════════════════
// Transaction 服务
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// gRPC Transaction service 实现。
/// 管理事务的 Begin / Commit / Rollback 生命周期。
/// 状态查询映射自 <see cref="System.Transactions.TransactionStatus"/>。
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

        // gRPC 的 transaction_id 直接复用 .NET TransactionInformation.LocalIdentifier
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

        // expires_at 从 CreationTime + 默认超时计算（一次性的值，不维护）
        var expiry = tx.TransactionInformation.CreationTime + _ctx.Options.DefaultTransactionTimeout;
        response.ExpiresAt = Timestamp.FromDateTime(expiry.ToUniversalTime());

        return Task.FromResult(response);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// DBSQL 服务
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// gRPC DBSQL service 实现。
/// 提供 SqlExecute / SqlQuery / SqlExecuteBatch 能力。
/// </summary>
public sealed class SqlServiceImpl : DBSQL.DBSQLBase
{
    private readonly AdaptorServiceContext _ctx;

    public SqlServiceImpl(AdaptorServiceContext ctx)
    {
        _ctx = ctx;
    }

    public override async Task<SqlExecuteResponse> SqlExecute(
        SqlExecuteRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlExecuteCapability, SqlExecuteResult>(
            txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlExecuteRequest(
                        request.Command,
                        ConvertParameters(request.Parameters));

                    return await driver.ExecuteAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

        return new SqlExecuteResponse
        {
            AffectedRows = result.AffectedRows,
            DurationMs = result.Duration.TotalMilliseconds,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };
    }

    public override async Task<global::Adaptor.Service.SqlQueryResponse> SqlQuery(
        global::Adaptor.Service.SqlQueryRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlQueryCapability, SqlQueryResult>(
            txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlQueryRequest(
                        request.Command,
                        ConvertParameters(request.Parameters));

                    return await driver.QueryAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

        var response = new global::Adaptor.Service.SqlQueryResponse
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

    public override async Task<SqlExecuteBatchResponse> SqlExecuteBatch(
        SqlExecuteBatchRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        var totalAffected = 0;
        var start = DateTime.UtcNow;

        foreach (var cmd in request.Commands)
        {
            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlExecuteCapability, SqlExecuteResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlExecuteRequest(cmd);
                    return await driver.ExecuteAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

            totalAffected += result.AffectedRows;
        }

        var duration = DateTime.UtcNow - start;

        return new SqlExecuteBatchResponse
        {
            TotalAffectedRows = totalAffected,
            DurationMs = duration.TotalMilliseconds,
        };
    }

    private static IReadOnlyList<Coordinator.Models.SqlParameter> ConvertParameters(
        IReadOnlyList<SqlParameter>? protoParams)
    {
        if (protoParams == null || protoParams.Count == 0)
            return Array.Empty<Coordinator.Models.SqlParameter>();

        return protoParams
            .Select(p => new Coordinator.Models.SqlParameter(
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

// ═══════════════════════════════════════════════════════════════════════════
// DBVector 服务
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// gRPC DBVector service 实现。
/// 提供 VectorUpsert / VectorSearch / VectorDelete / VectorListCollections 能力。
/// </summary>
public sealed class VectorServiceImpl : DBVector.DBVectorBase
{
    private readonly AdaptorServiceContext _ctx;

    public VectorServiceImpl(AdaptorServiceContext ctx)
    {
        _ctx = ctx;
    }

    public override async Task<VectorUpsertResponse> VectorUpsert(
        VectorUpsertRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IVectorUpsertCapability, VectorUpsertResult>(
            txId,
                async (driver, transaction) =>
                {
                    var sparseVector = request.SparseVector == null ? null
                        : new Coordinator.Models.SparseVector(
                            request.SparseVector.Indices.ToArray(),
                            request.SparseVector.Values.ToArray());
                    var internalRequest = new Coordinator.Models.VectorUpsertRequest(
                        request.Collection,
                        DenseVector: request.DenseVector?.ToArray(),
                        SparseVector: sparseVector,
                        Id: string.IsNullOrEmpty(request.Id) ? null : request.Id,
                        Metadata: ConvertMetadata(request.Metadata),
                        Dimension: request.Dimension > 0 ? request.Dimension : null);

                    return await driver.UpsertAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

        return new VectorUpsertResponse
        {
            Id = result.Id,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };
    }

    public override async Task<VectorSearchResponse> VectorSearch(
        VectorSearchRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<IVectorSearchCapability, VectorSearchResult>(
            txId,
                async (driver, transaction) =>
                {
                    var sparseVector = request.SparseVector == null ? null
                        : new Coordinator.Models.SparseVector(
                            request.SparseVector.Indices.ToArray(),
                            request.SparseVector.Values.ToArray());
                    var internalRequest = new Coordinator.Models.VectorSearchRequest(
                        request.Collection,
                        DenseVector: request.DenseVector?.ToArray(),
                        SparseVector: sparseVector,
                        TopK: request.TopK > 0 ? request.TopK : 10,
                        Filter: null, // Filter not fully mapped yet
                        Dimension: request.Dimension > 0 ? request.Dimension : null);

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
                {
                    protoHit.Metadata[kvp.Key] = AdaptorServiceContext.ObjectToValue(kvp.Value);
                }
            }

            response.Hits.Add(protoHit);
        }

        return response;
    }

    public override async Task<VectorDeleteResponse> VectorDelete(
        VectorDeleteRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        // 通过 ISqlExecuteCapability 执行原生 SQL 删除（pgvector 表操作）
        try
        {
            var sql = $"DELETE FROM adaptor_vector_store WHERE collection = @collection AND id = @id";

            await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlExecuteCapability, SqlExecuteResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlExecuteRequest(
                        sql,
                        new List<Coordinator.Models.SqlParameter>
                        {
                            new("collection", request.Collection),
                            new("id", request.Id),
                        });

                    return await driver.ExecuteAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

            return new VectorDeleteResponse { Success = true };
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "VectorDelete failed for collection={Collection} id={Id}",
                request.Collection, request.Id);
            return new VectorDeleteResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<VectorListCollectionsResponse> VectorListCollections(
        VectorListCollectionsRequest request, ServerCallContext context)
    {
        var txId = request.TransactionContext?.TransactionId ?? string.Empty;

        try
        {
            var sql = "SELECT DISTINCT collection FROM adaptor_vector_store ORDER BY collection";

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlQueryCapability, SqlQueryResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlQueryRequest(sql);
                    return await driver.QueryAsync(internalRequest, transaction, context.CancellationToken);
                },
                context.CancellationToken);

            var response = new VectorListCollectionsResponse();
            foreach (var row in result.Rows)
            {
                if (row.TryGetValue("collection", out var name) && name is string s)
                {
                    response.Collections.Add(s);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "VectorListCollections failed");
            return new VectorListCollectionsResponse { ErrorMessage = ex.Message };
        }
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

// ═══════════════════════════════════════════════════════════════════════════
// DBBLOB 服务
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// gRPC DBBLOB service 实现。
/// 提供 BlobUpload / BlobDownload / BlobDelete / BlobList 能力。
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
            // PostgresBlobDriver 有 DeleteAsync 方法，但不在 IBlobUploadCapability 接口中。
            // 我们通过 isql 直接操作 adaptor_blob_store 表来删除。
            // 由于 PostgreSQL Large Object 的 lo_unlink 需要特殊处理，
            // 这里走 ISqlExecuteCapability 执行删除映射记录和 lo_unlink。
            var sql = $"DELETE FROM adaptor_blob_store WHERE key = @key";
            await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlExecuteCapability, SqlExecuteResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlExecuteRequest(
                        sql,
                        new List<Coordinator.Models.SqlParameter>
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
                : new List<Coordinator.Models.SqlParameter>
                {
                    new("prefix", request.Prefix + "%"),
                };

            var result = await _ctx.Coordinator.ExecuteOnCapabilityAsync<ISqlQueryCapability, SqlQueryResult>(
                txId,
                async (driver, transaction) =>
                {
                    var internalRequest = new Coordinator.Models.SqlQueryRequest(sql, parameters);
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
}
