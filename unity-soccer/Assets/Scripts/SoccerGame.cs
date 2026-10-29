#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
#define USE_NEW_INPUT
#endif

using System.Collections.Generic;
using UnityEngine;
#if USE_NEW_INPUT
using UnityEngine.InputSystem;
#endif

// 2-player 3v3 mini soccer, ported from soccer.html.
// Drop this file anywhere under Assets and press Play: it builds the pitch, players and camera by itself.
// Game logic runs in the same "pixel" space as the web version (960x600, y pointing down)
// and is only converted to world units when drawing.
public class SoccerGame : MonoBehaviour
{
    const float W = 960, H = 600;
    const float L = 50, R = W - 50, T = 40, B = H - 40;            // touchlines
    const float GT = H / 2 - 70, GB = H / 2 + 70, GD = 32;          // goal mouth and depth
    const float PR = 15, BR = 7, MATCH = 180;
    const float PX = 40f;                                           // pixels per world unit

    static readonly Color Red = new Color(1f, .353f, .306f), RedGK = new Color(1f, .65f, .306f);
    static readonly Color Blue = new Color(.306f, .659f, 1f), BlueGK = new Color(.494f, .878f, .784f);
    static readonly Color Ink = new Color(.05f, .09f, .07f);
    static readonly Color Gold = new Color(1f, .82f, .4f);

    struct Controls { public KeyCode up, left, down, right, pass, shoot, curl; }
    static readonly Controls[] KEYS =
    {
        new Controls { up = KeyCode.W, left = KeyCode.A, down = KeyCode.S, right = KeyCode.D, pass = KeyCode.E, shoot = KeyCode.R, curl = KeyCode.Q },
        new Controls { up = KeyCode.I, left = KeyCode.J, down = KeyCode.K, right = KeyCode.L, pass = KeyCode.O, shoot = KeyCode.P, curl = KeyCode.U },
    };

    class Player
    {
        public int team; public bool gk;
        public Vector2 pos, vel, face;
        public float r, noTouch, gainAt, holdUntil;
        public Transform body, dot, ring;
    }
    class Ball { public Vector2 pos, vel; public float spin; public Player owner; public Transform tf; }
    class Team { public int dir; public float ownGoal; public Player active, receiver; public float receiverUntil; }

    readonly List<Player> players = new List<Player>();
    readonly HashSet<KeyCode> pressed = new HashSet<KeyCode>();
    Ball ball;
    Team[] teams;
    readonly int[] score = new int[2];
    float timeLeft = MATCH, now, stateUntil;
    string state = "ready", banner = "";
    int lastConceded;

    Sprite square, disc;
    Material spriteMat;   // only set on URP projects, so sprites don't need 2D lights
    Camera cam;
    GUIStyle big, mid, small;

    static SoccerGame instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance == null) new GameObject("SoccerGame").AddComponent<SoccerGame>();
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        var unlit = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (unlit != null) spriteMat = new Material(unlit);
        square = MakeSquare();
        disc = MakeDisc(128, 0);
        SetupCamera();
        BuildPitch();
        CreatePieces();
        Setup();
    }

    // ---------- input ----------
    static bool Held(KeyCode k)
    {
#if USE_NEW_INPUT
        var kb = Keyboard.current; return kb != null && kb[ToKey(k)].isPressed;
#else
        return Input.GetKey(k);
#endif
    }
    static bool Down(KeyCode k)
    {
#if USE_NEW_INPUT
        var kb = Keyboard.current; return kb != null && kb[ToKey(k)].wasPressedThisFrame;
#else
        return Input.GetKeyDown(k);
#endif
    }
#if USE_NEW_INPUT
    static Key ToKey(KeyCode k)
    {
        switch (k)
        {
            case KeyCode.Space: return Key.Space;
            case KeyCode.Return: return Key.Enter;
            default: return (Key)System.Enum.Parse(typeof(Key), k.ToString());
        }
    }
#endif

    // ---------- setup ----------
    void Setup()
    {
        teams = new Team[2];
        for (int t = 0; t < 2; t++) teams[t] = new Team { dir = t == 0 ? 1 : -1, ownGoal = t == 0 ? L : R };
        score[0] = score[1] = 0; timeLeft = MATCH;
        Kickoff(0);
    }

    void Kickoff(int team)
    {
        ball.pos = new Vector2(W / 2, H / 2); ball.vel = Vector2.zero; ball.spin = 0; ball.owner = null;
        for (int t = 0; t < 2; t++)
        {
            var mine = players.FindAll(p => p.team == t);
            Player gk = mine[0], f1 = mine[1], f2 = mine[2];
            float d = teams[t].dir, side = t == 1 ? R : L;
            gk.pos = new Vector2(side + d * 26, H / 2);
            if (t == team) { f1.pos = new Vector2(W / 2 - d * 22, H / 2); f2.pos = new Vector2(W / 2 - d * 150, H / 2 + 120); }
            else { f1.pos = new Vector2(W / 2 - d * 120, H / 2 - 60); f2.pos = new Vector2(W / 2 - d * 200, H / 2 + 110); }
            foreach (var p in mine) { p.vel = Vector2.zero; p.face = new Vector2(d, 0); p.noTouch = 0; }
            teams[t].active = f1; teams[t].receiver = null;
        }
    }

    void StartMatch()
    {
        Setup();
        state = "play"; banner = "";
    }

    List<Player> Field(int t) => players.FindAll(p => p.team == t && !p.gk);
    Player Keeper(int t) => players.Find(p => p.team == t && p.gk);

    // ---------- loop ----------
    void Update()
    {
        if ((state == "ready" || state == "end") && (Down(KeyCode.Space) || Down(KeyCode.Return))) StartMatch();

        pressed.Clear();
        foreach (var c in KEYS)
            foreach (var k in new[] { c.pass, c.shoot, c.curl })
                if (Down(k)) pressed.Add(k);

        Step(Mathf.Min(Time.deltaTime, 0.05f));
        Render();
    }

    void Step(float dt)
    {
        float k = dt * 60f;
        now += dt;
        if (state == "goal")
        {
            if (now > stateUntil) { state = "play"; banner = ""; Kickoff(lastConceded); }
            else PhysicsBall(k, true);
            return;
        }
        if (state != "play") return;

        timeLeft -= dt;
        if (timeLeft <= 0) { timeLeft = 0; state = "end"; return; }

        for (int t = 0; t < 2; t++) ChooseActive(t);
        foreach (var p in players)
        {
            if (p.gk) UpdateGK(p, k);
            else if (teams[p.team].active == p) UpdateHuman(p);
            else UpdateAIField(p, k);
        }
        foreach (var p in players)
        {
            p.pos += p.vel * k;
            p.pos.y = Mathf.Clamp(p.pos.y, T + p.r - 10, B - p.r + 10);
            p.pos.x = Mathf.Clamp(p.pos.x, L - 20, R + 20);
        }
        for (int i = 0; i < players.Count; i++)
            for (int j = i + 1; j < players.Count; j++)
            {
                Player a = players[i], b = players[j];
                Vector2 delta = b.pos - a.pos; float d = delta.magnitude, min = a.r + b.r;
                if (d > 0 && d < min) { Vector2 push = delta / d * ((min - d) / 2); a.pos -= push; b.pos += push; }
            }

        for (int t = 0; t < 2; t++)
        {
            var c = KEYS[t]; var p = teams[t].active;
            if (ball.owner != p) continue;
            if (pressed.Contains(c.shoot)) Shoot(p);
            else if (pressed.Contains(c.curl)) Curl(p);
            else if (pressed.Contains(c.pass)) Pass(p);
        }

        if (ball.owner != null)
        {
            var o = ball.owner;
            foreach (var p in players)
            {
                if (p.team == o.team || now < p.noTouch) continue;
                if (Vector2.Distance(p.pos, ball.pos) < p.r + BR + 3 && now - o.gainAt > 0.35f) { o.noTouch = now + 0.45f; GainBall(p); break; }
            }
            var q = ball.owner;
            ball.pos = q.pos + q.face * (q.r + BR + 1);
            ball.vel = q.vel;
        }
        else
        {
            PhysicsBall(k, false);
            if (state == "play")
                foreach (var p in players)
                {
                    if (now < p.noTouch || Vector2.Distance(p.pos, ball.pos) >= p.r + BR) continue;
                    if (p.gk && ball.vel.magnitude > 10 && Random.value < 0.4f)
                    {
                        ball.vel.x = -ball.vel.x * 0.5f; ball.vel.y += (Random.value - 0.5f) * 6; ball.spin = 0; p.noTouch = now + 0.3f;
                    }
                    else GainBall(p);
                    break;
                }
        }
    }

    // ---------- players ----------
    void ChooseActive(int t)
    {
        var tm = teams[t];
        if (ball.owner != null && ball.owner.team == t && !ball.owner.gk) { tm.active = ball.owner; tm.receiver = null; return; }
        if (tm.receiver != null && now < tm.receiverUntil && ball.owner == null) { tm.active = tm.receiver; return; }
        tm.receiver = null;
        var f = Field(t);
        var cur = tm.active; var other = cur == f[0] ? f[1] : f[0];
        if (Vector2.Distance(other.pos, ball.pos) + 40 < Vector2.Distance(cur.pos, ball.pos)) tm.active = other;
    }

    void MoveToward(Player p, Vector2 target, float speed, float k)
    {
        Vector2 delta = target - p.pos; float d = delta.magnitude;
        if (d < 3) { p.vel = Vector2.zero; return; }
        float s = Mathf.Min(speed, d / k);
        p.vel = delta / d * s; p.face = delta / d;
    }

    void UpdateHuman(Player p)
    {
        var c = KEYS[p.team];
        var input = new Vector2((Held(c.right) ? 1 : 0) - (Held(c.left) ? 1 : 0), (Held(c.down) ? 1 : 0) - (Held(c.up) ? 1 : 0));
        float sp = ball.owner == p ? 3.0f : 3.4f;
        if (input != Vector2.zero) { input.Normalize(); p.face = input; }
        p.vel = input * sp;
    }

    void UpdateAIField(Player p, float k)
    {
        var tm = teams[p.team]; var mate = tm.active; float d = tm.dir;
        bool hasBall = ball.owner != null && ball.owner.team == p.team;
        float distBall = Vector2.Distance(p.pos, ball.pos);
        Vector2 target;
        if (ball.owner == null && distBall < 80 && distBall < Vector2.Distance(mate.pos, ball.pos)) target = ball.pos;
        else if (hasBall)
        {
            var o = ball.owner;
            target = new Vector2(Mathf.Clamp(o.pos.x + d * 150, L + 60, R - 60), o.pos.y > H / 2 ? o.pos.y - 160 : o.pos.y + 160);
        }
        else
        {
            float g = tm.ownGoal;
            target = new Vector2(g + (ball.pos.x - g) * 0.5f, H / 2 + (ball.pos.y - H / 2) * 0.5f + (mate.pos.y > ball.pos.y ? -50 : 50));
        }
        target.x = Mathf.Clamp(target.x, L + 20, R - 20); target.y = Mathf.Clamp(target.y, T + 20, B - 20);
        MoveToward(p, target, 2.6f, k);
    }

    void UpdateGK(Player p, float k)
    {
        var tm = teams[p.team]; float d = tm.dir, line = tm.ownGoal;
        if (ball.owner == p)
        {
            p.vel = Vector2.zero;
            if (now > p.holdUntil) Pass(p);
            return;
        }
        bool inBox = Mathf.Abs(ball.pos.x - line) < 130 && ball.pos.y > GT - 50 && ball.pos.y < GB + 50;
        var target = new Vector2(line + d * 26, Mathf.Clamp(ball.pos.y, GT + 8, GB - 8));
        if (inBox && ball.owner == null && ball.vel.magnitude < 6) target = ball.pos;
        else if (inBox && ball.owner != null && ball.owner.team != p.team) target.x = line + d * 55;
        MoveToward(p, target, 2.1f, k);
        p.pos.x = d > 0 ? Mathf.Clamp(p.pos.x, line - 4, line + 140) : Mathf.Clamp(p.pos.x, line - 140, line + 4);
    }

    void GainBall(Player p)
    {
        ball.owner = p; p.gainAt = now; ball.vel = Vector2.zero; ball.spin = 0;
        if (p.gk) p.holdUntil = now + 0.7f;
    }

    // ---------- kicks ----------
    void Pass(Player p)
    {
        Player mate = null; float best = float.MaxValue;
        foreach (var m in Field(p.team))
        {
            if (m == p) continue;
            float d = Vector2.Distance(m.pos, p.pos);
            if (d < best) { best = d; mate = m; }
        }
        Vector2 target = mate.pos + mate.vel * 12;
        Kick(p, target, Mathf.Clamp(Vector2.Distance(p.pos, target) * 0.03f + 4.5f, 6, 11), 0);
        var tm = teams[p.team];
        tm.receiver = mate; tm.receiverUntil = now + 1.6f; tm.active = mate;
    }

    // Shots aim on their own: at the corner of the goal farther from the opposing keeper
    void Shoot(Player p)
    {
        var keeper = Keeper(1 - p.team);
        float gx = p.team == 1 ? L - 10 : R + 10;
        float gy = (keeper.pos.y < H / 2 ? GB - 18 : GT + 18) + (Random.value - 0.5f) * 24;
        Kick(p, new Vector2(gx, gy), 13.5f, 0);
    }

    // Curler: launched wide of the far post, then bends back in. The launch angle is found by
    // simulating the same ball physics and keeping the angle that lands on the corner.
    void Curl(Player p)
    {
        var keeper = Keeper(1 - p.team);
        float gx = p.team == 1 ? L - 10 : R + 10;
        float gy = keeper.pos.y < H / 2 ? GB - 14 : GT + 14, side = gy > H / 2 ? 1 : -1;
        float baseAngle = Mathf.Atan2(gy - p.pos.y, gx - p.pos.x), reach = p.r + BR + 1;
        bool found = false; float bestErr = float.MaxValue, bestSpin = 0; Vector2 bestDir = Vector2.zero;
        foreach (float spin in new[] { 0.016f, -0.016f })
            for (float a = -0.8f; a <= 0.8f; a += 0.005f)
            {
                var u = new Vector2(Mathf.Cos(baseAngle + a), Mathf.Sin(baseAngle + a));
                if (u.x * (gx - p.pos.x) <= 0 || (p.pos.y + u.y / u.x * (gx - p.pos.x) - gy) * side <= 0) continue; // must start out wide
                if (!LandingY(p.pos + u * reach, u * 12, spin, gx, out float y)) continue;
                float err = Mathf.Abs(y - gy);
                if (err < bestErr) { bestErr = err; bestSpin = spin; bestDir = u; found = true; }
            }
        if (!found) { Shoot(p); return; }
        Kick(p, p.pos + bestDir * 100, 12, bestSpin);
    }

    bool LandingY(Vector2 pos, Vector2 vel, float spin, float gx, out float y)
    {
        float dir = Mathf.Sign(gx - pos.x);
        for (int i = 0; i < 300; i++)
        {
            SpinStep(ref vel, ref spin, 1);
            pos += vel; vel *= 0.985f;
            if ((gx - pos.x) * dir <= 0) { y = pos.y; return true; }
            if (vel.magnitude < 1) break;
        }
        y = 0; return false;
    }

    static void SpinStep(ref Vector2 vel, ref float spin, float k)
    {
        if (spin == 0) return;
        float a = spin * k, c = Mathf.Cos(a), s = Mathf.Sin(a);
        vel = new Vector2(vel.x * c - vel.y * s, vel.x * s + vel.y * c);
        spin *= Mathf.Pow(0.99f, k);
    }

    // Kicks ignore which way the player faces: the ball is moved to the side facing the target first
    void Kick(Player p, Vector2 target, float speed, float spin)
    {
        Vector2 u = target - p.pos; u = u.sqrMagnitude > 0 ? u.normalized : Vector2.right;
        ball.pos = p.pos + u * (p.r + BR + 1);
        p.face = u;
        ball.owner = null; ball.vel = u * speed; ball.spin = spin; p.noTouch = now + 0.3f;
    }

    // ---------- ball ----------
    void PhysicsBall(float k, bool inNet)
    {
        float f = Mathf.Pow(0.985f, k);
        SpinStep(ref ball.vel, ref ball.spin, k);
        ball.pos += ball.vel * k; ball.vel *= f;
        bool inMouth = ball.pos.y > GT + BR && ball.pos.y < GB - BR;
        bool outside = ball.pos.x < L || ball.pos.x > R;
        if (ball.pos.y < T + BR && !outside) { ball.pos.y = T + BR; ball.vel.y = Mathf.Abs(ball.vel.y) * 0.8f; ball.spin = 0; }
        if (ball.pos.y > B - BR && !outside) { ball.pos.y = B - BR; ball.vel.y = -Mathf.Abs(ball.vel.y) * 0.8f; ball.spin = 0; }
        for (int i = 0; i < 2; i++)
        {
            float edge = i == 0 ? L : R, s = i == 0 ? -1 : 1; int scorer = i == 0 ? 1 : 0;
            bool past = s < 0 ? ball.pos.x - BR < edge : ball.pos.x + BR > edge;
            if (!past) continue;
            if (inMouth || inNet)
            {
                ball.pos.y = Mathf.Clamp(ball.pos.y, GT + BR, GB - BR);
                float back = edge + s * (GD - BR);
                if (s < 0 ? ball.pos.x < back : ball.pos.x > back) { ball.pos.x = back; ball.vel.x *= -0.2f; }
                if (!inNet && (s < 0 ? ball.pos.x < edge - BR : ball.pos.x > edge + BR)) Goal(scorer);
            }
            else { ball.pos.x = edge - s * BR; ball.vel.x = -ball.vel.x * 0.8f; ball.spin = 0; }
        }
    }

    void Goal(int scorer)
    {
        score[scorer]++; lastConceded = 1 - scorer;
        state = "goal"; stateUntil = now + 1.8f; banner = scorer == 1 ? "2P BLUE GOAL!" : "1P RED GOAL!";
        foreach (var p in players) p.vel = Vector2.zero;
    }

    // ---------- drawing ----------
    static Vector3 World(Vector2 p, float z = 0) => new Vector3((p.x - W / 2) / PX, -(p.y - H / 2) / PX, z);

    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null) { var go = new GameObject("Main Camera"); go.tag = "MainCamera"; cam = go.AddComponent<Camera>(); }
        cam.orthographic = true;
        cam.transform.position = new Vector3(0, 0, -10);
        cam.transform.rotation = Quaternion.identity;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(.05f, .09f, .07f);
    }

    static Sprite MakeSquare()
    {
        var tex = new Texture2D(4, 4) { filterMode = FilterMode.Point };
        var px = new Color32[16]; for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(.5f, .5f), 4);
    }

    // A white disc (inner = 0) or ring (inner = hole radius as a fraction of the outer radius), 1 world unit across
    static Sprite MakeDisc(int n, float inner)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n]; float c = (n - 1) / 2f, ro = n / 2f - 1;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(ro - d + 0.5f);
                if (inner > 0) a *= Mathf.Clamp01(d - inner * ro + 0.5f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(.5f, .5f), n);
    }

    Transform Piece(string name, Sprite s, Color c, int order, Vector2 center, Vector2 sizePx, Transform parent = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent != null ? parent : transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s; sr.color = c; sr.sortingOrder = order;
        if (spriteMat != null) sr.sharedMaterial = spriteMat;
        go.transform.position = World(center);
        go.transform.localScale = new Vector3(sizePx.x / PX, sizePx.y / PX, 1);
        return go.transform;
    }

    void Box(float x, float y, float w, float h, Color c, int order) =>
        Piece("rect", square, c, order, new Vector2(x + w / 2, y + h / 2), new Vector2(w, h));

    void Outline(float x, float y, float w, float h, Color c, int order, float t = 3)
    {
        Box(x - t / 2, y - t / 2, w + t, t, c, order);
        Box(x - t / 2, y + h - t / 2, w + t, t, c, order);
        Box(x - t / 2, y - t / 2, t, h + t, c, order);
        Box(x + w - t / 2, y - t / 2, t, h + t, c, order);
    }

    void BuildPitch()
    {
        var line = new Color(1, 1, 1, .85f);
        for (int i = 0; i < 12; i++)
            Box(i * W / 12, 0, W / 12 + 0.5f, H, i % 2 == 1 ? new Color(.122f, .42f, .227f) : new Color(.137f, .467f, .255f), 0);
        Outline(L, T, R - L, B - T, line, 1);
        Box(W / 2 - 1.5f, T, 3, B - T, line, 1);
        Piece("centre circle", MakeDisc(256, 1 - 3f / 70f), line, 1, new Vector2(W / 2, H / 2), new Vector2(143, 143));
        Piece("centre spot", disc, line, 1, new Vector2(W / 2, H / 2), new Vector2(8, 8));
        foreach (var (edge, s) in new[] { (L, 1f), (R, -1f) })
        {
            Outline(s > 0 ? edge : edge - 130, H / 2 - 140, 130, 280, line, 1);
            Outline(s > 0 ? edge : edge - 45, H / 2 - 85, 45, 170, line, 1);
            Piece("penalty spot", disc, line, 1, new Vector2(edge + s * 95, H / 2), new Vector2(7, 7));
            float nx = s > 0 ? edge - GD : edge;
            Box(nx, GT, GD, GB - GT, new Color(1, 1, 1, .12f), 2);
            for (float y = GT + 10; y < GB; y += 10) Box(nx, y - .5f, GD, 1, new Color(1, 1, 1, .25f), 2);
            for (float x = nx + 8; x < nx + GD; x += 8) Box(x - .5f, GT, 1, GB - GT, new Color(1, 1, 1, .25f), 2);
            float back = s > 0 ? nx : nx + GD;
            Box(Mathf.Min(edge, back) - 2, GT - 2, GD + 4, 4, Color.white, 3);
            Box(Mathf.Min(edge, back) - 2, GB - 2, GD + 4, 4, Color.white, 3);
            Box(back - 2, GT - 2, 4, GB - GT + 4, Color.white, 3);
        }
    }

    void CreatePieces()
    {
        var ringSprite = MakeDisc(128, 0.82f);
        for (int t = 0; t < 2; t++)
            for (int i = 0; i < 3; i++)
            {
                var p = new Player { team = t, gk = i == 0, r = i == 0 ? PR + 1 : PR, face = new Vector2(t == 0 ? 1 : -1, 0) };
                var root = new GameObject((t == 0 ? "Red " : "Blue ") + (p.gk ? "GK" : "Player " + i)).transform;
                root.SetParent(transform, false);
                Color c = p.gk ? (t == 0 ? RedGK : BlueGK) : (t == 0 ? Red : Blue);
                p.ring = Piece("ring", ringSprite, t == 0 ? Red : Blue, 9, Vector2.zero, Vector2.one * (p.r + 8) * 2, root);
                Piece("outline", disc, Ink, 10, Vector2.zero, Vector2.one * (p.r * 2 + 4), root);
                Piece("body", disc, c, 11, Vector2.zero, Vector2.one * p.r * 2, root);
                p.dot = Piece("facing", disc, Ink, 12, Vector2.zero, new Vector2(6, 6), root);
                p.body = root;
                foreach (Transform child in root) if (child != p.dot) child.localPosition = Vector3.zero;
                players.Add(p);
            }
        var b = new GameObject("Ball").transform; b.SetParent(transform, false);
        Piece("outline", disc, new Color(.13f, .13f, .13f), 20, Vector2.zero, Vector2.one * (BR * 2 + 3), b);
        Piece("ball", disc, Color.white, 21, Vector2.zero, Vector2.one * BR * 2, b);
        Piece("patch", disc, new Color(.13f, .13f, .13f), 22, Vector2.zero, new Vector2(5, 5), b);
        foreach (Transform child in b) child.localPosition = Vector3.zero;
        ball = new Ball { tf = b };
    }

    void Render()
    {
        cam.orthographicSize = Mathf.Max(8.2f, 12.6f / Mathf.Max(cam.aspect, 0.1f));
        foreach (var p in players)
        {
            p.body.position = World(p.pos);
            p.dot.localPosition = new Vector3(p.face.x, -p.face.y, 0) * ((p.r - 5) / PX);
            p.ring.gameObject.SetActive(state == "play" && teams[p.team].active == p);
        }
        ball.tf.position = World(ball.pos);
    }

    void OnGUI()
    {
        if (big == null)
        {
            big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            mid = new GUIStyle(big); small = new GUIStyle(big) { fontStyle = FontStyle.Normal };
        }
        float u = Screen.height / 600f;
        big.fontSize = Mathf.RoundToInt(56 * u); mid.fontSize = Mathf.RoundToInt(26 * u); small.fontSize = Mathf.RoundToInt(15 * u);

        // scoreboard
        int secs = Mathf.CeilToInt(timeLeft);
        GUI.color = new Color(0, 0, 0, .55f); GUI.DrawTexture(new Rect(Screen.width / 2f - 230 * u, 6 * u, 460 * u, 40 * u), Texture2D.whiteTexture); GUI.color = Color.white;
        Label(new Rect(Screen.width / 2f - 230 * u, 6 * u, 150 * u, 40 * u), "1P RED", mid, Red);
        Label(new Rect(Screen.width / 2f - 80 * u, 6 * u, 160 * u, 40 * u), $"{score[0]} : {score[1]}   {secs / 60}:{secs % 60:00}", mid, Color.white);
        Label(new Rect(Screen.width / 2f + 80 * u, 6 * u, 150 * u, 40 * u), "2P BLUE", mid, Blue);

        // player tags
        foreach (var p in players)
        {
            bool active = state == "play" && teams[p.team].active == p;
            if (!active && !p.gk) continue;
            Vector3 sp = cam.WorldToScreenPoint(World(p.pos + new Vector2(0, -p.r - (active ? 14 : 8))));
            Label(new Rect(sp.x - 30, Screen.height - sp.y - 12, 60, 24), active ? (p.team == 0 ? "1P" : "2P") : "GK", small, active ? Color.white : new Color(1, 1, 1, .75f));
        }

        if (banner != "") Overlay(banner, "", Gold);
        if (state == "ready") Overlay("KICK OFF", "Press SPACE to start   ·   3 minutes   ·   3 v 3", Color.white);
        if (state == "end")
        {
            string title = score[0] == score[1] ? "DRAW" : score[0] > score[1] ? "1P RED WINS" : "2P BLUE WINS";
            Overlay(title, $"Final score {score[0]} : {score[1]}   ·   Press SPACE to play again", Color.white);
        }
        if (state != "play" && state != "goal")
            Label(new Rect(0, Screen.height - 70 * u, Screen.width, 60 * u),
                "1P  move WASD · pass E · shoot R · curl Q          2P  move IJKL · pass O · shoot P · curl U", small, Color.white);
    }

    void Overlay(string title, string sub, Color c)
    {
        float u = Screen.height / 600f;
        GUI.color = new Color(0, 0, 0, .55f); GUI.DrawTexture(new Rect(0, Screen.height / 2f - 70 * u, Screen.width, 140 * u), Texture2D.whiteTexture); GUI.color = Color.white;
        Label(new Rect(0, Screen.height / 2f - 60 * u, Screen.width, 80 * u), title, big, c);
        if (sub != "") Label(new Rect(0, Screen.height / 2f + 20 * u, Screen.width, 40 * u), sub, small, new Color(.85f, .9f, .86f));
    }

    static void Label(Rect r, string text, GUIStyle style, Color c)
    {
        style.normal.textColor = c;
        GUI.Label(r, text, style);
    }
}
