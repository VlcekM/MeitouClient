# Meitou play reports, published Native AOT: one static executable on the chiseled runtime-deps image (no .NET runtime, no shell).
# Built from the repository root (it needs Directory.Build.props):
#   docker build -f services/Meitou.Reports/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets ./
COPY services/Meitou.Reports/Meitou.Reports.csproj services/Meitou.Reports/
RUN dotnet restore services/Meitou.Reports/Meitou.Reports.csproj -r linux-x64
COPY services/Meitou.Reports/ services/Meitou.Reports/
RUN dotnet publish services/Meitou.Reports/Meitou.Reports.csproj -c Release -r linux-x64 -o /out --no-restore
# The feedback attachments' folder, owned by the app user: a volume mounted on /data starts as a copy of it.
RUN mkdir -p /data/feedback

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled
WORKDIR /app
COPY --from=build /out/meitou-reports .
COPY --from=build --chown=$APP_UID /data /data
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["./meitou-reports"]
