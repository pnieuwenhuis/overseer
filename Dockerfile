# The Overseer image: `overseer serve` with the Azure CLI, which the deployed Overseer manages Sandboxes through, as its
# Managed Identity. The release workflow builds it with the release's version:
#
#   docker build --build-arg VERSION=1.2.3 --tag pnieuwenhuis/overseer:1.2.3 .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
WORKDIR /source
COPY Directory.Build.props ./
COPY src/Overseer/ src/Overseer/
RUN dotnet publish src/Overseer --configuration Release -p:Version="$VERSION" --output /app

# Ubuntu 24.04, which Microsoft's Azure CLI packages are built for.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG AZURE_CLI_VERSION=2.80.0
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor --output /etc/apt/keyrings/microsoft.gpg \
    && printf 'Types: deb\nURIs: https://packages.microsoft.com/repos/azure-cli/\nSuites: noble\nComponents: main\nSigned-By: /etc/apt/keyrings/microsoft.gpg\n' \
        > /etc/apt/sources.list.d/azure-cli.sources \
    && apt-get update \
    && apt-get install --yes --no-install-recommends "azure-cli=$AZURE_CLI_VERSION-1~noble" \
    && apt-get purge --yes --auto-remove curl gnupg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./
# Configuration only reads OVERSEER_ variables, so the base image's ASPNETCORE_HTTP_PORTS has no effect.
ENV OVERSEER_URLS=http://+:8080
EXPOSE 8080
# Links the GHCR package to this repository.
LABEL org.opencontainers.image.source=https://github.com/pnieuwenhuis/overseer
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/overseer.dll"]
CMD ["serve"]
