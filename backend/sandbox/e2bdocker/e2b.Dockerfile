FROM mcr.microsoft.com/playwright:v1.62.1-noble

ARG DOTNET_CHANNEL=8.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_XMLDOC_MODE=skip \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    CI=true

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        jq \
        procps \
        unzip \
        zip \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel ${DOTNET_CHANNEL} --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh \
    && rm -rf /var/lib/apt/lists/* /root/.cache

WORKDIR /src
COPY src/MnaiWork.BuildWorker/MnaiWork.BuildWorker.csproj src/MnaiWork.BuildWorker/
COPY src/MnaiWork.E2BRunner/MnaiWork.E2BRunner.csproj src/MnaiWork.E2BRunner/
RUN dotnet restore src/MnaiWork.E2BRunner/MnaiWork.E2BRunner.csproj
COPY src/MnaiWork.BuildWorker/ src/MnaiWork.BuildWorker/
COPY src/MnaiWork.E2BRunner/ src/MnaiWork.E2BRunner/
RUN dotnet publish src/MnaiWork.E2BRunner/MnaiWork.E2BRunner.csproj \
    -c Release \
    --no-restore \
    -o /opt/mnaiwork-runner

WORKDIR /
RUN rm -rf /src

RUN id -u user >/dev/null 2>&1 || useradd --create-home --shell /bin/bash user \
    && mkdir -p /home/user/workspace /home/user/.cache \
    && chown -R user:user /home/user /ms-playwright

COPY sandbox/e2bdocker/verify-toolchain.sh /usr/local/bin/verify-toolchain
RUN chmod +x /usr/local/bin/verify-toolchain \
    && verify-toolchain

USER user
WORKDIR /home/user/workspace

CMD ["dotnet", "/opt/mnaiwork-runner/MnaiWork.E2BRunner.dll", "--urls", "http://0.0.0.0:3000"]
