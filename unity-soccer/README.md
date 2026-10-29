# 2인 미니 축구 (Unity 2D)

웹 버전(`../soccer.html`)을 유니티로 옮긴 버전입니다. 스크립트 파일 하나(`Assets/Scripts/SoccerGame.cs`)가
경기장, 선수, 공, 카메라, 점수판을 전부 코드로 만들기 때문에 씬에 아무것도 배치할 필요가 없습니다.

## 실행 방법

1. [Unity Hub](https://unity.com/download)를 설치하고, Unity 에디터를 설치합니다. Unity 6 또는 2022.3 LTS를 권장합니다.
2. Unity Hub에서 **New project** → **2D (Built-In Render Pipeline)** 템플릿을 고르고 프로젝트를 만듭니다.
   (**Universal 2D** 템플릿으로 만들어도 동작합니다.)
3. 이 폴더의 `Assets/Scripts/SoccerGame.cs` 파일을 유니티 프로젝트의 `Assets` 폴더 안으로 끌어다 놓습니다.
4. 에디터 위쪽의 ▶ (Play) 버튼을 누릅니다.
5. Game 창을 한 번 클릭한 뒤 **스페이스바**를 누르면 경기가 시작됩니다.

## 조작법

| | 이동 | 패스 | 슛 | 감아차기 |
|---|---|---|---|---|
| 1P 레드 (오른쪽 골대 공격) | W A S D | E | R | Q |
| 2P 블루 (왼쪽 골대 공격) | I J K L | O | P | U |

- 패스는 동료에게, 슛은 골키퍼가 없는 쪽 구석으로 자동 조준됩니다.
- 감아차기는 골대 바깥쪽으로 날아가다가 휘어서 구석으로 들어갑니다.
- 경기 시간은 3분이고, 끝나면 스페이스바로 다시 시작합니다.

## 실행 파일로 만들기

**File → Build Settings**(Unity 6은 **Build Profiles**)에서 플랫폼(Windows/Mac)을 고르고, **Add Open Scenes**를 누른 뒤 **Build**를 누르면 됩니다.

## 참고

- 게임 화면 글자는 영어로 되어 있습니다. 유니티 기본 글꼴에 한글이 없을 수 있어서입니다.
- 입력은 예전 Input Manager와 새 Input System 둘 다 지원합니다.
