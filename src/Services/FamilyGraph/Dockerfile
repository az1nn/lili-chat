FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "Services/FamilyGraph/FamilyGraph.API.csproj"
RUN dotnet publish "Services/FamilyGraph/FamilyGraph.API.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app
RUN apk add --no-cache curl icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
COPY --from=build /app/publish .
EXPOSE 8080 8081
USER app
ENTRYPOINT ["dotnet", "FamilyGraph.API.dll"]
