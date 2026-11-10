FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Restore the API alone for layer caching; the importer and tests are not part of the image
COPY src/Glazion.Api/Glazion.Api.csproj src/Glazion.Api/
RUN dotnet restore src/Glazion.Api/Glazion.Api.csproj

COPY src/Glazion.Api/ src/Glazion.Api/
RUN dotnet publish src/Glazion.Api/Glazion.Api.csproj -c Release -o /out --no-restore /p:UseAppHost=false

# Debian, not Alpine: SkiaSharp.NativeAssets.Linux.NoDependencies ships glibc binaries (WebP derivatives, quality checks)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as the image's built-in non-root user
USER app

COPY --from=build --chown=app:app /out .

EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Railway injects PORT; fall back to 8080 for docker compose and local runs
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet Glazion.Api.dll"]
