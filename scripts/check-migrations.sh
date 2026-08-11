#!/usr/bin/env sh
set -eu

for service in Identity FamilyGraph Room Message Notification; do
  project="src/Services/$service/$service.API.csproj"
  echo "Checking migration model: $service"
  dotnet ef migrations has-pending-model-changes \
    --project "$project" \
    --configuration Release \
    --no-build
done

echo "Migration models OK"
