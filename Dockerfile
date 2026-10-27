FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/DuoTrack.Relay/DuoTrack.Relay.csproj src/DuoTrack.Relay/
COPY src/DuoTrack.Protocol/DuoTrack.Protocol.csproj src/DuoTrack.Protocol/
RUN dotnet restore src/DuoTrack.Relay/DuoTrack.Relay.csproj
COPY src/DuoTrack.Relay/ src/DuoTrack.Relay/
COPY src/DuoTrack.Protocol/ src/DuoTrack.Protocol/
RUN dotnet publish src/DuoTrack.Relay/DuoTrack.Relay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DuoTrack.Relay.dll"]
