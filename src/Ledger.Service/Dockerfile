# The build context is the repository root, for Directory.Build.props, Directory.Packages.props and the projects
# Platform and Contracts:
#   docker build -t ledger-service:dev -f src/Ledger.Service/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /repo

# Restore from the project files alone: the layer with the packages is reused until a csproj
# or a package version changes, so a change in the code does not download the packages again.
# Restored for linux-x64 and ReadyToRun, as publish below needs it: the restore that publish runs again then finds
# everything in place.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Platform/Platform.csproj src/Platform/
COPY src/Ledger.Service/Ledger.Service.csproj src/Ledger.Service/
# The NuGet packages folder is a BuildKit cache shared by both .NET services (id=nuget): a package is downloaded once,
# not once per service, and survives a rebuild of this layer after a csproj changes. The cache is not in the image.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Ledger.Service/Ledger.Service.csproj -r linux-x64 -p:PublishReadyToRun=true

COPY src/Contracts/ src/Contracts/
COPY src/Platform/ src/Platform/
COPY src/Ledger.Service/ src/Ledger.Service/
# Database/schema.sql goes into the output with the dll (CopyToOutputDirectory in the csproj).
# -r linux-x64: without a RID, librdkafka.redist (Confluent.Kafka, through Platform) copies its native libraries for
# every platform into runtimes/, about 110 MB of output instead of about 40.
# The chiseled image has no libsasl2, which librdkafka.so needs, so Confluent.Kafka loads centos8-librdkafka.so
# instead; with a RID, publish puts the linux-x64 builds next to the dll, not into runtimes/.
# --self-contained false: the .NET runtime comes from the aspnet base image, not with the app.
# PublishReadyToRun: the assemblies carry machine code compiled at build time, so a new pod serves its first requests
# without waiting for the JIT; tiered compilation still recompiles the hot methods later. About 9 MB more output.
# Restore needs it too: it brings the crossgen compiler for the runtime identifier.
# The same cache. No --no-restore: the restore layer above and the cache live apart, and the cache can lose packages
# while the layer stays cached (a pruned or reset build cache); publish then fails with NETSDK1064, package not found.
# Restoring again costs a couple of seconds when the packages are in place and downloads the ones that are not.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Ledger.Service/Ledger.Service.csproj -c Release \
    -r linux-x64 --self-contained false -o /app -p:UseAppHost=false -p:PublishReadyToRun=true

# Chiseled: no shell and no package manager, runs as the non-root user app, listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
# The content root is the working directory, so Database/schema.sql is found as with dotnet run.
ENTRYPOINT ["dotnet", "Ledger.Service.dll"]
