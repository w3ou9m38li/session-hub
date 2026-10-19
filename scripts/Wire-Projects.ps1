# One-shot project wiring: target frameworks, solution membership, references, packages.
# Re-runnable; every step is idempotent.
$ErrorActionPreference = 'Stop'
Set-Location 'D:\ClaudeLauncher'

# --- 1. remove template placeholders -------------------------------------
Get-ChildItem -Recurse -Include Class1.cs, UnitTest1.cs -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; "删除模板文件 $($_.Name)" }

# --- 2. target frameworks -------------------------------------------------
# Core stays net10.0 (portable, pure rules). Everything that touches Windows or
# references Infrastructure must be net10.0-windows, otherwise the test projects
# cannot reference it.
$windowsTargets = @(
    'src\ClaudeLauncher.Infrastructure\ClaudeLauncher.Infrastructure.csproj',
    'tests\ClaudeLauncher.UnitTests\ClaudeLauncher.UnitTests.csproj',
    'tests\ClaudeLauncher.IntegrationTests\ClaudeLauncher.IntegrationTests.csproj'
)
foreach ($p in $windowsTargets) {
    $text = Get-Content -LiteralPath $p -Raw
    if ($text -match '<TargetFramework>net10\.0</TargetFramework>') {
        $text = $text -replace '<TargetFramework>net10\.0</TargetFramework>',
                               '<TargetFramework>net10.0-windows</TargetFramework>'
        Set-Content -LiteralPath $p -Value $text -Encoding UTF8 -NoNewline
        "目标框架 -> net10.0-windows : $p"
    } else {
        "目标框架已正确 : $p"
    }
}

# --- 3. solution membership ----------------------------------------------
$projects = @(
    'src\ClaudeLauncher.Core\ClaudeLauncher.Core.csproj',
    'src\ClaudeLauncher.Infrastructure\ClaudeLauncher.Infrastructure.csproj',
    'src\ClaudeLauncher.App\ClaudeLauncher.App.csproj',
    'tests\ClaudeLauncher.UnitTests\ClaudeLauncher.UnitTests.csproj',
    'tests\ClaudeLauncher.IntegrationTests\ClaudeLauncher.IntegrationTests.csproj'
)
foreach ($p in $projects) { dotnet sln ClaudeLauncher.sln add $p 2>&1 | Out-Null }
"已加入解决方案 : $($projects.Count) 个项目"

# --- 4. project references ------------------------------------------------
dotnet add src\ClaudeLauncher.Infrastructure reference src\ClaudeLauncher.Core 2>&1 | Out-Null
dotnet add src\ClaudeLauncher.App reference src\ClaudeLauncher.Core src\ClaudeLauncher.Infrastructure 2>&1 | Out-Null
dotnet add tests\ClaudeLauncher.UnitTests reference src\ClaudeLauncher.Core src\ClaudeLauncher.Infrastructure 2>&1 | Out-Null
dotnet add tests\ClaudeLauncher.IntegrationTests reference src\ClaudeLauncher.Core src\ClaudeLauncher.Infrastructure 2>&1 | Out-Null
"项目引用已建立"

# --- 5. packages ----------------------------------------------------------
# Microsoft.Data.Sqlite is the only runtime dependency; it ships the bundled
# SQLite engine so the tool does not depend on anything installed system-wide.
dotnet add src\ClaudeLauncher.Infrastructure package Microsoft.Data.Sqlite 2>&1 | Select-String -Pattern 'info : PackageReference|error' | Select-Object -First 3
"包引用完成"
