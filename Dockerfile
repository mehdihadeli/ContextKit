FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/GroundKit.Registry.Server/GroundKit.Registry.Server.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
	&& apt-get install --no-install-recommends --yes curl \
	&& rm -rf /var/lib/apt/lists/* \
	&& addgroup --system app \
	&& adduser --system --ingroup app app
COPY --from=build /app/publish .
RUN mkdir -p /data && chown -R app:app /app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GroundKit.Registry.Server.dll"]