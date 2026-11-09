# Spine Support Tool 개발 및 아키텍처 인수인계 문서 (`AGENTS.md`)

## 1. 프로젝트 개요 & 타깃 환경
- **프로젝트명:** Spine Support Tool
- **목적:** 뮤즈대쉬(Muse Dash)의 `gamescene_*.bundle` 에셋 번들 추출, `spine-hwa` (`SpineViewer.exe` / `sv-cli.exe`)를 통한 Spine 3.8 몬스터/노트 배치 렌더링, BMS 커스텀 차트 에디터 노트 스킨 규격 동기화를 원클릭으로 지원하는 Avalonia UI 데스크톱 도구.
- **대상 게임:** Muse Dash (Unity / Il2Cpp)
  - 게임 경로: `H:\muse dash hwa` (또는 사용자 환경 커스텀 지정)
  - 에셋 번들 경로: `MuseDash_Data\StreamingAssets\aa\StandaloneWindows64\gamescene_*.bundle`
  - Steam Build ID / 버전 관리: Steam 다운그레이드 및 `.acf` 자동 업데이트 차단 환경 준수.
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
   - 파일 이동 후 빈 디렉터리는 자동으로 안전하게 정리(삭제)하여 디스크 오염 방지.
4. **BMS 에디터 규격 자동 구조화**:
   - 렌더된 `0105_air.png`, `0105_road.png` 등을 파일명의 장면 번호(01)와 몬스터 코드(0105)로 파싱하여 `장면01\0105\` 폴더로 자동 분류. 0101~0103번 보스 기물은 `장면01\보스\0101\` 구조로 자동 배치.

---

## 3. 핵심 병목 최적화 & 안정성 강화 내역
1. **UI 렌더링 병목 해소 (스마트 스로틀 로그 플러시)**:
   - **문제점:** 수백 개의 스켈레톤 파일이 연속으로 렌더링될 때 매 라인마다 전체 콘솔 텍스트를 `ToString()`으로 재할당하여 UI 스레드가 멈추거나 렉이 발생함.
   - **해결책:** `ConcurrentQueue<string>` 기반 비동기 로그 수집과 `DispatcherTimer` 100ms 간격 배치 Flush 적용.
   - 최대 보관 라인 수를 1,000줄(`MaxLogLines`)로 제한하여 메모리 무한 누수 및 렌더링 지연을 완벽 차단.
2. **비동기 프로세스 트리 강제 취소 (`CancellationToken`)**:
   - 추출(AssetStudio) 및 배치 렌더(sv-cli), 로컬 빌드 중 사용자가 언제든지 중단할 수 있도록 상태 표시줄에 `🛑 작업 취소` 버튼 탑재.
   - 토큰 취소 시 등록된 `proc.Kill(entireProcessTree: true)` 콜백을 통해 하위 프로세스까지 완전히 정리되어 고아 프로세스 잔류 방지.
3. **정규식 컴파일 캐싱**:
   - `RegexOptions.Compiled`를 적용한 `MonsterNameRegex`, `SceneNumRegex`, `GeneralSceneRegex` 정적 인스턴스를 캐싱하여 대량 파일 파싱 시 오버헤드 최소화.
4. **중복 클릭 방지 & Busy 상태 일괄 제어**:
   - 작업 시작 시 관련 버튼을 일괄 비활성화하여 리소스 경합 및 동시 실행 충돌 방지.
5. **환경 설정 원클릭 탐색**:
   - 5대 핵심 경로(뮤즈대쉬, CLI, spine-hwa, 작업 폴더, BMS 스킨)에 폴더/파일 찾아보기 다이얼로그(`PickFolderAsync`, `PickFileAsync`)를 탑재하여 오타 및 설정 불편 완전 해결.

---

## 4. 핵심 모듈 구성
- **`SpineSupportTool/Services/SpineHwaService.cs`**:
  - `SpineViewer.exe` 및 `sv-cli.exe` 상태 감지 및 프로세스 실행
  - GitHub (`magjangin/spine-hwa`) 최신 릴리즈 ZIP 비동기 스트리밍 다운로드 및 자동 설치
  - 로컬 `dist.ps1` 파워셸 스크립트를 통한 Rust 소스 릴리즈 컴파일
  - `sv-cli frame <dir> --skin all --scale 0.5 --out <out>` 비동기 배치 렌더 실행 (프로세스 트리 취소 지원)
- **`SpineSupportTool/Services/BundleExtractorService.cs`**:
  - `StreamingAssets` 내 `gamescene_*.bundle` 자동 탐색
  - `AssetStudioModCLI`를 통한 선택 번들 일괄 추출, 폴더 평탄화 및 빈 폴더 자동 정리
- **`SpineSupportTool/Services/BmsSyncService.cs`**:
  - 렌더링된 이미지를 BMS 에디터 노트 스킨 트리(`장면NN\XXXX\`)로 고속 자동 동기화
- **`SpineSupportTool/Services/ConfigService.cs`**:
  - `appsettings.json`을 통한 사용자 경로 설정 영구 보존

---

## 5. 시도했다가 실패한 것들 (Dead Ends / Failed Attempts)
- **AssetStudio C# 라이브러리 직접 참조 방식**:
  - AssetStudio 원본 DLL을 Avalonia 프로젝트에 직접 참조하여 인메모리로 에셋을 추출하려 했으나, 최신 Unity LZ4 압축 알고리즘 호환성 및 .NET 10과의 의존성 충돌 발생.
  - **결론:** 검증된 네이티브 성능의 `AssetStudioModCLI.exe`를 비동기 프로세스로 호출하고 폴더를 평탄화하는 파이프라인이 압도적으로 안정적이고 유지보수에 용이함.
- **렌더링 로그 실시간 직접 텍스트 블록 바인딩**:
  - `proc.OutputDataReceived`마다 UI 스레드로 텍스트박스 전체를 갱신하던 방식은 대량 파일 처리 시 수만 줄의 문자열 인스턴스를 생성해 GC 스파이크와 UI 프리징 유발.
  - **결론:** 100ms 배치 타이머 플러시 + 링 버퍼(1,000줄 캡) 구조로 완전히 해결함.

---

## 6. 빌드 및 배포

### 빌드 명령
```powershell
# 디버그 빌드
dotnet build SpineSupportTool\SpineSupportTool.csproj

# 단독 실행 Self-Contained SingleFile 릴리즈 배포
dotnet publish SpineSupportTool\SpineSupportTool.csproj -c Release -o publish
```

### 산출물 위치
- 단일 독립 실행 파일: `h:\source\repos\spine support tool\publish\SpineSupportTool.exe` (Self-Contained)
