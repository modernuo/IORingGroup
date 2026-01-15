# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2025, ModernUO
#
# Multi-stage Dockerfile for IORingGroup Linux benchmarks
# Supports both io_uring and epoll backends

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy LICENSE first (referenced by IORingGroup.csproj as ../LICENSE)
COPY LICENSE .

# Copy project files first for better layer caching
COPY IORingGroup/IORingGroup.csproj IORingGroup/
COPY TestServer/TestServer.csproj TestServer/
COPY TestClient/TestClient.csproj TestClient/
COPY IORingGroup.Tests/IORingGroup.Tests.csproj IORingGroup.Tests/

# Copy source code
COPY IORingGroup/ IORingGroup/
COPY TestServer/ TestServer/
COPY TestClient/ TestClient/
COPY IORingGroup.Tests/ IORingGroup.Tests/

# Restore dependencies
RUN dotnet restore IORingGroup/IORingGroup.csproj
RUN dotnet restore IORingGroup.Tests/IORingGroup.Tests.csproj
RUN dotnet restore TestServer/TestServer.csproj
RUN dotnet restore TestClient/TestClient.csproj

# Build all projects (with runtime identifier to match publish)
RUN dotnet build TestServer/TestServer.csproj -c Release -r linux-x64 --no-restore
RUN dotnet build TestClient/TestClient.csproj -c Release -r linux-x64 --no-restore

# Publish server
RUN dotnet publish TestServer/TestServer.csproj -c Release -r linux-x64 --no-build -o /app/server

# Publish client
RUN dotnet publish TestClient/TestClient.csproj -c Release -r linux-x64 --no-build -o /app/client

# Server runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS server
WORKDIR /app
COPY --from=build /app/server .

# Default to io_uring backend with benchmark mode
ENV BACKEND="--iouring"
ENV BENCHMARK_ARGS="-b -q"
ENV DURATION="60"

EXPOSE 5000

# Entry point script that handles arguments
ENTRYPOINT ["sh", "-c", "./TestServer $BACKEND $BENCHMARK_ARGS -d $DURATION"]

# Client runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS client
WORKDIR /app
COPY --from=build /app/client .

ENV SERVER_HOST="server"
ENV SERVER_PORT="5000"
ENV CONNECTIONS="100"
ENV MESSAGES="10000"
ENV MAX_CONCURRENT="1000"

ENTRYPOINT ["sh", "-c", "./TestClient -h $SERVER_HOST -P $SERVER_PORT -b $MESSAGES -c $CONNECTIONS -C $MAX_CONCURRENT"]

# Test runner image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS test
WORKDIR /src

COPY . .

RUN dotnet restore IORingGroup.Tests/IORingGroup.Tests.csproj

ENTRYPOINT ["dotnet", "test", "IORingGroup.Tests/IORingGroup.Tests.csproj", "-c", "Release", "-r", "linux-x64", "--logger", "console;verbosity=detailed"]
