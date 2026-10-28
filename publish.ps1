# 本地复刻 .github/workflows/release.yml 的构建与产物生成流程。
# 只生成发布产物，不做 GitHub 相关动作（比较版本、打 tag、创建 Release）。
# 产物目录 publish\ 与 GitHub Release 附件保持一致：
#   GDPackEditor.exe、SHA256SUMS.txt、LICENSE、THIRD-PARTY-NOTICES.md、release-notes.md
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# 1. 版本校验：csproj <Version> 必须与 Program.GUI_VERSION 一致
[xml]$csproj = Get-Content "GDPackEditor/GDPackEditor.csproj" -Raw -Encoding UTF8
$version = @($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "csproj <Version> is missing." }
$version = $version.Trim()

$gui = (Select-String -Path "GDPackEditor/Program.cs" -Pattern 'GUI_VERSION\s*=\s*"v([^"]+)"' -Encoding UTF8).Matches[0].Groups[1].Value
if ($gui -ne $version) { throw "Version mismatch: csproj=$version, GUI_VERSION=v$gui" }
Write-Host "Code version: v$version"

# 2. 清理旧产物
if (Test-Path "publish") { Remove-Item "publish" -Recurse -Force }

# 3. 还原、构建、测试
dotnet restore GDPackEditor.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

dotnet build GDPackEditor.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

dotnet test GDPackEditor.Tests/GDPackEditor.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }

# 4. 单文件发布
dotnet publish GDPackEditor/GDPackEditor.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:Version=$version `
  -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 5. 校验和（格式与工作流一致）
$hash = (Get-FileHash "publish/GDPackEditor.exe" -Algorithm SHA256).Hash.ToLower()
"$hash *GDPackEditor.exe" | Set-Content "publish/SHA256SUMS.txt" -Encoding ascii

# 6. 从变更日志截取当前版本段落，作为 Release 说明
$log = Get-Content "GDPackEditor/!ChangeLog.md" -Raw -Encoding UTF8
$pattern = "(?ms)^#\s*v?$([regex]::Escape($version))\b[^\r\n]*\r?\n(.*?)(?=^#\s|\z)"
$match = [regex]::Match($log, $pattern)
if (-not $match.Success) {
  throw "No changelog section found for v$version in GDPackEditor/!ChangeLog.md."
}
$notes = $match.Groups[1].Value.Trim()
if ([string]::IsNullOrWhiteSpace($notes)) { $notes = "Release v$version" }
[System.IO.File]::WriteAllText(
  (Join-Path $root "publish/release-notes.md"),
  $notes,
  [System.Text.UTF8Encoding]::new($false))

# 7. 汇总附件，使 publish\ 内容与 GitHub Release 一致
Copy-Item "LICENSE" "publish/LICENSE" -Force
Copy-Item "THIRD-PARTY-NOTICES.md" "publish/THIRD-PARTY-NOTICES.md" -Force
# 工作流只上传 EXE，pdb 不随发布分发
Remove-Item "publish/GDPackEditor.pdb" -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Release v$version generated in: $root\publish"
Get-ChildItem "publish" | Select-Object Name, Length | Format-Table -AutoSize
