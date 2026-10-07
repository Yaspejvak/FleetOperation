# syntax=docker/dockerfile:1
#
# Fserp.FleetOperations.Api — the one host process, serving REST on 8080 and gRPC on 8081.
#
# Nothing in this file contains a credential, a realm URL or a connection string, and nothing bakes one
# into the image. Every setting the host needs arrives as an environment variable at run time; see
# README.md, "Configuration".
#
# Verified on 2026-10-06: the image built and ran in the three-service Compose stack; the app reached
# /health/ready with PostgreSQL and Redis healthy. The optional private-feed BuildKit secret path has
# not been exercised. See OUTCOMES.md, section 4b.

# ---------------------------------------------------------------------------------------------------
# Build: the SDK compiles and publishes. Nothing from this stage reaches the final image except the
# published output, so the SDK, the NuGet cache and any feed configuration stay behind.
# ---------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Directory.Build.props carries the target framework and the pinned MP Core version, so it is copied
# before anything that reads it. The test projects are not copied: this image publishes the host.
COPY Directory.Build.props ./
COPY src/ ./src/

# The MPCore.* packages come from a NuGet feed. This repository holds no NuGet.config and no feed
# credential, deliberately. When the feed is not reachable anonymously, pass yours as a BuildKit secret:
#
#   docker build --secret id=nuget_config,src="$HOME/.nuget/NuGet/NuGet.Config" -t fserp-fleetoperations .
#
# The secret is mounted only for the duration of this one RUN and is never written into a layer.
RUN --mount=type=secret,id=nuget_config,target=/root/.nuget/NuGet/NuGet.Config \
    dotnet restore src/Fserp.FleetOperations.Api/Fserp.FleetOperations.Api.csproj

# UseAppHost=false: the entry point below is "dotnet <dll>", so no native launcher is needed.
RUN dotnet publish src/Fserp.FleetOperations.Api/Fserp.FleetOperations.Api.csproj \
      --configuration "$BUILD_CONFIGURATION" \
      --no-restore \
      --output /app/publish \
      -p:UseAppHost=false

# ---------------------------------------------------------------------------------------------------
# Runtime: the ASP.NET image, nothing else.
# ---------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ASP.NET Core resolves appsettings.json from the content root, which defaults to the working directory.
# Started from "/", the host finds no Kestrel section, cannot serve "both" from Kestrel's single implicit
# endpoint, and TransportEndpointGuard refuses to start. That refusal is correct, and WORKDIR is what
# keeps it from happening (docs/getting-started.md, "Running in a container").
WORKDIR /app
COPY --from=build /app/publish ./

# The official images set ASPNETCORE_HTTP_PORTS=8080, which Kestrel reports as overriding the endpoints
# declared in appsettings.json. Clearing it leaves the two declared listeners and a quiet log.
ENV ASPNETCORE_HTTP_PORTS=
# No diagnostics IPC socket in a shipped container; nothing in the image attaches to it.
ENV DOTNET_EnableDiagnostics=0

# Two cleartext ports, one transport each, exactly as appsettings.json declares them. A single-port
# "both" deployment is supported only over TLS, where ALPN negotiates; TLS is the edge gateway's job and
# nothing here terminates it.
EXPOSE 8080
EXPOSE 8081

# Non-root. $APP_UID is the unprivileged user the official ASP.NET images create. Nothing above writes
# into /app at run time, so no path needs to be chowned, and the process cannot modify its own binaries.
USER $APP_UID

ENTRYPOINT ["dotnet", "Fserp.FleetOperations.Api.dll"]
