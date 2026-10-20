$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$nativeProject = Join-Path $projectRoot "native\ComandosMDB\ComandosMDB.vcxproj"
$nativeDll = Join-Path $projectRoot "native\ComandosMDB\x64\Release\ComandosMDB.dll"
$publishDir = Join-Path $projectRoot "publish\iis"

if (-not (Get-Command msbuild.exe -ErrorAction SilentlyContinue)) {
    throw "No encuentro msbuild.exe. Ejecuta este script desde Developer PowerShell de Visual Studio con C++ Build Tools instalado."
}

Write-Host "Compilando ComandosMDB x64..."
& msbuild.exe $nativeProject /m /p:Configuration=Release /p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación de ComandosMDB." }

Write-Host "Publicando API .NET 8 para Windows x64..."
& dotnet publish (Join-Path $projectRoot "ApiLocalCommand.csproj") -c Release -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Falló dotnet publish." }

Copy-Item $nativeDll (Join-Path $publishDir "ComandosMDB.dll") -Force
Write-Host ""
Write-Host "Publicación lista: $publishDir"
Write-Host "Copia el contenido de esa carpeta a la carpeta física del sitio IIS."
Write-Host "Configura LocalApi__Username y LocalApi__Password en el entorno del App Pool."
