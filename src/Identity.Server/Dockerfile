# Build from the repository root: docker build -f src/Identity.Server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Identity.Server/Identity.Server.csproj src/Identity.Server/
RUN dotnet restore src/Identity.Server/Identity.Server.csproj
COPY src/Identity.Server/ src/Identity.Server/
RUN dotnet publish src/Identity.Server/Identity.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Default place for the SQLite database; mount a volume here.
RUN mkdir /data && chown $APP_UID /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Identity="Data Source=/data/identity.db"
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Identity.Server.dll"]
