# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Weft.Core/ src/Weft.Core/
COPY src/Weft.Server/ src/Weft.Server/

RUN dotnet publish src/Weft.Server/Weft.Server.csproj -c Release -o /app

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app ./

# The data directory holds the object store and the metadata database. It is the
# only state the server has: back this up and you have backed up the server.
VOLUME /data
ENV Weft__DataDir=/data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The .NET runtime images already ship a non-root user at UID 1654 and export it
# as APP_UID. Creating one is not merely redundant: adduser fails on the
# collision, and the whole image build with it.
#
# The volume is chowned so the server can actually write to it. A container that
# starts unprivileged and then cannot write is the more confusing failure,
# because it looks healthy until the first push.
# Defaulted rather than assumed: the value comes from the base image, and a
# Dockerfile that breaks silently when a future tag stops exporting it would
# leave the server running as root with nothing to say so.
RUN mkdir -p /data && chown -R "${APP_UID:-1654}" /data /app
USER ${APP_UID:-1654}

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
  CMD wget -qO- http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Weft.Server.dll"]
