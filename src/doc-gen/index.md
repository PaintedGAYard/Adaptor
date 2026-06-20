# Adaptor

A .NET 10 middleware service that exposes unified distributed transaction semantics via gRPC for runtimes lacking DTC infrastructure (Python, JavaScript, Go, etc.).

## Overview

Adaptor bridges the gap between modern application runtimes and enterprise-grade transaction coordination. It manages transactions across three heterogeneous storage types — **SQL databases**, **Vector databases**, and **BLOB storage** — through a modular driver architecture built on `System.Transactions`.

### Key Features

- **Unified Transaction Semantics** — `Begin` / `Commit` / `Rollback` across SQL, Vector, and BLOB stores
- **Modular Driver Architecture** — Composition-based interfaces for orthogonal capabilities
- **Semantic Kernel Integration** — Built on `Microsoft.SemanticKernel` Plugin/Connector model
- **gRPC Protocol** — Language-agnostic access for any runtime
- **All-or-Nothing Guarantee** — Two-phase commit with retry and compensation

## Projects

| Project | Description |
|---------|-------------|
| `Adaptor.Coordinator` | Core transaction coordination engine and abstraction interfaces |
| `Adaptor.Driver.Postgre` | PostgreSQL driver suite (SQL + Vector + BLOB) |
| `Adaptor.Service` | gRPC service host with BlobStream protocol support |

## Quick Links

- [Architecture Overview](articles/architecture.md)
- [API Reference](api/index.md)
- [Design Documents](../../Design/DETAILED-DESIGN.md)
