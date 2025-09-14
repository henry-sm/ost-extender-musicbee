# MusicBee OST Extender Plugin Installer
# This script helps install the plugin to the correct location

# Define paths
$pluginPath = Join-Path $env:APPDATA "MusicBee\Plugins"
$sourcePath = ".\bin\Debug\net48"
$pluginDll = "mb_OstLooper.dll"

# Create MusicBee plugins directory if it doesn't exist
if (-not (Test-Path $pluginPath)) {
    New-Item -Path $pluginPath -ItemType Directory -Force
    Write-Host "Created MusicBee Plugins directory at: $pluginPath"
}

# Check if MusicBee is running
$musicBeeProcess = Get-Process -Name "MusicBee" -ErrorAction SilentlyContinue

if ($musicBeeProcess) {
    Write-Host "WARNING: MusicBee is currently running. Please close MusicBee before installing the plugin."
    $response = Read-Host "Would you like to close MusicBee now? (y/n)"
    
    if ($response -eq 'y' -or $response -eq 'Y') {
        Write-Host "Closing MusicBee..."
        $musicBeeProcess | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        Start-Sleep -Seconds 2
        $musicBeeProcess | Where-Object { -not $_.HasExited } | ForEach-Object { $_.Kill() }
    } else {
        Write-Host "Installation aborted. Please close MusicBee manually and run this script again."
        exit
    }
}

# Copy plugin files
try {
    Write-Host "Copying plugin files..."
    Copy-Item "$sourcePath\$pluginDll" -Destination $pluginPath -Force
    
    # Copy dependencies (except those that should be already in MusicBee)
    $dependencies = @(
        "Accord.dll",
        "Accord.Audio.dll",
        "Accord.Math.dll",
        "NAudio.dll",
        "NAudio.Asio.dll",
        "NAudio.Core.dll", 
        "NAudio.Midi.dll", 
        "NAudio.Wasapi.dll",
        "NAudio.WinForms.dll",
        "NAudio.WinMM.dll"
    )
    
    foreach ($dep in $dependencies) {
        $source = "$sourcePath\$dep"
        if (Test-Path $source) {
            Copy-Item $source -Destination $pluginPath -Force
            Write-Host "Copied dependency: $dep"
        }
    }
    
    Write-Host "`nPlugin installation completed successfully!"
    Write-Host "`n===== USAGE INSTRUCTIONS ====="
    Write-Host "1. Start MusicBee"
    Write-Host "2. Access the plugin via:"
    Write-Host "   - Tools > OST Extender menu"
    Write-Host "   - Right-click on tracks to see OST Extender context menu"
    Write-Host "3. Use 'Analyze Track' to detect loop points first"
    Write-Host "4. Then use 'Play with Loop' to enable smart looping"
    Write-Host "`nEnjoy your seamless OST loops!"
    
} catch {
    Write-Host "Error installing plugin: $_"
}

Write-Host "`nPress any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")