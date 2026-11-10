FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled-extra

WORKDIR /app
COPY ./Migrator/publish .

USER app

ENTRYPOINT ["dotnet", "BackOffice.Migrator.dll"]
