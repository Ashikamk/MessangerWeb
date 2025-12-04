# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy everything
COPY . .

# Restore and publish
RUN dotnet restore
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copy build output
COPY --from=build /app .

# Render gives a PORT environment variable, so we listen on it
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# Start the application
ENTRYPOINT ["dotnet", "MessangerWeb.dll"]
