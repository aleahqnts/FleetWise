# One dashboard for the whole team, instead of a clone each.
#
# A per-person clone meant per-person git pulls, a service key copied onto every laptop,
# and audit rows that could not be written at all when that copy was missing. A single
# running instance removes all three: the key lives only on the host, as an env var, and
# everyone points a browser at the same URL.
#
#   docker build -t routesync-web -f RouteSyncWeb/Dockerfile .
#   docker run -p 8080:8080 -e Supabase__Key=sb_secret_... routesync-web
#
# Any host that takes a container works the same way (Render, Fly, Azure Container Apps).
# Set Supabase__Key in the host's environment/secrets panel. Never bake it into the image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RouteSyncWeb/FleetWise.csproj RouteSyncWeb/
RUN dotnet restore RouteSyncWeb/FleetWise.csproj
COPY RouteSyncWeb/ RouteSyncWeb/
RUN dotnet publish RouteSyncWeb/FleetWise.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Containers get the port from the platform; 8080 is the common default for the ones above.
ENV ASPNETCORE_URLS=http://+:8080

# Read appsettings.json once at startup instead of watching it for edits.
#
# Watching is the default, and it opens a file system watcher per settings file. A container
# is allotted very few of those, and running out crashes the process inside the watcher
# rather than raising an error, leaving a segmentation fault with no message. Nothing rereads
# configuration after startup anyway, so a deploy is what picks up a change.
ENV DOTNET_hostBuilder__reloadConfigOnChange=false

EXPOSE 8080
ENTRYPOINT ["dotnet", "FleetWise.dll"]
