# Spine Support Tool 개발 및 아키텍처 인수인계 문서 (`AGENTS.md`)

## 1. 프로젝트 개요 & 타깃 환경
- **프로젝트명:** Spine Support Tool
- **목적:** 뮤즈대쉬(Muse Dash)의 `gamescene_*.bundle` 에셋 번들 추출, `spine-hwa` (`SpineViewer.exe` / `sv-cli.exe`)를 통한 Spine 3.8 몬스터/노트 배치 렌더링, BMS 커스텀 차트 에디터 노트 스킨 규격 동기화를 원클릭으로 지원하는 Avalonia UI 데스크톱 도구.
- **대상 게임:** Muse Dash (Unity / Il2Cpp)
  - 게임 경로: `H:\muse dash hwa`
  - 에셋 번들 경로: `MuseDash_Data\StreamingAssets\aa\StandaloneWindows64\gamescene_*.bundle`
- **핵심 연동 도구:**
  - `spine-hwa` (`H:\source\repos\spine hwa`): Spine 3.8.99 C++ 공식 런타임 직결 Rust 고속 뷰어 및 헤드리스 배치 렌더러 CLI (`sv-cli.exe`)
  - `AssetStudioModCLI` (`H:\AssetStudioMod.v0.19.0\cli\AssetStudioModCLI_net9_win64\AssetStudioModCLI.exe`): Unity 번들에서 TextAsset(.skel, .atlas)과 Texture2D(PNG) 고속 추출

---

## 2. 설계 의도 & 파이프라인 Rationale
1. **분절된 작업 흐름의 단일 GUI 통합**:
   - 기존에는 ① AssetStudio 수동 번들 언팩 -> ② `gamescene_추출`로 복사 -> ③ 콘솔 명령어로 `sv-cli` 옵션 타이핑 -> ④ 렌더된 몬스터 이미지를 BMS 에디터 각 폴더(`장면01/0105/0105_air.png`)로 수동 분류하는 번거로운 수작업이 반복됨.
   - `Spine Support Tool`은 이 전체 흐름을 탭 단위 워크플로우로 자동화하여 마우스 클릭 몇 번으로 최종 BMS 노트 스킨까지 생성되도록 함.
2. **Spine 3.8 스케일 0.5 표준화 Rationale**:
   - 뮤즈대쉬 원본 Spine 스켈레톤은 인게임 표시 및 Spine Pro 에디터 기준(0.5 배율)의 2배 크기로 되어 있음.
   - 따라서 몬스터 렌더 시 `--scale 0.5`가 적용되어야 인게임 및 BMS 에디터의 표준 크기와 정확히 픽셀 단위로 일치함.
3. **에셋 번들 평탄화(Flatten) 메커니즘**:
   - AssetStudio 추출 시 `TextAsset\`과 `Texture2D\`로 폴더가 분리되는 문제가 있음.
   - `BundleExtractorService`가 추출 직후 동일 폴더(`장면NN\원본`)로 파일들을 자동 이동시켜 `sv-cli`가 스켈레톤과 아틀라스를 즉시 1:1 매칭할 수 있도록 구성.
4. **BMS 에디터 규격 자동 구조화**:
   - 렌더된 `0105_air.png`, `0105_road.png` 등을 파일명의 장면 번호(01)와 몬스터 코드(0105)로 파싱하여 `장면01\0105\` 폴더로 자동 분류. 0101~0103번 보스 기물은 `장면01\보스\0101\` 구조로 자동 배치.

---

## 3. 핵심 모듈 구성
- **`SpineSupportTool/Services/SpineHwaService.cs`**:
  - `SpineViewer.exe` 및 `sv-cli.exe` 상태 감지 및 프로세스 실행
  - GitHub (`magjangin/spine-hwa`) 최신 릴리즈 ZIP 다운로드 및 자동 설치
  - 로컬 `dist.ps1` 파워셸 스크립트를 통한 Rust 소스 릴리즈 컴파일
  - `sv-cli frame <dir> --skin all --scale 0.5 --out <out>` 비동기 배치 렌더 실행
- **`SpineSupportTool/Services/BundleExtractorService.cs`**:
  - `StreamingAssets` 내 `gamescene_*.bundle` 자동 탐색
  - `AssetStudioModCLI`를 통한 선택 번들 일괄 추출 및 폴더 평탄화
- **`SpineSupportTool/Services/BmsSyncService.cs`**:
  - 렌더링된 이미지를 BMS 에디터 노트 스킨 트리(`장면NN\XXXX\`)로 자동 동기화
- **`SpineSupportTool/Services/ConfigService.cs`**:
  - `appsettings.json`을 통한 사용자 경로 설정 영구 보존

---

## 4. 빌드 및 배포

### 빌드 명령
```powershell
# 디버그 빌드
dotnet build SpineSupportTool\SpineSupportTool.csproj

# 단독 실행 Self-Contained SingleFile 릴리즈 배포
dotnet publish SpineSupportTool\SpineSupportTool.csproj -c Release -o publish
```

### 산출물 위치
- 단일 독립 실행 파일: `h:\source\repos\spine support tool\publish\SpineSupportTool.exe` (약 49.5MB, Self-Contained)
