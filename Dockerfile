# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy only csproj first (better caching)
COPY MessangerWeb.csproj .

# Restore dependencies
RUN dotnet restore MessangerWeb.csproj

# Copy everything
COPY . .

# Publish the app
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copy published output
COPY --from=build /app .

# Render uses dynamic PORT variable
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# Start the app
ENTRYPOINT ["dotnet", "MessangerWeb.dll"]
