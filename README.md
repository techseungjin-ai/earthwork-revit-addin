# 토공사 — Revit 2027 애드인

수치지형도와 흙막이 도면을 이용해 Revit에서 대지 및 가시설 모델을 작성하는 도구입니다. 설치 후 리본에서 **토공사** 탭을 사용합니다. 기존 BOMI와 별도로 동작합니다.

## 설치

1. GitHub Releases에서 `CivilBIM-0.4.1.zip`을 내려받아 PC의 로컬 폴더에 **모두 압축 해제**합니다.
2. Revit 2027을 저장하고 종료한 후, 풀린 폴더의 `Install.cmd`를 실행합니다.
3. Revit 2027을 다시 시작하고 **토공사** 탭을 확인합니다. 서명되지 않은 애드인 확인창이 나오면 설치 출처를 확인한 후 로드하세요.

이전 CivilBIM이 설치된 PC도 Revit을 종료한 뒤 같은 방식으로 설치하면 새 버전이 적용됩니다. 기존 프로젝트와 BOMI는 삭제하지 않습니다. 각 Windows 사용자 계정별 설치입니다. Revit 2027이 필요하며 별도의 Dynamo 설치는 필요하지 않습니다.

## 주요 기능

- SHP 폴더에서 지형·도로 구획·주변 건물 생성
- CAD 사면선과 Revit 외곽선으로 굴착용 바닥 또는 지형솔리드 구역 작성
- CAD에서 CIP·H-PILE을 인식해 선택한 Revit 패밀리 배치
- 경로별 단면을 시트에 배치해 기준선까지 수직부재 높이 조정
- 평면선과 단별 높이선으로 STRUT·띠장 배치
- 시추자료 검토·지층 모델·지형/지층 중복체적 확인

사용 순서와 현재 기능 제한은 [간단 사용 안내](docs/QUICKSTART-PUBLIC.md)를 참고하세요. 사용자 DWG·RVT·SHP·PDF·패밀리 원본은 포함되어 있지 않습니다. 현장 자료와 패밀리에 따라 결과를 확인하고 사용하세요.

## 소스 빌드

Windows, .NET 10 SDK, Revit 2027 설치가 필요합니다. PowerShell에서 `dotnet restore src/CivilBim.Revit/CivilBim.Revit.csproj` 및 `dotnet build src/CivilBim.Revit/CivilBim.Revit.csproj -c Release`를 실행합니다. Autodesk API DLL은 별도 배포하지 않습니다. 외부 구성요소 고지는 [THIRD-PARTY](docs/THIRD-PARTY.md)를 확인하세요.

