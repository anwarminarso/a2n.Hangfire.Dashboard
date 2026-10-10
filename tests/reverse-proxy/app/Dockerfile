# syntax=docker/dockerfile:1
# Build context: repository root (see ../compose.yaml). Builds the dashboard from local source.
# SDK 10 is used to build because src/ multi-targets net8.0;net9.0;net10.0.
ARG RUNTIME_TAG=8.0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGET_FRAMEWORK=net8.0
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
COPY tests/reverse-proxy/app/ tests/reverse-proxy/app/
RUN --mount=type=cache,id=reverse-proxy-test-nuget,target=/root/.nuget/packages \
    dotnet publish tests/reverse-proxy/app/ProxyEnvApp.csproj \
        -c Release -p:AppTargetFramework=${TARGET_FRAMEWORK} -o /out

FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_TAG}
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Trust the stack's self-signed CA so server-side HTTPS calls to the public URL get past TLS
# (the same position as a real certificate in production), then start the app.
ENTRYPOINT ["/bin/sh", "-c", "if [ -f /certs/ca.crt ]; then cp /certs/ca.crt /usr/local/share/ca-certificates/reverse-proxy-test-ca.crt && update-ca-certificates >/dev/null 2>&1; fi; exec dotnet ProxyEnvApp.dll"]
