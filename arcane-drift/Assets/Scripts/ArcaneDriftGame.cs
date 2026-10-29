#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
#define USE_NEW_INPUT
#endif

using System.Collections.Generic;
using UnityEngine;
#if USE_NEW_INPUT
using UnityEngine.InputSystem;
#endif

namespace ArcaneDrift
{
    // 이 파일 하나를 Assets 아래 아무 곳에 넣고 ▶ 를 누르면 된다.
    // 씬에 아무것도 배치할 필요 없이 카메라·플레이어·배경을 코드가 직접 만든다.
    // 진행 단계: STEP 1 (우클릭 이동 + 카메라 + 무한 격자 배경)
    //
    // MonoBehaviour 는 이 클래스 하나뿐이다. 플레이어·클릭 표시 같은 나머지는 일반 C# 클래스로 두고
    // 여기 Update 에서 움직인다. (Unity는 MonoBehaviour 마다 같은 이름의 파일을 요구하기 때문)
    public class ArcaneDriftGame : MonoBehaviour
    {
        // ----- 원본 수치 (px ÷ 100) -----
        const float PlayerRadius = 0.15f;
        const float PlayerSpeed = 2.5f;
        const float CameraSize = 4.5f;

        // ----- 원본 arcane-drift.html 을 확인하지 못해 임시로 정한 수치 -----
        const float MarkerLife = 0.35f;
        const float MarkerSize = 0.25f;
        const float GridCell = 1f;
        const float GridExtent = 40f;

        static readonly Color PlayerColor = Hex(0xC9B6FF);
        static readonly Color MarkerColor = Hex(0x6BD5FF);
        static readonly Color GroundColor = Hex(0x1A1626);
        static readonly Color GridLineColor = Hex(0x2A243D);

        static ArcaneDriftGame instance;

        Camera cam;
        Player player;
        Transform grid;
        readonly List<ClickMarker> markers = new List<ClickMarker>();

        // 씬이 열리면 자동으로 게임 오브젝트를 만든다
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (instance == null) new GameObject("ArcaneDrift").AddComponent<ArcaneDriftGame>();
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;

            Art.Init();
            cam = SetupCamera();
            grid = Art.CreateGrid(transform, GridCell, GridExtent, GroundColor, GridLineColor);
            player = new Player(Art.CreateCircle("Player", transform, PlayerColor, PlayerRadius * 2, 10), PlayerRadius, PlayerSpeed);
        }

        void Update()
        {
            float dt = Time.deltaTime;

            if (GameInput.RightClickDown())
            {
                Vector2 point = CursorWorld();
                player.MoveTo(point);
                var marker = Art.CreateCircle("ClickMarker", transform, MarkerColor, MarkerSize, 5);
                markers.Add(new ClickMarker(marker, point, MarkerLife, MarkerSize));
            }

            player.Tick(dt);
            for (int i = markers.Count - 1; i >= 0; i--)
                if (!markers[i].Tick(dt)) markers.RemoveAt(i);
        }

        // 카메라와 배경은 플레이어가 움직인 뒤(LateUpdate)에 따라가야 떨리지 않는다
        void LateUpdate()
        {
            Vector2 p = player.Position;
            cam.transform.position = new Vector3(p.x, p.y, -10f);
            // 배경 판은 칸 크기 단위로만 옮겨서, 격자 선이 바닥에 고정된 것처럼 보이게 한다
            grid.position = new Vector3(Mathf.Round(p.x / GridCell) * GridCell, Mathf.Round(p.y / GridCell) * GridCell, 0f);
        }

        void OnGUI()
        {
            GUI.Label(new Rect(12, 8, 400, 24), "STEP 1  ·  Right-click to move");
        }

        Camera SetupCamera()
        {
            var c = Camera.main;
            if (c == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                c = go.AddComponent<Camera>();
            }
            c.orthographic = true;
            c.orthographicSize = CameraSize;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = GroundColor;
            c.transform.rotation = Quaternion.identity;
            return c;
        }

        public Vector2 CursorWorld()
        {
            Vector3 screen = GameInput.MousePosition();
            screen.z = -cam.transform.position.z;
            return cam.ScreenToWorldPoint(screen);
        }

        static Color Hex(int rgb) =>
            new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);
    }

    // ---------------------------------------------------------------- 플레이어

    // 우클릭한 지점으로 이동. 물리(Rigidbody2D) 없이 위치를 직접 옮긴다.
    // STEP 3에서 "적 위 우클릭 = 추격 + 자동 평타"가 여기에 추가된다.
    class Player
    {
        public readonly Transform Tf;
        public readonly float Radius;
        public readonly float Speed;

        public Vector2 Position { get; private set; }
        public Vector2 Facing { get; private set; } = Vector2.right;
        public bool IsMoving => moveTarget.HasValue;

        Vector2? moveTarget;

        public Player(Transform tf, float radius, float speed)
        {
            Tf = tf; Radius = radius; Speed = speed;
        }

        public void MoveTo(Vector2 point) => moveTarget = point;
        public void ClearMoveTarget() => moveTarget = null;

        public void Tick(float dt)
        {
            if (moveTarget.HasValue)
            {
                Vector2 toTarget = moveTarget.Value - Position;
                float dist = toTarget.magnitude;
                float step = Speed * dt;
                if (dist <= step)
                {
                    Position = moveTarget.Value;
                    moveTarget = null;
                }
                else
                {
                    Facing = toTarget / dist;
                    Position += Facing * step;
                }
            }
            Tf.position = new Vector3(Position.x, Position.y, 0f);
        }
    }

    // ---------------------------------------------------------------- 클릭 표시

    // 우클릭 지점에 잠깐 나타났다가 작아지며 사라지는 표시
    class ClickMarker
    {
        readonly Transform tf;
        readonly SpriteRenderer sr;
        readonly float life, size;
        float age;

        public ClickMarker(Transform tf, Vector2 at, float life, float size)
        {
            this.tf = tf; this.life = life; this.size = size;
            sr = tf.GetComponent<SpriteRenderer>();
            tf.position = new Vector3(at.x, at.y, 0f);
        }

        // 수명이 끝나면 false 를 돌려주고 스스로 사라진다
        public bool Tick(float dt)
        {
            age += dt;
            float k = age / life;
            if (k >= 1f) { Object.Destroy(tf.gameObject); return false; }
            tf.localScale = Vector3.one * size * (1f - k);
            Color c = sr.color; c.a = 1f - k; sr.color = c;
            return true;
        }
    }

    // ---------------------------------------------------------------- 입력

    // 프로젝트의 입력 설정(Old / New Input System / Both)이 무엇이든 똑같이 동작하게 감싼 헬퍼
    static class GameInput
    {
        public static bool RightClickDown()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(1);
#endif
        }

        public static Vector2 MousePosition()
        {
#if USE_NEW_INPUT
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
    }

    // ---------------------------------------------------------------- 그림

    // 에셋 파일 없이 흰 원·격자 그림을 코드로 만든다. 색은 SpriteRenderer.color 로 입힌다.
    // 미션 3(그래픽 적용)에서 이 부분을 실제 에셋으로 바꾼다.
    static class Art
    {
        public static Sprite Circle { get; private set; }
        static Material spriteMaterial;   // URP 프로젝트일 때만 쓰는 조명 없는 재질 (2D 조명 없이도 보이게)

        public static void Init()
        {
            var unlit = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            spriteMaterial = unlit != null ? new Material(unlit) : null;
            Circle = MakeCircle(128);
        }

        public static SpriteRenderer CreateSprite(string name, Transform parent, Sprite sprite, Color color, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (spriteMaterial != null) sr.sharedMaterial = spriteMaterial;
            return sr;
        }

        // 지름(diameter) 유닛짜리 원
        public static Transform CreateCircle(string name, Transform parent, Color color, float diameter, int order)
        {
            var sr = CreateSprite(name, parent, Circle, color, order);
            sr.transform.localScale = Vector3.one * diameter;
            return sr.transform;
        }

        // extent×extent 유닛 크기로 깔리는 격자 배경
        public static Transform CreateGrid(Transform parent, float cell, float extent, Color ground, Color line)
        {
            var sr = CreateSprite("Grid", parent, MakeGridCell(cell, ground, line), Color.white, -10);
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.size = new Vector2(extent, extent);
            return sr.transform;
        }

        // 지름 1유닛짜리 흰 원 (가장자리를 부드럽게)
        static Sprite MakeCircle(int px)
        {
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[px * px];
            float c = (px - 1) / 2f, r = px / 2f - 1;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    byte a = (byte)(Mathf.Clamp01(r - d + 0.5f) * 255);
                    pixels[y * px + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px);
        }

        // 한 칸(cell 유닛)짜리 그림: 배경색 위에 왼쪽·아래쪽 테두리 선
        static Sprite MakeGridCell(float cell, Color ground, Color line)
        {
            const int px = 64;
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                    pixels[y * px + x] = (x < 2 || y < 2) ? line : ground;
            tex.SetPixels(pixels);
            tex.Apply();
            // Tiled 그리기 모드는 Full Rect 메시가 필요하다
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px / cell, 0, SpriteMeshType.FullRect);
        }
    }
}
