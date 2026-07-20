# CamusDB Web Console image (Blazor Interactive Server).
#
# Build context MUST be the repository root, e.g.:
#   docker build -f docker/Dockerfile -t camusdb/camusdb-webconsole .
# or use docker/publish.sh which wires this up for Docker Hub (camusdb/camusdb-webconsole).

# Pin the build stage to the NATIVE build platform. Running the .NET SDK under QEMU
# emulation (e.g. building linux/amd64 on an arm64 host) throws MSB4184
# GetTargetPlatformIdentifier('') during MSBuild evaluation. A framework-dependent
# publish is portable IL, so one native build serves all target architectures.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env

RUN rm -fr /src && mkdir -p /src
COPY src/CamusDB.WebConsole /src/CamusDB.WebConsole/

# Drop any host-built obj/bin that slipped into the context. A host-restored
# obj/project.assets.json poisons the in-container restore for the target arch
# (MSB4184: GetTargetPlatformIdentifier('') cannot be evaluated). .dockerignore
# alone is not reliable across buildkit versions, so scrub explicitly.
RUN find /src \( -type d -name obj -o -type d -name bin \) -prune -exec rm -rf {} +

RUN cd /src/CamusDB.WebConsole/ \
  && dotnet publish --no-self-contained --configuration Release -p:RunAnalyzers=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

EXPOSE 8080

COPY --from=build-env /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "CamusDB.WebConsole.dll"]
