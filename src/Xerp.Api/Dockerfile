# Build context is the repository root: docker build -f src/Xerp.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Xerp.Domain/Xerp.Domain.csproj src/Xerp.Domain/
COPY src/Xerp.Application/Xerp.Application.csproj src/Xerp.Application/
COPY src/Xerp.Infrastructure/Xerp.Infrastructure.csproj src/Xerp.Infrastructure/
COPY src/Xerp.Api/Xerp.Api.csproj src/Xerp.Api/
RUN dotnet restore src/Xerp.Api/Xerp.Api.csproj
COPY src/ src/
RUN dotnet publish src/Xerp.Api/Xerp.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8000
EXPOSE 8000
USER $APP_UID
ENTRYPOINT ["dotnet", "Xerp.Api.dll"]
