# Das Image packt das fertige, im Release-Workflow gebaute Paket aus (dasselbe Binary wie bei install.sh); es wird hier nichts
# neu gebaut. Der Build-Kontext enthält je Architektur ein Paket: package-amd64.tar.gz und package-arm64.tar.gz.
#
#   docker buildx build -f deploy/docker/Dockerfile <Ordner mit den Paketen>
#
# Alle Schritte mit RUN laufen auf der Build-Plattform; für arm64 ist deshalb kein Emulator nötig.

FROM --platform=$BUILDPLATFORM debian:stable-slim AS prepare
ARG TARGETARCH
COPY package-${TARGETARCH}.tar.gz /package.tar.gz
RUN mkdir -p /out/app /out/data \
 && tar -xzf /package.tar.gz -C /out/app \
 && chmod 755 /out/app/Wunschkino

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
ARG VERSION=dev
ARG SOURCE_URL=https://github.com/dahamalp/Wunschkino
LABEL org.opencontainers.image.title="Wunschkino" \
      org.opencontainers.image.description="Wunschportal für Jellyfin" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.version="${VERSION}"

# Das Programm gehört root und ist für den Nutzer „app“ nur lesbar; schreiben darf er nur in /data.
COPY --from=prepare /out/app /app
COPY --from=prepare --chown=app:app /out/data /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    Wunschkino__Port=8080 \
    Wunschkino__DataDirectory=/data \
    Wunschkino__SourceUrl=${SOURCE_URL}

VOLUME /data
USER app
WORKDIR /app
EXPOSE 8080

# Das Image enthält kein curl; bash kann die Anfrage selbst stellen. „Host: localhost“ ist auch mit gesetzter Domain erlaubt.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["/app/Wunschkino"]
