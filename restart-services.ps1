# Restart DerivCTrader Services Script
# This script stops running services, rebuilds in Debug mode, and restarts them

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  DerivCTrader Service Restart Script  " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Stop running processes
Write-Host "Step 1: Stopping running services..." -ForegroundColor Yellow
$processes = Get-Process | Where-Object { $_.ProcessName -like "*DerivCTrader*" }

if ($processes) {
    foreach ($proc in $processes) {
        Write-Host "  Stopping: $($proc.ProcessName) (PID: $($proc.Id))" -ForegroundColor Red
        Stop-Process -Id $proc.Id -Force
    }
    Write-Host "  Waiting for processes to terminate..." -ForegroundColor Yellow
    Start-Sleep -Seconds 3
} else {
    Write-Host "  No running services found." -ForegroundColor Green
}

# Step 2: Build solution in Debug mode
Write-Host ""
Write-Host "Step 2: Building solution in Debug mode..." -ForegroundColor Yellow
Set-Location "c:\Users\fjadu\source\repos\DerivCTraderAutomation"
$buildResult = dotnet build -c Debug 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Build succeeded!" -ForegroundColor Green
} else {
    Write-Host "  Build failed!" -ForegroundColor Red
    Write-Host $buildResult
    exit 1
}

# Step 3: Start services
Write-Host ""
Write-Host "Step 3: Starting services..." -ForegroundColor Yellow
Write-Host ""

# Start SignalScraper in a new window
Write-Host "  Starting SignalScraper..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd c:\Users\fjadu\source\repos\DerivCTraderAutomation\src\DerivCTrader.SignalScraper; Write-Host 'Starting DerivCTrader.SignalScraper...' -ForegroundColor Green; dotnet run -c Debug --no-build -- --contentRoot 'c:\Users\fjadu\source\repos\DerivCTraderAutomation\src\DerivCTrader.SignalScraper'" -WindowStyle Normal

Start-Sleep -Seconds 2

# Start TradeExecutor in a new window
Write-Host "  Starting TradeExecutor..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd c:\Users\fjadu\source\repos\DerivCTraderAutomation\src\DerivCTrader.TradeExecutor; Write-Host 'Starting DerivCTrader.TradeExecutor...' -ForegroundColor Green; dotnet run -c Debug --no-build -- --contentRoot 'c:\Users\fjadu\source\repos\DerivCTraderAutomation\src\DerivCTrader.TradeExecutor'" -WindowStyle Normal

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Services Started Successfully!        " -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Check the new PowerShell windows for service logs." -ForegroundColor Yellow
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
