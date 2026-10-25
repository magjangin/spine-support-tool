# Spine Support Tool (뮤즈대쉬 & Spine 3.8 자동화 워크스테이션)

> **뮤즈대쉬 `gamescene` 에셋 번들 추출부터 Spine 3.8 몬스터/노트 배치 렌더링, BMS 커스텀 차트 에디터 노트 스킨 규격 동기화까지 원클릭으로 처리하는 Avalonia UI 데스크톱 도구**

[![GitHub Release](https://img.shields.io/github/v/release/magjangin/spine-support-tool?color=purple)](https://github.com/magjangin/spine-support-tool/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](https://github.com/magjangin/spine-support-tool)
[![Target Framework](https://img.shields.io/badge/.NET-10.0-512bd4.svg)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/UI-Avalonia_12.1.3-purple.svg)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## 📌 프로젝트 소개

**Spine Support Tool**은 리듬 게임 *뮤즈대쉬(Muse Dash)*의 Spine 3.8 에셋을 BMS 커스텀 차트 에디터 및 2차 창작 환경에 간편하게 도입할 수 있도록 설계된 통합 워크스테이션 GUI 애플리케이션입니다.

기존의 번거로웠던 4단계 수작업 흐름을 하나의 직관적인 탭 기반 파이프라인으로 일원화했습니다:
```text
[Muse Dash StreamingAssets]
            │
            ▼ (1. 번들 자동 감지 & AssetStudioModCLI 고속 추출)
[gamescene 번들 평탄화 (장면NN/원본)]
            │
            ▼ (2. spine-hwa sv-cli.exe --scale 0.5 헤드리스 배치 렌더)
[알파 투명 PNG 몬스터/노트 프레임 (52종+)]
            │
            ▼ (3. BMS 차트 에디터 규격 파싱 & 자동 분류)
[BMS 노트 스킨 폴더 (장면NN\XXXX\XXXX_air.png / 보스 기물 구조)]
```

---

## ✨ 핵심 기능

### 1. `spine-hwa` 통합 관리
- **원클릭 자동 설치**: GitHub 최신 릴리즈([magjangin/spine-hwa](https://github.com/magjangin/spine-hwa)) ZIP 에셋을 비동기 스트리밍 다운로드하여 자동 압축 해제 및 경로 연결.
- **로컬 Rust 릴리즈 빌드**: 로컬 소스(`Cargo.toml`, `dist.ps1`) 감지 시 PowerShell 프로세스를 통해 Rust 릴리즈 컴파일을 원클릭으로 수행.
- **뷰어 직결 실행**: `SpineViewer.exe`를 단독 실행하거나 지정된 스켈레톤 파일을 뷰어에 바로 오픈.

### 2. 뮤즈대쉬 에셋 번들 추출 (`BundleExtractorService`)
- 뮤즈대쉬 설치 폴더 내 `StreamingAssets/aa/StandaloneWindows64/gamescene_*.bundle` 자동 탐색.
- 장면 01~12 빠른 선택 및 전체 선택/해제 지원.
- `AssetStudioModCLI`를 백그라운드로 구동하여 `.skel`, `.atlas`, `.png` 에셋을 고속 추출.
- **자동 평탄화(Flatten)**: AssetStudio 특유의 `TextAsset\`, `Texture2D\` 분리 폴더 구조를 감지하여 동일 디렉터리로 즉시 일원화, `sv-cli`가 1:1 아틀라스를 즉시 매칭하도록 정돈.

### 3. 몬스터/노트 배치 렌더러 (`SpineHwaService`)
- `sv-cli frame <dir> --skin all --scale 0.5 --out <out>` 헤드리스 렌더 파이프라인 직결.
- **표준 0.5 배율 최적화**: 원본 Spine 스켈레톤의 2배율 상태를 인게임 및 BMS 에디터 규격에 정확히 맞춘 `--scale 0.5` 자동 적용.
- 실시간 콘솔 로그 스트리밍 및 진행률 프로그레스 바 제공.

### 4. BMS 에디터 규격 자동 동기화 (`BmsSyncService`)
- 렌더된 이미지 파일명(`0105_air.png`, `0105_road.png` 등)을 파싱하여 BMS 에디터 노트 스킨 트리(`장면01\0105\`)로 자동 복사 및 분류.
- 0101~0103번 보스 기물은 `장면01\보스\0101\` 계층 구조로 지능적 자동 배치.

### 5. 영구 환경 설정 (`ConfigService`)
- 게임 경로, `spine-hwa` 도구 경로, AssetStudio CLI 경로, 작업 폴더 경로 등을 GUI에서 간편하게 지정하고 `appsettings.json`에 영구 보존.

---

## 🚀 빠른 시작 (바이너리 다운로드)

1. [GitHub Releases](https://github.com/magjangin/spine-support-tool/releases)에서 최신 `SpineSupportTool-v*.zip`을 다운로드합니다.
2. 압축을 해제하고 `SpineSupportTool.exe`를 실행합니다 (.NET 런타임이 포함된 Self-Contained 단일 실행 파일이므로 별도 런타임 설치 불필요).
3. **⚙️ 환경 설정** 탭에서 Muse Dash 설치 경로 및 연동 툴 경로를 확인/저장합니다.
4. **📦 번들 추출** ➔ **🎨 몬스터 렌더** ➔ **🎵 BMS 동기화** 순으로 원클릭 파이프라인을 실행합니다.

---

## 🛠️ 소스 빌드 가이드

### 사전 요구사항
- Windows 10/11 x64
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)

### 빌드 및 배포

```powershell
# 저장소 클론
git clone https://github.com/magjangin/spine-support-tool.git
cd spine-support-tool

# 디버그 빌드
dotnet build SpineSupportTool/SpineSupportTool.csproj

# 단독 실행 Self-Contained SingleFile 릴리즈 배포
dotnet publish SpineSupportTool/SpineSupportTool.csproj -c Release -o publish
```

빌드가 완료되면 `publish/SpineSupportTool.exe` 단독 실행 파일이 생성됩니다.

---

## 🔗 연동 프로젝트 및 라이브러리

- **[spine-hwa](https://github.com/magjangin/spine-hwa)**: Spine 3.8.99 C++ 공식 런타임 직결 Rust 고속 뷰어 및 배치 렌더러 CLI (`sv-cli`)
- **[AssetStudioModCLI](https://github.com/Sterbenz/AssetStudioModCLI)**: Unity 에셋 번들 고속 CLI 언팩 도구
- **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia)**: 크로스 플랫폼 데스크톱 XAML UI 프레임워크

---

## 📄 라이선스

본 프로젝트는 [MIT License](LICENSE)에 따라 자유롭게 사용, 수정 및 재배포할 수 있습니다.
뮤즈대쉬(Muse Dash)의 오리지널 에셋 및 Spine 런타임 저작권은 각 원저작권자에게 있습니다.
