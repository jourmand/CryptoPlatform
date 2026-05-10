FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["CryptoPlatform.API/CryptoPlatform.API.csproj", "CryptoPlatform.API/"]
COPY ["CryptoPlatform.Application/CryptoPlatform.Application.csproj", "CryptoPlatform.Application/"]
COPY ["CryptoPlatform.Domain/CryptoPlatform.Domain.csproj", "CryptoPlatform.Domain/"]
COPY ["CryptoPlatform.Infrastructure/CryptoPlatform.Infrastructure.csproj", "CryptoPlatform.Infrastructure/"]
RUN dotnet restore "CryptoPlatform.API/CryptoPlatform.API.csproj"
COPY . .
RUN dotnet publish "CryptoPlatform.API/CryptoPlatform.API.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CryptoPlatform.API.dll"]
