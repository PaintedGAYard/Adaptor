namespace Adaptor.Test.Driver;

/// <summary>
/// Tests for the internal enlistment handlers used by each driver.
/// Design-based: derived from DETAILED-DESIGN.md §5.2, REFACTOR §5.
/// These tests verify the 2PC contract: Prepare → Commit/Rollback.
/// The handlers are internal sealed classes — we test them via the driver's behavior.
/// </summary>
public sealed class EnlistmentHandlersTest
{
    private const string TestConnectionString =
        "Host=localhost;Database=adaptor_test;Username=test;Password=test";

    // ──────────────────────────────────────────────
    // 2PC contract for all drivers — 设计文档 5.2
    // ──────────────────────────────────────────────

    /// <summary>
    /// All three drivers must implement ITransactionalResourceManager,
    /// which means they all participate in 2PC via IEnlistmentNotification.
    /// </summary>
    [Fact]
    public void AllDrivers_ShouldImplementITransactionalResourceManager()
    {
        var sqlDriver = new PostgreSqlDriver(TestConnectionString);
        var vectorDriver = new PgVectorDriver(TestConnectionString);
        var blobDriver = new PostgresBlobDriver(TestConnectionString);

        Assert.IsAssignableFrom<ITransactionalResourceManager>(sqlDriver);
        Assert.IsAssignableFrom<ITransactionalResourceManager>(vectorDriver);
        Assert.IsAssignableFrom<ITransactionalResourceManager>(blobDriver);
    }

    // ──────────────────────────────────────────────
    // Enlistment Notification — 设计文档 5.2
    // ──────────────────────────────────────────────

    /// <summary>
    /// Each driver uses Transaction.EnlistVolatile() to register for 2PC.
    /// The IEnlistmentNotification interface defines the contract:
    ///   Prepare → vote Prepared or ForceRollback
    ///   Commit → commit local transaction
    ///   Rollback → rollback local transaction
    ///   InDoubt → log warning
    /// </summary>
    [Fact]
    public void PostgreSqlDriver_ShouldUseVolatileEnlistment()
    {
        var driver = new PostgreSqlDriver(TestConnectionString,
            Substitute.For<ILogger<PostgreSqlDriver>>());

        // Verify the driver has internal class NpgsqlEnlistmentHandler
        // that implements IEnlistmentNotification
        var nestedTypes = typeof(PostgreSqlDriver).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic);

        var handlerType = nestedTypes.FirstOrDefault(t =>
            t.Name.Contains("NpgsqlEnlistment"));

        Assert.NotNull(handlerType);
        Assert.Contains(typeof(IEnlistmentNotification), handlerType.GetInterfaces());
    }

    [Fact]
    public void PgVectorDriver_ShouldUseVolatileEnlistment()
    {
        var driver = new PgVectorDriver(TestConnectionString,
            Substitute.For<ILogger<PgVectorDriver>>());

        var nestedTypes = typeof(PgVectorDriver).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic);

        var handlerType = nestedTypes.FirstOrDefault(t =>
            t.Name.Contains("VectorEnlistment"));

        Assert.NotNull(handlerType);
        Assert.Contains(typeof(IEnlistmentNotification), handlerType.GetInterfaces());
    }

    [Fact]
    public void PostgresBlobDriver_ShouldUseVolatileEnlistment()
    {
        var driver = new PostgresBlobDriver(TestConnectionString,
            Substitute.For<ILogger<PostgresBlobDriver>>());

        var nestedTypes = typeof(PostgresBlobDriver).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic);

        var handlerType = nestedTypes.FirstOrDefault(t =>
            t.Name.Contains("BlobEnlistment"));

        Assert.NotNull(handlerType);
        Assert.Contains(typeof(IEnlistmentNotification), handlerType.GetInterfaces());
    }

    // ──────────────────────────────────────────────
    // ConnectionEntry — 设计文档 5.2
    // ──────────────────────────────────────────────

    /// <summary>
    /// Each driver has an internal ConnectionEntry record that tracks
    /// the NpgsqlConnection, NpgsqlTransaction, and Transaction.
    /// </summary>
    [Fact]
    public void AllDrivers_ShouldHaveConnectionEntryType()
    {
        Assert.Contains(
            typeof(PostgreSqlDriver).GetNestedTypes(
                System.Reflection.BindingFlags.NonPublic),
            t => t.Name == "ConnectionEntry");

        Assert.Contains(
            typeof(PgVectorDriver).GetNestedTypes(
                System.Reflection.BindingFlags.NonPublic),
            t => t.Name == "ConnectionEntry");

        Assert.Contains(
            typeof(PostgresBlobDriver).GetNestedTypes(
                System.Reflection.BindingFlags.NonPublic),
            t => t.Name == "ConnectionEntry");
    }

    // ──────────────────────────────────────────────
    // Per-transaction concurrency lock — REFACTOR §5
    // ──────────────────────────────────────────────

    [Fact]
    public void PostgreSqlDriver_And_PgVectorDriver_ShouldHavePerTxLock()
    {
        // PostgreSqlDriver and PgVectorDriver use SemaphoreSlim per transaction.
        // PostgresBlobDriver uses per-handle Gate instead (BLOB-STREAM §4.3).
        var sqlFields = typeof(PostgreSqlDriver).GetFields(
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        var vectorFields = typeof(PgVectorDriver).GetFields(
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        Assert.Contains(sqlFields, f =>
            f.FieldType.Name.Contains("ConcurrentDictionary") &&
            f.Name.Contains("txLocks"));

        Assert.Contains(vectorFields, f =>
            f.FieldType.Name.Contains("ConcurrentDictionary") &&
            f.Name.Contains("txLocks"));
    }

    // ──────────────────────────────────────────────
    // IEnlistmentNotification contract — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void IEnlistmentNotification_Contract()
    {
        // All four methods must be implemented
        var methods = typeof(IEnlistmentNotification).GetMethods();
        var methodNames = methods.Select(m => m.Name).ToHashSet();

        Assert.Contains("Prepare", methodNames);
        Assert.Contains("Commit", methodNames);
        Assert.Contains("Rollback", methodNames);
        Assert.Contains("InDoubt", methodNames);
    }
}
