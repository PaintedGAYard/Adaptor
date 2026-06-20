# Adaptor

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download)

A .NET 10 middleware service that exposes **unified distributed transaction semantics** via gRPC for runtimes that lack DTC infrastructure — such as Python, JavaScript, and Go.

> **Problem:** Modern application runtimes often lack mature distributed transaction coordination (DTC) facilities. When an application needs to operate across SQL databases, vector databases, and BLOB storage simultaneously, managing consistent transactions across these three heterogeneous stores becomes extremely complex.
>
> **Solution:** Adaptor provides a lightweight gRPC service that wraps .NET's `System.Transactions` infrastructure behind a simple `Begin` / `Commit` / `Rollback` protocol, while offering SQL, vector search, and BLOB operations through a single coordinated interface.

## Architecture

```
┌──────────────────────────────────────────────┐
│  Consumer (Python / JS / Go / ...)            │
│  tx = client.Begin()                          │
│  client.SqlQuery(tx, "...")                   │
│  client.Commit(tx)                            │
└─────────────────────┬────────────────────────┘
                      │ gRPC
┌─────────────────────▼────────────────────────┐
│  Adaptor Service  (.NET 10)                   │
│  ┌──────────────────────────────────────┐    │
│  │  TransactionCoordinator               │    │
│  │  (System.Transactions + 2PC engine)   │    │
│  ├──────────────────────────────────────┤    │
│  │  Drivers: SQL | Vector | BLOB        │    │
│  └──────────────────────────────────────┘    │
└──────────────────────────────────────────────┘
```

## Projects

| Project | Path | Description |
|---------|------|-------------|
| **Adaptor.Coordinator** | `src/Coordinator/` | Core transaction coordination engine, abstraction interfaces, and Semantic Kernel plugins |
| **Adaptor.Driver.Postgre** | `src/Driver/Postgre/` | PostgreSQL driver suite — SQL, vector (pgvector), and BLOB (large object) drivers |
| **Adaptor.Service** | `src/Service/` | gRPC service host, BlobStream protocol handler, and middleware |
| **Adaptor.Test.Coordinator** | `test/Adaptor.Test.Coordinator/` | Unit tests for the coordinator |
| **Adaptor.Test.Driver** | `test/Adaptor.Test.Driver/` | Integration tests for PostgreSQL drivers |
| **Adaptor.Test.Service** | `test/Adaptor.Test.Service/` | Unit tests for the gRPC service |
| **Adaptor.Test.BlobStream** | `test/Adaptor.Test.BlobStream/` | Unit tests for the BlobStream protocol |

## Key Concepts

### Driver Architecture

Drivers use a **composition-based capability model**. Instead of a monolithic driver interface, orthogonal capabilities are defined as separate interfaces:

- `IRelationalExecuteCapability` / `IRelationalQueryCapability` — SQL operations
- `IRelationalVectorSearchCapability` — Vector similarity search via pgvector
- `IBlobUploadCapability` / `IBlobDownloadCapability` / `IBlobRandomAccessCapability` — BLOB operations
- `ITransactionalResourceManager` — Distributed transaction enlistment
- `IHealthCheckCapability` — Health monitoring

Drivers implement only the capabilities they support, and the coordinator discovers them at runtime.

### Transaction Lifecycle

1. **Begin** — Consumer requests a new transaction via gRPC; a `CommittableTransaction` is created and associated with a session
2. **Enlist** — Each driver that implements `ITransactionalResourceManager` is enlisted in the transaction
3. **Operate** — Consumer performs SQL queries, vector searches, and BLOB operations within the transaction scope
4. **Commit/Rollback** — Consumer issues commit or rollback; the `TransactionCoordinator` orchestrates the two-phase commit protocol (prepare → vote → commit/rollback)
5. **Cleanup** — Session resources are released

### BlobStream Protocol

The BlobStream protocol provides an efficient, chunked streaming mechanism for BLOB data transfer over the same gRPC connection, supporting:

- Streaming upload and download
- Handle-based resource management
- Random access to BLOB data

## Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A PostgreSQL instance with [pgvector](https://github.com/pgvector/pgvector) extension (for development/testing)

### Build

```bash
dotnet build
```

### Run

```bash
cd src/Service
dotnet run
```

The service will start listening on the configured gRPC endpoints (default: `https://localhost:5001`).

### Configuration

Configuration is handled via `appsettings.json` and environment variables:

```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Database=adaptor;Username=postgres;Password=..."
  },
  "Adaptor": {
    "SessionTimeout": "00:05:00",
    "MaxDriversPerTransaction": 32
  }
}
```

### Docker

A Docker Compose file is available for easy deployment:

```bash
docker compose -f deployment/docker/Adaptor/docker-compose.yml up
```

## Documentation

- [Detailed Design Document (Chinese)](Design/DETAILED-DESIGN.md)
- [BlobStream Design](Design/BLOB-STREAM-DESIGN.md)

### Generating API Docs

The API reference is auto-generated from XML doc comments using DocFX.
Config and source content live in `src/doc-gen/`; output goes to `doc/`.

```bash
./generate-docs.ps1
```

This will generate a static documentation site in `doc/` using DocFX.
Use `-Serve` to preview locally, `-Clean` to rebuild from scratch.

## Development

### Solution Structure

```
Adaptor.slnx
├── src/
│   ├── Coordinator/         # Core transaction coordination
│   ├── Driver/Postgre/      # PostgreSQL drivers
│   ├── Service/             # gRPC service host
│   └── doc-gen/             # DocFX config and doc source content
├── test/
│   ├── Adaptor.Test.Coordinator/
│   ├── Adaptor.Test.BlobStream/
│   ├── Adaptor.Test.Driver/
│   └── Adaptor.Test.Service/
├── doc/                      # Generated documentation output
├── generate-docs.ps1         # DocFX documentation generator
├── Design/                   # Design documents (Chinese)
└── deployment/               # Deployment configurations
```

### Coding Conventions

See [Working Guidelines](Design/Working%20Guidelines.md) for the detailed coding style guide.

Key principles:
- **Composition over Inheritance** — orthogonal capability interfaces over monolithic drivers
- **Operation/Transaction Separation** — SQL, Vector, and BLOB keep independent APIs; transaction semantics are unified
- **All or Nothing** — transactions either fully succeed or fully roll back
- **Async-first** — I/O-bound operations run on the calling thread; CPU-bound work uses the thread pool

### Testing

```bash
dotnet test
```

Integration tests require a PostgreSQL instance (via Testcontainers or local).

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.

## Author

Mingxi "Lucien" Du &mdash; [GitHub](https://github.com/PaintedGAYard)
