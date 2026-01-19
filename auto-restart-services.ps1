# Automated DerivCTrader Service Restart Script
# Stops services, rebuilds, and restarts automatically without user interaction

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Auto-Restart DerivCTrader Services   " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Kill all dotnet processes (services)
Write-Host "Step 1: Stopping all services..." -ForegroundColor Yellow
$processes = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue
if ($processes) {
    foreach ($proc in $processes) {
        Write-Host "  Stopping: dotnet (PID: $($proc.Id))" -ForegroundColor Red
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Write-Host "  Waiting for processes to terminate..." -ForegroundColor Yellow
    Start-Sleep -Seconds 2
    Write-Host "  ✅ All services stopped" -ForegroundColor Green
} else {
    Write-Host "  No running services found" -ForegroundColor Green
}

# Step 2: Build solution in Debug mode
Write-Host ""
Write-Host "Step 2: Building solution in Debug mode..." -ForegroundColor Yellow
Set-Location "c:\Users\fjadu\source\repos\DerivCTraderAutomation"

$buildOutput = dotnet build -c Debug 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✅ Build succeeded!" -ForegroundColor Green
} else {
    Write-Host "  ❌ Build failed!" -ForegroundColor Red
    Write-Host $buildOutput
    Write-Host ""
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

# Step 3: Start services in background
Write-Host ""
Write-Host "Step 3: Starting services in background..." -ForegroundColor Yellow

# Start SignalScraper
Write-Host "  Starting SignalScraper..." -ForegroundColor Cyan
$scraperJob = Start-Job -ScriptBlock {
    Set-Location "c:\Users\fjadu\source\repos\DerivCTraderAutomation\src\DerivCTrader.SignalScraper"
    dotnet run -c Debug
}
Write-Host "    Job ID: $($scraperJob.Id)" -ForegroundColor Gray

Start-Sleep -Seconds 1

# Start TradeExecutor
Write-Host "  Starting TradeExecutor..." -ForegroundColor Cyan
$executorJob = Start-Job -ScriptBlock {
    Set-Location "c:\Users\fjadu\source\repos\DerivCTraderAutomation\src\DerivCTrader.TradeExecutor"
    dotnet run -c Debug
}
Write-Host "    Job ID: $($executorJob.Id)" -ForegroundColor Gray

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ✅ Services Started Successfully!     " -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Services are running in background jobs." -ForegroundColor Yellow
Write-Host ""
Write-Host "To view logs, use:" -ForegroundColor White
Write-Host "  Receive-Job -Id $($scraperJob.Id) -Keep   # SignalScraper logs" -ForegroundColor Gray
Write-Host "  Receive-Job -Id $($executorJob.Id) -Keep   # TradeExecutor logs" -ForegroundColor Gray
Write-Host ""
Write-Host "To stop services, use:" -ForegroundColor White
Write-Host "  Stop-Job -Id $($scraperJob.Id),$($executorJob.Id)" -ForegroundColor Gray
Write-Host "  Remove-Job -Id $($scraperJob.Id),$($executorJob.Id)" -ForegroundColor Gray
Write-Host ""
