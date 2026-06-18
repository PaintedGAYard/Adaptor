using System.Collections.Concurrent;
using System.Transactions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// PostgreSQL BLOB Driver — 提供 BLOB 上传/下载能力，基于 PostgreSQL
/// Large Object (LO) API 实现。与 <see cref="PostgreSqlDriver"/> 和
/// <see cref="PgVectorDriver"/> 共享同库模式，
/// 通过 <see cref="ITransactionalResourceManager.Enlist"/> 参与分布式事务。
/// </summary>
/// <remarks>
/// 使用 PostgreSQL 内置的 Large Object 机制（<c>lo_creat</c>/<c>lo_write</c>/<c>lo_read</c>/<c>lo_unlink</c>）。
/// 所有 LO 操作在事务内完成，支持事务回滚。首次上传时会自动创建
/// <c>adaptor_blob_store</c> 映射表。
/// </remarks>
public sealed class PostgresBlobDriver :
    IResourceManager,
    ITransactionalResourceManager,
    IBlobUploadCapability,
    IBlobDownloadCapability,
    IBlobRandomAccessCapability,
    IHealthCheckCapability,
    IDisposable
{
    private const string DefaultTableName = "adaptor_blob_store";

    private readonly string _connectionString;
    private readonly ILogger<PostgresBlobDriver>? _logger;
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private bool _disposed;

    public string Name => "PostgreSQL BLOB";
    public ResourceType ResourceType => ResourceType.Blob;
    public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

    public PostgresBlobDriver(string connectionString, ILogger<PostgresBlobDriver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logger = logger;
    }

    // ─── ITransactionalResourceManager ──────────────────────────────────────

    public void Enlist(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.ContainsKey(txId))
        {
            _logger?.LogDebug("PostgresBlobDriver already enlisted in transaction {TxId}", txId);
            return;
        }

        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        var localTransaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        var entry = new ConnectionEntry(connection, localTransaction, transaction);
        if (!_connections.TryAdd(txId, entry))
        {
            localTransaction.Dispose();
            connection.Dispose();
            return;
        }

        transaction.EnlistVolatile(new BlobEnlistmentHandler(this, txId), EnlistmentOptions.None);

        _logger?.LogDebug("PostgresBlobDriver enlisted in transaction {TxId}", txId);
    }

    // ─── IBlobUploadCapability ──────────────────────────────────────────────

    public async Task<BlobUploadResult> UploadAsync(BlobUploadRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);
        await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

        try
        {
            // Step 1: Create a new Large Object, returning its OID
            await using var cmdCreate = entry.Connection.CreateCommand();
            cmdCreate.CommandText = "SELECT lo_creat(-1)";
            cmdCreate.Transaction = entry.LocalTransaction;
            var oid = Convert.ToInt32(await cmdCreate.ExecuteScalarAsync(ct).ConfigureAwait(false));

            // Step 2: Write data into the Large Object
            await using var cmdWrite = entry.Connection.CreateCommand();
            cmdWrite.CommandText = "SELECT lo_write(@oid, 0, @data)";
            cmdWrite.Transaction = entry.LocalTransaction;
            cmdWrite.Parameters.AddWithValue("oid", oid);
            cmdWrite.Parameters.AddWithValue("data", request.Data);
            await cmdWrite.ExecuteScalarAsync(ct).ConfigureAwait(false);

            // Step 3: Insert the mapping record
            await using var cmdInsert = entry.Connection.CreateCommand();
            var metadataStr = MetadataToJsonString(request.Metadata);
            cmdInsert.CommandText = $"""
                INSERT INTO {DefaultTableName} (key, oid, content_type, metadata, size)
                VALUES (@key, @oid, @content_type, @metadata::jsonb, @size)
                """;
            cmdInsert.Transaction = entry.LocalTransaction;
            cmdInsert.Parameters.AddWithValue("key", request.Key);
            cmdInsert.Parameters.AddWithValue("oid", oid);
            cmdInsert.Parameters.AddWithValue("content_type", (object?)request.ContentType ?? DBNull.Value);
            cmdInsert.Parameters.AddWithValue("metadata", metadataStr);
            cmdInsert.Parameters.AddWithValue("size", request.Data.LongLength);
            await cmdInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger?.LogDebug("PostgresBlobDriver uploaded key={Key} oid={Oid} size={Size}",
                request.Key, oid, request.Data.Length);

            return new BlobUploadResult(request.Key, request.Data.LongLength);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver UploadAsync failed for key={Key}", request.Key);
            return new BlobUploadResult(request.Key, 0, ex.Message);
        }
    }

    // ─── IBlobDownloadCapability ────────────────────────────────────────────

    public async Task<BlobDownloadResult> DownloadAsync(BlobDownloadRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);
        await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

        try
        {
            // Step 1: Query the mapping table to get LO metadata
            await using var cmdQuery = entry.Connection.CreateCommand();
            cmdQuery.CommandText = $"""
                SELECT oid, content_type, size
                FROM {DefaultTableName}
                WHERE key = @key
                """;
            cmdQuery.Transaction = entry.LocalTransaction;
            cmdQuery.Parameters.AddWithValue("key", request.Key);

            int oid;
            string? contentType;
            long size;
            bool found;

            await using (var reader = await cmdQuery.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    _logger?.LogWarning("PostgresBlobDriver key={Key} not found", request.Key);
                    return new BlobDownloadResult(request.Key, Array.Empty<byte>(), null,
                        $"BLOB key '{request.Key}' not found.");
                }

                oid = reader.GetInt32(0);
                contentType = reader.IsDBNull(1) ? null : reader.GetString(1);
                size = reader.GetInt64(2);
                found = true;
            }

            if (!found || size <= 0)
            {
                return new BlobDownloadResult(request.Key, Array.Empty<byte>(), contentType,
                    size <= 0 ? $"BLOB '{request.Key}' is empty." : null);
            }

            // Step 2: Read the Large Object data
            await using var cmdRead = entry.Connection.CreateCommand();
            // lo_read(oid, offset, len) returns bytea; read in chunks for large blobs
            var data = await ReadLargeObjectAsync(entry.Connection, entry.LocalTransaction, oid, size, ct)
                .ConfigureAwait(false);

            _logger?.LogDebug("PostgresBlobDriver downloaded key={Key} oid={Oid} size={Size}",
                request.Key, oid, data.Length);

            return new BlobDownloadResult(request.Key, data, contentType);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver DownloadAsync failed for key={Key}", request.Key);
            return new BlobDownloadResult(request.Key, Array.Empty<byte>(), null, ex.Message);
        }
    }

    // ─── IBlobRandomAccessCapability ────────────────────────────────────────

    public async Task<BlobOpenResult> OpenAsync(string key, BlobAccessMode mode, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);
        await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

        try
        {
            if (mode is BlobAccessMode.Create or BlobAccessMode.CreateOrReplace)
            {
                // ── Create or CreateOrReplace ──
                await using var cmdCheck = entry.Connection.CreateCommand();
                cmdCheck.CommandText = $"SELECT oid FROM {DefaultTableName} WHERE key = @key";
                cmdCheck.Transaction = entry.LocalTransaction;
                cmdCheck.Parameters.AddWithValue("key", key);
                var existingOid = await cmdCheck.ExecuteScalarAsync(ct).ConfigureAwait(false);

                if (mode == BlobAccessMode.Create && existingOid != null)
                {
                    return new BlobOpenResult(0, 0, $"BLOB key '{key}' already exists.");
                }

                int oid;
                if (existingOid != null)
                {
                    // CreateOrReplace: create new LO, swap OID, unlink old
                    await using var cmdCreate = entry.Connection.CreateCommand();
                    cmdCreate.CommandText = "SELECT lo_creat(-1)";
                    cmdCreate.Transaction = entry.LocalTransaction;
                    oid = Convert.ToInt32(await cmdCreate.ExecuteScalarAsync(ct).ConfigureAwait(false));

                    await using var cmdUpdate = entry.Connection.CreateCommand();
                    cmdUpdate.CommandText = $"""
                        UPDATE {DefaultTableName}
                        SET oid = @oid, size = 0, content_type = NULL
                        WHERE key = @key
                        """;
                    cmdUpdate.Transaction = entry.LocalTransaction;
                    cmdUpdate.Parameters.AddWithValue("oid", oid);
                    cmdUpdate.Parameters.AddWithValue("key", key);
                    await cmdUpdate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    var oldOid = Convert.ToInt32(existingOid);
                    await using var cmdUnlink = entry.Connection.CreateCommand();
                    cmdUnlink.CommandText = "SELECT lo_unlink(@oid)";
                    cmdUnlink.Transaction = entry.LocalTransaction;
                    cmdUnlink.Parameters.AddWithValue("oid", oldOid);
                    await cmdUnlink.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    // Pure Create
                    await using var cmdCreate = entry.Connection.CreateCommand();
                    cmdCreate.CommandText = "SELECT lo_creat(-1)";
                    cmdCreate.Transaction = entry.LocalTransaction;
                    oid = Convert.ToInt32(await cmdCreate.ExecuteScalarAsync(ct).ConfigureAwait(false));

                    await using var cmdInsert = entry.Connection.CreateCommand();
                    cmdInsert.CommandText = $$"""
                        INSERT INTO {{DefaultTableName}} (key, oid, content_type, metadata, size)
                        VALUES (@key, @oid, NULL, '{}'::jsonb, 0)
                        """;
                    cmdInsert.Transaction = entry.LocalTransaction;
                    cmdInsert.Parameters.AddWithValue("key", key);
                    cmdInsert.Parameters.AddWithValue("oid", oid);
                    await cmdInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Open with INV_WRITE
                await using var cmdOpen = entry.Connection.CreateCommand();
                cmdOpen.CommandText = "SELECT lo_open(@oid, @mode)";
                cmdOpen.Transaction = entry.LocalTransaction;
                cmdOpen.Parameters.AddWithValue("oid", oid);
                cmdOpen.Parameters.AddWithValue("mode", 0x40000); // INV_WRITE
                var fd = Convert.ToInt32(await cmdOpen.ExecuteScalarAsync(ct).ConfigureAwait(false));

                _logger?.LogDebug("PostgresBlobDriver Open(Create) key={Key} oid={Oid} fd={Fd}", key, oid, fd);
                return new BlobOpenResult(fd, 0);
            }
            else
            {
                // ── Open existing blob ──
                await using var cmdQuery = entry.Connection.CreateCommand();
                cmdQuery.CommandText = $"SELECT oid, size FROM {DefaultTableName} WHERE key = @key";
                cmdQuery.Transaction = entry.LocalTransaction;
                cmdQuery.Parameters.AddWithValue("key", key);

                int oid;
                long blobSize;
                await using (var reader = await cmdQuery.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return new BlobOpenResult(0, 0, $"BLOB key '{key}' not found.");
                    }
                    oid = reader.GetInt32(0);
                    blobSize = reader.GetInt64(1);
                }

                var pgMode = mode switch
                {
                    BlobAccessMode.Read => 0x20000,      // INV_READ
                    BlobAccessMode.Write => 0x40000,     // INV_WRITE
                    BlobAccessMode.ReadWrite => 0x60000, // INV_READ | INV_WRITE
                    BlobAccessMode.Append => 0x40000,    // INV_WRITE
                    _ => 0x20000,
                };

                await using var cmdOpen = entry.Connection.CreateCommand();
                cmdOpen.CommandText = "SELECT lo_open(@oid, @mode)";
                cmdOpen.Transaction = entry.LocalTransaction;
                cmdOpen.Parameters.AddWithValue("oid", oid);
                cmdOpen.Parameters.AddWithValue("mode", pgMode);
                var fd = Convert.ToInt32(await cmdOpen.ExecuteScalarAsync(ct).ConfigureAwait(false));

                // For Append mode, seek to end
                if (mode == BlobAccessMode.Append && blobSize > 0)
                {
                    await using var cmdSeek = entry.Connection.CreateCommand();
                    cmdSeek.CommandText = "SELECT lo_lseek(@fd, 0, 2)"; // SEEK_END
                    cmdSeek.Transaction = entry.LocalTransaction;
                    cmdSeek.Parameters.AddWithValue("fd", fd);
                    await cmdSeek.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }

                _logger?.LogDebug("PostgresBlobDriver Open key={Key} oid={Oid} fd={Fd} mode={Mode} size={Size}",
                    key, oid, fd, mode, blobSize);
                return new BlobOpenResult(fd, blobSize);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver OpenAsync failed for key={Key}", key);
            return new BlobOpenResult(0, 0, ex.Message);
        }
    }

    public async Task CloseAsync(int loFd, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);

        try
        {
            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_close(@fd)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("fd", loFd);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            _logger?.LogDebug("PostgresBlobDriver closed fd={Fd}", loFd);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver CloseAsync failed for fd={Fd}", loFd);
            throw;
        }
    }

    public async Task<BlobReadResult> ReadAsync(int loFd, int count, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);

        try
        {
            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_read(@fd, @count)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("fd", loFd);
            cmd.Parameters.AddWithValue("count", count);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var data = result as byte[] ?? Array.Empty<byte>();

            _logger?.LogDebug("PostgresBlobDriver Read fd={Fd} count={Count} got={BytesRead}", loFd, count, data.Length);
            return new BlobReadResult(data, data.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver ReadAsync failed for fd={Fd}", loFd);
            return new BlobReadResult(Array.Empty<byte>(), 0, ex.Message);
        }
    }

    public async Task<int> WriteAsync(int loFd, byte[] data, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(data);

        var entry = GetEntry(transaction);

        try
        {
            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_write(@fd, @data)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("fd", loFd);
            cmd.Parameters.AddWithValue("data", data);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var bytesWritten = Convert.ToInt32(result);

            _logger?.LogDebug("PostgresBlobDriver Write fd={Fd} wrote={BytesWritten}", loFd, bytesWritten);
            return bytesWritten;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver WriteAsync failed for fd={Fd}", loFd);
            throw;
        }
    }

    public async Task<long> SeekAsync(int loFd, long offset, SeekOrigin origin, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);

        try
        {
            var whence = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => 1,
                SeekOrigin.End => 2,
                _ => 0,
            };

            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_lseek(@fd, @offset, @whence)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("fd", loFd);
            cmd.Parameters.AddWithValue("offset", offset);
            cmd.Parameters.AddWithValue("whence", whence);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var newPosition = Convert.ToInt64(result);

            _logger?.LogDebug("PostgresBlobDriver Seek fd={Fd} offset={Offset} whence={Whence} newPos={NewPos}",
                loFd, offset, origin, newPosition);
            return newPosition;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver SeekAsync failed for fd={Fd}", loFd);
            throw;
        }
    }

    public async Task TruncateAsync(int loFd, long length, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);

        try
        {
            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_truncate(@fd, @length)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("fd", loFd);
            cmd.Parameters.AddWithValue("length", length);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            _logger?.LogDebug("PostgresBlobDriver Truncate fd={Fd} length={Length}", loFd, length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver TruncateAsync failed for fd={Fd}", loFd);
            throw;
        }
    }

    // ─── IHealthCheckCapability ─────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PostgresBlobDriver health check failed");
            return false;
        }
    }

    // ─── Public utility: delete a BLOB ──────────────────────────────────────

    /// <summary>
    /// Delete a BLOB by key. Unlinks the underlying Large Object and removes
    /// the mapping record. This method is NOT part of the capability interfaces
    /// in the current abstractions; call it via direct driver usage.
    /// </summary>
    public async Task<BlobUploadResult> DeleteAsync(string key, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);
        await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

        try
        {
            // Get OID first
            await using var cmdGet = entry.Connection.CreateCommand();
            cmdGet.CommandText = $"SELECT oid FROM {DefaultTableName} WHERE key = @key";
            cmdGet.Transaction = entry.LocalTransaction;
            cmdGet.Parameters.AddWithValue("key", key);

            var oidResult = await cmdGet.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (oidResult == null)
            {
                return new BlobUploadResult(key, 0, $"BLOB key '{key}' not found.");
            }

            var oid = Convert.ToInt32(oidResult);

            // Unlink the Large Object
            await using var cmdUnlink = entry.Connection.CreateCommand();
            cmdUnlink.CommandText = "SELECT lo_unlink(@oid)";
            cmdUnlink.Transaction = entry.LocalTransaction;
            cmdUnlink.Parameters.AddWithValue("oid", oid);
            await cmdUnlink.ExecuteScalarAsync(ct).ConfigureAwait(false);

            // Delete the mapping record
            await using var cmdDelete = entry.Connection.CreateCommand();
            cmdDelete.CommandText = $"DELETE FROM {DefaultTableName} WHERE key = @key";
            cmdDelete.Transaction = entry.LocalTransaction;
            cmdDelete.Parameters.AddWithValue("key", key);
            await cmdDelete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger?.LogDebug("PostgresBlobDriver deleted key={Key} oid={Oid}", key, oid);

            return new BlobUploadResult(key, 0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver DeleteAsync failed for key={Key}", key);
            return new BlobUploadResult(key, 0, ex.Message);
        }
    }

    // ─── Internal helpers ───────────────────────────────────────────────────

    private ConnectionEntry GetEntry(Transaction transaction)
    {
        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.TryGetValue(txId, out var entry))
            return entry;

        throw new InvalidOperationException(
            $"Transaction (LocalIdentifier={txId}) is not enlisted with this driver. " +
            "Enlist() must be called before data operations.");
    }

    /// <summary>
    /// Auto-create the BLOB mapping table if it does not exist.
    /// </summary>
    private async Task EnsureTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {DefaultTableName} (
                key TEXT PRIMARY KEY,
                oid OID NOT NULL,
                content_type TEXT,
                metadata JSONB,
                size BIGINT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        cmd.Transaction = transaction;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger?.LogDebug("PostgresBlobDriver ensured table {Table} exists", DefaultTableName);
    }

    /// <summary>
    /// Read the full content of a Large Object using <c>lo_read</c>.
    /// Reads in 32 MiB chunks to avoid oversized single queries.
    /// </summary>
    private static async Task<byte[]> ReadLargeObjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int oid,
        long totalSize,
        CancellationToken ct)
    {
        if (totalSize == 0)
            return Array.Empty<byte>();

        const int chunkSize = 32 * 1024 * 1024; // 32 MiB

        if (totalSize <= chunkSize)
        {
            // Single read for small blobs
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT lo_read(@oid, 0, @len)";
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("oid", oid);
            cmd.Parameters.AddWithValue("len", (int)totalSize);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result as byte[] ?? Array.Empty<byte>();
        }

        // Chunked read for large blobs
        using var ms = new MemoryStream((int)totalSize);
        var offset = 0;

        while (offset < totalSize)
        {
            var remaining = totalSize - offset;
            var len = (int)Math.Min(chunkSize, remaining);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT lo_read(@oid, @offset, @len)";
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("oid", oid);
            cmd.Parameters.AddWithValue("offset", offset);
            cmd.Parameters.AddWithValue("len", len);

            var chunk = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (chunk is byte[] bytes)
            {
                await ms.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            offset += len;
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Serialize metadata dictionary to a JSON string.
    /// Used as a parameter value with ::jsonb cast.
    /// </summary>
    private static string MetadataToJsonString(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return "{}";

        var parts = metadata.Select(kvp =>
        {
            var key = System.Text.Json.JsonSerializer.Serialize(kvp.Key);
            var value = System.Text.Json.JsonSerializer.Serialize(kvp.Value);
            return $"{key}:{value}";
        });

        return $"{{{string.Join(",", parts)}}}";
    }

    /// <summary>
    /// Called by <see cref="BlobEnlistmentHandler"/> when a transaction completes.
    /// </summary>
    internal void RemoveEntry(string txId)
    {
        if (_connections.TryRemove(txId, out var entry))
        {
            _logger?.LogDebug("PostgresBlobDriver removing transaction {TxId}", txId);
            entry.Dispose();
        }
    }

    // ─── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, entry) in _connections)
        {
            entry.Dispose();
        }
        _connections.Clear();
    }

    // ─── Nested types ───────────────────────────────────────────────────────

    private sealed record ConnectionEntry : IDisposable
    {
        public NpgsqlConnection Connection { get; }
        public NpgsqlTransaction LocalTransaction { get; }

        public ConnectionEntry(NpgsqlConnection connection, NpgsqlTransaction localTransaction, Transaction transaction)
        {
            Connection = connection;
            LocalTransaction = localTransaction;
        }

        public void Dispose()
        {
            try { LocalTransaction.Dispose(); } catch { }
            try { Connection.Dispose(); } catch { }
        }
    }

    private sealed class BlobEnlistmentHandler : IEnlistmentNotification
    {
        private readonly PostgresBlobDriver _driver;
        private readonly string _txId;

        public BlobEnlistmentHandler(PostgresBlobDriver driver, string txId)
        {
            _driver = driver;
            _txId = txId;
        }

        void IEnlistmentNotification.Prepare(PreparingEnlistment preparingEnlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PostgresBlobDriver [{TxId}] Prepare: voting Prepared", _txId);
                preparingEnlistment.Prepared();
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PostgresBlobDriver [{TxId}] Prepare: force rollback", _txId);
                preparingEnlistment.ForceRollback();
            }
        }

        void IEnlistmentNotification.Commit(Enlistment enlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PostgresBlobDriver [{TxId}] Commit: committing local transaction", _txId);

                if (_driver._connections.TryGetValue(_txId, out var entry))
                {
                    entry.LocalTransaction.Commit();
                    _driver._logger?.LogDebug("PostgresBlobDriver [{TxId}] local transaction committed", _txId);
                }
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PostgresBlobDriver [{TxId}] Commit failed", _txId);
            }
            finally
            {
                _driver.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.Rollback(Enlistment enlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PostgresBlobDriver [{TxId}] Rollback: rolling back local transaction", _txId);

                if (_driver._connections.TryGetValue(_txId, out var entry))
                {
                    entry.LocalTransaction.Rollback();
                    _driver._logger?.LogDebug("PostgresBlobDriver [{TxId}] local transaction rolled back", _txId);
                }
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PostgresBlobDriver [{TxId}] Rollback failed", _txId);
            }
            finally
            {
                _driver.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.InDoubt(Enlistment enlistment)
        {
            _driver._logger?.LogWarning("PostgresBlobDriver [{TxId}] InDoubt: transaction outcome unknown", _txId);
            _driver.RemoveEntry(_txId);
            enlistment.Done();
        }
    }
}
