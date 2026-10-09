# syntax=docker/dockerfile:1
#
# Soforr API image (DEPLOY-001): for LOCAL development with docker compose. It is not a production deployment (DEPLOY-002 owns
# that). Stages: build -> publish (the API) -> migrations (an EF Core migration bundle) -> api / migrate runtime images.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files only, so the layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY .config/dotnet-tools.json .config/dotnet-tools.json
COPY src/Soforr.Domain/Soforr.Domain.csproj src/Soforr.Domain/
COPY src/Soforr.Application/Soforr.Application.csproj src/Soforr.Application/
COPY src/Soforr.Infrastructure/Soforr.Infrastructure.csproj src/Soforr.Infrastructure/
COPY src/Soforr.Api/Soforr.Api.csproj src/Soforr.Api/
RUN dotnet restore src/Soforr.Api/Soforr.Api.csproj && dotnet tool restore

COPY src src

FROM build AS publish
RUN dotnet publish src/Soforr.Api/Soforr.Api.csproj -c Release --no-restore -o /app/publish

# The migration bundle applies the committed EF Core migrations to a database (the application never migrates itself). The
# connection string is needed only so the design-time host can start; the bundle takes the real one as --connection.
FROM build AS migrations
ENV ConnectionStrings__Database="Host=design-time;Database=design-time;Username=design-time;Password=design-time"
RUN dotnet ef migrations bundle --project src/Soforr.Infrastructure --startup-project src/Soforr.Api --force -o /app/efbundle \
    && chmod +x /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrate
WORKDIR /app
COPY --from=migrations /app/efbundle ./efbundle
USER $APP_UID
ENTRYPOINT ["/app/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=publish /app/publish .
# Runs as the image's non-root user. HTTPS on 8443 (the session and antiforgery cookies are Secure, so sign-in needs it) and
# plain HTTP on 8080; the certificate and the connection string are supplied by compose, never baked into the image.
ENV ASPNETCORE_URLS="https://+:8443;http://+:8080"
EXPOSE 8443 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Soforr.Api.dll"]
