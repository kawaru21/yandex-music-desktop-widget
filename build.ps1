param([switch]$Probe)
$ErrorActionPreference = 'Stop'
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs = @('Windows.Foundation.winmd','Windows.Media.winmd','Windows.Storage.winmd') | ForEach-Object { '/r:C:\Windows\System32\WinMetadata\' + $_ }
foreach ($name in @('System.Runtime','System.Runtime.InteropServices.WindowsRuntime','System.Threading.Tasks')) {
    $refs += Get-ChildItem "C:/Windows/Microsoft.NET/assembly/GAC_MSIL/$name" -Recurse -Filter "$name.dll" | ForEach-Object { '/r:' + $_.FullName }
}
Push-Location $PSScriptRoot
try {
    if ($Probe) {
        & "$framework\csc.exe" /nologo /target:exe /out:Probe.exe $refs Probe.cs
    } else {
        $refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path "$framework\WPF" $_) }
        $refs += '/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.Xaml.dll'
        & "$framework\csc.exe" /nologo /optimize+ /target:winexe /platform:x64 /win32manifest:app.manifest /out:MusicWidget.exe $refs MusicWidget.cs
    }
    if ($LASTEXITCODE -ne 0) { throw 'Compile failed' }
} finally { Pop-Location }
