<#
.SYNOPSIS
  Builds the package for handing the app to someone else: one self-contained .exe that needs
  no installation and no .NET, plus a zip of that exe with a short 使用说明.

.DESCRIPTION
  Output, in dist\ at the repository root:
    会话管家-<version>-win-x64.exe   the whole app in one file — for cloud drives and direct links
    会话管家-<version>-win-x64.zip   the same exe with 使用说明.txt — for chat apps and mail that
                                      refuse a bare .exe
    会话管家-语义搜索组件-<version>-win-x64.zip
                                    the optional component for search by meaning: a "semantic"
                                    folder to unpack beside the exe (scripts\Publish-Semantic.ps1).
                                    The exe runs without it; no model is included.

  The native parts (WPF, SQLite) travel inside the exe and are unpacked once, on first run, to
  %TEMP%\.net on the receiving machine. Nothing personal is included: every user's catalog and
  settings live in their own data folder, created on their machine.
#>
param([string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\ClaudeLauncher.App\ClaudeLauncher.App.csproj'
$version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText
$name = "会话管家-$version-win-x64"
$work = Join-Path $Output '_build'

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
    -o (Join-Path $work 'publish') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$leftovers = Get-ChildItem (Join-Path $work 'publish') -File | Where-Object Name -ne 'ClaudeLauncher.App.exe'
if ($leftovers) { throw "单文件包之外还有文件，分享出去会缺东西：$($leftovers.Name -join ', ')" }

$exe = Join-Path $Output "$name.exe"
Copy-Item (Join-Path $work 'publish\ClaudeLauncher.App.exe') $exe -Force

$stage = Join-Path $work 'zip'
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $repo 'docs\使用说明.txt') $stage
$zip = Join-Path $Output "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# UTF-8 entry names, so the Chinese file names survive Windows' own "全部解压缩".
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false,
    [Text.Encoding]::UTF8)

# The optional component, apart from the exe (whose single-file checks above it never touches):
# a zip holding the "semantic" folder, unpacked beside the exe.
$componentName = "会话管家-语义搜索组件-$version-win-x64"
$componentStage = Join-Path $work 'component'
& (Join-Path $PSScriptRoot 'Publish-Semantic.ps1') -Output $componentStage | Out-Null
$componentZip = Join-Path $Output "$componentName.zip"
if (Test-Path $componentZip) { Remove-Item $componentZip }
[IO.Compression.ZipFile]::CreateFromDirectory($componentStage, $componentZip, [IO.Compression.CompressionLevel]::Optimal,
    $false, [Text.Encoding]::UTF8)

Remove-Item $work -Recurse -Force
Get-ChildItem $Output -File | Where-Object { $_.Name -like "$name.*" -or $_.Name -eq "$componentName.zip" } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
