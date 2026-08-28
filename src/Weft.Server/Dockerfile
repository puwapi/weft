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

# Non-root, and the volume is chowned so the server can actually write to it.
# A container that starts as root and then cannot write is the more confusing
# failure, because it looks healthy until the first push.
RUN adduser -D -u 1654 weft && mkdir -p /data && chown -R weft:weft /data /app
USER weft

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
  CMD wget -qO- http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Weft.Server.dll"]
