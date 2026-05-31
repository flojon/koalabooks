FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY src/KoalaBooks.Web/KoalaBooks.Web.csproj src/KoalaBooks.Web/
COPY src/KoalaBooks.Domain/KoalaBooks.Domain.csproj src/KoalaBooks.Domain/
COPY src/KoalaBooks.Application/KoalaBooks.Application.csproj src/KoalaBooks.Application/
COPY src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj src/KoalaBooks.Infrastructure/
COPY src/KoalaBooks.Components/KoalaBooks.Components.csproj src/KoalaBooks.Components/
COPY src/KoalaBooks.ServiceDefaults/KoalaBooks.ServiceDefaults.csproj src/KoalaBooks.ServiceDefaults/
RUN dotnet restore src/KoalaBooks.Web -a $TARGETARCH

COPY . .

# Cross-compile for the target arch to get the correct native binaries.
RUN dotnet publish src/KoalaBooks.Web -c Release -o /app --no-restore -a $TARGETARCH

# Publish again natively on the builder (no QEMU) to produce the correct static web
# asset manifest. Cross-compilation skips blazor.web.js in the manifest because the
# arm64 runtime pack doesn't expose framework static web assets — only the host SDK's
# shared framework does. The managed DLLs are MSIL so they're identical either way.
RUN dotnet publish src/KoalaBooks.Web -c Release -o /app-native --no-restore && \
    cp /app-native/*.staticwebassets.runtime.json /app/ && \
    cp -rT /app-native/_framework /app/_framework

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "KoalaBooks.Web.dll"]
