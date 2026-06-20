using System.Collections.Concurrent;
using System.Transactions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// PostgreSQL BLOB driver providing upload/download capabilities via the
/// PostgreSQL Large Object API. Shares the same database schema with
/// <see cref="PostgreSqlDriver"/> and <see cref="PgVectorDriver"/>,
/// and participates in distributed transactions via
/// <see cref="ITransactionalResourceManager.Enlist"/>.
/// </summary>
/// <remarks>
/// Uses PostgreSQL built-in Large Object mechanism (<c>lo_creat</c>/<c>lo_write</c>/<c>lo_read</c>/<c>lo_unlink</c>).
/// All LO operations are transactional and support rollback.
/// The <c>adaptor_blob_store</c> mapping table is auto-created on first upload.
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
    private readonly NpgsqlConnectionManager _connectionManager;
    private readonly ILogger<PostgresBlobDriver>? _logger;
    private readonly ConcurrentDictionary<int, RandomAccessState> _randomAccessStates = new();
    private bool _disposed;

    public string Name => "PostgreSQL BLOB";
    public ResourceType ResourceType => ResourceType.Blob;
    public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

    public PostgresBlobDriver(string connectionString, ILogger<PostgresBlobDriver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _connectionManager = new NpgsqlConnectionManager(connectionString, Name, logger);
        _logger = logger;
    }

    /// <summary>Create with an NpgsqlDataSource (recommended for connection pooling).</summary>
    public PostgresBlobDriver(NpgsqlDataSource dataSource, ILogger<PostgresBlobDriver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _connectionString = dataSource.ConnectionString;
        _connectionManager = new NpgsqlConnectionManager(dataSource, Name, logger);
        _logger = logger;
    }

    #region ITransactionalResourceManager

    public void Enlist(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connectionManager.Enlist(transaction);
    }

    #endregion

    #region IBlobUploadCapability

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

            // Step 2: Open the Large Object for writing and write data
            // lo_open returns a file descriptor; lowrite writes data at the current position
            int fd;
            await using (var cmdOpen = entry.Connection.CreateCommand())
            {
                cmdOpen.CommandText = "SELECT lo_open(@oid, @mode)";
                cmdOpen.Transaction = entry.LocalTransaction;
                cmdOpen.Parameters.AddWithValue("oid", oid);
                cmdOpen.Parameters.AddWithValue("mode", 0x20000); // INV_WRITE
                fd = Convert.ToInt32(await cmdOpen.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            await using (var cmdWrite = entry.Connection.CreateCommand())
            {
                cmdWrite.CommandText = "SELECT lowrite(@fd, @data)";
                cmdWrite.Transaction = entry.LocalTransaction;
                cmdWrite.Parameters.AddWithValue("fd", fd);
                cmdWrite.Parameters.AddWithValue("data", request.Data);
                await cmdWrite.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

            await using (var cmdClose = entry.Connection.CreateCommand())
            {
                cmdClose.CommandText = "SELECT lo_close(@fd)";
                cmdClose.Transaction = entry.LocalTransaction;
                cmdClose.Parameters.AddWithValue("fd", fd);
                await cmdClose.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

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

    #endregion

    #region IBlobDownloadCapability

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

                oid = Convert.ToInt32(reader.GetValue(0));
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
            var data = await ReadLargeObjectAsync(entry, oid, size, ct)
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

    #endregion

    #region IBlobRandomAccessCapability

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
                return await OpenCreateOrReplaceAsync(entry, key, mode, ct).ConfigureAwait(false);
            }
            else
            {
                return await OpenExistingAsync(entry, key, mode, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver OpenAsync failed for key={Key}", key);
            return new BlobOpenResult(0, 0, ex.Message);
        }
    }

    /// <summary>Handle Open for Create/CreateOrReplace modes.</summary>
    private async Task<BlobOpenResult> OpenCreateOrReplaceAsync(
        NpgsqlConnectionManager.ConnectionEntry entry, string key, BlobAccessMode mode, CancellationToken ct)
    {
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

        // Register the random-access state with position tracking
        var state = new RandomAccessState(oid, 0);
        _randomAccessStates[oid] = state;

        _logger?.LogDebug("PostgresBlobDriver Open(Create) key={Key} oid={Oid}", key, oid);
        return new BlobOpenResult(oid, 0);
    }

    /// <summary>Handle Open for existing blob modes (Read/Write/ReadWrite/Append).</summary>
    private async Task<BlobOpenResult> OpenExistingAsync(
        NpgsqlConnectionManager.ConnectionEntry entry, string key, BlobAccessMode mode, CancellationToken ct)
    {
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
            oid = Convert.ToInt32(reader.GetValue(0));
            blobSize = reader.GetInt64(1);
        }

        // Register the random-access state with position tracking
        var state = new RandomAccessState(oid, 0);
        _randomAccessStates[oid] = state;

        _logger?.LogDebug("PostgresBlobDriver Open key={Key} oid={Oid} mode={Mode} size={Size}",
            key, oid, mode, blobSize);
        return new BlobOpenResult(oid, blobSize);
    }

    public Task CloseAsync(int loFd, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        if (_randomAccessStates.TryRemove(loFd, out var state))
        {
            state.IsClosed = true;
            _logger?.LogDebug("PostgresBlobDriver Close(oid={Oid}) — released", loFd);
        }
        else
        {
            _logger?.LogDebug("PostgresBlobDriver Close(oid={Oid}) — already closed or unknown", loFd);
        }

        return Task.CompletedTask;
    }

    public async Task<BlobReadResult> ReadAsync(int loFd, int count, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!_randomAccessStates.TryGetValue(loFd, out var state) || state.IsClosed)
        {
            return new BlobReadResult(Array.Empty<byte>(), 0, $"Handle {loFd} is not open or has been closed.");
        }

        var entry = GetEntry(transaction);

        try
        {
            // Use the tracked position to read from the correct offset
            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_get(@oid, @offset, @count)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("oid", loFd);
            cmd.Parameters.AddWithValue("offset", state.Position);
            cmd.Parameters.AddWithValue("count", count);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var data = result as byte[] ?? Array.Empty<byte>();

            // Advance the tracked position
            state.Position += data.Length;

            _logger?.LogDebug("PostgresBlobDriver Read oid={Oid} offset={Offset} count={Count} got={BytesRead}",
                loFd, state.Position - data.Length, count, data.Length);
            return new BlobReadResult(data, data.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver ReadAsync failed for oid={Oid}", loFd);
            return new BlobReadResult(Array.Empty<byte>(), 0, ex.Message);
        }
    }

    public async Task<int> WriteAsync(int loFd, byte[] data, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(data);

        if (!_randomAccessStates.TryGetValue(loFd, out var state) || state.IsClosed)
        {
            throw new InvalidOperationException($"Handle {loFd} is not open or has been closed.");
        }

        var entry = GetEntry(transaction);

        try
        {
            // Write at the tracked position using lo_put(oid, offset, data)
            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = "SELECT lo_put(@oid, @offset, @data)";
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("oid", loFd);
            cmd.Parameters.AddWithValue("offset", state.Position);
            cmd.Parameters.AddWithValue("data", data);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Advance the tracked position
            state.Position += data.Length;

            _logger?.LogDebug("PostgresBlobDriver Write oid={Oid} offset={Offset} size={Size}",
                loFd, state.Position - data.Length, data.Length);
            return data.Length;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver WriteAsync failed for oid={Oid}", loFd);
            throw;
        }
    }

    public async Task<long> SeekAsync(int loFd, long offset, SeekOrigin origin, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!_randomAccessStates.TryGetValue(loFd, out var state) || state.IsClosed)
        {
            throw new InvalidOperationException($"Handle {loFd} is not open or has been closed.");
        }

        var entry = GetEntry(transaction);

        long newPosition;

        switch (origin)
        {
            case SeekOrigin.Begin:
                newPosition = Math.Max(0, offset);
                break;

            case SeekOrigin.Current:
                newPosition = Math.Max(0, state.Position + offset);
                break;

            case SeekOrigin.End:
                // Seek from end: need the blob size.
                // Open the LO, seek to end to get size, then close.
                long blobSize;
                int fd;
                await using (var cmdOpen = entry.Connection.CreateCommand())
                {
                    cmdOpen.CommandText = "SELECT lo_open(@oid, @mode)";
                    cmdOpen.Transaction = entry.LocalTransaction;
                    cmdOpen.Parameters.AddWithValue("oid", loFd);
                    cmdOpen.Parameters.AddWithValue("mode", 0x40000); // INV_READ
                    fd = Convert.ToInt32(await cmdOpen.ExecuteScalarAsync(ct).ConfigureAwait(false));
                }

                try
                {
                    await using var cmdSeek = entry.Connection.CreateCommand();
                    cmdSeek.CommandText = "SELECT lo_lseek64(@fd, 0, 2)";
                    cmdSeek.Transaction = entry.LocalTransaction;
                    cmdSeek.Parameters.AddWithValue("fd", fd);
                    var result = await cmdSeek.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    blobSize = result != null ? Convert.ToInt64(result) : 0;
                }
                finally
                {
                    await using var cmdClose = entry.Connection.CreateCommand();
                    cmdClose.CommandText = "SELECT lo_close(@fd)";
                    cmdClose.Transaction = entry.LocalTransaction;
                    cmdClose.Parameters.AddWithValue("fd", fd);
                    await cmdClose.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }

                newPosition = Math.Max(0, blobSize + offset);
                break;

            default:
                newPosition = state.Position;
                break;
        }

        state.Position = newPosition;
        _logger?.LogDebug("PostgresBlobDriver Seek oid={Oid} origin={Origin} offset={Offset} newPos={NewPos}",
            loFd, origin, offset, newPosition);
        return newPosition;
    }

    public async Task TruncateAsync(int loFd, long length, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!_randomAccessStates.TryGetValue(loFd, out var state) || state.IsClosed)
        {
            throw new InvalidOperationException($"Handle {loFd} is not open or has been closed.");
        }

        var entry = GetEntry(transaction);

        try
        {
            // lo_truncate requires a file descriptor. Open, truncate, close.
            int fd;
            await using (var cmdOpen = entry.Connection.CreateCommand())
            {
                cmdOpen.CommandText = "SELECT lo_open(@oid, @mode)";
                cmdOpen.Transaction = entry.LocalTransaction;
                cmdOpen.Parameters.AddWithValue("oid", loFd);
                cmdOpen.Parameters.AddWithValue("mode", 0x20000); // INV_WRITE
                fd = Convert.ToInt32(await cmdOpen.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            try
            {
                await using var cmdTrunc = entry.Connection.CreateCommand();
                cmdTrunc.CommandText = "SELECT lo_truncate64(@fd, @length)";
                cmdTrunc.Transaction = entry.LocalTransaction;
                cmdTrunc.Parameters.AddWithValue("fd", fd);
                cmdTrunc.Parameters.AddWithValue("length", length);
                await cmdTrunc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await using var cmdClose = entry.Connection.CreateCommand();
                cmdClose.CommandText = "SELECT lo_close(@fd)";
                cmdClose.Transaction = entry.LocalTransaction;
                cmdClose.Parameters.AddWithValue("fd", fd);
                await cmdClose.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

            // If the current position is past the new end, clamp it
            if (state.Position > length)
            {
                state.Position = length;
            }

            _logger?.LogDebug("PostgresBlobDriver Truncate oid={Oid} length={Length} done", loFd, length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresBlobDriver TruncateAsync failed for oid={Oid}", loFd);
            throw;
        }
    }

    #endregion

    #region IHealthCheckCapability

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _connectionManager.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    #endregion

    #region Public utility: delete a BLOB

    /// <summary>
    /// Delete a BLOB by key.
    /// <b>Not</b> part of the capability interfaces — call via direct driver reference.
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

    #endregion

    #region Internal helpers

    private NpgsqlConnectionManager.ConnectionEntry GetEntry(Transaction transaction)
    {
        return _connectionManager.GetEntry(transaction);
    }

    /// <summary>Auto-create the <c>adaptor_blob_store</c> table if it does not exist.</summary>
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

    /// <summary>Read the full content of a Large Object.</summary>
    /// <remarks>Reads in 32 MiB chunks to avoid oversized single queries.
    /// Uses <c>lo_open</c>/<c>loread</c>/<c>lo_close</c> — the standard PostgreSQL
    /// server-side Large Object API (not the client-side libpq API).</remarks>
    private static async Task<byte[]> ReadLargeObjectAsync(
        NpgsqlConnectionManager.ConnectionEntry entry,
        int oid,
        long totalSize,
        CancellationToken ct)
    {
        if (totalSize == 0)
            return Array.Empty<byte>();

        // Open the Large Object for reading
        int fd;
        await using (var cmdOpen = entry.Connection.CreateCommand())
        {
            cmdOpen.CommandText = "SELECT lo_open(@oid, @mode)";
            cmdOpen.Transaction = entry.LocalTransaction;
            cmdOpen.Parameters.AddWithValue("oid", oid);
            cmdOpen.Parameters.AddWithValue("mode", 0x40000); // INV_READ
            fd = Convert.ToInt32(await cmdOpen.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        try
        {
            const int chunkSize = 32 * 1024 * 1024; // 32 MiB

            if (totalSize <= chunkSize)
            {
                // Single read for small blobs
                await using var cmd = entry.Connection.CreateCommand();
                cmd.CommandText = "SELECT loread(@fd, @len)";
                cmd.Transaction = entry.LocalTransaction;
                cmd.Parameters.AddWithValue("fd", fd);
                cmd.Parameters.AddWithValue("len", (int)totalSize);

                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result as byte[] ?? Array.Empty<byte>();
            }

            // Chunked read for large blobs
            using var ms = new MemoryStream((int)totalSize);

            // loread reads from the current position and advances the internal offset,
            // so we just call it repeatedly without needing lo_lseek between chunks.
            while (ms.Length < totalSize)
            {
                var remaining = totalSize - ms.Length;
                var len = (int)Math.Min(chunkSize, remaining);

                await using var cmd = entry.Connection.CreateCommand();
                cmd.CommandText = "SELECT loread(@fd, @len)";
                cmd.Transaction = entry.LocalTransaction;
                cmd.Parameters.AddWithValue("fd", fd);
                cmd.Parameters.AddWithValue("len", len);

                var chunk = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (chunk is byte[] bytes)
                {
                    await ms.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
            }

            return ms.ToArray();
        }
        finally
        {
            // Always close the LO descriptor
            if (fd > 0)
            {
                await using var cmdClose = entry.Connection.CreateCommand();
                cmdClose.CommandText = "SELECT lo_close(@fd)";
                cmdClose.Transaction = entry.LocalTransaction;
                cmdClose.Parameters.AddWithValue("fd", fd);
                await cmdClose.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
        }
    }

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

    internal void RemoveEntry(string txId)
    {
        _connectionManager.RemoveEntry(txId);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectionManager.Dispose();
    }

    #endregion

    #region Nested types

    /// <summary>Tracks per-open-instance state for random-access operations.</summary>
    private sealed class RandomAccessState
    {
        public int Oid { get; }
        public long Position { get; set; }
        public bool IsClosed { get; set; }

        public RandomAccessState(int oid, long position)
        {
            Oid = oid;
            Position = position;
            IsClosed = false;
        }
    }

    #endregion
}
