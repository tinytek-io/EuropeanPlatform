FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra

WORKDIR /app
COPY ./Workers/publish .

USER app

ENTRYPOINT ["dotnet", "Main.Workers.dll"]
