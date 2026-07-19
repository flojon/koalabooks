FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/KoalaBooks.Web/KoalaBooks.Web.csproj src/KoalaBooks.Web/
COPY src/KoalaBooks.Domain/KoalaBooks.Domain.csproj src/KoalaBooks.Domain/
COPY src/KoalaBooks.Application/KoalaBooks.Application.csproj src/KoalaBooks.Application/
COPY src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj src/KoalaBooks.Infrastructure/
COPY src/KoalaBooks.Client/KoalaBooks.Client.csproj src/KoalaBooks.Client/
COPY src/KoalaBooks.Components/KoalaBooks.Components.csproj src/KoalaBooks.Components/
COPY src/KoalaBooks.ServiceDefaults/KoalaBooks.ServiceDefaults.csproj src/KoalaBooks.ServiceDefaults/
RUN dotnet restore src/KoalaBooks.Web -a $TARGETARCH

COPY . .
RUN dotnet publish src/KoalaBooks.Web -c Release -o /app --no-restore -a $TARGETARCH

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "KoalaBooks.Web.dll"]
