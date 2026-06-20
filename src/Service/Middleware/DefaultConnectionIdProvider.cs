using Grpc.Core;
using Grpc.AspNetCore.Server;
using Adaptor.Service.Abstractions;

namespace Adaptor.Service.Middleware;

/// <summary>
/// Default implementation of <see cref="IConnectionIdProvider"/> that uses
/// <c>context.GetHttpContext().Connection.Id</c> from the ASP.NET Core hosting layer.
/// </summary>
internal sealed class DefaultConnectionIdProvider : IConnectionIdProvider
{
    public string GetConnectionId(ServerCallContext context)
        => context.GetHttpContext().Connection.Id;
}
