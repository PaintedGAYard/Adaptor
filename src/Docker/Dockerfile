# =============================================================================
#  Adaptor — Distributed Transaction Coordinator for heterogeneous storage
#  Multi-stage Docker build file
# =============================================================================
#
# !! IMPORTANT !!
# Build context MUST be the repository root.  Do NOT cd into this directory
# and run "docker build ." — COPY source paths are relative to the build
# context, not the Dockerfile location.
#
#   Correct:
#     docker build -f src/Docker/Dockerfile .
#     docker compose -f deployment/docker/docker-compose.yml build
#
#   Wrong (will cause "not found" errors):
#     cd src/Docker && docker build .

# -----------------------------------------------------------------------------
# Stage 1: Restore & build
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first to leverage Docker layer caching
COPY Adaptor.slnx ./
COPY src/Coordinator/Adaptor.Coordinator.csproj src/Coordinator/
COPY src/Driver/Postgre/Adaptor.Driver.Postgre.csproj src/Driver/Postgre/
COPY src/Service/Adaptor.Service.csproj src/Service/

# Restore NuGet packages
RUN dotnet restore src/Service/Adaptor.Service.csproj

# Copy the src/ directory tree (Coordinator, Driver/Postgre, Service).
# test/ and Design/ live outside src/, so they are not included.
COPY src/ src/

# Build and publish the Service project in Release mode
RUN dotnet publish src/Service/Adaptor.Service.csproj \
    -c Release \
    -o /publish \
    --no-restore

# -----------------------------------------------------------------------------
# Stage 2: Runtime image
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Expose ports:
#   80  — gRPC (HTTP/2)
#   443 — gRPC (HTTP/2 with TLS, optional)
EXPOSE 80
EXPOSE 443

# Copy the published binaries from the build stage
COPY --from=build /publish .

# Default environment (can be overridden at container run time)
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:80

ENTRYPOINT ["dotnet", "Adaptor.Service.dll"]
