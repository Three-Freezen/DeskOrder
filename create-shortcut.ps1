# DesktopZones - Create Desktop Shortcut
# This script creates a shortcut on the desktop to launch DesktopZones

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DesktopPath = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = Join-Path $DesktopPath "DesktopZones.lnk"

Write-Host ""
Write-Host "╔══════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   Create DesktopZones Shortcut   ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Create the shortcut
$WScriptShell = New-Object -ComObject WScript.Shell
$Shortcut = $WScriptShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = "dotnet"
$Shortcut.Arguments = "run --project `"$ProjectDir\DesktopZones.csproj`""
$Shortcut.WorkingDirectory = $ProjectDir
$Shortcut.Description = "DesktopZones - Desktop Enhancement"
$Shortcut.IconLocation = "$ProjectDir\Resources\app.ico"
$Shortcut.Save()

Write-Host "[OK] Shortcut created on Desktop: DesktopZones.lnk" -ForegroundColor Green
Write-Host ""
Write-Host "You can now double-click the shortcut on your desktop to launch DesktopZones."
Write-Host ""

# Generate an icon resource if it doesn't exist
$IconDir = Join-Path $ProjectDir "Resources"
if (-not (Test-Path $IconDir)) {
    New-Item -ItemType Directory -Path $IconDir -Force | Out-Null
}

# Copy self to startup if requested
$StartupPath = [Environment]::GetFolderPath("Startup")
if ($StartupPath) {
    $AutoStart = Read-Host "Add to startup? (y/n)"
    if ($AutoStart -eq "y") {
        $StartupShortcut = Join-Path $StartupPath "DesktopZones.lnk"
        Copy-Item $ShortcutPath $StartupShortcut -Force
        Write-Host "[OK] Added to startup" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Done! Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
