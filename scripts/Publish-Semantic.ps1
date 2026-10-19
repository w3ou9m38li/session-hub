<#
.SYNOPSIS
  Publishes the optional semantic-search component into a "semantic" folder beside the exe.

.DESCRIPTION
  The component is the embedding model's runtime (ONNX Runtime) and the code that drives it. Only
  the semantic worker — the window's child process, started when search by meaning is switched on —
  loads it, from <exe folder>\semantic; the window itself never does, and the app runs as before
  without it. The model is not part of it: the user puts one in the data folder (models\embedding).

  Output: <Output>\semantic, holding ClaudeLauncher.Semantic.dll, its .deps.json and ONNX Runtime
  (managed and native). The host's own assemblies (Infrastructure, Core, SQLite) are left out on
  purpose: the component must use the exe's, or its types would not be the exe's types.

.PARAMETER Output
  The folder the exe is in (default: artifacts\win-x64, where the self-use build goes).
#>
param([string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\win-x64'))

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\ClaudeLauncher.Semantic\ClaudeLauncher.Semantic.csproj'
$target = Join-Path $Output 'semantic'

# A component left from another version would mix its files with these.
if (Test-Path $target) { Remove-Item $target -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:DebugType=none -p:DebugSymbols=false -o $target --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '语义搜索组件发布失败' }

# Import libraries for linking against ONNX Runtime from C++: nothing here loads them.
Get-ChildItem $target -File -Filter '*.lib' | Remove-Item

$required = 'ClaudeLauncher.Semantic.dll', 'ClaudeLauncher.Semantic.deps.json', 'Microsoft.ML.OnnxRuntime.dll', 'onnxruntime.dll'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $target $_)) }
if ($missing) { throw "语义搜索组件缺文件：$($missing -join ', ')" }

$hosts = Get-ChildItem $target -File | Where-Object Name -match '^(ClaudeLauncher\.(Infrastructure|Core)|Microsoft\.Data\.Sqlite|SQLitePCLRaw|e_sqlite3|ZstdSharp)'
if ($hosts) { throw "语义搜索组件里混进了程序自己的文件，会和程序的版本打架：$($hosts.Name -join ', ')" }

Get-ChildItem $target -File |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
