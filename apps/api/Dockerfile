# syntax=docker/dockerfile:1
# CouplesCash API — multi-stage, multi-arch (linux/amd64 + linux/arm64).
# Build context is apps/api:   docker build -t couplescash-api apps/api
# No RID is passed: the output is framework-dependent IL, so one build runs on any architecture
# and the build stage can run natively on the build host while the runtime stage targets the Pi.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src

# Restore first so the package layer is cached across source changes.
COPY Directory.Build.props ./
COPY CouplesCash.Api/CouplesCash.Api.csproj CouplesCash.Api/
RUN dotnet restore CouplesCash.Api/CouplesCash.Api.csproj

COPY CouplesCash.Api/ CouplesCash.Api/
RUN dotnet publish CouplesCash.Api/CouplesCash.Api.csproj \
      -c Release -o /app/publish --no-restore \
      -p:UseAppHost=false -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1 \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish ./

# Non-root; `app` (uid 1654) ships with the official images. Works with a read-only root FS.
USER app
EXPOSE 8080

# No HEALTHCHECK here: the aspnet image has neither curl nor wget. compose.yml performs the
# healthcheck against GET /health instead.
ENTRYPOINT ["dotnet", "CouplesCash.Api.dll"]
