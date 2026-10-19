# Creates a desktop shortcut for the launcher.
# Run it yourself — the tool never puts anything on your desktop on its own,
# and it never touches your existing official Claude shortcut.
param(
    [string]$Exe = 'D:\ClaudeLauncher\artifacts\win-x64\ClaudeLauncher.App.exe',
    [string]$Name = '会话管家（Claude · ChatGPT）'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "未找到产物：$Exe（先执行 dotnet publish）" }

$desktop = [Environment]::GetFolderPath('Desktop')
$link = Join-Path $desktop "$Name.lnk"

if (Test-Path $link) {
    Write-Host "快捷方式已存在，将覆盖：$link" -ForegroundColor Yellow
}

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($link)
$sc.TargetPath = $Exe
$sc.WorkingDirectory = Split-Path $Exe -Parent
$sc.Description = '独立第三方启动器；官方 Claude 快捷方式不受影响'
$sc.Save()

Write-Host "已创建：$link" -ForegroundColor Green
Write-Host '你原有的官方 Claude 快捷方式没有任何改动，随时可以直接用它。'
