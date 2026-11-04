$ErrorActionPreference='Stop'
$manifest=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027\CivilBIM.addin'
if(Test-Path -LiteralPath $manifest){
    [xml]$xml=Get-Content -LiteralPath $manifest -Raw
    if($xml.RevitAddIns.AddIn.AddInId -ne '495CC06D-1D80-43F0-8B48-C57ED9A15436'){throw 'CivilBIM 소유 매니페스트가 아니므로 중단했습니다.'}
    Move-Item -LiteralPath $manifest -Destination ($manifest+'.disabled') -Force
    Write-Output 'CivilBIM 자동 로드를 해제했습니다. 다음 Revit 실행부터 적용됩니다.'
}else{Write-Output '활성 CivilBIM 매니페스트가 없습니다.'}
Write-Output '설치 DLL, 프로젝트 및 시추 검토 자료는 보존됩니다. 재설치하면 다시 활성화됩니다.'
