# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy csproj and restore as distinct layers
COPY MessangerWeb/MessangerWeb.csproj MessangerWeb/
RUN dotnet restore MessangerWeb/MessangerWeb.csproj

# Copy everything
COPY . .

# Publish
WORKDIR /source/MessangerWeb
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copy output
COPY --from=build /app .

# Bind to Render port
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

ENTRYPOINT ["dotnet", "MessangerWeb.dll"]
