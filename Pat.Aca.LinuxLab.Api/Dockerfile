# Multi-stage build: SDK image to restore/build/publish, slim ASP.NET
# runtime image to actually run — keeps the final image small. Build
# context is this directory (Pat.Aca.LinuxLab.Api/), per
# .github/workflows/deploy-api.yml.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Target the .csproj explicitly, not just "dotnet restore/publish" bare —
# this directory also has a .slnx (added by Visual Studio), and letting
# dotnet resolve that ambiguously works today only because there's a
# single project in it; explicit is correct regardless of that.
RUN dotnet restore Pat.Aca.LinuxLab.Api.csproj
RUN dotnet publish Pat.Aca.LinuxLab.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# .NET 8+ container images default ASPNETCORE_HTTP_PORTS to 8080 anyway —
# set explicitly so this doesn't silently depend on that default.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Pat.Aca.LinuxLab.Api.dll"]
