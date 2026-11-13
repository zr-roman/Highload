# The build context is the repository root, for Directory.Build.props, Directory.Packages.props and the projects
# Platform and Contracts:
#   docker build -t payments-api:dev -f src/Payments.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore from the project files alone: the layer with the packages is reused until a csproj
# or a package version changes, so a change in the code does not download the packages again.
# Restored for linux-x64, the runtime that publish targets below: publish --no-restore needs the assets for that RID.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Platform/Platform.csproj src/Platform/
COPY src/Payments.Api/Payments.Api.csproj src/Payments.Api/
# The NuGet packages folder is a BuildKit cache shared by both .NET services (id=nuget): a package is downloaded once,
# not once per service, and survives a rebuild of this layer after a csproj changes. The cache is not in the image.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Payments.Api/Payments.Api.csproj -r linux-x64

COPY src/Contracts/ src/Contracts/
COPY src/Platform/ src/Platform/
COPY src/Payments.Api/ src/Payments.Api/
# Database/schema.sql goes into the output with the dll (CopyToOutputDirectory in the csproj).
# -r linux-x64: without a RID, librdkafka.redist (Confluent.Kafka, through Platform) copies its native libraries for
# every platform into runtimes/, about 110 MB of output instead of about 40.
# The chiseled image has no libsasl2, which librdkafka.so needs, so Confluent.Kafka loads centos8-librdkafka.so
# instead; with a RID, publish puts the linux-x64 builds next to the dll, not into runtimes/.
# --self-contained false: the .NET runtime comes from the aspnet base image, not with the app.
# The same cache: with --no-restore, publish takes the packages from the folder restore filled.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Payments.Api/Payments.Api.csproj -c Release --no-restore \
    -r linux-x64 --self-contained false -o /app -p:UseAppHost=false

# Chiseled: no shell and no package manager, runs as the non-root user app, listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
# The content root is the working directory, so Database/schema.sql is found as with dotnet run.
ENTRYPOINT ["dotnet", "Payments.Api.dll"]
