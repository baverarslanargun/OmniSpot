# Test script for large folder (10,000 files) crash scenario
# Creates a test folder with 10,000 files and tests OmniSpot performance

$testFolder = "C:\TestOmniSpot_10k"
$fileCount = 10000

Write-Host "🧪 OmniSpot Large Folder Test Script" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# Create test folder if it doesn't exist
if (-not (Test-Path $testFolder)) {
    Write-Host "📁 Creating test folder: $testFolder" -ForegroundColor Yellow
    New-Item -Path $testFolder -ItemType Directory -Force | Out-Null
    
    Write-Host "📝 Creating $fileCount test files..." -ForegroundColor Yellow
    
    # Create files in batches for better performance
    $batchSize = 1000
    for ($i = 0; $i -lt $fileCount; $i++) {
        $fileName = "test_file_$($i.ToString('D5')).txt"
        $filePath = Join-Path $testFolder $fileName
        "Test content for file $i" | Out-File -FilePath $filePath -Encoding UTF8
        
        if (($i + 1) % $batchSize -eq 0) {
            $progress = [math]::Round(($i + 1) / $fileCount * 100, 1)
            Write-Host "  Progress: $($i + 1)/$fileCount files ($progress%)" -ForegroundColor Gray
        }
    }
    
    Write-Host "✅ Created $fileCount test files" -ForegroundColor Green
} else {
    $existingFileCount = (Get-ChildItem $testFolder -File).Count
    Write-Host "✅ Test folder already exists with $existingFileCount files" -ForegroundColor Green
}

Write-Host ""
Write-Host "📊 Test Scenario Information:" -ForegroundColor Cyan
Write-Host "  Folder: $testFolder" -ForegroundColor White
Write-Host "  Files: $fileCount" -ForegroundColor White
Write-Host "  Expected issues (BEFORE optimization):" -ForegroundColor White
Write-Host "    ❌ UI freeze during sync (5-10 seconds)" -ForegroundColor Red
Write-Host "    ❌ High memory usage (~100MB spike)" -ForegroundColor Red
Write-Host "    ❌ Loading indicator frozen" -ForegroundColor Red
Write-Host "    ❌ Possible deadlock with background delta sync" -ForegroundColor Red
Write-Host ""
Write-Host "  Expected behavior (AFTER optimization):" -ForegroundColor White
Write-Host "    ✅ Loading indicator animates smoothly" -ForegroundColor Green
Write-Host "    ✅ Folder opens within 2-3 seconds" -ForegroundColor Green
Write-Host "    ✅ Memory usage optimized (streaming enumeration)" -ForegroundColor Green
Write-Host "    ✅ Transaction batching for DB operations" -ForegroundColor Green
Write-Host "    ✅ No deadlock (timeout protection + async wait)" -ForegroundColor Green
Write-Host ""

Write-Host "🚀 Test Instructions:" -ForegroundColor Cyan
Write-Host "1. Start OmniSpot application" -ForegroundColor White
Write-Host "2. Wait for delta sync to start" -ForegroundColor White
Write-Host "3. While delta sync is running (~47% visible), click on test folder:" -ForegroundColor White
Write-Host "   $testFolder" -ForegroundColor Yellow
Write-Host "4. Observe:" -ForegroundColor White
Write-Host "   - Loading indicator should show and animate" -ForegroundColor White
Write-Host "   - No UI freeze" -ForegroundColor White
Write-Host "   - Folder should open within 2-3 seconds" -ForegroundColor White
Write-Host "   - No crash or deadlock" -ForegroundColor White
Write-Host ""

# Offer to start OmniSpot
$response = Read-Host "Start OmniSpot now? (y/n)"
if ($response -eq 'y' -or $response -eq 'Y') {
    Write-Host "🚀 Starting OmniSpot..." -ForegroundColor Green
    Start-Process ".\SmartFileLauncher.UI\bin\Release\net8.0-windows\win-x64\OmniSpot.exe"
    Write-Host "✅ OmniSpot started. Follow test instructions above." -ForegroundColor Green
} else {
    Write-Host "📝 Run OmniSpot manually and test with the folder above." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "🧹 Cleanup:" -ForegroundColor Cyan
Write-Host "To delete test folder after testing, run:" -ForegroundColor White
Write-Host "  Remove-Item -Path '$testFolder' -Recurse -Force" -ForegroundColor Gray
