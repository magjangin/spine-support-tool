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

### v1.1 실측 (2026-11-14, 장면01~06 + cy 11개 번들, 12스레드 CPU, 동시 6개)
| 단계 | v1.0 | v1.1 | 원인 / 해결 |
|---|---|---|---|
| 번들 추출 | 10.6초 | 3.1초 | 번들마다 AssetStudioModCLI를 순차 실행 → 장면 태그 단위 `Parallel.ForEachAsync` 병렬 실행 |
| 배치 렌더 | 98.8초 | 27.2초 | `sv-cli frame <폴더>`는 내부에서 스켈레톤마다 자식 프로세스를 **순차** 실행(supervise) → 툴이 스켈레톤 단위로 `sv-cli frame <파일> --root <입력루트>`를 직접 병렬 실행 |
| BMS 재동기화 | 145ms | 31ms | 매번 전체 덮어쓰기 → 크기+수정시각 동일 파일은 건너뜀 |
| 로그 출력량 | 157,915자 | 3,994자 | sv-cli의 최종 결과 JSON 한 줄(수백 KB)을 로그에 통째로 출력 → JSON 파싱 후 요약/실패 사유만 출력 |
- 렌더/BMS 출력은 v1.0과 **바이트 단위 동일**함을 확인. 추출 결과도 파일 집합은 동일(아래 Dead Ends의 `#ID` 접미사 참고).
- 동시 작업 수는 `AppConfig.MaxParallelJobs`(설정 탭 "동시 작업 수", 0=자동=코어 수/2, 1~6)로 조절.

### 구조적 개선
1. **UI 스로틀 (로그 + 진행률)**: 서비스 이벤트는 백그라운드 스레드에서 `ConcurrentQueue`(로그)와 "최신 진행률 1개" 슬롯에만 기록하고, `DispatcherTimer` 100ms 틱에서 한 번에 반영. v1.0은 다운로드 80KB 청크마다 `Dispatcher.Post`를 쏴서 UI 큐가 포화됐음. 로그는 1,000줄 링 버퍼 + 한 줄 2,000자 캡.
2. **`ProcessRunner` 공통화**: 외부 CLI 4곳(AssetStudio, sv-cli 렌더/도움말, dist.ps1)의 실행 코드를 통합. stdout/stderr 동시 비동기 소비(파이프 교착 방지), 취소 토큰에 `Kill(entireProcessTree)`를 **동기 등록**해 창 닫기·취소 즉시 고아 프로세스 정리, 선택적 타임아웃.
3. **`RunTaskAsync` 공통화 (MainWindow)**: 버튼 잠금, CTS 수명, 취소/예외 처리, 상태 표시를 한 곳에서 처리. v1.0은 핸들러 6곳에 복붙돼 있었고 렌더/빌드/다운로드는 취소를 "실패"로 표시하던 불일치가 있었음.
4. **UI 스레드 차단 제거**: 서비스는 `ConfigureAwait(false)` + 대용량 열거/ZIP 해제/평탄화를 스레드 풀에서 실행. (v1.0은 await 뒤 연속 실행이 UI 스레드로 돌아와 평탄화·ZIP 해제·스켈레톤 열거 중 UI가 멈췄음)
5. **설정 안정성**: 손상된 `appsettings.json`은 `.bak`으로 백업 후 기본값 시작(v1.0은 조용히 덮어씀). 저장은 임시 파일 → 교체(원자적). 저장 실패 사유를 로그에 표시.
6. **창 종료 시 작업 취소**: `Closing`에서 CTS 취소 → 실행 중 sv-cli/AssetStudio 프로세스 트리 종료.
7. **기타**: `[GeneratedRegex]` 소스 생성 정규식, `ServiceBase`(로그/진행률 이벤트 공통), GitHub URL 파싱 강화(`.git` 접미사, `owner/repo` 축약형), sv-cli 테스트에 10초 타임아웃, 배포본 `PublishReadyToRun`으로 시작 속도 개선.

## 4. 핵심 모듈 구성
- **`Services/ServiceBase.cs`**: `LogReceived` / `ProgressChanged` 이벤트 공통 베이스. 이벤트는 백그라운드 스레드에서 올 수 있음.
- **`Services/ProcessRunner.cs`**: 외부 프로세스 실행 공통 헬퍼(출력 수집, 취소 시 트리 종료, 타임아웃, 출력 인코딩). sv-cli는 UTF-8, AssetStudio/PowerShell은 시스템 기본 인코딩.
- **`Services/SpineHwaService.cs`**:
  - `SpineViewer.exe` / `sv-cli.exe` 상태 감지 및 실행, `--help` 테스트
  - GitHub (`magjangin/spine-hwa`) 최신 릴리즈 ZIP 다운로드(1% 단위 진행률) 및 설치
  - 로컬 `dist.ps1` 파워셸 스크립트를 통한 Rust 소스 릴리즈 컴파일
  - 배치 렌더: 스켈레톤(.skel/.json)마다 `sv-cli frame <파일> --root <입력루트> --skin all --scale 0.5 --out <out>` 병렬 실행, 마지막 줄 JSON(`{ok,count,results,skipped}`)을 `ParseCliSummary`로 요약
- **`Services/BundleExtractorService.cs`**:
  - `StreamingAssets` 내 `gamescene_*.bundle` 자동 탐색
  - `AssetStudioModCLI`로 장면 태그 단위 병렬 추출, 폴더 평탄화 및 빈 폴더 자동 정리
- **`Services/BmsSyncService.cs`**:
  - 렌더 PNG를 BMS 노트 스킨 트리(`장면NN\NNMM\`, 01~03번은 `장면NN\보스\NNMM\`)로 증분 동기화. 소스/대상 폴더가 포함 관계면 거부.
- **`Services/ConfigService.cs`**: `appsettings.json`(실행 파일 옆) 로드/원자적 저장, 손상 시 `.bak` 백업.

### sv-cli 연동 규약 (spine-hwa `crates/spine-viewer-core/src/headless.rs` 기준)
- 폴더 입력 시 하위 `.skel`/`.json`을 재귀 수집하고, 2개 이상이면 자기 자신을 스켈레톤마다 자식 프로세스로 **순차** 실행(크래시 격리).
- 진행 로그 없이 **마지막 줄에 JSON 한 덩어리**만 stdout으로 출력. 성공 시 종료 코드 0, `ok:false`면 1.
- 출력 경로는 `--root` 기준 상대 폴더 구조를 `--out` 아래에 그대로 따라감. 파일명: 스킨 있으면 `{stem}__{skin}.png`, 없으면 `{stem}.png`.
- 사용법(USAGE)은 stderr로 출력.

## 5. 시도했다가 실패한 것들 (Dead Ends / Failed Attempts)
- **AssetStudio C# 라이브러리 직접 참조 방식**:
  - AssetStudio 원본 DLL을 Avalonia 프로젝트에 직접 참조하여 인메모리로 에셋을 추출하려 했으나, 최신 Unity LZ4 압축 알고리즘 호환성 및 .NET 10과의 의존성 충돌 발생.
  - **결론:** 검증된 네이티브 성능의 `AssetStudioModCLI.exe`를 비동기 프로세스로 호출하고 폴더를 평탄화하는 파이프라인이 압도적으로 안정적이고 유지보수에 용이함.
- **렌더링 로그 실시간 직접 텍스트 블록 바인딩**:
  - `proc.OutputDataReceived`마다 UI 스레드로 텍스트박스 전체를 갱신하던 방식은 대량 파일 처리 시 수만 줄의 문자열 인스턴스를 생성해 GC 스파이크와 UI 프리징 유발.
  - **결론:** 100ms 배치 타이머 플러시 + 링 버퍼(1,000줄 캡) 구조로 완전히 해결함.
- **sv-cli 폴더 입력 그대로 사용 (v1.0)**:
  - 폴더 하나만 넘기면 sv-cli 내부 supervise가 스켈레톤을 순차 처리해 CPU가 놀고, 진행 로그도 없어서 진행률이 사실상 동작하지 않았음("saved/frame/png/ok" 문자열 매칭 추정은 최종 JSON 1줄에만 걸림).
  - **결론:** 툴에서 스켈레톤 단위로 쪼개 `--root`와 함께 병렬 호출. 출력 구조/내용은 동일.
- **추출 결과 `#ID` 접미사 차이는 툴 문제가 아님**:
  - 같은 번들 안에 이름이 같은 텍스처(예: 장면02 `S02_word`)가 둘 있으면 AssetStudioModCLI가 한쪽에 `_#<PathID>`를 붙이는데, 어느 쪽이 접미사를 받는지는 실행마다 달라질 수 있음(단일 번들·단일 프로세스에서도 발생). 파일 집합과 내용은 동일.

---

## 6. 빌드 및 배포

### 빌드 명령
```powershell
# 디버그 빌드
dotnet build SpineSupportTool\SpineSupportTool.csproj

# 단독 실행 Self-Contained SingleFile 릴리즈 배포
dotnet publish SpineSupportTool\SpineSupportTool.csproj -c Release -o publish
```

### 성능/회귀 검증 방법
- 서비스(`Models/`, `Services/`)는 Avalonia 의존성이 없으므로, 콘솔 하네스 프로젝트에서 `<Compile Include="...\Services\*.cs" />`로 링크해 실제 번들로 추출→렌더→BMS 동기화를 돌리고 이전 버전 출력과 `diff -rq`로 비교한다.
- 실측 환경: 게임 `H:\muse dash hwa`, AssetStudioModCLI v0.19.0, `H:\source\repos\spine hwa\bin\sv-cli.exe`.

### 산출물 위치
- 단일 독립 실행 파일: `h:\source\repos\spine support tool\publish\SpineSupportTool.exe` (Self-Contained)
