namespace Adaptor.Test.Service;

using CoordCommitStatus = Adaptor.Coordinator.Models.CommitStatus;
using CoordTxStatus = System.Transactions.TransactionStatus;
using ProtoCommitStatus = global::Adaptor.Service.CommitStatus;
using ProtoTxState = global::Adaptor.Service.TransactionState;

/// <summary>
/// Tests for <see cref="AdaptorServiceContext"/> static helper methods.
/// These methods handle protobuf ↔ internal model conversions for the gRPC layer.
/// </summary>
public sealed class AdaptorServiceContextTests
{
    // ──── ConvertStatus ────

    [Fact]
    public void ConvertStatus_ShouldMapCommitted() =>
        Assert.Equal(ProtoCommitStatus.Committed, AdaptorServiceContext.ConvertStatus(CoordCommitStatus.Committed));

    [Fact]
    public void ConvertStatus_ShouldMapPartial() =>
        Assert.Equal(ProtoCommitStatus.Partial, AdaptorServiceContext.ConvertStatus(CoordCommitStatus.Partial));

    [Fact]
    public void ConvertStatus_ShouldMapRolledBack() =>
        Assert.Equal(ProtoCommitStatus.RolledBack, AdaptorServiceContext.ConvertStatus(CoordCommitStatus.RolledBack));

    [Fact]
    public void ConvertStatus_ShouldMapTimeout() =>
        Assert.Equal(ProtoCommitStatus.Timeout, AdaptorServiceContext.ConvertStatus(CoordCommitStatus.Timeout));

    [Fact]
    public void ConvertStatus_ShouldDefaultToUnspecified() =>
        Assert.Equal(ProtoCommitStatus.Unspecified, AdaptorServiceContext.ConvertStatus((CoordCommitStatus)99));

    // ──── ConvertTransactionStatus ────

    [Fact]
    public void ConvertTxStatus_ShouldMapActive() =>
        Assert.Equal(ProtoTxState.Active, AdaptorServiceContext.ConvertTransactionStatus(CoordTxStatus.Active));

    [Fact]
    public void ConvertTxStatus_ShouldMapCommitted() =>
        Assert.Equal(ProtoTxState.Committed, AdaptorServiceContext.ConvertTransactionStatus(CoordTxStatus.Committed));

    [Fact]
    public void ConvertTxStatus_ShouldMapAborted() =>
        Assert.Equal(ProtoTxState.RolledBack, AdaptorServiceContext.ConvertTransactionStatus(CoordTxStatus.Aborted));

    [Fact]
    public void ConvertTxStatus_ShouldMapInDoubt() =>
        Assert.Equal(ProtoTxState.InDoubt, AdaptorServiceContext.ConvertTransactionStatus(CoordTxStatus.InDoubt));

    [Fact]
    public void ConvertTxStatus_ShouldDefaultToUnspecified() =>
        Assert.Equal(ProtoTxState.Unspecified, AdaptorServiceContext.ConvertTransactionStatus((CoordTxStatus)99));

    // ──── ObjectToValue ────

    [Fact]
    public void ObjectToValue_ShouldConvertNull()
    {
        var value = AdaptorServiceContext.ObjectToValue(null);
        Assert.Equal(Value.KindOneofCase.NullValue, value.KindCase);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertString()
    {
        Assert.Equal("hello", AdaptorServiceContext.ObjectToValue("hello").StringValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertInt()
    {
        Assert.Equal(42.0, AdaptorServiceContext.ObjectToValue(42).NumberValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertLong()
    {
        Assert.Equal(99.0, AdaptorServiceContext.ObjectToValue(99L).NumberValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertFloat()
    {
        Assert.Equal(3.140000104904175, AdaptorServiceContext.ObjectToValue(3.14f).NumberValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertDouble()
    {
        Assert.Equal(2.71828, AdaptorServiceContext.ObjectToValue(2.71828).NumberValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertBool()
    {
        Assert.True(AdaptorServiceContext.ObjectToValue(true).BoolValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertDictionaryToStruct()
    {
        var dict = new Dictionary<string, object?> { ["name"] = "Alice", ["age"] = 30 };
        var value = AdaptorServiceContext.ObjectToValue(dict);
        Assert.Equal(Value.KindOneofCase.StructValue, value.KindCase);
        Assert.Equal("Alice", value.StructValue.Fields["name"].StringValue);
        Assert.Equal(30.0, value.StructValue.Fields["age"].NumberValue);
    }

    [Fact]
    public void ObjectToValue_ShouldConvertList()
    {
        var value = AdaptorServiceContext.ObjectToValue(new List<object?> { "a", 1, true });
        Assert.Equal(Value.KindOneofCase.ListValue, value.KindCase);
        Assert.Equal("a", value.ListValue.Values[0].StringValue);
        Assert.Equal(1.0, value.ListValue.Values[1].NumberValue);
        Assert.True(value.ListValue.Values[2].BoolValue);
    }

    [Fact]
    public void ObjectToValue_ShouldHandleNestedStruct()
    {
        var nested = new Dictionary<string, object?>
        {
            ["inner"] = new Dictionary<string, object?> { ["key"] = "val" },
        };
        var value = AdaptorServiceContext.ObjectToValue(nested);
        Assert.Equal("val", value.StructValue.Fields["inner"].StructValue.Fields["key"].StringValue);
    }

    [Fact]
    public void ObjectToValue_ShouldFallbackToString()
    {
        var value = AdaptorServiceContext.ObjectToValue(Guid.Empty);
        Assert.Equal("00000000-0000-0000-0000-000000000000", value.StringValue);
    }
}
