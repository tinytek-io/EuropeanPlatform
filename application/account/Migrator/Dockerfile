FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra

WORKDIR /app
COPY ./Migrator/publish .

USER app

ENTRYPOINT ["dotnet", "Account.Migrator.dll"]
