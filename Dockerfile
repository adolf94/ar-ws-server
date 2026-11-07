




FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY docker/NuGet.docker.config ./NuGet.config
COPY nuget-feed/ /nuget-feed/


COPY src/Realtime/Realtime.csproj ./src/Realtime/
RUN dotnet restore src/Realtime/Realtime.csproj

COPY src/ ./src/
RUN dotnet publish src/Realtime/Realtime.csproj -c Release -o /app --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# curl for the compose healthcheck probe (the ubuntu base ships no HTTP client)
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*



ENV ASPNETCORE_URLS=http://0.0.0.0:5000 \
    AUTH_AUTHORITY=https://auth.adolfrey.com/api/

EXPOSE 5000
ENTRYPOINT ["dotnet", "Realtime.dll"]
