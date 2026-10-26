FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b

RUN useradd --uid 10001 --user-group --home-dir /work/home --no-create-home --shell /bin/bash yabt
COPY init-workspace.sh /usr/local/bin/yabt-init-workspace
RUN chmod 0755 /usr/local/bin/yabt-init-workspace

ENV TMPDIR=/work/tmp \
    XDG_STATE_HOME=/work/state \
    NUGET_PACKAGES=/work/nuget \
    DOTNET_CLI_HOME=/work/cli-home \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

USER yabt
WORKDIR /work
CMD ["sleep", "infinity"]
