FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/LedgerPay.Domain/LedgerPay.Domain.csproj src/LedgerPay.Domain/
COPY src/LedgerPay.Api/LedgerPay.Api.csproj src/LedgerPay.Api/
RUN dotnet restore src/LedgerPay.Api/LedgerPay.Api.csproj
COPY src/ src/
RUN dotnet publish src/LedgerPay.Api/LedgerPay.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 Urls=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "LedgerPay.Api.dll"]
