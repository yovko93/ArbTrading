#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
configuration="${1:-Release}"
dotnet restore src/Arbitrage.Backend/Arbitrage.Backend.csproj
dotnet build src/Arbitrage.Backend/Arbitrage.Backend.csproj -c "$configuration" --no-restore
for project in tests/Arbitrage.Domain.Tests tests/Arbitrage.Application.Tests tests/Arbitrage.Backend.IntegrationTests; do
  dotnet restore "$project"
  dotnet test "$project" -c "$configuration" --no-restore --logger 'trx;LogFilePrefix=phase01a'
done
