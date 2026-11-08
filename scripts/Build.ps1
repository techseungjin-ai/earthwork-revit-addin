param([string]$AssemblyDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if(!$AssemblyDirectory){
    $stage=Join-Path $repo ('tmp\package-'+[DateTime]::Now.ToString('yyyyMMddHHmmss'))
    dotnet restore src/CivilBim.Revit/CivilBim.Revit.csproj -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0){throw 'restore 실패'}
    dotnet build src/CivilBim.Revit/CivilBim.Revit.csproj --no-restore -c Release "-p:BaseOutputPath=$stage\" -v:minimal
    if($LASTEXITCODE -ne 0){throw 'build 실패'}
    $AssemblyDirectory=Join-Path $stage 'Release\net10.0-windows'
    }
    dotnet run --project tests/CivilBim.Checks --no-restore
    if($LASTEXITCODE -ne 0){throw 'checks 실패'}
    $package=Join-Path $repo 'artifacts\CivilBIM-0.4.2'
    if(Test-Path -LiteralPath $package){Remove-Item -LiteralPath $package -Recurse -Force}
    $payload=Join-Path $package 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Get-ChildItem -LiteralPath $AssemblyDirectory -File | Where-Object {$_.Extension -in '.dll','.json','.pdb'} | Copy-Item -Destination $payload -Force
    if(Test-Path -LiteralPath (Join-Path $payload 'RevitAPI.dll')){throw 'Autodesk Revit API DLL은 배포하지 않습니다.'}
    Get-ChildItem -LiteralPath $payload -File | Where-Object {$_.Extension -in '.dll','.json','.pdb'} | Sort-Object Name | ForEach-Object {"$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)"} | Set-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt') -Encoding ascii
    Copy-Item -LiteralPath 'scripts/Install.ps1' -Destination (Join-Path $package 'Install.ps1') -Force
    $installer=Join-Path $package 'Install.ps1'
    [IO.File]::WriteAllText($installer,[IO.File]::ReadAllText($installer),[Text.UTF8Encoding]::new($true))
    Copy-Item -LiteralPath 'scripts/Uninstall.ps1' -Destination $package -Force
    $uninstaller=Join-Path $package 'Uninstall.ps1'
    [IO.File]::WriteAllText($uninstaller,[IO.File]::ReadAllText($uninstaller),[Text.UTF8Encoding]::new($true))
    Copy-Item -LiteralPath 'README.md' -Destination $package -Force
    New-Item -ItemType Directory -Path (Join-Path $package 'docs') -Force | Out-Null
    foreach($guide in @('QUICKSTART-PUBLIC.md','LOCAL-2026-11-05.md','CHANGELOG.md','THIRD-PARTY.md','PdfPig-LICENSE.txt','PdfPig-NOTICES.txt','NetTopologySuite-LICENSE.md')){Copy-Item -LiteralPath (Join-Path 'docs' $guide) -Destination (Join-Path $package 'docs') -Force}
    Set-Content -LiteralPath (Join-Path $package 'Install.cmd') -Encoding ascii -Value @('@echo off','powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -AssemblyDirectory "%~dp0payload"','if errorlevel 1 (echo Installation failed. See message above. & pause & exit /b 1)','echo Installation complete. Start Revit 2027.','pause')
    Set-Content -LiteralPath (Join-Path $package 'Uninstall.cmd') -Encoding ascii -Value @('@echo off','powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"','pause')
    Compress-Archive -LiteralPath $package -DestinationPath ($package+'.zip') -Force
    Write-Output $package
} finally {Pop-Location}



