# Arcane Drift — Unity 2D 포팅

Beatus 2026 GMC 제출용 탑다운 액션 게임. HTML 프로토타입(`arcane-drift.html`)을 Unity 2D로 포팅 중.

작업 배경, 대회 조건, 설계 결정, 단위 변환표, 진행 상황 전체는 [`CLAUDE.md`](./CLAUDE.md) 참고.

## 프로젝트 열기

1. Unity Hub → **열기(Add)** → 이 저장소 폴더 선택
2. Unity 버전은 `ProjectSettings/ProjectVersion.txt`에 적힌 **2022.3 LTS** 계열 사용 (없으면 Hub에서 해당 버전 설치)
3. 처음 열면 Library 폴더를 새로 만드느라 시간이 좀 걸림 (정상)

## STEP 1 — 우클릭 이동 + 카메라 추적

`Assets/Scripts/`
- `PlayerController.cs` — 우클릭 이동, `CursorWorld()`, `IsMoving`, `Facing`, `ClearMoveTarget()`
- `CameraFollow.cs` — LateUpdate에서 target을 그대로 추적
- `ClickMarker.cs` — 우클릭 지점 표시, 작아지며 사라짐
- `ScrollingGrid.cs` — 배경 타일을 격자 단위로 스냅해서 무한 배경처럼 보이게 함

### 씬 설정 (Unity 에디터에서 직접 할 것)

1. **Player**
   - 빈 GameObject 생성, 이름 `Player`
   - `Sprite Renderer` 추가 → Sprite: 기본 원(Circle) 스프라이트, Color `#C9B6FF`
   - `Transform` Scale `0.3, 0.3, 1`
   - `PlayerController` 컴포넌트 추가
     - `Cam` 필드에 `Main Camera` 드래그
     - `Click Marker Prefab` 필드는 3번에서 만든 프리팹을 아래에서 연결

2. **Main Camera**
   - Projection: **Orthographic**, Size: **4.5**
   - `CameraFollow` 컴포넌트 추가 → `Target` 필드에 `Player` 드래그

3. **ClickMarker 프리팹**
   - 빈 GameObject 생성 → `Sprite Renderer`(원 스프라이트, Color `#6BD5FF`) + `ClickMarker` 컴포넌트 추가
   - `Assets/Prefabs/` 폴더로 드래그해서 프리팹으로 만들고, 씬에 남은 원본은 삭제
   - `Player`의 `PlayerController` → `Click Marker Prefab` 필드에 이 프리팹 연결

4. **Grid (배경)**
   - 빈 GameObject 생성, 이름 `Grid`
   - `Sprite Renderer` 추가 → Sprite: 기본 정사각형(Square), Draw Mode: **Tiled**, Size **40 × 40**, Color `#1A1626`
   - `Order in Layer` **-10** (플레이어보다 뒤에 그려지도록)
   - `ScrollingGrid` 컴포넌트 추가 → `Target` 필드에 `Player` 드래그

5. 씬 저장 (`Assets/Scenes/Main.unity` 권장) 후 Play 버튼으로 확인:
   - 우클릭한 지점으로 플레이어가 이동
   - 클릭 지점에 마커가 나타났다가 작아지며 사라짐
   - 카메라가 플레이어를 따라다님
   - 배경 격자가 무한한 것처럼 스크롤됨

확인되면 STEP 2(적 3종 + 추격 + 스폰 + 풀링)로 진행.
