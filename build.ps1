$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
$out = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $out | Out-Null

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:"$PSScriptRoot\app.manifest" `
  /win32icon:"$PSScriptRoot\assets\project-center.ico" `
  /out:"$out\CodexProjectCenter.exe" `
  /reference:"$framework\WPF\PresentationCore.dll" `
  /reference:"$framework\WPF\PresentationFramework.dll" `
  /reference:"$framework\WPF\WindowsBase.dll" `
  /reference:"$framework\WPF\UIAutomationClient.dll" `
  /reference:"$framework\WPF\UIAutomationTypes.dll" `
  /reference:"$framework\System.Xaml.dll" `
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
  /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll `
  "$PSScriptRoot\Program.cs"

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
Write-Host "Built: $out\CodexProjectCenter.exe"
