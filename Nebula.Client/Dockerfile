# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Nebula.Client/Nebula.Client.csproj", "Nebula.Client/"]
COPY ["Nebula.Shared/Nebula.Shared.csproj", "Nebula.Shared/"]
RUN dotnet restore "Nebula.Client/Nebula.Client.csproj"
COPY . .
WORKDIR "/src/Nebula.Client"
RUN dotnet publish "Nebula.Client.csproj" -c Release -o /app/publish

# Stage 2: Serve with Nginx
FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html

# Remove default Nginx static files
RUN rm -rf ./*

# Copy the published Blazor WASM static files
COPY --from=build /app/publish/wwwroot .

# Add a custom nginx.conf to support SPA routing
COPY Nebula.Client/nginx.conf /etc/nginx/nginx.conf

EXPOSE 80
