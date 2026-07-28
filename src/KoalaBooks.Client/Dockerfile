FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/KoalaBooks.Client/KoalaBooks.Client.csproj src/KoalaBooks.Client/
COPY src/KoalaBooks.Domain/KoalaBooks.Domain.csproj src/KoalaBooks.Domain/
COPY src/KoalaBooks.Components/KoalaBooks.Components.csproj src/KoalaBooks.Components/
RUN dotnet restore src/KoalaBooks.Client -a $TARGETARCH

COPY . .
RUN dotnet publish src/KoalaBooks.Client -c Release -o /app --no-restore -a $TARGETARCH

FROM caddy:2-alpine
COPY --from=build /app/wwwroot /srv
COPY src/KoalaBooks.Client/Caddyfile /etc/caddy/Caddyfile
EXPOSE 80
