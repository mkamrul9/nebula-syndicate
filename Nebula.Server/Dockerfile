# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY ["Nebula.Server/Nebula.Server.csproj", "Nebula.Server/"]
COPY ["Nebula.Shared/Nebula.Shared.csproj", "Nebula.Shared/"]
COPY ["Nebula.Domain/Nebula.Domain.csproj", "Nebula.Domain/"]
RUN dotnet restore "Nebula.Server/Nebula.Server.csproj"

# Copy the rest of the code and publish
COPY . .
WORKDIR "/src/Nebula.Server"
RUN dotnet publish "Nebula.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Run (Slim Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose standard HTTP/HTTPS ports
EXPOSE 8080
EXPOSE 8081

ENTRYPOINT ["dotnet", "Nebula.Server.dll"]
