<#
  Gera a versão de distribuição do FieldProbe (Windows x64, self-contained: não exige .NET instalado).

  Uso (PowerShell, na raiz do repositório):
      .\build\publish.ps1 -Version 1.0.0

  Saída: artifacts\publish\  (depois rode o Inno Setup com build\installer.iss)
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish "$root\src\FieldProbe.App\FieldProbe.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:DebugType=none `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }

Write-Host ""
Write-Host "Publicado em $out" -ForegroundColor Green
Write-Host "Próximo passo: abrir build\installer.iss no Inno Setup e compilar (ou: iscc build\installer.iss /DAppVersion=$Version)."
