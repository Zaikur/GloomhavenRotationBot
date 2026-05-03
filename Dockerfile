# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /out

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Bundle local transcription runtime dependencies so transcript features work out of the box.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends python3 python3-pip python3-venv ffmpeg \
	&& python3 -m venv /opt/whisperx-venv \
	&& /opt/whisperx-venv/bin/pip install --no-cache-dir whisperx \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*

ENV PATH="/opt/whisperx-venv/bin:$PATH"

COPY --from=build /out ./

ENV ASPNETCORE_URLS=http://0.0.0.0:5055
EXPOSE 5055

ENTRYPOINT ["dotnet", "JankDiscordBot.dll"]
