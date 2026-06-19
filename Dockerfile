FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["EconomyBot.Worker/EconomyBot.Worker.csproj", "EconomyBot.Worker/"]
RUN dotnet restore "EconomyBot.Worker/EconomyBot.Worker.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/EconomyBot.Worker"
RUN dotnet build "EconomyBot.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "EconomyBot.Worker.csproj" -c Release -o /app/publish

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update && apt-get install -y libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EconomyBot.Worker.dll"]
