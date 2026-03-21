FROM mcr.microsoft.com/dotnet/runtime:10.0

# Install MS core fonts including Arial
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    fontconfig \
    cabextract \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Install MS core fonts
RUN echo "ttf-mscorefonts-installer msttcorefonts/accepted-mscorefonts-eula select true" | debconf-set-selections && \
    apt-get update && \
    apt-get install -y ttf-mscorefonts-installer ffmpeg imagemagick && \
    fc-cache -fv && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
