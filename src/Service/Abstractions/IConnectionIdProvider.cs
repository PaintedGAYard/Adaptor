using Grpc.Core;

namespace Adaptor.Service.Abstractions;

/// <summary>
/// Provides the gRPC connection ID for associating transactions with connections.
/// Abstracted to enable unit testing without an ASP.NET Core host.
/// </summary>
public interface IConnectionIdProvider
{
    /// <summary>Get the connection ID from the gRPC server call context.</summary>
    string GetConnectionId(ServerCallContext context);
}
