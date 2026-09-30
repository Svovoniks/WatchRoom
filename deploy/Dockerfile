FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props NuGet.Config ./
COPY src/Watchroom.Core/ src/Watchroom.Core/
COPY src/Watchroom.Server/ src/Watchroom.Server/
RUN dotnet publish src/Watchroom.Server -c Release -o /app --configfile NuGet.Config
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Watchroom.Server.dll"]
