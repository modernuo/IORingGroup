# syntax=docker/dockerfile:1
# Builds the TestServer and TestClient for running the echo integration test
# on real Linux (io_uring and epoll backends). See docker/README.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore/build against the whole solution so project references resolve.
COPY . .
RUN dotnet publish TestServer/TestServer.csproj -c Release -o /app/server \
 && dotnet publish TestClient/TestClient.csproj -c Release -o /app/client

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app /app
