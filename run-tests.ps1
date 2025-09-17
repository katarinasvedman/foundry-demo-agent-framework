param()

Write-Host "Restoring, building and running tests..."

dotnet restore
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet build
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

dotnet test --logger:trx
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }

Write-Host "Tests completed."