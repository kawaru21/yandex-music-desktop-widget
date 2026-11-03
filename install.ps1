$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'MusicWidget.exe'
if (-not (Test-Path -LiteralPath $source)) { throw "Сначала соберите MusicWidget.exe через build.ps1" }

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\YandexMusicWidget'
$installedExe = Join-Path $installDir 'MusicWidget.exe'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Яндекс Музыка — виджет.lnk'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Яндекс Музыка — виджет.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Яндекс Музыка — виджет.lnk'

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Get-Process MusicWidget -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -eq $source -or $_.Path -eq $installedExe
} | Stop-Process
Copy-Item -LiteralPath $source -Destination $installedExe -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($link in @($startupLink, $startMenuLink, $desktopLink)) {
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $installedExe
    $shortcut.Arguments = if ($link -eq $startupLink) { '--tray' } else { '' }
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = 'Виджет Яндекс Музыки: обложка, время, управление и настройки в трее'
    $shortcut.Save()
}

Start-Process -FilePath $installedExe -WindowStyle Hidden

# Keep the icon in the visible notification area, not only in its overflow menu.
$iconSettings = 'HKCU:\Control Panel\NotifyIconSettings'
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $iconKey = Get-ChildItem -Path $iconSettings -ErrorAction SilentlyContinue | Where-Object {
        (Get-ItemPropertyValue -LiteralPath $_.PSPath -Name ExecutablePath -ErrorAction SilentlyContinue) -eq $installedExe
    } | Select-Object -First 1
    if ($iconKey) {
        New-ItemProperty -LiteralPath $iconKey.PSPath -Name IsPromoted -PropertyType DWord -Value 1 -Force | Out-Null
        break
    }
    Start-Sleep -Milliseconds 250
}
[pscustomobject]@{
    InstalledExe = $installedExe
    StartupLink = $startupLink
    StartMenuLink = $startMenuLink
    DesktopLink = $desktopLink
}
