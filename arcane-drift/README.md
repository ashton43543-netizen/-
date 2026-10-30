# Arcane Drift (Unity 2D · 무한 모드)

LoL식 우클릭 조작 탑다운 액션. 몰려오는 적을 잡고, 레벨업마다 증강 카드를 골라 강해지며 최대한 오래 버티는 게임입니다.
설계와 진행 상황은 [`CLAUDE.md`](CLAUDE.md)에 있습니다.

## 실행 방법

1. Unity Hub에서 **New project → 2D (Built-In Render Pipeline)** 로 새 프로젝트를 만듭니다. (Unity 6 또는 2022.3 LTS)
2. [`Assets/Scripts/ArcaneDriftGame.cs`](Assets/Scripts/ArcaneDriftGame.cs) 를 내려받아 프로젝트의 **Assets** 폴더에 넣습니다.
3. ▶ 를 누르고 **Game 창을 한 번 클릭**한 뒤 "시작하기"를 누릅니다.

씬에 아무것도 배치할 필요가 없습니다. 카메라·플레이어·배경·UI를 코드가 직접 만듭니다.

## 조작

| 입력 | 동작 |
|---|---|
| 빈 땅 우클릭 (누르고 있으면 계속) | 이동 |
| 적 우클릭 | 그 적을 추격하며 자동 공격 |
| Q / W / E | 아케인 볼트 / 공허의 파동 / 섬광 돌진 (레벨업 카드로 배움) |
| 1~4 또는 클릭 | 증강 카드 선택 |
| Esc | 일시정지 |

## .exe 만들기 (제출용)

1. **File → Build Profiles** (2022.3은 **File → Build Settings**) 를 엽니다.
2. 플랫폼에서 **Windows** 를 고릅니다. 없으면 Unity Hub → Installs → 톱니바퀴 → Add modules 에서 **Windows Build Support** 를 설치합니다.
3. **Add Open Scenes** 를 눌러 지금 씬을 넣습니다.
4. **Build** 를 누르고 빈 폴더를 고릅니다. 그 폴더 전체(.exe 와 `_Data` 폴더)를 함께 제출해야 실행됩니다.

## 수치 바꾸기

모든 수치는 파일 아래쪽 `Tune` 에 모여 있습니다. `[임시]` 표시는 원본 프로토타입을 확인하지 못해 정한 값입니다.
