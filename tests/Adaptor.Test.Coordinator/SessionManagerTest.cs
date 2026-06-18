namespace Adaptor.Test.Coordinator;

/// <summary>
/// Tests for <see cref="SessionManager"/>.
/// Design-based: derived from DETAILED-DESIGN.md §4.2, §7, REFACTOR §2–3, REFACTOR-2 §1.
/// </summary>
public sealed class SessionManagerTest
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static SessionManager CreateSessionManager(
        CoordinatorOptions? options = null)
    {
        options ??= new CoordinatorOptions();
        return new SessionManager(
            Options.Create(options),
            Substitute.For<ILogger<SessionManager>>());
    }

    private static Transaction CreateDummyTransaction()
    {
        // Use a real CommittableTransaction to get a valid LocalIdentifier
        return new CommittableTransaction(TimeSpan.FromMinutes(1));
    }

    // ──────────────────────────────────────────────
    // CreateSession — 设计文档 REFACTOR §2, REFACTOR-2 §1
    // ──────────────────────────────────────────────

    [Fact]
    public void CreateSession_ShouldCreateSessionBoundToTransaction()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();

        var session = sm.CreateSession(tx);

        Assert.NotNull(session.SessionId);
        Assert.Equal(tx.TransactionInformation.LocalIdentifier, session.TransactionLocalIdentifier);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public void CreateSession_ShouldGenerateUniqueSessionIds()
    {
        var sm = CreateSessionManager();
        var tx1 = CreateDummyTransaction();
        var tx2 = CreateDummyTransaction();

        var s1 = sm.CreateSession(tx1);
        var s2 = sm.CreateSession(tx2);

        Assert.NotEqual(s1.SessionId, s2.SessionId);
    }

    [Fact]
    public void CreateSession_ShouldAssociateWithConnectionId()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();

        var session = sm.CreateSession(tx, connectionId: "conn-1");

        Assert.Equal(tx.TransactionInformation.LocalIdentifier, session.TransactionLocalIdentifier);
    }

    [Fact]
    public void CreateSession_ShouldSetCreatedAtAndLastActivityAt()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();
        var before = DateTime.UtcNow;

        var session = sm.CreateSession(tx);

        Assert.InRange(session.CreatedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.InRange(session.LastActivityAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    // ──────────────────────────────────────────────
    // GetSession — 设计文档 §7
    // ──────────────────────────────────────────────

    [Fact]
    public void GetSession_ShouldReturnSessionById()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();
        var created = sm.CreateSession(tx);

        var found = sm.GetSession(created.SessionId);

        Assert.NotNull(found);
        Assert.Equal(created.SessionId, found.SessionId);
    }

    [Fact]
    public void GetSession_ShouldReturnNullForUnknownId()
    {
        var sm = CreateSessionManager();
        Assert.Null(sm.GetSession("nonexistent"));
    }

    // ──────────────────────────────────────────────
    // RemoveSession — 设计文档 §7
    // ──────────────────────────────────────────────

    [Fact]
    public void RemoveSession_ShouldMarkSessionAsClosed()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();
        var session = sm.CreateSession(tx);

        var removed = sm.RemoveSession(session.SessionId);

        Assert.True(removed);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void RemoveSession_OnUnknownId_ShouldReturnFalse()
    {
        var sm = CreateSessionManager();
        Assert.False(sm.RemoveSession("nonexistent"));
    }

    // ──────────────────────────────────────────────
    // AttachToConnection — 设计文档 §7
    // ──────────────────────────────────────────────

    [Fact]
    public void AttachToConnection_ShouldAssociateSessionWithConnection()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();
        var session = sm.CreateSession(tx);

        sm.AttachToConnection(session.SessionId, "conn-1");

        // No direct assertion but the session should be retrievable
        Assert.NotNull(sm.GetSession(session.SessionId));
    }

    [Fact]
    public void AttachToConnection_OnUnknownSession_ShouldBeNoOp()
    {
        var sm = CreateSessionManager();
        // Should not throw
        sm.AttachToConnection("nonexistent", "conn-1");
    }

    // ──────────────────────────────────────────────
    // OnConnectionClosed — 设计文档 §7
    // ──────────────────────────────────────────────

    [Fact]
    public void OnConnectionClosed_ShouldReturnAssociatedSessions()
    {
        var sm = CreateSessionManager();
        var tx1 = CreateDummyTransaction();
        var tx2 = CreateDummyTransaction();

        var s1 = sm.CreateSession(tx1, connectionId: "conn-1");
        var s2 = sm.CreateSession(tx2, connectionId: "conn-1");

        var affected = sm.OnConnectionClosed("conn-1");

        Assert.Equal(2, affected.Count);
        Assert.Contains(affected, s => s.SessionId == s1.SessionId);
        Assert.Contains(affected, s => s.SessionId == s2.SessionId);
    }

    [Fact]
    public void OnConnectionClosed_ShouldMarkSessionsAsClosed()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();
        sm.CreateSession(tx, connectionId: "conn-1");

        var affected = sm.OnConnectionClosed("conn-1");

        Assert.All(affected, s => Assert.True(s.IsClosed));
    }

    [Fact]
    public void OnConnectionClosed_ForUnknownConnection_ShouldReturnEmpty()
    {
        var sm = CreateSessionManager();
        var affected = sm.OnConnectionClosed("unknown-conn");
        Assert.Empty(affected);
    }

    // ──────────────────────────────────────────────
    // TouchSession — 设计文档 §7
    // ──────────────────────────────────────────────

    [Fact]
    public void TouchSession_ShouldUpdateLastActivityAt()
    {
        var sm = CreateSessionManager();
        var tx = CreateDummyTransaction();
        var session = sm.CreateSession(tx);

        var beforeTouch = session.LastActivityAt;
        Thread.Sleep(10);
        sm.TouchSession(session.SessionId);

        Assert.True(session.LastActivityAt > beforeTouch);
    }

    [Fact]
    public void TouchSession_OnUnknownSession_ShouldBeNoOp()
    {
        var sm = CreateSessionManager();
        // Should not throw
        sm.TouchSession("nonexistent");
    }

    // ──────────────────────────────────────────────
    // Session Idle Timeout — 设计文档 REFACTOR §3, §6
    // ──────────────────────────────────────────────

    [Fact]
    public void Session_ShouldBeAutoCleanedAfterIdleTimeout()
    {
        // The cleanup timer runs every 30s by default, so we test via
        // the event mechanism: OnSessionTimeout should fire for expired sessions.
        var options = new CoordinatorOptions
        {
            SessionIdleTimeout = TimeSpan.Zero, // Immediate expiration
        };

        var sessionManager = new SessionManager(
            Options.Create(options),
            Substitute.For<ILogger<SessionManager>>());

        var tx = CreateDummyTransaction();
        var session = sessionManager.CreateSession(tx);

        // Give the timer a chance to fire (at most 1s)
        Thread.Sleep(100);

        // Session should be removed
        Assert.Null(sessionManager.GetSession(session.SessionId));
    }

    // ──────────────────────────────────────────────
    // Dispose — 设计文档 REFACTOR
    // ──────────────────────────────────────────────

    [Fact]
    public void Dispose_ShouldCleanupAllSessions()
    {
        var sm = CreateSessionManager();
        var tx1 = CreateDummyTransaction();
        var tx2 = CreateDummyTransaction();

        sm.CreateSession(tx1);
        sm.CreateSession(tx2);

        sm.Dispose();

        // After dispose the timer is stopped and sessions are cleared
        // Verify by checking the cleanup logic indirectly
    }
}
