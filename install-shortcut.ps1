$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist\CodexProjectCenter.exe'
if (-not (Test-Path $exe)) {
  & (Join-Path $PSScriptRoot 'build.ps1')
}
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcut = $shell.CreateShortcut((Join-Path $desktop 'Codex 项目中心.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe
$shortcut.Description = '实时查看 Codex 任务状态'
$shortcut.Save()
Write-Host "Created: $desktop\Codex 项目中心.lnk"
