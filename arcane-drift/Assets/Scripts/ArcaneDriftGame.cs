#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
#define USE_NEW_INPUT
#endif

using System.Collections.Generic;
using UnityEngine;
#if USE_NEW_INPUT
using UnityEngine.InputSystem;
#endif

// Arcane Drift — 무한 모드 (STEP 1~8 전체)
//
// 이 파일 하나를 Assets 아래 아무 곳에 넣고 ▶ 를 누르면 된다. 씬에 아무것도 배치할 필요가 없다.
//
// 구조
//  - MonoBehaviour 는 ArcaneDriftGame 하나뿐이다. 적·투사체·경험치 구슬·효과는 일반 클래스로 두고
//    ArcaneDriftGame.Update 에서 직접 움직인다. (Unity는 MonoBehaviour 마다 같은 이름의 파일을 요구하기 때문)
//  - 물리(Rigidbody2D)는 쓰지 않는다. 위치를 직접 옮기고 충돌은 거리로 판정한다.
//  - 적·투사체·구슬·효과는 풀링한다. 죽은 것은 alive = false 로 표시해 두고 프레임 끝에 한꺼번에 풀로 돌려보낸다.
//  - 수치는 맨 아래 Tune 에 모았다. "[임시]" 는 원본 arcane-drift.html 을 확인하지 못해 정한 값이다.
namespace ArcaneDrift
{
    public class ArcaneDriftGame : MonoBehaviour
    {
        enum State { Title, Playing, LevelUp, Paused, Info, GameOver }

        const string BestTimeKey = "ArcaneDrift.BestTime";
        const string BestKillsKey = "ArcaneDrift.BestKills";
        const float FloatTextLife = 0.7f;

        static ArcaneDriftGame instance;

        // 씬이 열리면 자동으로 게임 오브젝트를 만든다
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (instance == null) new GameObject("ArcaneDrift").AddComponent<ArcaneDriftGame>();
        }

        State state = State.Title;
        Camera cam;
        Transform grid;

        // ---- 플레이어 ----
        Transform playerTf, playerBody, targetRing;
        SpriteRenderer playerSr;
        bool walking;
        float walkTime, shake;
        Vector2 ppos, facing = Vector2.right;
        Vector2? moveTarget;
        bool holdMove;
        Enemy attackTarget;
        int attackSerial;
        float hp, attackCd, attackLock, hurtFlash, eBuff;
        int attackCount;
        PlayerMods mods;
        SkillState[] skills;
        readonly Dictionary<string, int> stacks = new Dictionary<string, int>();
        readonly List<Augment> pickedOrder = new List<Augment>();   // 고른 순서 (Tab 화면용)
        bool dashing, dashKilled;

        // ---- 진행 ----
        float elapsed, spawnTimer, xp, bannerTime;
        int eliteIndex, bossIndex, kills, level, xpNeed, pendingLevelUps, serial;
        string bannerText = "";
        readonly List<Card> cards = new List<Card>();
        float cardsReadyAt;
        bool CardsReady { get { return Time.unscaledTime >= cardsReadyAt; } }
        float bestTime;
        int bestKills;
        bool newRecord;

        // ---- 풀 ----
        Pool<Enemy> enemies;
        Pool<Projectile> projectiles;
        Pool<XpOrb> orbs;
        Pool<Fx> fxs;
        readonly List<FloatText> texts = new List<FloatText>();
        readonly List<Enemy> nearby = new List<Enemy>();
        readonly HashSet<Enemy> chainSet = new HashSet<Enemy>();
        readonly HashSet<Enemy> dashSet = new HashSet<Enemy>();

        // ---- 패시브 스킬 ----
        readonly PassiveState[] passives = new PassiveState[Passive.Count];
        readonly List<SpriteRenderer> shurikenSprites = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> laserSprites = new List<SpriteRenderer>();
        Pool<Zone> zones;
        Pool<Falling> meteors;
        readonly List<Delayed> delayed = new List<Delayed>();
        readonly List<Enemy> picks = new List<Enemy>();
        float invulnUntil;
        int stepCount;

        // ---- 계산된 능력치 ----
        float MaxHp { get { return Tune.PlayerHp + mods.maxHpAdd; } }
        // 레벨이 오를수록 모든 피해가 커진다 (몬스터 체력이 시간에 따라 계속 늘어나서)
        float LevelBonus { get { return 1f + (level - 1) * Tune.DamagePerLevel; } }
        float Attack { get { return (Tune.PlayerDamage + mods.damageAdd) * (1 + mods.damagePct) * LevelBonus; } }
        float Aspd { get { return Tune.PlayerAspd * (1 + mods.aspdPct) + (eBuff > 0 ? Tune.EBuffAspd : 0); } }
        float Range { get { return Tune.PlayerRange + mods.rangeAdd; } }
        float MoveSpeed
        {
            get
            {
                float baseSpeed = Tune.PlayerSpeedVsMonster > 0
                    ? EnemyKind.Grunt.speed * Tune.SpeedScale(elapsed) * Tune.PlayerSpeedVsMonster
                    : Tune.PlayerSpeed;
                return baseSpeed * (1 + mods.movePct);
            }
        }
        float CritChance { get { return Tune.CritChance + mods.critChance; } }
        float CritMult { get { return Tune.CritMult + mods.critMult; } }
        bool AllSkillsLearned { get { return skills[0].level > 0 && skills[1].level > 0 && skills[2].level > 0; } }

        // ================================================================ 시작

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            useGUILayout = false;

            Art.Init();
            cam = SetupCamera();
            grid = Art.CreateFloor(transform);

            // 플레이어: 그림자 + 도트 캐릭터. 맞는 판정은 그림과 상관없이 반지름 PlayerRadius 원이다
            playerTf = new GameObject("Player").transform;
            playerTf.SetParent(transform, false);
            var shadow = Art.CreateCircle("Shadow", playerTf, new Color(0f, 0f, 0f, 0.35f), 1f, 7);
            shadow.localScale = new Vector3(0.38f, 0.13f, 1f);
            shadow.localPosition = new Vector3(0f, -Art.HeroFeet, 0f);
            playerSr = Art.CreateSprite("Body", playerTf, Art.HeroIdle, Color.white, 8);
            playerBody = playerSr.transform;
            targetRing = Art.CreateSprite("TargetRing", transform, Art.Ring, Palette.Target, 11).transform;

            enemies = new Pool<Enemy>(transform, "Enemy", 5);
            projectiles = new Pool<Projectile>(transform, "Projectile", 9);
            orbs = new Pool<XpOrb>(transform, "XpOrb", 2);
            fxs = new Pool<Fx>(transform, "Fx", 10);
            zones = new Pool<Zone>(transform, "Zone", -4);
            meteors = new Pool<Falling>(transform, "Meteor", 12);

            bestTime = PlayerPrefs.GetFloat(BestTimeKey, 0f);
            bestKills = PlayerPrefs.GetInt(BestKillsKey, 0);
            ResetRun();
            state = State.Title;
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
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
            c.orthographicSize = Tune.CameraSize;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = Palette.Ground;
            c.transform.rotation = Quaternion.identity;
            return c;
        }

        void ResetRun()
        {
            enemies.Clear(); projectiles.Clear(); orbs.Clear(); fxs.Clear(); texts.Clear();
            zones.Clear(); meteors.Clear(); delayed.Clear();
            for (int i = 0; i < Passive.Count; i++) passives[i] = new PassiveState();
            foreach (var sr in shurikenSprites) sr.gameObject.SetActive(false);
            foreach (var sr in laserSprites) sr.gameObject.SetActive(false);
            invulnUntil = 0f;
            mods = new PlayerMods();
            skills = new[] { new SkillState(0), new SkillState(1), new SkillState(2) };
            stacks.Clear(); cards.Clear(); pickedOrder.Clear();

            ppos = Vector2.zero; facing = Vector2.right;
            moveTarget = null; holdMove = false; attackTarget = null;
            walking = false; walkTime = 0f; shake = 0f;
            hp = MaxHp;
            attackCd = attackLock = hurtFlash = eBuff = 0;
            attackCount = 0;

            elapsed = 0; spawnTimer = 0.5f; xp = 0; bannerTime = 0;
            eliteIndex = bossIndex = kills = pendingLevelUps = 0;
            level = 1; xpNeed = Tune.XpToNext(level);
            newRecord = false;
            Time.timeScale = 1f;
            SyncPlayerVisual();
        }

        void StartRun()
        {
            ResetRun();
            state = State.Playing;
            // 시작하자마자 증강을 하나 고르고 시작한다
            pendingLevelUps = Tune.StartingAugments;
            if (pendingLevelUps > 0) OpenLevelUp();
        }

        void GoTitle()
        {
            ResetRun();
            state = State.Title;
        }

        // Tab: 멈추고 지금까지 고른 증강·능력치를 본다
        void SetInfo(bool open)
        {
            state = open ? State.Info : State.Playing;
            Time.timeScale = open ? 0f : 1f;
        }

        void SetPaused(bool paused)
        {
            state = paused ? State.Paused : State.Playing;
            Time.timeScale = paused ? 0f : 1f;
        }

        void GameOver()
        {
            state = State.GameOver;
            Time.timeScale = 1f;
            newRecord = elapsed > bestTime;
            if (newRecord) { bestTime = elapsed; PlayerPrefs.SetFloat(BestTimeKey, bestTime); }
            if (kills > bestKills) { bestKills = kills; PlayerPrefs.SetInt(BestKillsKey, bestKills); }
            PlayerPrefs.Save();
            targetRing.gameObject.SetActive(false);
        }

        // ================================================================ 루프

        void Update()
        {
            switch (state)
            {
                case State.Title:
                case State.GameOver:
                    if (GameInput.KeyDown(KeyCode.Return) || GameInput.KeyDown(KeyCode.Space)) StartRun();
                    break;
                case State.Playing:
                    if (GameInput.KeyDown(KeyCode.Escape)) { SetPaused(true); break; }
                    if (GameInput.KeyDown(KeyCode.Tab)) { SetInfo(true); break; }
                    Step(Mathf.Min(Time.deltaTime, 0.05f));
                    break;
                case State.Paused:
                    if (GameInput.KeyDown(KeyCode.Escape)) SetPaused(false);
                    break;
                case State.Info:
                    if (GameInput.KeyDown(KeyCode.Tab) || GameInput.KeyDown(KeyCode.Escape)) SetInfo(false);
                    break;
                case State.LevelUp:
                    for (int i = 0; i < cards.Count && i < 4; i++)
                        if (GameInput.KeyDown(KeyCode.Alpha1 + i)) { ChooseCard(i); break; }
                    break;
            }
        }

        // 카메라와 배경은 플레이어가 움직인 뒤(LateUpdate)에 따라가야 떨리지 않는다
        void LateUpdate()
        {
            // 화면 흔들림: 게임 중에만 (일시정지·카드 선택 중에는 멈춘 화면이 떨리지 않게)
            Vector2 offset = Vector2.zero;
            if (state == State.Playing && shake > 0f)
            {
                offset = Random.insideUnitCircle * (shake * Tune.ShakeStrength);
                shake = Mathf.Max(0f, shake - Time.deltaTime * 3f);
            }
            cam.transform.position = new Vector3(ppos.x + offset.x, ppos.y + offset.y, -10f);
            // 바닥 무늬 한 장(FloorUnits) 단위로만 옮겨서, 무늬가 바닥에 고정된 것처럼 보이게 한다
            float period = Art.FloorUnits;
            grid.position = new Vector3(Mathf.Round(ppos.x / period) * period, Mathf.Round(ppos.y / period) * period, 0f);
        }

        void Step(float dt)
        {
            elapsed += dt;
            bannerTime -= dt;
            stepCount++;

            HandleCommands();
            UpdatePlayer(dt);
            UpdateSpawning(dt);
            UpdateEnemies(dt);
            UpdateProjectiles(dt);
            UpdatePassives(dt);
            UpdateOrbs(dt);
            UpdateFx(dt);

            enemies.Sweep(); projectiles.Sweep(); orbs.Sweep(); fxs.Sweep(); zones.Sweep(); meteors.Sweep();
            SyncPlayerVisual();

            if (hp <= 0) { GameOver(); return; }
            if (pendingLevelUps > 0) OpenLevelUp();
        }

        // ================================================================ 입력과 명령

        void HandleCommands()
        {
            Vector2 cursor = CursorWorld();

            // 우클릭: 이동. 누르고 있으면 커서를 계속 따라간다
            if (GameInput.RightClickDown())
            {
                attackTarget = null;
                moveTarget = cursor; holdMove = true;
                SpawnFx(cursor, Art.Circle, Palette.Marker, 0.25f, 0f, 0.35f, 12);
            }
            else if (holdMove && GameInput.RightClickHeld()) moveTarget = cursor;
            if (!GameInput.RightClickHeld()) holdMove = false;

            // 좌클릭: 그 적을 추격하며 자동 공격 (새 명령 전까지 유지). 적을 정확히 안 찍어도 커서 근처 적을 고른다
            if (GameInput.LeftClickDown())
            {
                Enemy e = EnemyAt(cursor) ?? Nearest(cursor, Tune.ClickAssist, null);
                if (e != null)
                {
                    attackTarget = e; attackSerial = e.serial;
                    moveTarget = null; holdMove = false;
                    SpawnFx(e.pos, Art.Ring, Palette.Target, e.radius * 2f + 0.5f, e.radius * 2f + 0.16f, 0.2f, 12);
                }
            }

            if (GameInput.KeyDown(KeyCode.Q)) CastQ(cursor);
            if (GameInput.KeyDown(KeyCode.W)) CastW();
            if (GameInput.KeyDown(KeyCode.E)) CastE(cursor);
        }

        Vector2 CursorWorld()
        {
            Vector3 screen = GameInput.MousePosition();
            screen.z = -cam.transform.position.z;
            return cam.ScreenToWorldPoint(screen);
        }

        // ================================================================ 플레이어

        void UpdatePlayer(float dt)
        {
            attackCd -= dt; attackLock -= dt; hurtFlash -= dt; eBuff -= dt;
            if (mods.regen > 0) Heal(mods.regen * dt);
            for (int i = 0; i < 3; i++) skills[i].cdLeft = Mathf.Max(0, skills[i].cdLeft - dt);

            Enemy target = Valid(attackTarget, attackSerial);
            // [임시] 공격하던 적이 죽으면 근처 적으로, 가만히 서 있으면 사거리 안의 적을 자동으로 공격한다
            if (target == null && attackTarget != null) target = Retarget(Range + 0.5f);
            if (target == null && !moveTarget.HasValue) target = Retarget(Range);

            Vector2? dest = null;
            if (target != null)
            {
                Vector2 to = target.pos - ppos;
                if (to.magnitude - target.radius > Range) dest = target.pos;
                else
                {
                    if (to.sqrMagnitude > 0.0001f) facing = to.normalized;
                    if (attackCd <= 0 && attackLock <= 0) FireBasic(target);
                }
            }
            else if (moveTarget.HasValue) dest = moveTarget.Value;

            // 평타 경직: 발사 직후 잠깐 이동 불가 (캔슬 불가)
            walking = false;
            if (dest.HasValue && attackLock <= 0)
            {
                walking = true;
                walkTime += dt;
                Vector2 to = dest.Value - ppos;
                float d = to.magnitude, step = MoveSpeed * dt;
                if (d <= step)
                {
                    ppos = dest.Value;
                    if (target == null) moveTarget = null;
                }
                else
                {
                    facing = to / d;
                    ppos += facing * step;
                }
            }
        }

        Enemy Retarget(float range)
        {
            Enemy e = Nearest(ppos, range, null);
            attackTarget = e;
            if (e != null) attackSerial = e.serial;
            return e;
        }

        void FireBasic(Enemy target)
        {
            float aspd = Aspd;
            attackCd = 1f / aspd;
            attackLock = Tune.AttackLock(aspd);
            attackCount++;

            float dmg = Attack * Tune.BasicDamageBoost;
            SpawnFx(GunTip(), Art.Circle, Palette.Muzzle, 0.16f, 0.04f, 0.08f, 12);
            SpawnBolt(target, dmg, 0f);
            if (mods.multishot > 0)
            {
                FindNearby(ppos, Range, target);
                for (int i = 0; i < mods.multishot && i < nearby.Count; i++) SpawnBolt(nearby[i], dmg, 0f);
            }
            if (mods.echoEvery > 0 && attackCount % mods.echoEvery == 0) SpawnBolt(target, dmg, 0.12f);
        }

        void DamagePlayer(float amount)
        {
            if (elapsed < invulnUntil) return;   // 무적 돌진
            float dmg = Mathf.Max(1f, amount - mods.armor);
            hp -= dmg;
            hurtFlash = 0.12f;
            AddText(ppos + new Vector2(0, 0.3f), Mathf.RoundToInt(dmg).ToString(), Palette.Hurt, false);
        }

        void Heal(float amount)
        {
            hp = Mathf.Min(MaxHp, hp + amount);
        }

        float RollCrit(float dmg, out bool crit)
        {
            crit = Random.value < CritChance;
            return crit ? dmg * CritMult : dmg;
        }

        void SyncPlayerVisual()
        {
            playerTf.position = new Vector3(ppos.x, ppos.y, 0f);
            // 오른쪽을 보고 그린 그림이라 왼쪽을 볼 때 좌우 반전. 거의 위·아래로 움직일 때는 방향을 유지
            if (Mathf.Abs(facing.x) > 0.05f) playerSr.flipX = facing.x < 0;
            // 걸을 때: 두 프레임 다리 교차 + 살짝 통통 튀기
            bool step = walking && Mathf.FloorToInt(walkTime * 8f) % 2 == 1;
            playerSr.sprite = step ? Art.HeroStep : Art.HeroIdle;
            playerBody.localPosition = new Vector3(0f, walking ? Mathf.Abs(Mathf.Sin(walkTime * 16f)) * 0.03f : 0f, 0f);
            playerSr.color = hurtFlash > 0 ? Palette.Hurt : eBuff > 0 ? Palette.Haste : Color.white;
            if (elapsed < invulnUntil) playerSr.color = Palette.Fade(Palette.Haste, Mathf.FloorToInt(elapsed * 20f) % 2 == 0 ? 0.45f : 0.9f);

            Enemy t = Valid(attackTarget, attackSerial);
            targetRing.gameObject.SetActive(t != null && state == State.Playing);
            if (t != null)
            {
                targetRing.position = new Vector3(t.pos.x, t.pos.y, 0f);
                targetRing.localScale = Vector3.one * (t.radius * 2 + 0.16f);
            }
        }

        // ================================================================ 스킬 Q W E

        float SkillDamage(int index, float coef)
        {
            var s = skills[index];
            return SkillPower * coef * (s.evolved ? Tune.EvolveDamage[index] : 1f);
        }

        void StartCooldown(int index)
        {
            var s = skills[index];
            s.cdLeft = s.cdTotal = Tune.Cooldown(index, s.level, mods.SkillCdMult);
            // 삼위일체: 스킬을 쓰면 나머지 두 스킬 쿨다운 감소
            if (mods.synergyFinale)
                for (int k = 0; k < 3; k++)
                    if (k != index) skills[k].cdLeft = Mathf.Max(0, skills[k].cdLeft - Tune.SynergyCdCut);
        }

        static Vector2 DirOr(Vector2 v, Vector2 fallback)
        {
            return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
        }

        // Q 아케인 볼트: 커서 방향으로 적을 관통하는 마력탄 (삼연발·거대화·잔류 증강)
        void CastQ(Vector2 cursor)
        {
            var s = skills[0];
            if (s.level == 0 || s.cdLeft > 0) return;
            Vector2 dir = DirOr(cursor - ppos, facing);
            float dmg = SkillDamage(0, Tune.QDamage(s.level)) * (mods.qGiant ? Tune.QGiantDamage : 1f);
            float radius = Tune.QRadius * (1 + mods.skillArea) * (mods.qGiant ? Tune.QGiantSize : 1f);
            int bolts = 1 + (mods.qTriple ? 2 : 0) + mods.skillAmount;
            Color c = s.evolved ? Palette.Legendary : Palette.QBolt;
            for (int b = 0; b < bolts; b++)
            {
                Vector2 d = Rotate(dir, (b - (bolts - 1) / 2f) * Tune.QSpreadDeg * Mathf.Deg2Rad);
                var p = NewProjectile(ProjKind.QBolt, ppos, d * Tune.QSpeed, radius, dmg, Tune.QRange / Tune.QSpeed, c, 1.5f);
                p.evolved = s.evolved;
            }
            Glow(ppos + dir * 0.3f, c, 1f, 0.2f);
            Sparks(ppos + dir * 0.3f, c, 8, 3f, 0.1f, 0.25f);
            facing = dir;
            Shake(0.2f);
            StartCooldown(0);
        }

        // W 공허의 파동: 자기 중심 원형 피해 + 넉백 (진화하면 기절, 이중 파동·중력 붕괴 증강)
        void CastW()
        {
            var s = skills[1];
            if (s.level == 0 || s.cdLeft > 0) return;
            VoidWave(1f);
            if (mods.wDouble) After(Tune.WDoubleDelay, () => VoidWave(Tune.WDoubleRatio));
            StartCooldown(1);
        }

        void VoidWave(float power)
        {
            var s = skills[1];
            float radius = (Tune.WRadius(s.level) + (s.evolved ? Tune.WEvolveRadius : 0f)) * (1 + mods.skillArea) * (1 + mods.wRadiusPct);
            float dmg = SkillDamage(1, Tune.WDamage(s.level)) * power * (mods.wPull ? Tune.WPullDamage : 1f);
            Color c = Palette.Void;
            Glow(ppos, c, radius * 2.4f, 0.3f);
            SpawnFx(ppos, Art.Circle, Palette.Fade(c, 0.35f), radius * 1.2f, radius * 2f, 0.25f, 4);
            SpawnFx(ppos, Art.Ring, c, 0.3f, radius * 2f, 0.35f, 10);
            SpawnFx(ppos, Art.Ring, Color.white, 0.3f, radius * 1.6f, 0.22f, 10);
            for (int k = 0; k < 20; k++)
            {
                var f = SpawnFx(ppos, Art.Glow, c, 0.3f, 0.05f, 0.4f, 12);
                if (f == null) break;
                Vector2 d = Rotate(Vector2.right, k * Mathf.PI / 10f);
                f.vel = (mods.wPull ? -d : d) * radius * 3f;
                if (mods.wPull) f.pos = ppos + d * radius;
                f.drag = 3f;
            }

            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || !InReach(e, ppos, radius)) continue;
                float resist = e.IsBoss ? Tune.BossResist : 1f;
                Vector2 away = DirOr(e.pos - ppos, facing);
                e.knock += (mods.wPull ? -away * 0.8f : away) * Tune.WKnockback * resist;   // 중력 붕괴: 밀지 않고 끌어당긴다
                if (s.evolved) e.stun = Mathf.Max(e.stun, Tune.WEvolveStun * resist);
                bool crit;
                float hit = RollCrit(dmg, out crit);
                DealDamage(e, hit, crit, 0, HitKind.Skill);
            }
            Shake(0.6f * power);
        }

        // E 섬광 돌진: 커서 방향 대시를 14단계로 나눠 이동하며 경로의 적을 타격 (순간이동하면 적을 통과만 한다)
        void CastE(Vector2 cursor)
        {
            var s = skills[2];
            if (s.level == 0) return;
            if (s.cdLeft <= 0 && s.stock <= 0) s.stock = 1 + mods.eCharges;   // 연속 돌진: 쿨다운이 끝나면 충전이 가득 찬다
            if (s.stock <= 0) return;
            Vector2 dir = DirOr(cursor - ppos, facing);
            float dist = Tune.EDistance(s.level) * (1 + mods.skillArea * 0.5f);
            float dmg = SkillDamage(2, Tune.EDamage(s.level));
            Vector2 start = ppos;

            Glow(start, Palette.Marker, 1f, 0.2f);
            dashSet.Clear();
            dashing = true; dashKilled = false;
            for (int step = 1; step <= Tune.EDashSteps; step++)
            {
                Vector2 at = start + dir * (dist * step / Tune.EDashSteps);
                var list = enemies.Active;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (!e.alive || dashSet.Contains(e) || !InReach(e, at, Tune.PlayerRadius + Tune.EHitPadding)) continue;
                    dashSet.Add(e);
                    bool crit;
                    float hit = RollCrit(dmg, out crit);
                    DealDamage(e, hit, crit, 0, HitKind.Skill);
                    Sparks(e.pos, Palette.Marker, 3, 2.5f, 0.08f, 0.2f);
                }
                SpawnFx(at, Art.Glow, Palette.Dash, 0.5f, 0.1f, 0.3f, 7);
                if (step % 3 == 0) Ghost(at, 0.25f + 0.04f * step);
            }
            dashing = false;

            ppos = start + dir * dist;
            facing = dir;
            moveTarget = null;
            attackLock = 0;
            eBuff = Tune.EBuffTime;
            Glow(ppos, Palette.Marker, 1.2f, 0.25f);
            Sparks(ppos, Palette.Marker, 8, 3f, 0.1f, 0.25f);
            Shake(0.4f);

            if (mods.eAfterBlast)
            {
                // 잔상 폭발: 지나간 길이 잠시 뒤 터진다
                float blastDmg = dmg * Tune.EAfterRatio, blastR = Tune.EAfterRadius * (1 + mods.skillArea);
                After(Tune.EAfterDelay, () =>
                {
                    for (int k = 0; k < 5; k++) Blast(start + dir * (dist * (k + 0.5f) / 5f), blastR, blastDmg, Palette.Marker, 0f);
                    Shake(0.3f);
                });
            }
            if (mods.eGuard) invulnUntil = elapsed + Tune.EGuardTime;

            s.stock--;
            if (s.stock <= 0)
            {
                StartCooldown(2);
                if (s.evolved && dashKilled) s.cdLeft *= 1f - Tune.EEvolveRefund;
            }
        }

        // ================================================================ 패시브 스킬 (자동 발동)
        // 레벨업 카드로 얻는다. 쿨다운마다 알아서 터지고, Lv 5 다음에 진화 카드가 나온다.

        float SkillPower
        {
            get
            {
                float synergy = mods.synergyFinale ? Tune.SynergyDamage : 1f;
                float resonance = mods.resonance && OwnedPassives >= 3 ? Tune.ResonanceDamage : 1f;
                return Attack * Tune.SkillDamageBoost * (1 + mods.skillDmgPct) * synergy * resonance;
            }
        }

        float PassiveDamage(int id, float coef)
        {
            return SkillPower * coef * (passives[id].evolved ? Tune.PassiveEvolveDamage : 1f);
        }

        // 스킬 범위 배율 (광역화 증강 + 그 패시브 전용 크기 증강)
        float Area(int id)
        {
            return (1 + mods.skillArea) * (id >= 0 ? 1 + mods.passiveSize[id] : 1f);
        }

        float Duration { get { return 1 + mods.skillDuration; } }

        int OwnedPassives
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Passive.Count; i++) if (passives[i] != null && passives[i].level > 0) n++;
                return n;
            }
        }

        void UpdatePassives(float dt)
        {
            UpdateShurikens(dt);
            UpdateLasers(dt);
            for (int id = 0; id < Passive.Count; id++)
            {
                var s = passives[id];
                if (s.level == 0 || id == Passive.Shuriken) continue;
                if (id == Passive.Laser && s.active > 0) continue;   // 레이저는 켜져 있는 동안 쿨다운이 돌지 않는다
                s.timer -= dt;
                if (s.timer > 0) continue;
                s.timer = Passive.Cooldown(id, s.level, mods.SkillCdMult);
                switch (id)
                {
                    case Passive.Thunder: FireThunder(s); break;
                    case Passive.Acid: FireAcid(s); break;
                    case Passive.Missile: FireMissiles(s); break;
                    case Passive.Frost: FireFrost(s, 1f); break;
                    case Passive.Laser: s.active = Tune.LaserTime * Duration; break;
                    case Passive.Vortex: FireVortex(s); break;
                    case Passive.Meteor: FireMeteors(s); break;
                }
            }
            UpdateZones(dt);
            UpdateMeteors(dt);
            UpdateDelayed(dt);
        }

        // 사거리 안의 적 중 무작위로 n명을 picks 에 고른다 (겹치지 않게)
        void PickTargets(float range, int n)
        {
            picks.Clear();
            FindNearby(ppos, range, null);
            for (int i = 0; i < n && nearby.Count > 0; i++)
            {
                int k = Random.Range(0, nearby.Count);
                picks.Add(nearby[k]);
                nearby[k] = nearby[nearby.Count - 1];
                nearby.RemoveAt(nearby.Count - 1);
            }
        }

        void EnsureSprites(List<SpriteRenderer> list, int count, Sprite sprite, int order, string name)
        {
            while (list.Count < count) list.Add(Art.CreateSprite(name, transform, sprite, Color.white, order));
            for (int i = 0; i < list.Count; i++)
            {
                bool on = i < count;
                if (list[i].gameObject.activeSelf != on) list[i].gameObject.SetActive(on);
            }
        }

        // ---- 회전 표창: 몸 주위를 도는 표창이 닿는 적을 벤다 ----
        void UpdateShurikens(float dt)
        {
            var s = passives[Passive.Shuriken];
            int count = s.level == 0 ? 0 : 2 + s.level / 2 + mods.passiveExtra[Passive.Shuriken] + mods.skillAmount + (s.evolved ? Tune.ShurikenEvolveExtra : 0);
            EnsureSprites(shurikenSprites, count, Art.Shuriken, 10, "Shuriken");
            if (count == 0) return;

            s.angle += Tune.ShurikenSpin * (mods.shurikenFast ? Tune.ShurikenFastMult : 1f) * dt;
            float orbit = (Tune.ShurikenOrbit + Tune.ShurikenOrbitPerLv * s.level) * Area(Passive.Shuriken);
            if (s.evolved) orbit *= 1f + 0.35f * Mathf.Sin(elapsed * 2.2f);   // 칼날 폭풍: 궤도가 숨 쉬듯 커졌다 작아진다
            float dmg = PassiveDamage(Passive.Shuriken, Passive.Coef(Passive.Shuriken, s.level)) * (mods.shurikenFast ? Tune.ShurikenFastDamage : 1f);
            float hitRadius = Tune.ShurikenHitRadius * (1 + mods.skillArea);
            Color c = Passive.Colors[Passive.Shuriken];
            var list = enemies.Active;
            for (int i = 0; i < count; i++)
            {
                float a = s.angle + i * Mathf.PI * 2f / count;
                Vector2 pos = ppos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * orbit;
                var t = shurikenSprites[i].transform;
                t.position = new Vector3(pos.x, pos.y, 0f);
                t.rotation = Quaternion.Euler(0f, 0f, -elapsed * 900f);
                t.localScale = Vector3.one * (s.evolved ? 1.35f : 1f);
                if ((stepCount + i) % 3 == 0) SpawnFx(pos, Art.Glow, Palette.Fade(c, 0.45f), 0.4f, 0.05f, 0.2f, 9);

                for (int k = 0; k < list.Count; k++)
                {
                    var e = list[k];
                    if (!e.alive || elapsed < e.shurikenReady || !InReach(e, pos, hitRadius)) continue;
                    e.shurikenReady = elapsed + Tune.ShurikenHitEvery;
                    e.knock += DirOr(e.pos - ppos, Vector2.right) * (e.IsBoss ? 0.2f : 1.2f);
                    bool crit;
                    float hit = RollCrit(dmg, out crit);
                    DealDamage(e, hit, crit, 0, HitKind.Passive);
                    Sparks(pos, c, 2, 2f, 0.07f, 0.15f);
                }
            }
        }

        // ---- 낙뢰: 무작위 적에게 번개 ----
        void FireThunder(PassiveState s)
        {
            int n = 2 + s.level + mods.passiveExtra[Passive.Thunder] + mods.skillAmount;
            PickTargets(Tune.ThunderRange, n);
            if (picks.Count == 0) { s.timer = 0.3f; return; }   // 적이 없으면 곧 다시 확인
            float radius = Tune.ThunderRadius * Area(Passive.Thunder);
            float dmg = PassiveDamage(Passive.Thunder, Passive.Coef(Passive.Thunder, s.level));
            Color c = Passive.Colors[Passive.Thunder];
            for (int i = 0; i < picks.Count; i++)
            {
                Enemy target = picks[i];
                Vector2 at = target.pos;
                Lightning(at + new Vector2(Random.Range(-0.7f, 0.7f), 4.6f), at, c, 0.08f);
                Blast(at, radius, dmg, c, mods.thunderStun ? Tune.ThunderStun : 0f);
                if (s.evolved) ChainLightning(target, dmg * 0.5f, 3, c);
            }
            Shake(0.25f);
        }

        // ---- 산성 웅덩이: 적이 모인 곳에 웅덩이 ----
        void FireAcid(PassiveState s)
        {
            int n = 1 + s.level / 2 + mods.passiveExtra[Passive.Acid] + mods.skillAmount;
            float radius = (Tune.AcidRadius + Tune.AcidRadiusPerLv * s.level) * Area(Passive.Acid) * (s.evolved ? 1.5f : 1f);
            float dmg = PassiveDamage(Passive.Acid, Passive.Coef(Passive.Acid, s.level)) * (mods.acidCorrode ? Tune.AcidCorrodeDamage : 1f);
            float slow = mods.acidCorrode ? Tune.AcidCorrodeSlow : s.evolved ? Tune.AcidEvolveSlow : 1f;
            PickTargets(Tune.AcidRange, n);
            for (int i = 0; i < n; i++)
            {
                Vector2 at = i < picks.Count ? picks[i].pos : ppos + Random.insideUnitCircle * 2.5f;
                SpawnZone(ZoneKind.Acid, at, radius, Tune.AcidTime * Duration, Tune.AcidTick, dmg, slow);
                Sparks(at, Passive.Colors[Passive.Acid], 5, 2.2f, 0.1f, 0.3f);
            }
        }

        // ---- 유도 미사일: 적을 쫓아가 터진다 ----
        void FireMissiles(PassiveState s)
        {
            int n = 1 + s.level / 2 + mods.passiveExtra[Passive.Missile] + mods.skillAmount + (s.evolved ? 2 : 0);
            PickTargets(Tune.MissileRange, n);
            if (picks.Count == 0) { s.timer = 0.3f; return; }
            float dmg = PassiveDamage(Passive.Missile, Passive.Coef(Passive.Missile, s.level));
            for (int i = 0; i < n; i++)
            {
                Enemy target = picks[i % picks.Count];
                // 위쪽으로 부채꼴로 튀어나갔다가 방향을 꺾어 적을 쫓는다
                float spread = (i - (n - 1) / 2f) * 0.45f + Random.Range(-0.15f, 0.15f);
                Vector2 dir = Rotate(Vector2.up, spread);
                var p = NewProjectile(ProjKind.Missile, ppos, dir * Tune.MissileSpeed * 0.5f, Tune.MissileRadius, dmg, Tune.MissileLife, Color.white, 1f);
                p.sr.sprite = Art.Missile;
                p.tf.localScale = Vector3.one * (s.evolved ? 1.3f : 1f);
                p.target = target; p.targetSerial = target.serial;
                p.evolved = s.evolved;
            }
            Glow(ppos, Passive.Colors[Passive.Missile], 0.9f, 0.15f);
        }

        void MissileBoom(Projectile p)
        {
            float radius = Tune.MissileBlast * Area(Passive.Missile) * (p.evolved ? 1.4f : 1f);
            float dmg = p.damage;
            Vector2 at = p.pos;
            Color c = Passive.Colors[Passive.Missile];
            Blast(at, radius, dmg, c, 0f);
            Shake(0.12f);
            if (mods.missileCluster)
            {
                // 집속탄: 작은 폭발 4개가 사방으로 퍼진다
                for (int k = 0; k < 4; k++)
                {
                    Vector2 off = Rotate(Vector2.right, k * Mathf.PI / 2f + Random.Range(-0.3f, 0.3f)) * radius * 0.9f;
                    After(0.1f + k * 0.04f, () => Blast(at + off, radius * 0.6f, dmg * 0.4f, c, 0f));
                }
            }
            p.alive = false;
        }

        // ---- 냉기 폭발: 주변에 냉기를 터뜨려 느리게 (얼리기) ----
        void FireFrost(PassiveState s, float power)
        {
            float radius = (Tune.FrostRadius + Tune.FrostRadiusPerLv * s.level) * Area(Passive.Frost);
            float dmg = PassiveDamage(Passive.Frost, Passive.Coef(Passive.Frost, s.level)) * power;
            bool freeze = mods.frostFreeze || s.evolved;
            float freezeTime = s.evolved ? 1.2f : 1f;
            Color c = Passive.Colors[Passive.Frost];
            Glow(ppos, c, radius * 2.4f, 0.3f);
            SpawnFx(ppos, Art.Circle, Palette.Fade(c, 0.3f), radius * 0.4f, radius * 2f, 0.3f, 4);
            SpawnFx(ppos, Art.Ring, c, radius * 0.3f, radius * 2f, 0.35f, 10);
            for (int k = 0; k < 16; k++)
            {
                var f = SpawnFx(ppos, Art.Star, Color.white, 0.25f, 0.1f, 0.45f, 12);
                if (f == null) break;
                f.vel = Rotate(Vector2.right, k * Mathf.PI / 8f) * radius * 2.4f;
                f.drag = 3f;
                f.spin = 360f;
            }
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || !InReach(e, ppos, radius)) continue;
                float resist = e.IsBoss ? Tune.BossResist : 1f;
                Slow(e, Tune.FrostSlow, Tune.FrostSlowTime * Duration);
                if (freeze)
                {
                    e.stun = Mathf.Max(e.stun, freezeTime * resist);
                    e.iceUntil = elapsed + freezeTime * resist;
                }
                bool crit;
                float hit = RollCrit(dmg, out crit);
                DealDamage(e, hit, crit, 0, HitKind.Passive);
            }
            Shake(0.3f * power);
            if (s.evolved && power >= 1f) After(0.35f, () => FireFrost(s, 0.6f));   // 절대 빙결: 한 번 더
        }

        // ---- 회전 레이저: 잠시 켜져서 몸 주위를 돈다 ----
        void UpdateLasers(float dt)
        {
            var s = passives[Passive.Laser];
            int beams = 0;
            if (s.level > 0 && s.active > 0)
            {
                s.active -= dt;
                beams = (1 + (s.level >= 3 ? 1 : 0) + mods.passiveExtra[Passive.Laser] + mods.skillAmount) * (s.evolved ? 2 : 1);
                if (s.active <= 0) { s.timer = Passive.Cooldown(Passive.Laser, s.level, mods.SkillCdMult); beams = 0; }
            }
            EnsureSprites(laserSprites, beams * 2, Art.Pixel, 9, "Laser");   // 광선마다 바깥 빛 + 흰 심
            if (beams == 0) return;

            s.angle += Tune.LaserSpin * dt;
            float length = (Tune.LaserLength + Tune.LaserLengthPerLv * s.level) * Area(Passive.Laser);
            float width = Tune.LaserWidth * (mods.laserPower ? 2f : 1f) * (1 + mods.skillArea);
            float dmg = PassiveDamage(Passive.Laser, Passive.Coef(Passive.Laser, s.level)) * (mods.laserPower ? Tune.LaserPowerDamage : 1f);
            Color c = Passive.Colors[Passive.Laser];
            float pulse = 0.8f + 0.2f * Mathf.Sin(elapsed * 40f);
            var list = enemies.Active;
            for (int b = 0; b < beams; b++)
            {
                float a = s.angle + b * Mathf.PI * 2f / beams;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Vector2 end = ppos + dir * length;
                Vector2 mid = (ppos + end) * 0.5f;
                for (int layer = 0; layer < 2; layer++)
                {
                    var sr = laserSprites[b * 2 + layer];
                    sr.sortingOrder = 9 + layer;
                    sr.transform.position = new Vector3(mid.x, mid.y, 0f);
                    sr.transform.rotation = Quaternion.Euler(0f, 0f, a * Mathf.Rad2Deg);
                    sr.transform.localScale = new Vector3(length, layer == 0 ? width * 2.4f * pulse : width * 0.6f, 1f);
                    sr.color = layer == 0 ? Palette.Fade(c, 0.55f) : Color.white;
                }
                if (stepCount % 2 == 0) SpawnFx(end, Art.Glow, c, 0.6f, 0.2f, 0.12f, 11);

                for (int k = 0; k < list.Count; k++)
                {
                    var e = list[k];
                    if (!e.alive || elapsed < e.laserReady) continue;
                    if (DistToSegment(e.pos, ppos, end) > width * 0.5f + e.radius) continue;
                    e.laserReady = elapsed + Tune.LaserHitEvery;
                    bool crit;
                    float hit = RollCrit(dmg, out crit);
                    DealDamage(e, hit, crit, 0, HitKind.Passive);
                    Sparks(e.pos, c, 2, 1.8f, 0.07f, 0.15f);
                }
            }
            if (stepCount % 3 == 0) Glow(ppos, c, 0.8f, 0.12f);
        }

        // ---- 블랙홀: 적 무리 한가운데에서 끌어당기며 피해 ----
        void FireVortex(PassiveState s)
        {
            PickTargets(Tune.VortexRange, 1);
            if (picks.Count == 0) { s.timer = 0.3f; return; }
            float radius = (Tune.VortexRadius + Tune.VortexRadiusPerLv * s.level) * Area(Passive.Vortex) * (s.evolved ? 1.5f : 1f);
            float dmg = PassiveDamage(Passive.Vortex, Passive.Coef(Passive.Vortex, s.level));
            var z = SpawnZone(ZoneKind.Vortex, picks[0].pos, radius, Tune.VortexTime * Duration, Tune.VortexTick, dmg, 1f);
            z.pull = Tune.VortexPull * (1 + mods.passiveSize[Passive.Vortex]);
            if (mods.vortexCollapse || s.evolved) z.collapse = dmg * Tune.VortexCollapseRatio;
            Glow(z.pos, Passive.Colors[Passive.Vortex], radius * 2f, 0.3f);
        }

        // ---- 메테오: 운석이 떨어져 넓게 터진다 ----
        void FireMeteors(PassiveState s)
        {
            int n = 1 + s.level / 2 + mods.passiveExtra[Passive.Meteor] + mods.skillAmount + (s.evolved ? 1 : 0);
            float radius = (Tune.MeteorRadius + Tune.MeteorRadiusPerLv * s.level) * Area(Passive.Meteor) * (s.evolved ? 1.3f : 1f);
            float dmg = PassiveDamage(Passive.Meteor, Passive.Coef(Passive.Meteor, s.level));
            PickTargets(Tune.MeteorRange, n);
            for (int i = 0; i < n; i++)
            {
                Vector2 at = i < picks.Count ? picks[i].pos : ppos + Random.insideUnitCircle * 3f;
                var m = meteors.Get();
                m.to = at;
                m.from = at + new Vector2(-2.4f, 5f);
                m.t = 0f;
                m.duration = Tune.MeteorFall + i * 0.12f;
                m.damage = dmg; m.radius = radius;
                m.burn = mods.meteorBurn || s.evolved;
                m.sr.sprite = Art.Meteor;
                m.sr.color = Color.white;
                m.tf.localScale = Vector3.one * (s.evolved ? 1.6f : 1.25f);
                m.tf.position = new Vector3(m.from.x, m.from.y, 0f);
                // 떨어질 자리: 붉은 그림자가 점점 커진다
                SpawnFx(at, Art.Circle, new Color(1f, 0.3f, 0.2f, 0.35f), radius * 0.3f, radius * 2f, m.duration, -3);
            }
        }

        void UpdateMeteors(float dt)
        {
            var list = meteors.Active;
            Color c = Passive.Colors[Passive.Meteor];
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (!m.alive) continue;
                m.t += dt;
                float k = m.t / m.duration;
                if (k >= 1f)
                {
                    m.alive = false;
                    Blast(m.to, m.radius, m.damage, c, 0f);
                    SpawnFx(m.to, Art.Puddle, new Color(0.08f, 0.05f, 0.05f, 0.6f), m.radius * 1.8f, m.radius * 2f, 1.6f, -4);   // 그을린 자국
                    Shake(0.35f);
                    if (m.burn) SpawnZone(ZoneKind.Fire, m.to, m.radius * 0.9f, Tune.BurnTime * Duration, Tune.BurnTick, m.damage * Tune.BurnRatio, 1f);
                    continue;
                }
                Vector2 p = Vector2.Lerp(m.from, m.to, k * k);
                m.tf.position = new Vector3(p.x, p.y, 0f);
                m.tf.rotation = Quaternion.Euler(0f, 0f, m.t * 400f);
                if (stepCount % 2 == 0)
                {
                    var f = SpawnFx(p, Art.Glow, c, 0.6f, 0.1f, 0.3f, 11);
                    if (f != null) f.vel = new Vector2(-1.2f, 2.5f) * 0.3f;
                }
            }
        }

        // ---- 장판 (산성·불·블랙홀) ----
        Zone SpawnZone(ZoneKind kind, Vector2 at, float radius, float duration, float tick, float dmg, float slow)
        {
            var z = zones.Get();
            z.kind = kind;
            z.pos = at; z.radius = radius;
            z.life = z.duration = duration;
            z.tickEvery = tick; z.tick = 0.05f;
            z.damage = dmg; z.slow = slow;
            z.pull = 0f; z.collapse = 0f; z.spin = Random.value * 360f;
            z.color = kind == ZoneKind.Acid ? Passive.Colors[Passive.Acid] : kind == ZoneKind.Fire ? Passive.Colors[Passive.Meteor] : Passive.Colors[Passive.Vortex];
            z.sr.sprite = kind == ZoneKind.Vortex ? Art.Spiral : Art.Puddle;
            z.sr.sortingOrder = kind == ZoneKind.Vortex ? 3 : -4;
            z.sr.color = Palette.Fade(z.color, 0f);
            z.tf.position = new Vector3(at.x, at.y, 0f);
            z.tf.rotation = Quaternion.Euler(0f, 0f, z.spin);
            z.tf.localScale = Vector3.zero;
            return z;
        }

        void UpdateZones(float dt)
        {
            var list = zones.Active;
            var elist = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var z = list[i];
                if (!z.alive) continue;
                z.life -= dt;
                if (z.life <= 0)
                {
                    z.alive = false;
                    if (z.collapse > 0) { Blast(z.pos, z.radius * 1.3f, z.collapse, z.color, 0f); Shake(0.5f); }
                    continue;
                }
                float age = z.duration - z.life;
                float grow = Mathf.Min(1f, age * 6f), fade = Mathf.Min(1f, z.life * 2.5f);
                bool vortex = z.kind == ZoneKind.Vortex;
                z.tf.localScale = Vector3.one * (z.radius * 2f * (vortex ? 1.1f : 1.15f) * grow);
                z.sr.color = Palette.Fade(z.color, (vortex ? 0.9f : 0.6f) * fade);

                if (vortex)
                {
                    z.spin -= dt * 420f;
                    z.tf.rotation = Quaternion.Euler(0f, 0f, z.spin);
                    float reach = z.radius * 1.5f;
                    for (int k = 0; k < elist.Count; k++)
                    {
                        var e = elist[k];
                        if (!e.alive) continue;
                        Vector2 to = z.pos - e.pos;
                        float d = to.magnitude;
                        if (d > reach + e.radius || d < 0.05f) continue;
                        e.pos += to / d * (z.pull * dt * (e.IsBoss ? 0.15f : 1f) * Mathf.Min(1f, d));
                    }
                    if (stepCount % 2 == 0)
                    {
                        Vector2 dir = Rotate(Vector2.right, Random.value * Mathf.PI * 2f);
                        var f = SpawnFx(z.pos + dir * reach, Art.Glow, z.color, 0.25f, 0.05f, 0.4f, 11);
                        if (f != null) f.vel = -dir * reach * 2.4f;
                    }
                }
                else if (stepCount % 5 == 0)
                {
                    var f = SpawnFx(z.pos + Random.insideUnitCircle * z.radius * 0.8f, Art.Glow, Color.Lerp(z.color, Color.white, 0.4f), 0.12f, 0.3f, 0.45f, -3);
                    if (f != null) f.vel = new Vector2(0f, 0.35f);
                }

                z.tick -= dt;
                if (z.tick > 0) continue;
                z.tick += z.tickEvery;
                for (int k = 0; k < elist.Count; k++)
                {
                    var e = elist[k];
                    if (!e.alive || !InReach(e, z.pos, z.radius)) continue;
                    if (z.slow < 1f) Slow(e, z.slow, 0.6f);
                    bool crit;
                    float hit = RollCrit(z.damage, out crit);
                    DealDamage(e, hit, crit, 0, HitKind.Passive);
                }
            }
        }

        // ---- 공용 ----

        // 스킬 범위 공격: 치명타가 터지고, 처치 폭발도 일으킨다 (직접 공격 취급)
        void Blast(Vector2 at, float radius, float dmg, Color color, float stun)
        {
            Glow(at, color, radius * 2.6f, 0.25f);
            SpawnFx(at, Art.Circle, Palette.Fade(color, 0.4f), radius * 0.5f, radius * 2f, 0.2f, 4);
            SpawnFx(at, Art.Ring, color, radius * 0.6f, radius * 2.2f, 0.3f, 10);
            Sparks(at, color, 6, radius * 4f, 0.1f, 0.3f);
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || !InReach(e, at, radius)) continue;
                if (stun > 0) e.stun = Mathf.Max(e.stun, stun * (e.IsBoss ? Tune.BossResist : 1f));
                bool crit;
                float hit = RollCrit(dmg, out crit);
                DealDamage(e, hit, crit, 0, HitKind.Passive);
            }
        }

        void ChainLightning(Enemy from, float dmg, int hops, Color color)
        {
            chainSet.Clear();
            chainSet.Add(from);
            Enemy cur = from;
            for (int k = 0; k < hops; k++)
            {
                Enemy next = Nearest(cur.pos, Tune.ChainRange, chainSet);
                if (next == null) break;
                chainSet.Add(next);
                Lightning(cur.pos, next.pos, color, 0.05f);
                DealDamage(next, dmg, false, 1, HitKind.Passive);
                cur = next;
            }
        }

        void Slow(Enemy e, float factor, float time)
        {
            if (e.IsBoss) factor = Mathf.Lerp(factor, 1f, 0.6f);
            e.slowFactor = elapsed >= e.slowUntil ? factor : Mathf.Min(e.slowFactor, factor);
            e.slowUntil = Mathf.Max(e.slowUntil, elapsed + time);
        }

        void After(float delay, System.Action action)
        {
            delayed.Add(new Delayed { t = delay, action = action });
        }

        void UpdateDelayed(float dt)
        {
            for (int i = delayed.Count - 1; i >= 0; i--)
            {
                if (i >= delayed.Count) continue;
                var d = delayed[i];
                d.t -= dt;
                if (d.t > 0) continue;
                delayed.RemoveAt(i);
                d.action();
            }
        }

        static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        static Vector2 RotateTowards(Vector2 from, Vector2 to, float maxRadians)
        {
            float angle = Mathf.Atan2(from.x * to.y - from.y * to.x, from.x * to.x + from.y * to.y);
            return Rotate(from, Mathf.Clamp(angle, -maxRadians, maxRadians));
        }

        static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(((p.x - a.x) * ab.x + (p.y - a.y) * ab.y) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (a + ab * t - p).magnitude;
        }

        // ---- 화려한 효과 ----

        Fx Glow(Vector2 at, Color color, float size, float life)
        {
            return SpawnFx(at, Art.Glow, color, size, size * 1.3f, life, 11);
        }

        void Sparks(Vector2 at, Color color, int n, float speed, float size, float life)
        {
            for (int i = 0; i < n; i++)
            {
                var f = SpawnFx(at, Art.Glow, color, size * 2.4f, 0f, life * Random.Range(0.6f, 1.2f), 12);
                if (f == null) return;
                f.vel = Rotate(Vector2.right, Random.value * Mathf.PI * 2f) * speed * Random.Range(0.4f, 1f);
                f.drag = 4f;
            }
        }

        Fx Beam(Vector2 a, Vector2 b, float width, Color color, float life, int order)
        {
            Vector2 d = b - a;
            var f = SpawnFx((a + b) * 0.5f, Art.Pixel, color, 1f, 1f, life, order);
            if (f == null) return null;
            f.size = new Vector2(d.magnitude, width);
            f.tf.localScale = new Vector3(d.magnitude, width, 1f);
            f.tf.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            return f;
        }

        void Lightning(Vector2 from, Vector2 to, Color color, float width)
        {
            const int segments = 6;
            Vector2 prev = from;
            for (int i = 1; i <= segments; i++)
            {
                Vector2 next = Vector2.Lerp(from, to, i / (float)segments);
                if (i < segments) next += new Vector2(Random.Range(-0.25f, 0.25f), Random.Range(-0.1f, 0.1f));
                Beam(prev, next, width * 3.5f, Palette.Fade(color, 0.45f), 0.2f, 11);
                Beam(prev, next, width, Color.white, 0.16f, 12);
                prev = next;
            }
            Glow(to, color, 1.4f, 0.25f);
        }

        // 플레이어 잔상
        void Ghost(Vector2 at, float alpha)
        {
            var f = SpawnFx(at, playerSr.sprite, Palette.Fade(Palette.Haste, alpha), 1f, 1f, 0.3f, 7);
            if (f != null) f.sr.flipX = playerSr.flipX;
        }

        // ================================================================ 적

        void UpdateSpawning(float dt)
        {
            spawnTimer -= dt;
            if (spawnTimer <= 0)
            {
                spawnTimer += Tune.SpawnInterval(elapsed);
                int n = Tune.SpawnCount(elapsed);
                for (int i = 0; i < n && enemies.Count < Tune.MaxEnemies; i++) SpawnNormal(PickKind(), false);
            }
            if (elapsed >= (eliteIndex + 1) * Tune.EliteEvery)
            {
                eliteIndex++;
                SpawnNormal(PickKind(), true);
            }
            if (elapsed >= (bossIndex + 1) * Tune.BossEvery)
            {
                bossIndex++;
                bool big = bossIndex % Tune.BigBossEveryNthBoss == 0;
                SpawnBoss(big);
                Announce(big ? "대형 보스 등장!" : "보스 등장!");
            }
        }

        // [임시] 원본의 적 종류별 등장 비율을 몰라서 정한 값
        EnemyKind PickKind()
        {
            float g = 60f, r = elapsed > 20 ? 28f : 0f, b = elapsed > 45 ? 14f : 0f;
            float x = Random.value * (g + r + b);
            if (x < g) return EnemyKind.Grunt;
            if (x < g + r) return EnemyKind.Runner;
            return EnemyKind.Brute;
        }

        Vector2 SpawnPoint()
        {
            float a = Random.value * Mathf.PI * 2f;
            return ppos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(Tune.SpawnMin, Tune.SpawnMax);
        }

        void SpawnNormal(EnemyKind kind, bool elite)
        {
            var e = enemies.Get();
            float t = elapsed;
            e.rank = elite ? Rank.Elite : Rank.Normal;
            e.radius = kind.radius * (elite ? Tune.EliteRadius : 1f);
            e.maxHp = kind.hp * Tune.HpScale(t) * (elite ? Tune.EliteHp : 1f);
            e.damage = kind.damage * Tune.DamageScale(t) * (elite ? Tune.EliteDamage : 1f);
            e.speed = kind.speed * Tune.SpeedScale(t) * (elite ? Tune.EliteSpeed : 1f);
            e.xp = kind.xp * (elite ? Tune.EliteXp : 1);
            e.color = elite ? Palette.Elite : kind.color;
            e.look = elite ? kind.look + Art.EliteOffset : kind.look;
            e.scale = elite ? Tune.EliteRadius : 1f;
            InitEnemy(e, elite ? 6 : 5);
        }

        void SpawnBoss(bool big)
        {
            var e = enemies.Get();
            float t = elapsed;
            e.rank = big ? Rank.BigBoss : Rank.Boss;
            e.radius = big ? Tune.BigBossRadius : Tune.BossRadius;
            e.maxHp = big ? Tune.BigBossHp(t) : Tune.BossHp(t);
            e.damage = big ? Tune.BigBossDamage(t) : Tune.BossDamage(t);
            e.speed = big ? Tune.BigBossSpeed : Tune.BossSpeed;
            e.xp = big ? Tune.BigBossXp : Tune.BossXp;
            e.color = big ? Palette.BigBoss : Palette.Boss;
            e.look = big ? Art.LookMutant : Art.LookGoggle;
            e.scale = 1f;
            InitEnemy(e, 7);
        }

        void InitEnemy(Enemy e, int order)
        {
            e.serial = ++serial;
            e.hp = e.maxHp;
            e.pos = SpawnPoint();
            e.knock = Vector2.zero;
            e.stun = e.flash = e.fade = 0f;
            e.contactReady = 0f;
            e.shurikenReady = e.laserReady = 0f;
            e.slowUntil = e.iceUntil = 0f;
            e.slowFactor = 1f;
            e.barrage = 1f;
            e.sr.sprite = Art.Monster[e.look];
            e.sr.sortingOrder = order;
            e.sr.color = Color.white;
            e.sr.flipX = false;
            e.tf.position = new Vector3(e.pos.x, e.pos.y, 0f);
            e.tf.localScale = Vector3.zero;
        }

        void UpdateEnemies(float dt)
        {
            var list = enemies.Active;
            float knockDecay = Mathf.Exp(-8f * dt);
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive) continue;

                Vector2 to = ppos - e.pos;
                float d = to.magnitude;
                if (e.stun > 0) e.stun -= dt;
                else if (d > 0.001f) e.pos += to / d * (e.speed * (elapsed < e.slowUntil ? e.slowFactor : 1f) * dt);
                e.pos += e.knock * dt;
                e.knock *= knockDecay;

                // 닿으면 피해. 같은 적은 ContactCooldown 마다 한 번
                if (d < Tune.PlayerRadius + e.radius && elapsed >= e.contactReady)
                {
                    e.contactReady = elapsed + Tune.ContactCooldown;
                    DamagePlayer(e.damage);
                }

                // 보스: 가까이 오면 사방으로 탄막
                if (e.IsBoss && d < Tune.BarrageRange)
                {
                    e.barrage -= dt;
                    if (e.barrage <= 0)
                    {
                        e.barrage = e.rank == Rank.BigBoss ? Tune.BigBossBarrageEvery : Tune.BossBarrageEvery;
                        Barrage(e);
                    }
                }

                // 무한 모드: 너무 멀어진 적은 회수해서 플레이어 근처에 다시 배치한다
                if (!e.IsBoss && d > Tune.RecycleDistance) { e.pos = SpawnPoint(); e.fade = 0f; }
            }

            Separate();

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive) continue;
                e.fade = Mathf.Min(1f, e.fade + dt * 4f);
                e.flash -= dt;
                e.tf.position = new Vector3(e.pos.x, e.pos.y, 0f);
                // 말랑말랑하게 출렁이기 (적마다 박자가 다르게)
                float wobble = Mathf.Sin(elapsed * (e.IsBoss ? 5f : 9f) + e.serial * 1.3f);
                float size = e.scale * e.fade;
                e.tf.localScale = new Vector3(size * (1f - 0.05f * wobble), size * (1f + 0.07f * wobble), 1f);
                e.sr.flipX = ppos.x < e.pos.x;   // 그림은 오른쪽을 보고 있다. 플레이어 쪽을 보게
                e.sr.sprite = e.flash > 0 ? Art.MonsterFlash[e.look] : Art.Monster[e.look];
                e.sr.color = elapsed < e.iceUntil ? Palette.Ice
                    : e.stun > 0 ? Palette.Stunned
                    : elapsed < e.slowUntil && e.slowFactor < 1f ? Palette.Slowed
                    : Color.white;
            }
        }

        // 적끼리 겹치지 않게 살짝 밀어낸다 [임시] (보스는 잘 안 밀린다)
        void Separate()
        {
            var list = enemies.Active;
            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                var a = list[i];
                if (!a.alive) continue;
                for (int j = i + 1; j < n; j++)
                {
                    var b = list[j];
                    if (!b.alive) continue;
                    Vector2 delta = b.pos - a.pos;
                    float min = a.radius + b.radius, sq = delta.sqrMagnitude;
                    if (sq >= min * min || sq < 1e-8f) continue;
                    float d = Mathf.Sqrt(sq);
                    Vector2 push = delta / d * ((min - d) * 0.5f);
                    float wa = a.IsBoss ? 0.1f : 1f, wb = b.IsBoss ? 0.1f : 1f, sum = wa + wb;
                    a.pos -= push * (2f * wa / sum);
                    b.pos += push * (2f * wb / sum);
                }
            }
        }

        void Barrage(Enemy boss)
        {
            int n = boss.rank == Rank.BigBoss ? Tune.BigBossShots : Tune.BossShots;
            float offset = Random.value * Mathf.PI * 2f;
            for (int k = 0; k < n; k++)
            {
                float a = offset + k * Mathf.PI * 2f / n;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                NewProjectile(ProjKind.EnemyBullet, boss.pos + dir * boss.radius, dir * Tune.BulletSpeed, Tune.BulletRadius,
                    boss.damage * Tune.BulletDamageRatio, Tune.BulletLife, Palette.Bullet, 1f);
            }
        }

        void DealDamage(Enemy e, float dmg, bool crit, int depth, HitKind kind = HitKind.Basic)
        {
            if (!e.alive) return;
            e.hp -= dmg;
            e.flash = 0.08f;
            Color textColor = crit ? Palette.Crit : kind == HitKind.Skill ? Palette.SkillHit : kind == HitKind.Passive ? Palette.PassiveHit : Palette.DamageText;
            // 패시브 피해 숫자는 작게 (화면이 숫자로 덮이지 않게)
            AddText(e.pos + new Vector2(0, e.radius + 0.1f), Mathf.RoundToInt(dmg).ToString(), textColor, crit || kind == HitKind.Skill, kind == HitKind.Passive && !crit);
            if (e.hp <= 0) Kill(e, depth);
        }

        // depth: 0 = 직접 공격, 1 = 폭발·연쇄 같은 2차 피해. 2차 피해로 죽은 적은 처치 폭발을 일으키지 않는다 (무한 연쇄 방지)
        void Kill(Enemy e, int depth)
        {
            e.alive = false;
            kills++;
            if (dashing) dashKilled = true;
            SpawnOrb(e.pos, e.xp);
            SpawnFx(e.pos, Art.Circle, Palette.Fade(e.color, 0.6f), e.radius * 2f, e.radius * 3.2f, 0.25f, 4);
            Sparks(e.pos, e.color, e.IsBoss ? 24 : 3, e.IsBoss ? 5f : 2f, e.IsBoss ? 0.2f : 0.08f, 0.3f);
            if (e.IsBoss)
            {
                Glow(e.pos, e.color, e.radius * 8f, 0.6f);
                SpawnFx(e.pos, Art.Ring, e.color, e.radius * 2f, e.radius * 8f, 0.6f, 10);
                Announce(e.rank == Rank.BigBoss ? "대형 보스 처치!" : "보스 처치!");
                Shake(1f);
            }
            if (mods.killExplode > 0 && depth == 0)
                Explosion(e.pos, Tune.KillExplodeRadius, e.maxHp * mods.killExplode, 1, Palette.Explosion, null);
        }

        void Explosion(Vector2 at, float radius, float dmg, int depth, Color color, Enemy skip)
        {
            Glow(at, color, radius * 2.2f, 0.2f);
            SpawnFx(at, Art.Circle, Palette.Fade(color, 0.45f), radius * 0.6f, radius * 2f, 0.22f, 4);
            Sparks(at, color, 3, radius * 3f, 0.07f, 0.2f);
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || e == skip || !InReach(e, at, radius)) continue;
                DealDamage(e, dmg, false, depth);
            }
        }

        void Chain(Enemy from, float dmg)
        {
            chainSet.Clear();
            chainSet.Add(from);
            Enemy cur = from;
            for (int k = 0; k < Tune.ChainTargets; k++)
            {
                Enemy next = Nearest(cur.pos, Tune.ChainRange, chainSet);
                if (next == null) break;
                chainSet.Add(next);
                SpawnFx(Vector2.Lerp(cur.pos, next.pos, 0.5f), Art.Circle, Palette.Chain, 0.25f, 0.05f, 0.2f, 10);
                DealDamage(next, dmg, false, 1);
                cur = next;
            }
        }

        static bool InReach(Enemy e, Vector2 at, float radius)
        {
            float reach = radius + e.radius;
            return (e.pos - at).sqrMagnitude <= reach * reach;
        }

        static Enemy Valid(Enemy e, int serialNumber)
        {
            return e != null && e.alive && e.serial == serialNumber ? e : null;
        }

        Enemy Nearest(Vector2 at, float range, HashSet<Enemy> skip)
        {
            Enemy best = null;
            float bestSq = float.MaxValue;
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || (skip != null && skip.Contains(e)) || !InReach(e, at, range)) continue;
                float sq = (e.pos - at).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e; }
            }
            return best;
        }

        // 사거리 안의 다른 적들을 가까운 순으로 nearby 에 담는다
        void FindNearby(Vector2 at, float range, Enemy exclude)
        {
            nearby.Clear();
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.alive && e != exclude && InReach(e, at, range)) nearby.Add(e);
            }
            nearby.Sort((a, b) => (a.pos - at).sqrMagnitude.CompareTo((b.pos - at).sqrMagnitude));
        }

        Enemy EnemyAt(Vector2 point)
        {
            Enemy best = null;
            float bestSq = float.MaxValue;
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || !InReach(e, point, 0.15f)) continue;
                float sq = (e.pos - point).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e; }
            }
            return best;
        }

        Enemy StrongestBoss()
        {
            Enemy best = null;
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.alive && e.IsBoss && (best == null || e.maxHp > best.maxHp)) best = e;
            }
            return best;
        }

        // ================================================================ 투사체

        Projectile NewProjectile(ProjKind kind, Vector2 at, Vector2 vel, float radius, float damage, float life, Color color, float visualScale)
        {
            var p = projectiles.Get();
            p.kind = kind;
            p.pos = at; p.vel = vel; p.speed = vel.magnitude;
            p.radius = radius; p.damage = damage; p.life = life; p.delay = 0f;
            p.target = null; p.targetSerial = 0;
            p.pierceLeft = 0; p.bouncesLeft = 0; p.evolved = false;
            p.hit.Clear();   // 풀에서 꺼낼 때 꼭 비운다 (안 비우면 같은 적을 영원히 못 맞힌다)
            p.trail = 0f;
            p.tf.rotation = Quaternion.identity;
            p.sr.sprite = Art.Circle;
            p.sr.color = color;
            p.sr.enabled = true;
            p.tf.position = new Vector3(at.x, at.y, 0f);
            p.tf.localScale = Vector3.one * (radius * 2f * visualScale);
            return p;
        }

        // 총구 위치 (캐릭터 그림 기준)
        Vector2 GunTip()
        {
            return ppos + new Vector2(playerSr.flipX ? -Art.HeroGunX : Art.HeroGunX, Art.HeroGunY);
        }

        void SpawnBolt(Enemy target, float dmg, float delay)
        {
            Vector2 from = GunTip();
            Vector2 dir = DirOr(target.pos - from, facing);
            var p = NewProjectile(ProjKind.Basic, from, dir * Tune.BoltSpeed, Tune.BoltRadius, dmg, Tune.BoltLife, Palette.Bolt, 1.8f);
            p.target = target; p.targetSerial = target.serial;
            p.pierceLeft = mods.pierce;
            p.bouncesLeft = mods.ricochet;
            p.delay = delay;
            p.sr.enabled = delay <= 0;
        }

        void UpdateProjectiles(float dt)
        {
            var list = projectiles.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (!p.alive) continue;

                if (p.delay > 0)
                {
                    p.delay -= dt;
                    p.pos = GunTip();
                    p.sr.enabled = p.delay <= 0;
                    p.tf.position = new Vector3(p.pos.x, p.pos.y, 0f);
                    continue;
                }

                p.life -= dt;
                if (p.life <= 0)
                {
                    if (p.kind == ProjKind.QBolt && p.evolved)
                    {
                        Explosion(p.pos, Tune.QExplodeRadius * (1 + mods.skillArea), p.damage * Tune.QExplodeRatio, 1, Palette.Legendary, null);
                        Shake(0.5f);
                    }
                    if (p.kind == ProjKind.Missile) { MissileBoom(p); continue; }
                    p.alive = false;
                    continue;
                }

                if (p.kind == ProjKind.Missile)
                {
                    // 미사일: 점점 빨라지며 대상 쪽으로 방향을 튼다. 대상이 죽으면 근처 적으로
                    Enemy t = Valid(p.target, p.targetSerial);
                    if (t == null)
                    {
                        t = Nearest(p.pos, 5f, null);
                        if (t != null) { p.target = t; p.targetSerial = t.serial; }
                    }
                    float speed = Mathf.Min(Tune.MissileSpeed, p.vel.magnitude + Tune.MissileAccel * dt);
                    Vector2 cur = DirOr(p.vel, Vector2.up);
                    if (t != null) cur = RotateTowards(cur, DirOr(t.pos - p.pos, cur), Tune.MissileTurn * dt);
                    p.vel = cur * speed;
                    p.tf.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(cur.y, cur.x) * Mathf.Rad2Deg);
                    if (stepCount % 2 == 0) SpawnFx(p.pos - cur * 0.16f, Art.Glow, new Color(0.75f, 0.75f, 0.8f, 0.5f), 0.12f, 0.4f, 0.4f, 8);
                }
                else if (p.kind == ProjKind.QBolt)
                {
                    // Q 꼬리 빛, 마력 잔류 증강이면 지나간 길에 작은 폭발
                    if (stepCount % 2 == 0) SpawnFx(p.pos, Art.Glow, p.sr.color, p.radius * 5f, 0f, 0.25f, 8);
                    if (mods.qTrail)
                    {
                        p.trail += p.speed * dt;
                        if (p.trail >= Tune.QTrailEvery)
                        {
                            p.trail -= Tune.QTrailEvery;
                            Explosion(p.pos, Tune.QTrailRadius * (1 + mods.skillArea), p.damage * Tune.QTrailRatio, 1, p.sr.color, null);
                        }
                    }
                }

                if (p.kind == ProjKind.Basic)
                {
                    // 평타는 대상을 따라간다 [임시]. 대상이 없어지면 직진
                    Enemy t = Valid(p.target, p.targetSerial);
                    if (t != null) p.vel = DirOr(t.pos - p.pos, p.vel) * p.speed;
                }
                p.pos += p.vel * dt;
                p.tf.position = new Vector3(p.pos.x, p.pos.y, 0f);

                if (p.kind == ProjKind.EnemyBullet)
                {
                    float reach = Tune.PlayerRadius + p.radius;
                    if ((ppos - p.pos).sqrMagnitude <= reach * reach) { DamagePlayer(p.damage); p.alive = false; }
                    continue;
                }

                var elist = enemies.Active;
                for (int k = 0; k < elist.Count && p.alive; k++)
                {
                    var e = elist[k];
                    if (!e.alive || p.hit.Contains(e) || !InReach(e, p.pos, p.radius)) continue;
                    if (p.kind == ProjKind.Basic) { OnBoltHit(p, e); break; }
                    if (p.kind == ProjKind.Missile) { MissileBoom(p); break; }

                    // Q: 관통하며 닿는 모든 적을 한 번씩
                    p.hit.Add(e);
                    bool crit;
                    float hit = RollCrit(p.damage, out crit);
                    DealDamage(e, hit, crit, 0, HitKind.Skill);
                    Sparks(e.pos, p.sr.color, 4, 2.5f, 0.08f, 0.2f);
                }
            }
        }

        void OnBoltHit(Projectile p, Enemy e)
        {
            p.hit.Add(e);
            bool crit;
            float dmg = RollCrit(p.damage, out crit);
            DealDamage(e, dmg, crit, 0);
            Sparks(e.pos, Palette.Bolt, crit ? 4 : 1, 1.6f, 0.06f, 0.15f);
            if (mods.lifesteal > 0) Heal(dmg * mods.lifesteal);
            OnHitEffects(e, dmg, crit);

            // 튕김: 맞히지 않은 근처 적에게로 방향을 바꾼다
            if (p.bouncesLeft > 0)
            {
                Enemy next = Nearest(e.pos, Tune.RicochetRange, p.hit);
                if (next != null)
                {
                    p.bouncesLeft--;
                    p.target = next; p.targetSerial = next.serial;
                    return;
                }
            }
            // 관통: 그대로 직진
            if (p.pierceLeft > 0)
            {
                p.pierceLeft--;
                p.target = null;
                return;
            }
            p.alive = false;
        }

        // 온힛 효과: 평타가 맞았을 때만
        void OnHitEffects(Enemy e, float dmg, bool crit)
        {
            if (mods.explodeOnHit > 0)
                Explosion(e.pos, Tune.HitExplodeRadius, dmg * mods.explodeOnHit, 1, Palette.Explosion, e);
            if (mods.chainChance > 0 && Random.value < mods.chainChance)
                Chain(e, dmg * Tune.ChainRatio);
            if (crit && mods.critAftermath > 0)
                Explosion(e.pos, Tune.AftermathRadius, dmg * mods.critAftermath, 1, Palette.Crit, e);
        }

        // ================================================================ 경험치

        void SpawnOrb(Vector2 at, float value)
        {
            if (orbs.Count >= Tune.MaxOrbs)
            {
                // 구슬이 너무 많으면 떨어진 자리에서 가장 가까운 구슬에 합친다 (멀리 있는 구슬에 합치면 경험치를 사실상 잃는다)
                var list = orbs.Active;
                XpOrb nearest = null;
                float best = float.MaxValue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!list[i].alive) continue;
                    float d = (list[i].pos - at).sqrMagnitude;
                    if (d < best) { best = d; nearest = list[i]; }
                }
                if (nearest != null) { nearest.value += value; ScaleOrb(nearest); return; }
            }
            var o = orbs.Get();
            o.pos = at + Random.insideUnitCircle * 0.08f;
            o.value = value;
            o.magnet = false;
            o.speed = 0f;
            o.sr.sprite = Art.XpDrop;
            o.sr.color = value >= 100 ? Palette.Legendary : Palette.Xp;
            o.tf.position = new Vector3(o.pos.x, o.pos.y, 0f);
            ScaleOrb(o);
        }

        static void ScaleOrb(XpOrb o)
        {
            o.tf.localScale = Vector3.one * Mathf.Clamp(0.8f + Mathf.Sqrt(o.value) * 0.08f, 0.9f, 2.2f);
        }

        void UpdateOrbs(float dt)
        {
            float magnet = Tune.XpMagnet * (1 + mods.magnetPct);
            var list = orbs.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (!o.alive) continue;
                Vector2 to = ppos - o.pos;
                float d = to.magnitude;
                if (d < magnet) o.magnet = true;
                if (o.magnet && d > 0.0001f)
                {
                    // [임시] 끌려오는 속도
                    o.speed += 9f * dt;
                    o.pos += to / d * Mathf.Min(d, (2f + o.speed) * dt);
                    d = (ppos - o.pos).magnitude;
                }
                if (d < Tune.XpPickup) { o.alive = false; GainXp(o.value); continue; }
                o.tf.position = new Vector3(o.pos.x, o.pos.y, 0f);
            }
        }

        void GainXp(float amount)
        {
            xp += amount;
            while (xp >= xpNeed)
            {
                xp -= xpNeed;
                level++;
                xpNeed = Tune.XpToNext(level);
                pendingLevelUps++;
            }
        }

        // ================================================================ 레벨업 · 증강 카드

        void OpenLevelUp()
        {
            RollCards();
            state = State.LevelUp;
            Time.timeScale = 0f;
            targetRing.gameObject.SetActive(false);
        }

        void RollCards()
        {
            cards.Clear();
            cardsReadyAt = Time.unscaledTime + Tune.CardPickDelay;   // 카드 선택 중에는 timeScale = 0 이라 실제 시간으로 잰다
            var pool = new List<Card>();
            var weights = new List<float>();
            var evolves = new List<Card>();

            foreach (var a in Augments.All)
            {
                int have;
                stacks.TryGetValue(a.id, out have);
                if (have >= a.maxStacks) continue;
                if (a.needsAllSkills && !AllSkillsLearned) continue;
                if (a.needsSkill >= 0 && skills[a.needsSkill].level == 0) continue;       // 그 스킬을 배워야 나오는 증강
                if (a.needsPassive >= 0 && passives[a.needsPassive].level == 0) continue;
                pool.Add(Card.ForAugment(a, have));
                weights.Add(Tune.RarityWeight[(int)a.rarity]);
            }
            for (int i = 0; i < 3; i++)
            {
                var s = skills[i];
                if (s.level == 0) { pool.Add(Card.Unlock(i)); weights.Add(Tune.SkillUnlockWeight); }
                else if (s.level < Tune.MaxSkillLevel) { pool.Add(Card.LevelUp(i, s.level, mods.SkillCdMult)); weights.Add(Tune.SkillLevelWeight); }
                else if (!s.evolved) evolves.Add(Card.Evolve(i));
            }
            int owned = OwnedPassives;
            for (int id = 0; id < Passive.Count; id++)
            {
                var ps = passives[id];
                if (ps.level == 0) { if (owned < Tune.PassiveSlots) { pool.Add(Card.PassiveUnlock(id)); weights.Add(Tune.PassiveUnlockWeight); } }
                else if (ps.level < Tune.MaxSkillLevel) { pool.Add(Card.PassiveLevel(id, ps.level)); weights.Add(Tune.PassiveLevelWeight); }
                else if (!ps.evolved) evolves.Add(Card.PassiveEvolve(id));
            }

            // 진화 카드가 후보에 있으면 1장은 반드시 진화 카드
            if (evolves.Count > 0) cards.Add(evolves[Random.Range(0, evolves.Count)]);
            while (cards.Count < Tune.CardCount && pool.Count > 0)
            {
                int k = WeightedIndex(weights);
                cards.Add(pool[k]);
                pool.RemoveAt(k); weights.RemoveAt(k);
            }
            if (cards.Count == 0) cards.Add(Card.Heal());
        }

        static int WeightedIndex(List<float> weights)
        {
            float total = 0;
            for (int i = 0; i < weights.Count; i++) total += weights[i];
            float x = Random.value * total;
            for (int i = 0; i < weights.Count; i++)
            {
                x -= weights[i];
                if (x < 0) return i;
            }
            return weights.Count - 1;
        }

        void ChooseCard(int index)
        {
            if (state != State.LevelUp || index < 0 || index >= cards.Count || !CardsReady) return;
            Card c = cards[index];
            switch (c.kind)
            {
                case CardKind.Augment:
                    int have;
                    stacks.TryGetValue(c.augment.id, out have);
                    stacks[c.augment.id] = have + 1;
                    if (have == 0) pickedOrder.Add(c.augment);
                    // 최대 체력이 늘면 늘어난 만큼 현재 체력도 채운다
                    foreach (var fx in c.augment.effects) hp += AugmentApplier.Apply(mods, fx);
                    break;
                case CardKind.SkillUnlock: skills[c.skill].level = 1; skills[c.skill].cdLeft = 0; skills[c.skill].stock = 0; break;
                case CardKind.SkillLevel: skills[c.skill].level++; break;
                case CardKind.SkillEvolve: skills[c.skill].evolved = true; break;
                case CardKind.PassiveUnlock: passives[c.passive].level = 1; passives[c.passive].timer = 0.5f; break;
                case CardKind.PassiveLevel: passives[c.passive].level++; break;
                case CardKind.PassiveEvolve: passives[c.passive].evolved = true; break;
                case CardKind.Heal: Heal(Tune.HealCard); break;
            }
            hp = Mathf.Min(hp, MaxHp);

            pendingLevelUps--;
            if (pendingLevelUps > 0) RollCards();
            else { state = State.Playing; Time.timeScale = 1f; }   // 시간 복구를 빠뜨리면 게임이 계속 멈춰 있다
        }

        // ================================================================ 효과

        // 효과 하나. 너무 많으면 null (효과가 몇 개 빠질 뿐 게임에는 영향 없음)
        Fx SpawnFx(Vector2 at, Sprite sprite, Color color, float fromSize, float toSize, float life, int order)
        {
            if (fxs.Count >= Tune.MaxFx) return null;
            var f = fxs.Get();
            f.pos = at; f.age = 0f; f.life = life; f.from = fromSize; f.to = toSize; f.color = color;
            f.vel = Vector2.zero; f.drag = 0f; f.spin = 0f; f.rot = 0f;
            f.size = Vector2.one;
            f.sr.sprite = sprite;
            f.sr.sortingOrder = order;
            f.sr.color = color;
            f.sr.flipX = false;
            f.tf.position = new Vector3(at.x, at.y, 0f);
            f.tf.rotation = Quaternion.identity;
            f.tf.localScale = Vector3.one * fromSize;
            return f;
        }

        void UpdateFx(float dt)
        {
            var list = fxs.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (!f.alive) continue;
                f.age += dt;
                float k = f.age / f.life;
                if (k >= 1f) { f.alive = false; continue; }
                if (f.vel.sqrMagnitude > 0f)
                {
                    f.pos += f.vel * dt;
                    if (f.drag > 0f) f.vel *= Mathf.Exp(-f.drag * dt);
                    f.tf.position = new Vector3(f.pos.x, f.pos.y, 0f);
                }
                if (f.spin != 0f)
                {
                    f.rot += f.spin * dt;
                    f.tf.rotation = Quaternion.Euler(0f, 0f, f.rot);
                }
                float scale = Mathf.Lerp(f.from, f.to, k);
                f.tf.localScale = new Vector3(f.size.x * scale, f.size.y * scale, 1f);
                f.sr.color = Palette.Fade(f.color, f.color.a * (1f - k));
            }

            for (int i = texts.Count - 1; i >= 0; i--)
            {
                var t = texts[i];
                t.age += dt;
                t.pos.y += 0.5f * dt;
                if (t.age >= FloatTextLife) texts.RemoveAt(i);
            }
        }

        void AddText(Vector2 at, string text, Color color, bool big, bool small = false)
        {
            if (texts.Count >= Tune.MaxTexts) texts.RemoveAt(0);
            texts.Add(new FloatText { pos = at + Random.insideUnitCircle * 0.06f, text = text, color = color, big = big, small = small });
        }

        void Shake(float amount)
        {
            shake = Mathf.Min(1.2f, Mathf.Max(shake, amount));
        }

        void Announce(string text)
        {
            bannerText = text;
            bannerTime = 2.5f;
        }

        // ================================================================ 화면 (OnGUI)
        // 모든 좌표는 세로 720 기준 가상 좌표로 쓰고, 그릴 때 실제 화면 크기에 맞춰 늘린다.

        Font uiFont;
        GUIStyle style;
        float u = 1f, vw = 1280f;

        void OnGUI()
        {
            if (style == null)
            {
                // 유니티 기본 글꼴에는 한글이 없을 수 있어서 OS 글꼴을 쓴다
                uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "AppleGothic", "Noto Sans CJK KR", "NanumGothic", "Arial" }, 16);
                style = new GUIStyle(GUI.skin.label) { font = uiFont, richText = false, clipping = TextClipping.Overflow };
            }
            u = Screen.height / 720f;
            vw = Screen.width / u;

            if (state != State.Title) DrawWorldText();
            if (state == State.Playing || state == State.Paused || state == State.LevelUp || state == State.Info) DrawHud();

            switch (state)
            {
                case State.Title: DrawTitle(); break;
                case State.LevelUp: DrawLevelUp(); break;
                case State.Paused: DrawPause(); break;
                case State.Info: DrawInfo(); break;
                case State.GameOver: DrawGameOver(); break;
            }
        }

        Rect Scaled(Rect r)
        {
            return new Rect(r.x * u, r.y * u, r.width * u, r.height * u);
        }

        void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(Scaled(r), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        void Text(Rect r, string s, float size, Color c, TextAnchor anchor, bool bold = false, bool wrap = false, bool shadow = true)
        {
            style.fontSize = Mathf.Max(1, Mathf.RoundToInt(size * u));
            style.alignment = anchor;
            style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            style.wordWrap = wrap;
            Rect sr = Scaled(r);
            if (shadow)
            {
                style.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.7f);
                GUI.Label(new Rect(sr.x + u, sr.y + u, sr.width, sr.height), s, style);
            }
            style.normal.textColor = c;
            GUI.Label(sr, s, style);
        }

        bool Button(Rect r, string label, Color color)
        {
            Rect sr = Scaled(r);
            bool hover = sr.Contains(Event.current.mousePosition);
            Fill(r, hover ? Color.Lerp(color, Color.white, 0.18f) : color);
            Text(r, label, 20, Color.white, TextAnchor.MiddleCenter, true);
            return GUI.Button(sr, GUIContent.none, GUIStyle.none);
        }

        static string FormatTime(float t)
        {
            int s = Mathf.FloorToInt(t);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        void DrawWorldText()
        {
            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i];
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(t.pos.x, t.pos.y, 0f));
                float x = sp.x / u, y = (Screen.height - sp.y) / u;
                float a = 1f - t.age / FloatTextLife;
                Text(new Rect(x - 50, y - 14, 100, 28), t.text, t.big ? 20 : t.small ? 11 : 14, Palette.Fade(t.color, a), TextAnchor.MiddleCenter, t.big);
            }
        }

        void DrawHud()
        {
            // 체력
            Fill(new Rect(20, 18, 280, 22), Palette.Panel);
            Fill(new Rect(22, 20, 276 * Mathf.Clamp01(hp / MaxHp), 18), Palette.HpBar);
            Text(new Rect(20, 18, 280, 22), "HP " + Mathf.Max(0, Mathf.CeilToInt(hp)) + " / " + Mathf.RoundToInt(MaxHp), 14, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(22, 44, 280, 24), "Lv " + level, 18, Palette.Xp, TextAnchor.MiddleLeft, true);

            // 시간 · 처치 · 최고 기록
            Text(new Rect(0, 8, vw, 38), FormatTime(elapsed), 30, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 44, vw, 22), "처치 " + kills, 16, Palette.Muted, TextAnchor.MiddleCenter);
            Text(new Rect(vw - 280, 18, 260, 22), "최고 기록 " + FormatTime(bestTime), 15, Palette.Muted, TextAnchor.MiddleRight);

            // 보스 체력
            Enemy b = StrongestBoss();
            if (b != null)
            {
                float w = Mathf.Min(520f, vw - 80f);
                Rect r = new Rect((vw - w) / 2f, 96, w, 16);
                Text(new Rect(r.x, r.y - 22, w, 20), b.rank == Rank.BigBoss ? "대형 보스" : "보스", 15, b.color, TextAnchor.MiddleCenter, true);
                Fill(r, Palette.Panel);
                Fill(new Rect(r.x + 2, r.y + 2, (w - 4) * Mathf.Clamp01(b.hp / b.maxHp), 12), b.color);
            }

            // 알림
            if (bannerTime > 0)
                Text(new Rect(0, 170, vw, 50), bannerText, 34, Palette.Fade(Palette.Legendary, Mathf.Clamp01(bannerTime)), TextAnchor.MiddleCenter, true);

            // 처음 몇 초 동안 조작 안내
            if (elapsed < 12f)
                Text(new Rect(0, 548, vw, 24), "우클릭: 이동   ·   좌클릭: 공격할 적 지정   ·   Q W E: 스킬   ·   Tab: 내 빌드   ·   Esc: 일시정지", 15, Palette.Muted, TextAnchor.MiddleCenter);

            DrawSkills();
            DrawPassives();

            // 경험치
            Fill(new Rect(0, 710, vw, 10), Palette.Panel);
            Fill(new Rect(0, 710, vw * Mathf.Clamp01(xp / xpNeed), 10), Palette.Xp);
        }

        void DrawSkills()
        {
            const float size = 64f, gap = 14f;
            float x0 = (vw - (size * 3 + gap * 2)) / 2f, y = 618f;
            for (int i = 0; i < 3; i++)
            {
                var s = skills[i];
                Rect r = new Rect(x0 + i * (size + gap), y, size, size);
                Fill(r, Palette.Panel);
                if (s.level == 0)
                {
                    Text(r, SkillState.Keys[i], 26, Palette.Locked, TextAnchor.MiddleCenter, true);
                    Text(new Rect(r.x - 10, r.y + size + 2, size + 20, 18), "잠김", 12, Palette.Locked, TextAnchor.UpperCenter);
                    continue;
                }
                Color c = s.evolved ? Palette.Legendary : Palette.Skill;
                Fill(new Rect(r.x, r.y, size, 4), c);
                if (s.cdLeft > 0)
                {
                    float f = Mathf.Clamp01(s.cdLeft / s.cdTotal);
                    Fill(new Rect(r.x, r.y + size * (1 - f), size, size * f), new Color(0f, 0f, 0f, 0.55f));
                    Text(r, s.cdLeft.ToString("0.0"), 18, Color.white, TextAnchor.MiddleCenter, true);
                }
                else Text(r, SkillState.Keys[i], 26, Color.white, TextAnchor.MiddleCenter, true);
                if (i == 2 && mods.eCharges > 0 && s.cdLeft <= 0)
                    Text(new Rect(r.x, r.y + 2, size - 4, 18), "×" + (s.stock > 0 ? s.stock : 1 + mods.eCharges), 12, Palette.Marker, TextAnchor.UpperRight, true);
                Text(new Rect(r.x - 10, r.y + size + 2, size + 20, 18), s.evolved ? "진화" : "Lv " + s.level, 12, c, TextAnchor.UpperCenter, true);
            }
        }

        // 가진 패시브 스킬: Q W E 위에 작은 칸으로
        void DrawPassives()
        {
            int n = OwnedPassives;
            if (n == 0) return;
            const float w = 78f, h = 24f, gap = 6f;
            float x = (vw - (n * w + (n - 1) * gap)) / 2f, y = 584f;
            for (int id = 0; id < Passive.Count; id++)
            {
                var s = passives[id];
                if (s.level == 0) continue;
                Color c = Passive.Colors[id];
                Rect r = new Rect(x, y, w, h);
                Fill(r, Palette.Panel);
                Fill(new Rect(x, y, 3, h), c);
                if (id == Passive.Laser && s.active > 0) Fill(new Rect(x + 3, y + h - 3, w - 3, 3), c);
                Text(new Rect(x + 4, y, w - 4, h), Passive.Short[id] + " " + (s.evolved ? "★" : s.level.ToString()), 12, c, TextAnchor.MiddleCenter, true);
                x += w + gap;
            }
        }

        void DrawTitle()
        {
            Fill(new Rect(0, 0, vw, 720), Palette.Fade(Palette.Ground, 0.82f));
            Text(new Rect(0, 160, vw, 80), "ARCANE DRIFT", 64, Palette.Player, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 240, vw, 30), "무한 모드  ·  쓰러질 때까지 버티세요", 20, Palette.Body, TextAnchor.MiddleCenter);
            if (bestTime > 0)
                Text(new Rect(0, 280, vw, 26), "최고 기록  " + FormatTime(bestTime) + "   ·   최다 처치 " + bestKills, 17, Palette.Xp, TextAnchor.MiddleCenter);
            if (Button(new Rect(vw / 2f - 120, 340, 240, 56), "시작하기", Palette.ButtonPrimary)) StartRun();
            Text(new Rect(0, 402, vw, 22), "Enter 또는 Space 로도 시작", 14, Palette.Muted, TextAnchor.MiddleCenter);
            Text(new Rect(vw / 2f - 300, 460, 600, 120),
                "우클릭: 이동   ·   적을 좌클릭: 추격하며 자동 공격\nQ W E: 스킬   ·   패시브 스킬 8종은 알아서 발동 (레벨업 카드로 배우고 강화)\n1~4: 카드 선택   ·   Tab: 내 빌드 보기   ·   Esc: 일시정지",
                16, Palette.Body, TextAnchor.UpperCenter, false, true);
        }

        void DrawLevelUp()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.72f));
            Text(new Rect(0, 100, vw, 50), elapsed <= 0f ? "시작 증강" : "레벨 업!", 38, Palette.Xp, TextAnchor.MiddleCenter, true);
            string sub = "증강을 하나 고르세요  (클릭 또는 1~" + cards.Count + ")";
            if (pendingLevelUps > 1) sub += "   ·   남은 선택 " + pendingLevelUps;
            Text(new Rect(0, 150, vw, 26), sub, 16, Palette.Body, TextAnchor.MiddleCenter);

            // 대기 시간 막대: 다 차야 고를 수 있다
            bool ready = CardsReady;
            if (!ready)
            {
                float k = 1f - Mathf.Clamp01((cardsReadyAt - Time.unscaledTime) / Tune.CardPickDelay);
                Fill(new Rect(vw / 2f - 120, 180, 240, 6), Palette.Panel);
                Fill(new Rect(vw / 2f - 120, 180, 240 * k, 6), Palette.Xp);
            }

            const float gap = 18f, ch = 300f;
            int n = cards.Count;
            float cw = Mathf.Min(230f, (vw - 40f - gap * (n - 1)) / n);
            float x0 = (vw - (n * cw + (n - 1) * gap)) / 2f, y = 200f;
            for (int i = 0; i < n; i++)
            {
                Card c = cards[i];
                Rect r = new Rect(x0 + i * (cw + gap), y, cw, ch);
                bool hover = ready && Scaled(r).Contains(Event.current.mousePosition);
                Fill(r, hover ? Palette.CardHover : Palette.Card);
                Fill(new Rect(r.x, r.y, cw, 6), c.color);
                Text(new Rect(r.x + 16, r.y + 20, cw - 32, 20), c.tag, 13, c.color, TextAnchor.MiddleLeft, true);
                Text(new Rect(r.x + 16, r.y + 44, cw - 32, 56), c.title, 20, Color.white, TextAnchor.UpperLeft, true, true);
                Text(new Rect(r.x + 16, r.y + 104, cw - 32, 150), c.desc, 15, Palette.Body, TextAnchor.UpperLeft, false, true, false);
                Text(new Rect(r.x, r.y + ch - 36, cw, 24), "[" + (i + 1) + "]", 15, Palette.Muted, TextAnchor.MiddleCenter);
                if (!ready)
                {
                    // 대기 중에는 카드를 어둡게 덮고 버튼도 만들지 않는다.
                    // 버튼이 없으면 대기 중에 누른 클릭이 대기가 끝난 뒤 떼어져도 선택되지 않는다
                    Fill(r, new Color(0.04f, 0.03f, 0.08f, 0.55f));
                    continue;
                }
                if (GUI.Button(Scaled(r), GUIContent.none, GUIStyle.none)) { ChooseCard(i); break; }
            }
        }

        void DrawInfo()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.86f));
            Text(new Rect(0, 70, vw, 44), "현재 빌드", 32, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 112, vw, 22), "일시정지 중   ·   Tab 또는 Esc: 돌아가기", 14, Palette.Muted, TextAnchor.MiddleCenter);

            float colW = Mathf.Min(360f, (vw - 80f) / 3f), gap = 20f;
            float x0 = (vw - (colW * 3 + gap * 2)) / 2f, y0 = 150f;

            // 1) 능력치
            float x = x0, y = y0;
            Text(new Rect(x, y, colW, 26), "능력치", 19, Palette.Xp, TextAnchor.MiddleLeft, true);
            y += 34;
            string[,] stats =
            {
                { "평타 피해", (Attack * Tune.BasicDamageBoost).ToString("0.#") },
                { "공격 속도", Aspd.ToString("0.00") + " /초" },
                { "평타 사거리", Range.ToString("0.0#") },
                { "이동 속도", MoveSpeed.ToString("0.00") },
                { "치명타", Mathf.RoundToInt(CritChance * 100) + "%  ×" + CritMult.ToString("0.0#") },
                { "체력", Mathf.CeilToInt(hp) + " / " + Mathf.RoundToInt(MaxHp) },
                { "초당 회복", mods.regen.ToString("0.#") },
                { "받는 피해 감소", mods.armor.ToString("0.#") },
                { "흡혈", Mathf.RoundToInt(mods.lifesteal * 100) + "%" },
                { "관통 · 튕김 · 추가탄", mods.pierce + " · " + mods.ricochet + " · " + mods.multishot },
                { "스킬 피해", "+" + Mathf.RoundToInt(mods.skillDmgPct * 100) + "%" + (mods.synergyFinale ? "  ×1.3" : "") },
                { "스킬 쿨다운", "×" + mods.SkillCdMult.ToString("0.00") },
                { "경험치 흡수 범위", (Tune.XpMagnet * (1 + mods.magnetPct)).ToString("0.0") },
            };
            for (int i = 0; i < stats.GetLength(0); i++)
            {
                Text(new Rect(x, y, colW, 22), stats[i, 0], 15, Palette.Body, TextAnchor.MiddleLeft);
                Text(new Rect(x, y, colW, 22), stats[i, 1], 15, Color.white, TextAnchor.MiddleRight, true);
                y += 26;
            }

            // 2) 스킬
            x = x0 + colW + gap; y = y0;
            Text(new Rect(x, y, colW, 26), "스킬", 19, Palette.Skill, TextAnchor.MiddleLeft, true);
            y += 34;
            for (int i = 0; i < 3; i++)
            {
                var sk = skills[i];
                string head = SkillState.Keys[i] + "  " + SkillState.Names[i];
                if (sk.level == 0)
                {
                    Text(new Rect(x, y, colW, 24), head, 17, Palette.Locked, TextAnchor.MiddleLeft, true);
                    Text(new Rect(x, y + 24, colW, 20), "잠김 (레벨업 카드로 배움)", 14, Palette.Locked, TextAnchor.MiddleLeft);
                }
                else
                {
                    Color c = sk.evolved ? Palette.Legendary : Palette.Skill;
                    Text(new Rect(x, y, colW, 24), head + "   " + (sk.evolved ? "진화" : "Lv " + sk.level), 17, c, TextAnchor.MiddleLeft, true);
                    float coef = i == 0 ? Tune.QDamage(sk.level) : i == 1 ? Tune.WDamage(sk.level) : Tune.EDamage(sk.level);
                    Text(new Rect(x, y + 24, colW, 20), "피해 " + Mathf.RoundToInt(SkillDamage(i, coef)) + "   ·   쿨다운 " + Tune.Cooldown(i, sk.level, mods.SkillCdMult).ToString("0.0") + "초",
                        14, Palette.Body, TextAnchor.MiddleLeft);
                }
                y += 50;
            }
            Text(new Rect(x, y, colW, 24), "패시브 스킬  " + OwnedPassives + " / " + Tune.PassiveSlots, 17, Palette.Skill, TextAnchor.MiddleLeft, true);
            y += 28;
            for (int id = 0; id < Passive.Count; id++)
            {
                var ps = passives[id];
                if (ps.level == 0) continue;
                Color c = ps.evolved ? Palette.Legendary : Passive.Colors[id];
                Text(new Rect(x, y, colW, 20), (ps.evolved ? Passive.EvolveNames[id] : Passive.Names[id]) + "   " + (ps.evolved ? "진화" : "Lv " + ps.level), 15, c, TextAnchor.MiddleLeft, true);
                Text(new Rect(x, y + 19, colW, 18), "피해 " + Mathf.RoundToInt(PassiveDamage(id, Passive.Coef(id, ps.level))) + (id == Passive.Shuriken ? "" : "   ·   쿨다운 " + Passive.Cooldown(id, ps.level, mods.SkillCdMult).ToString("0.0") + "초"),
                    12, Palette.Body, TextAnchor.MiddleLeft);
                y += 40;
            }
            Text(new Rect(x, y + 6, colW, 22), "생존 " + FormatTime(elapsed) + "   ·   처치 " + kills + "   ·   Lv " + level + "   ·   레벨 보너스 ×" + LevelBonus.ToString("0.00"), 13, Palette.Muted, TextAnchor.MiddleLeft);

            // 3) 고른 증강 (고른 순서)
            x = x0 + (colW + gap) * 2; y = y0;
            Text(new Rect(x, y, colW, 26), "증강  " + pickedOrder.Count + "종", 19, Palette.Legendary, TextAnchor.MiddleLeft, true);
            y += 34;
            if (pickedOrder.Count == 0) Text(new Rect(x, y, colW, 22), "아직 고른 증강이 없어요", 14, Palette.Muted, TextAnchor.MiddleLeft);
            // 많이 고르면 줄 간격을 줄이고, 그래도 좁으면 설명은 빼고 이름만
            float row = Mathf.Min(44f, (700f - y) / Mathf.Max(1, pickedOrder.Count));
            bool showDesc = row >= 34f;
            for (int i = 0; i < pickedOrder.Count; i++)
            {
                var a = pickedOrder[i];
                int have;
                stacks.TryGetValue(a.id, out have);
                Color c = Palette.Rarity[(int)a.rarity];
                Fill(new Rect(x, y + 3, 4, Mathf.Max(8f, row - 10)), c);
                Text(new Rect(x + 12, y, colW - 12, 20), a.name + (have > 1 ? "  ×" + have : ""), 15, c, TextAnchor.MiddleLeft, true);
                if (showDesc) Text(new Rect(x + 12, y + 18, colW - 12, 18), a.desc.Replace("\n", " "), 12, Palette.Body, TextAnchor.MiddleLeft);
                y += row;
            }
        }

        void DrawPause()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.7f));
            Text(new Rect(0, 190, vw, 60), "일시정지", 44, Color.white, TextAnchor.MiddleCenter, true);
            float x = vw / 2f - 120;
            if (Button(new Rect(x, 290, 240, 54), "계속하기", Palette.ButtonPrimary)) SetPaused(false);
            if (Button(new Rect(x, 358, 240, 54), "다시 시작", Palette.ButtonSecondary)) StartRun();
            if (Button(new Rect(x, 426, 240, 54), "타이틀로", Palette.ButtonSecondary)) GoTitle();
        }

        void DrawGameOver()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.75f));
            Text(new Rect(0, 140, vw, 60), "쓰러졌습니다", 48, Palette.Hurt, TextAnchor.MiddleCenter, true);
            if (newRecord) Text(new Rect(0, 202, vw, 30), "새 최고 기록!", 22, Palette.Legendary, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 240, vw, 36), "생존 시간  " + FormatTime(elapsed), 28, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 282, vw, 26), "처치 " + kills + "   ·   레벨 " + level, 18, Palette.Body, TextAnchor.MiddleCenter);
            Text(new Rect(0, 314, vw, 24), "최고 기록 " + FormatTime(bestTime) + "   ·   최다 처치 " + bestKills, 16, Palette.Muted, TextAnchor.MiddleCenter);
            if (Button(new Rect(vw / 2f - 250, 380, 240, 56), "다시 시작", Palette.ButtonPrimary)) StartRun();
            if (Button(new Rect(vw / 2f + 10, 380, 240, 56), "타이틀로", Palette.ButtonSecondary)) GoTitle();
            Text(new Rect(0, 448, vw, 22), "Enter: 다시 시작", 14, Palette.Muted, TextAnchor.MiddleCenter);
        }
    }

    // ==================================================================== 수치

    static class Tune
    {
        // ---- 원본 수치 (arcane-drift.html, px ÷ 100) ----
        public const float CameraSize = 4.5f;

        public const float PlayerRadius = 0.15f;
        public const float PlayerSpeed = 2.5f;   // 원본 이속. 아래 PlayerSpeedVsMonster 가 0 일 때만 쓴다
        // 사용자 요청: 플레이어 이속 = 기본 몬스터(grunt) 이속 × 1.8. 몬스터 이속이 시간에 따라 오르면 같이 오른다.
        // 0 으로 바꾸면 원본 2.5 로 돌아간다
        public const float PlayerSpeedVsMonster = 1.8f;
        public const float PlayerHp = 100f;
        public const float PlayerDamage = 10f;
        public const float PlayerAspd = 1.1f;
        public const float PlayerRange = 3.4f;   // 사용자 요청으로 늘림 (원본 2.55)
        public const float BasicDamageBoost = 2f;   // 사용자 요청: 평타 피해만 2배 (스킬 피해는 SkillDamageBoost 가 따로 맡는다)
        public const float CritChance = 0.05f;
        public const float CritMult = 1.6f;
        public const float BoltSpeed = 5.6f;
        public static float AttackLock(float aspd) { return Mathf.Min(0.22f, 0.4f / aspd); }

        public const float XpMagnet = 4f;   // 사용자 요청: 이 거리 안의 경험치는 알아서 끌려온다. 평타 사거리(3.4)보다 넓게 (원본 1.4)
        public const float CardPickDelay = 0.7f;   // 카드가 뜨고 이 시간(초) 동안은 클릭·숫자키를 받지 않는다 (공격하다 잘못 누르는 것 방지)
        public const float XpPickup = 0.34f;
        // 원본 곡선 round(10 + lv×7 + lv²×0.6) 에 XpRequirement 를 곱한다 (사용자 요청: 증강을 더 자주)
        public const float XpRequirement = 0.6f;
        public static int XpToNext(int level) { return Mathf.Max(1, Mathf.RoundToInt((10 + level * 7 + level * level * 0.6f) * XpRequirement)); }
        public const int StartingAugments = 1;   // 사용자 요청: 시작하자마자 증강 카드 선택

        public const float SpawnMin = 5.6f, SpawnMax = 7.0f;
        // 원본: 간격 max(0.16, 1.05 - t×0.002), 한 번에 min(1 + t/35, 5)마리.
        // 사용자 요청으로 덜 나오게: 간격 × SpawnIntervalMult, 한 번에 나오는 수는 45초마다 1씩, 최대 4마리
        public const float SpawnIntervalMult = 1.4f;
        public static float SpawnInterval(float t) { return Mathf.Max(0.16f, 1.05f - t * 0.002f) * SpawnIntervalMult; }
        public static int SpawnCount(float t) { return Mathf.Min(1 + Mathf.FloorToInt(t / 45f), 4); }
        public const float DamagePerLevel = 0.04f;   // 레벨마다 모든 피해 +4% (몬스터 체력이 시간에 따라 계속 늘어나서)
        public static float HpScale(float t) { return 1 + t / 50f; }
        public static float DamageScale(float t) { return 1 + t / 140f; }
        public static float SpeedScale(float t) { return 1 + Mathf.Min(t / 320f, 0.7f); }

        public const float EliteEvery = 22f;
        public const float EliteHp = 5f, EliteDamage = 1.8f, EliteSpeed = 0.85f, EliteRadius = 1.6f;
        public const int EliteXp = 6;

        public const float BossEvery = 90f;
        public static float BossHp(float t) { return 650f * (1 + t / 70f); }
        public static float BossDamage(float t) { return 20f * (1 + t / 200f); }
        public const float BossSpeed = 0.66f;
        public const int BossXp = 120, BossShots = 6;

        // 원본의 "6분 최종 보스". 무한 모드에서는 360초마다(보스 4번째마다) 나오고, 잡아도 게임이 계속된다.
        public const int BigBossEveryNthBoss = 4;
        public static float BigBossHp(float t) { return 2200f * (1 + t / 70f); }
        public static float BigBossDamage(float t) { return 34f * (1 + t / 200f); }   // [확인 필요] "피해 34 기준"의 증가식은 보스와 같다고 가정
        public const float BigBossSpeed = 0.58f;
        public const int BigBossXp = 300, BigBossShots = 10;

        public const float QSpeed = 6.2f;
        public static float QDamage(int lv) { return 1.8f + lv * 0.35f; }
        public static float WDamage(int lv) { return 1.2f + lv * 0.3f; }
        public static float EDamage(int lv) { return 1.1f + lv * 0.25f; }
        public static float WRadius(int lv) { return 1.5f + lv * 0.14f; }
        public static float EDistance(int lv) { return 2.3f + lv * 0.22f; }
        // 원문 "최소 1.2, × skillCdMult". 기본식은 Lv5에서도 1.6초 이상이라 최소값은 배율을 곱한 뒤에 적용해야 의미가 있다.
        public static float Cooldown(int skill, int lv, float mult)
        {
            float baseCd = skill == 0 ? 3.0f - lv * 0.28f : skill == 1 ? 6.5f - lv * 0.5f : 5.5f - lv * 0.45f;
            return Mathf.Max(1.2f, baseCd * mult);
        }
        public const int MaxSkillLevel = 5;
        public static readonly float[] EvolveDamage = { 1.6f, 1.5f, 1.5f };
        // 사용자 요청: 스킬 피해를 시원하게. Q·W·E 피해 계수(위 QDamage·WDamage·EDamage)에 모두 곱한다 (원본 1)
        public const float SkillDamageBoost = 4.5f;   // 사용자 요청으로 3 → 4.5 (패시브 스킬에도 똑같이 곱한다)
        public const float ShakeStrength = 0.12f;   // 스킬 쓸 때 화면 흔들림 세기 (0 이면 안 흔들림)
        public const float WEvolveRadius = 0.7f, WEvolveStun = 0.6f;
        public const int EDashSteps = 14;
        public const float EBuffAspd = 0.6f, EBuffTime = 2f, EEvolveRefund = 0.7f;

        public const int CardCount = 4;
        public static readonly float[] RarityWeight = { 100f, 55f, 25f, 8f };

        // ---- 무한 모드 때문에 새로 생긴 수치 [임시] ----
        public const int MaxEnemies = 300;
        public const float RecycleDistance = 11f;

        // ---- 원본을 확인하지 못해 정한 수치 [임시] ----
        public const float ContactCooldown = 0.7f;
        public const float ClickAssist = 0.6f;   // 좌클릭이 적을 살짝 빗나가도 이 거리 안의 가장 가까운 적을 고른다
        public const float BoltRadius = 0.06f, BoltLife = 2f;
        public const float QRange = 6.5f, QRadius = 0.14f, QExplodeRadius = 0.9f, QExplodeRatio = 0.8f;
        public const float WKnockback = 4f;
        public const float EHitPadding = 0.12f;
        public const float BossRadius = 0.45f, BigBossRadius = 0.65f, BossResist = 0.15f;
        public const float BarrageRange = 4.5f, BossBarrageEvery = 2.2f, BigBossBarrageEvery = 1.8f;
        public const float BulletSpeed = 2.4f, BulletRadius = 0.08f, BulletDamageRatio = 0.5f, BulletLife = 5f;
        public const float SkillUnlockWeight = 110f, SkillLevelWeight = 75f;   // 증강 사이에서 스킬 카드가 너무 드물게 나오지 않도록

        // ---- 패시브 스킬 (사용자 요청으로 추가) ----
        public const int PassiveSlots = 6;                    // 한 번에 가질 수 있는 패시브 수
        public const float PassiveUnlockWeight = 110f, PassiveLevelWeight = 75f;
        public const float PassiveEvolveDamage = 1.5f;
        public const float ResonanceDamage = 1.35f;
        public const float ShurikenOrbit = 0.85f, ShurikenOrbitPerLv = 0.08f, ShurikenSpin = 3.4f, ShurikenHitRadius = 0.16f, ShurikenHitEvery = 0.4f;
        public const float ShurikenFastMult = 1.6f, ShurikenFastDamage = 1.2f;
        public const int ShurikenEvolveExtra = 3;
        public const float ThunderRange = 7f, ThunderRadius = 0.55f, ThunderStun = 0.6f;
        public const float AcidRange = 6f, AcidRadius = 0.7f, AcidRadiusPerLv = 0.06f, AcidTime = 3f, AcidTick = 0.5f;
        public const float AcidCorrodeDamage = 1.3f, AcidCorrodeSlow = 0.6f, AcidEvolveSlow = 0.7f;
        public const float MissileRange = 8f, MissileSpeed = 5.5f, MissileAccel = 6f, MissileTurn = 7f, MissileLife = 3f, MissileRadius = 0.12f, MissileBlast = 0.55f;
        public const float FrostRadius = 1.4f, FrostRadiusPerLv = 0.12f, FrostSlow = 0.5f, FrostSlowTime = 2f;
        public const float LaserTime = 2.2f, LaserSpin = 2.6f, LaserLength = 3f, LaserLengthPerLv = 0.2f, LaserWidth = 0.16f, LaserHitEvery = 0.2f, LaserPowerDamage = 1.3f;
        public const float VortexRange = 6f, VortexRadius = 1.1f, VortexRadiusPerLv = 0.08f, VortexTime = 2.6f, VortexTick = 0.3f, VortexPull = 2.4f, VortexCollapseRatio = 4f;
        public const float MeteorRange = 7f, MeteorRadius = 0.9f, MeteorRadiusPerLv = 0.06f, MeteorFall = 0.7f;
        public const float BurnTime = 3f, BurnTick = 0.5f, BurnRatio = 0.12f;

        // ---- Q W E 증강 ----
        public const float QSpreadDeg = 12f, QGiantSize = 1.8f, QGiantDamage = 1.25f, QTrailEvery = 0.8f, QTrailRadius = 0.45f, QTrailRatio = 0.35f;
        public const float WDoubleDelay = 0.4f, WDoubleRatio = 0.7f, WPullDamage = 1.2f;
        public const float EAfterDelay = 0.3f, EAfterRatio = 0.6f, EAfterRadius = 0.6f, EGuardTime = 0.8f;
        public const float HitExplodeRadius = 0.6f, AftermathRadius = 0.8f, KillExplodeRadius = 0.7f;
        public const float ChainRange = 1.8f, ChainRatio = 0.5f, RicochetRange = 2.5f;
        public const int ChainTargets = 3;
        public const float SynergyCdCut = 1f, SynergyDamage = 1.3f;
        public const float MinSkillCdMult = 0.4f;
        public const float HealCard = 30f;
        public const int MaxOrbs = 300, MaxTexts = 80, MaxFx = 700;
    }

    static class Palette
    {
        public static Color Hex(int rgb)
        {
            return new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);
        }
        public static Color Fade(Color c, float a) { return new Color(c.r, c.g, c.b, a); }

        public static readonly Color Ground = Hex(0x131A20);
        public static readonly Color Player = Hex(0xC9B6FF);
        public static readonly Color Marker = Hex(0x6BD5FF);
        public static readonly Color Muzzle = Hex(0xE4C8FF);
        public static readonly Color Haste = Hex(0xB8EEFF);
        public static readonly Color Stunned = Hex(0xB9A2FF);
        public static readonly Color SkillHit = Hex(0x8FE9FF);
        public static readonly Color PassiveHit = Hex(0xC6F2FF);
        public static readonly Color Ice = Hex(0xA8E6FF);
        public static readonly Color Slowed = Hex(0xCFE9D8);
        public static readonly Color Target = Hex(0xFF6B6B);
        public static readonly Color Bolt = Hex(0xE4D8FF);
        public static readonly Color QBolt = Hex(0x6BD5FF);
        public static readonly Color Void = Hex(0x9B6BFF);
        public static readonly Color Dash = Fade(Hex(0x6BD5FF), 0.6f);
        public static readonly Color Elite = Hex(0xF0B429);
        public static readonly Color Boss = Hex(0xE0508C);
        public static readonly Color BigBoss = Hex(0xFF7A3D);
        public static readonly Color Bullet = Hex(0xFF6B9A);
        public static readonly Color Xp = Hex(0x5EE3A1);
        public static readonly Color Explosion = Hex(0xFF9F4A);
        public static readonly Color Chain = Hex(0x8FD3FF);
        public static readonly Color Crit = Hex(0xFFD166);
        public static readonly Color Hurt = Hex(0xFF5A5A);
        public static readonly Color DamageText = Color.white;

        public static readonly Color Panel = new Color(0.05f, 0.04f, 0.09f, 0.78f);
        public static readonly Color Card = Hex(0x231E36);
        public static readonly Color CardHover = Hex(0x2F2848);
        public static readonly Color Body = Hex(0xD6D0EA);
        public static readonly Color Muted = Hex(0x9A93B5);
        public static readonly Color Locked = Hex(0x5C5675);
        public static readonly Color HpBar = Hex(0xE5484D);
        public static readonly Color Skill = Hex(0x6BE3A8);
        public static readonly Color ButtonPrimary = Hex(0x6E56CF);
        public static readonly Color ButtonSecondary = Hex(0x3A3352);

        public static readonly Color[] Rarity = { Hex(0xA8B3C7), Hex(0x59C3FF), Hex(0xC38BFF), Hex(0xFFB84D) };
        public static readonly Color Legendary = Hex(0xFFB84D);
    }

    // ==================================================================== 적 종류

    class EnemyKind
    {
        public readonly string key;
        public readonly Color color;
        public readonly float radius, hp, damage, speed;
        public readonly int xp, look;   // look: Art.Monster 의 그림 번호

        EnemyKind(string key, Color color, float radius, float hp, float damage, float speed, int xp, int look)
        {
            this.key = key; this.color = color; this.radius = radius; this.hp = hp; this.damage = damage; this.speed = speed; this.xp = xp; this.look = look;
        }

        public static readonly EnemyKind Grunt = new EnemyKind("grunt", Palette.Hex(0x7A4FB0), 0.13f, 16f, 6f, 0.95f, 2, Art.LookSlime);
        public static readonly EnemyKind Runner = new EnemyKind("runner", Palette.Hex(0x3FA789), 0.10f, 10f, 4f, 1.65f, 2, Art.LookFlask);
        public static readonly EnemyKind Brute = new EnemyKind("brute", Palette.Hex(0xB0554F), 0.19f, 44f, 12f, 0.70f, 5, Art.LookBrute);
    }

    // ==================================================================== 풀링되는 것들

    class Pooled
    {
        public Transform tf;
        public SpriteRenderer sr;
        public bool alive;
    }

    enum Rank { Normal, Elite, Boss, BigBoss }

    class Enemy : Pooled
    {
        public int serial;   // 풀에서 다시 쓰일 때마다 바뀐다. 예전 대상을 쫓던 투사체가 새 적을 쫓지 않게 하려고
        public Rank rank;
        public Color color;       // 체력바·터지는 효과 색
        public int look;          // Art.Monster 그림 번호
        public float scale;       // 그림 배율 (엘리트는 크게)
        public Vector2 pos, knock;
        public float radius, hp, maxHp, damage, speed;
        public int xp;
        public float contactReady, stun, flash, fade, barrage;
        public float shurikenReady, laserReady;     // 같은 표창·레이저에 연달아 맞지 않게
        public float slowUntil, slowFactor, iceUntil;
        public bool IsBoss { get { return rank == Rank.Boss || rank == Rank.BigBoss; } }
    }

    enum ProjKind { Basic, QBolt, EnemyBullet, Missile }

    class Projectile : Pooled
    {
        public ProjKind kind;
        public Vector2 pos, vel;
        public float speed, radius, damage, life, delay, trail;
        public Enemy target;
        public int targetSerial, pierceLeft, bouncesLeft;
        public bool evolved;
        public readonly HashSet<Enemy> hit = new HashSet<Enemy>();
    }

    class XpOrb : Pooled
    {
        public Vector2 pos;
        public float value, speed;
        public bool magnet;
    }

    class Fx : Pooled
    {
        public Vector2 pos, vel, size;
        public float age, life, from, to, drag, spin, rot;
        public Color color;
    }

    class FloatText
    {
        public Vector2 pos;
        public string text;
        public Color color;
        public float age;
        public bool big, small;
    }

    class Pool<T> where T : Pooled, new()
    {
        readonly Stack<T> free = new Stack<T>();
        readonly Transform parent;
        readonly string name;
        readonly int order;
        public readonly List<T> Active = new List<T>();

        public Pool(Transform parent, string name, int order)
        {
            this.parent = parent; this.name = name; this.order = order;
        }

        public int Count { get { return Active.Count; } }

        public T Get()
        {
            T x;
            if (free.Count > 0) x = free.Pop();
            else
            {
                x = new T();
                x.sr = Art.CreateSprite(name, parent, Art.Circle, Color.white, order);
                x.tf = x.sr.transform;
            }
            x.alive = true;
            x.sr.gameObject.SetActive(true);
            Active.Add(x);
            return x;
        }

        // alive == false 인 것들을 한꺼번에 풀로 돌려보낸다
        public void Sweep()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                T x = Active[i];
                if (x.alive) continue;
                x.sr.gameObject.SetActive(false);
                int last = Active.Count - 1;
                Active[i] = Active[last];
                Active.RemoveAt(last);
                free.Push(x);
            }
        }

        public void Clear()
        {
            for (int i = 0; i < Active.Count; i++) Active[i].alive = false;
            Sweep();
        }
    }

    // ==================================================================== 스킬

    class SkillState
    {
        public static readonly string[] Keys = { "Q", "W", "E" };
        public static readonly string[] Names = { "아케인 볼트", "공허의 파동", "섬광 돌진" };

        public readonly int index;
        public int level;        // 0 = 잠김
        public bool evolved;
        public float cdLeft, cdTotal = 1f;
        public int stock;        // E 연속 돌진 남은 횟수

        public SkillState(int index) { this.index = index; }
    }

    // ==================================================================== 패시브 스킬 정의

    static class Passive
    {
        public const int Shuriken = 0, Thunder = 1, Acid = 2, Missile = 3, Frost = 4, Laser = 5, Vortex = 6, Meteor = 7, Count = 8;

        public static readonly string[] Names = { "회전 표창", "낙뢰", "산성 웅덩이", "유도 미사일", "냉기 폭발", "회전 레이저", "블랙홀", "메테오" };
        public static readonly string[] Short = { "표창", "낙뢰", "산성", "미사일", "냉기", "레이저", "블랙홀", "메테오" };
        public static readonly string[] EvolveNames = { "칼날 폭풍", "뇌신의 심판", "독성 범람", "융단 폭격", "절대 빙결", "프리즘 레이저", "특이점", "천벌" };
        public static readonly Color[] Colors =
        {
            Palette.Hex(0xBFE3FF), Palette.Hex(0xFFE45C), Palette.Hex(0x59E06A), Palette.Hex(0xFF8A4C),
            Palette.Hex(0x8FDBFF), Palette.Hex(0xFF4FD8), Palette.Hex(0x9B6BFF), Palette.Hex(0xFF6A2B),
        };
        public static readonly string[] What =
        {
            "몸 주위를 도는 표창이 닿는 적을 벱니다",
            "주변의 무작위 적들에게 번개가 떨어집니다",
            "적이 모인 곳에 산성 웅덩이를 쏟아 계속 피해를 줍니다",
            "적을 쫓아가 터지는 미사일을 발사합니다",
            "주변에 냉기를 터뜨려 피해를 주고 적을 느리게 합니다",
            "몸 주위를 도는 레이저를 잠시 켭니다. 닿는 적은 계속 피해",
            "적 무리 한가운데에 블랙홀을 열어 끌어당기며 피해를 줍니다",
            "하늘에서 운석을 떨어뜨려 넓은 범위를 터뜨립니다",
        };
        public static readonly string[] EvolveWhat =
        {
            "표창 +3개, 궤도가 숨 쉬듯 커졌다 작아집니다\n피해 ×1.5",
            "번개가 근처 적 3명에게 다시 튑니다\n피해 ×1.5",
            "웅덩이 크기 ×1.5, 위에 있는 적이 느려집니다\n피해 ×1.5",
            "미사일 +2발, 폭발 범위 ×1.4\n피해 ×1.5",
            "맞은 적을 얼리고, 0.35초 뒤 한 번 더 터집니다\n피해 ×1.5",
            "광선 수 ×2\n피해 ×1.5",
            "크기 ×1.5, 사라질 때 크게 폭발합니다\n피해 ×1.5",
            "운석 +1개, 범위 ×1.3, 떨어진 자리가 불탑니다\n피해 ×1.5",
        };

        // 피해 계수 (× 공격력 × SkillDamageBoost). 장판·레이저는 한 번 틱당 피해
        public static float Coef(int id, int lv)
        {
            switch (id)
            {
                case Shuriken: return 0.35f + 0.08f * lv;
                case Thunder: return 1.0f + 0.25f * lv;
                case Acid: return 0.22f + 0.05f * lv;
                case Missile: return 0.9f + 0.2f * lv;
                case Frost: return 0.8f + 0.2f * lv;
                case Laser: return 0.28f + 0.06f * lv;
                case Vortex: return 0.16f + 0.04f * lv;
                default: return 2.2f + 0.45f * lv;
            }
        }

        static readonly float[] BaseCd = { 0f, 2.4f, 3.0f, 1.8f, 4.0f, 5.0f, 6.0f, 4.0f };
        static readonly float[] CdPerLv = { 0f, 0.15f, 0.2f, 0.1f, 0.3f, 0.3f, 0.4f, 0.25f };

        public static float Cooldown(int id, int lv, float mult)
        {
            return Mathf.Max(0.4f, (BaseCd[id] - CdPerLv[id] * lv) * mult);
        }

        // 카드·Tab 화면에 보여줄 핵심 수치 (증강 보너스 제외)
        public static string Stats(int id, int lv)
        {
            string dmg = "피해: 공격력 × " + (Coef(id, lv) * Tune.SkillDamageBoost).ToString("0.##");
            string cd = "쿨다운 " + Cooldown(id, lv, 1f).ToString("0.0") + "초";
            switch (id)
            {
                case Shuriken: return "표창 " + (2 + lv / 2) + "개 · 0.4초마다 벰\n" + dmg;
                case Thunder: return "번개 " + (2 + lv) + "개 · " + cd + "\n" + dmg;
                case Acid: return "웅덩이 " + (1 + lv / 2) + "개 · 3초 · " + cd + "\n" + dmg + " (0.5초마다)";
                case Missile: return "미사일 " + (1 + lv / 2) + "발 · " + cd + "\n" + dmg;
                case Frost: return "범위 " + (Tune.FrostRadius + Tune.FrostRadiusPerLv * lv).ToString("0.0") + " · 50% 감속 · " + cd + "\n" + dmg;
                case Laser: return "광선 " + (1 + (lv >= 3 ? 1 : 0)) + "줄 · 2.2초 · " + cd + "\n" + dmg + " (0.2초마다)";
                case Vortex: return "범위 " + (Tune.VortexRadius + Tune.VortexRadiusPerLv * lv).ToString("0.0") + " · 2.6초 · " + cd + "\n" + dmg + " (0.3초마다)";
                default: return "운석 " + (1 + lv / 2) + "개 · " + cd + "\n" + dmg;
            }
        }
    }

    class PassiveState
    {
        public int level;        // 0 = 없음
        public bool evolved;
        public float timer;      // 다음 발동까지
        public float active;     // 레이저가 켜져 있는 남은 시간
        public float angle;      // 표창·레이저 회전 각도
    }

    enum HitKind { Basic, Skill, Passive }

    enum ZoneKind { Acid, Fire, Vortex }

    class Zone : Pooled
    {
        public ZoneKind kind;
        public Vector2 pos;
        public Color color;
        public float radius, life, duration, tick, tickEvery, damage, slow, pull, collapse, spin;
    }

    class Falling : Pooled
    {
        public Vector2 from, to;
        public float t, duration, damage, radius;
        public bool burn;
    }

    class Delayed
    {
        public float t;
        public System.Action action;
    }


    // ==================================================================== 증강

    // 원본 player.mods 를 옮긴 것. 증강은 값을 더하기만 한다.
    // [확인 필요] 원본 필드 이름·구성을 확인하지 못해 증강 효과에 맞춰 정했다.
    class PlayerMods
    {
        public float damageAdd, damagePct, aspdPct, movePct, maxHpAdd, regen, rangeAdd;
        public float critChance, critMult, lifesteal, magnetPct, armor;
        public float skillCdCut, skillDmgPct;
        public int pierce, multishot, ricochet, echoEvery;
        public float explodeOnHit, chainChance, critAftermath, killExplode;
        public bool synergyFinale;

        // 스킬 증강
        public float skillArea, skillDuration, wRadiusPct;
        public int skillAmount, eCharges;
        public bool resonance, qTriple, qGiant, qTrail, wDouble, wPull, eAfterBlast, eGuard;
        public readonly int[] passiveExtra = new int[Passive.Count];      // 패시브별 개수 +
        public readonly float[] passiveSize = new float[Passive.Count];   // 패시브별 크기 +%
        public bool shurikenFast, thunderStun, acidCorrode, missileCluster, frostFreeze, laserPower, vortexCollapse, meteorBurn;

        public float SkillCdMult { get { return Mathf.Max(Tune.MinSkillCdMult, 1f - skillCdCut); } }
    }

    enum Rarity { Common, Advanced, Rare, Legendary }

    enum ModOp
    {
        DamageAdd, DamagePct, AspdPct, MovePct, MaxHp, Regen, RangeAdd,
        CritChance, CritMult, Lifesteal, MagnetPct, Armor, SkillCdCut, SkillDmgPct,
        Pierce, Multishot, Ricochet, Echo,
        ExplodeOnHit, Chain, CritAftermath, KillExplode, SynergyFinale,
        SkillArea, SkillDuration, SkillAmount, Resonance,
        QTriple, QGiant, QTrail, WDouble, WPull, WRadius, ECharge, EAfterBlast, EGuard,
        PassiveExtra, PassiveSize,
        ShurikenFast, ThunderStun, AcidCorrode, MissileCluster, FrostFreeze, LaserPower, VortexCollapse, MeteorBurn,
    }

    struct ModEffect
    {
        public readonly ModOp op;
        public readonly float value;
        public readonly int target;   // PassiveExtra·PassiveSize 가 어느 패시브인지
        public ModEffect(ModOp op, float value) { this.op = op; this.value = value; target = 0; }
        public ModEffect(ModOp op, float value, int target) { this.op = op; this.value = value; this.target = target; }
    }

    class Augment
    {
        public readonly string id, name, desc;
        public readonly Rarity rarity;
        public readonly int maxStacks;
        public readonly List<ModEffect> effects;
        public bool needsAllSkills;
        public int needsSkill = -1, needsPassive = -1;   // 이 스킬(Q=0, W=1, E=2)·패시브를 가지고 있어야 나온다

        public Augment(string id, string name, Rarity rarity, int maxStacks, string desc, params ModEffect[] effects)
        {
            this.id = id; this.name = name; this.rarity = rarity; this.maxStacks = maxStacks; this.desc = desc;
            this.effects = new List<ModEffect>(effects);
        }
    }

    static class AugmentApplier
    {
        // 증강 효과는 전부 여기 한 곳에서 적용한다.
        // 돌려주는 값: 최대 체력이 늘어난 양 (그만큼 현재 체력도 채워 주기 위해)
        public static float Apply(PlayerMods m, ModEffect fx)
        {
            float v = fx.value;
            switch (fx.op)
            {
                case ModOp.DamageAdd: m.damageAdd += v; break;
                case ModOp.DamagePct: m.damagePct += v; break;
                case ModOp.AspdPct: m.aspdPct += v; break;
                case ModOp.MovePct: m.movePct += v; break;
                case ModOp.MaxHp: m.maxHpAdd += v; return v;
                case ModOp.Regen: m.regen += v; break;
                case ModOp.RangeAdd: m.rangeAdd += v; break;
                case ModOp.CritChance: m.critChance += v; break;
                case ModOp.CritMult: m.critMult += v; break;
                case ModOp.Lifesteal: m.lifesteal += v; break;
                case ModOp.MagnetPct: m.magnetPct += v; break;
                case ModOp.Armor: m.armor += v; break;
                case ModOp.SkillCdCut: m.skillCdCut += v; break;
                case ModOp.SkillDmgPct: m.skillDmgPct += v; break;
                case ModOp.Pierce: m.pierce += (int)v; break;
                case ModOp.Multishot: m.multishot += (int)v; break;
                case ModOp.Ricochet: m.ricochet += (int)v; break;
                case ModOp.Echo: m.echoEvery = (int)v; break;
                case ModOp.ExplodeOnHit: m.explodeOnHit += v; break;
                case ModOp.Chain: m.chainChance += v; break;
                case ModOp.CritAftermath: m.critAftermath += v; break;
                case ModOp.KillExplode: m.killExplode += v; break;
                case ModOp.SynergyFinale: m.synergyFinale = true; break;
                case ModOp.SkillArea: m.skillArea += v; break;
                case ModOp.SkillDuration: m.skillDuration += v; break;
                case ModOp.SkillAmount: m.skillAmount += (int)v; break;
                case ModOp.Resonance: m.resonance = true; break;
                case ModOp.QTriple: m.qTriple = true; break;
                case ModOp.QGiant: m.qGiant = true; break;
                case ModOp.QTrail: m.qTrail = true; break;
                case ModOp.WDouble: m.wDouble = true; break;
                case ModOp.WPull: m.wPull = true; break;
                case ModOp.WRadius: m.wRadiusPct += v; break;
                case ModOp.ECharge: m.eCharges += (int)v; break;
                case ModOp.EAfterBlast: m.eAfterBlast = true; break;
                case ModOp.EGuard: m.eGuard = true; break;
                case ModOp.PassiveExtra: m.passiveExtra[fx.target] += (int)v; break;
                case ModOp.PassiveSize: m.passiveSize[fx.target] += v; break;
                case ModOp.ShurikenFast: m.shurikenFast = true; break;
                case ModOp.ThunderStun: m.thunderStun = true; break;
                case ModOp.AcidCorrode: m.acidCorrode = true; break;
                case ModOp.MissileCluster: m.missileCluster = true; break;
                case ModOp.FrostFreeze: m.frostFreeze = true; break;
                case ModOp.LaserPower: m.laserPower = true; break;
                case ModOp.VortexCollapse: m.vortexCollapse = true; break;
                case ModOp.MeteorBurn: m.meteorBurn = true; break;
            }
            return 0f;
        }
    }

    // [확인 필요] 원본 AUGMENTS 배열을 확인하지 못해, 인수인계 문서의 이름·등급별 개수(일반 8, 고급 6, 희귀 6, 전설 5)에 맞춰 새로 만든 목록
    // + 사용자 요청으로 추가한 스킬 증강 33종 (해당 스킬을 가지고 있을 때만 나온다)
    static class Augments
    {
        static ModEffect E(ModOp op, float v) { return new ModEffect(op, v); }
        static ModEffect E(ModOp op, float v, int target) { return new ModEffect(op, v, target); }
        static Augment NeedsSkill(int skill, Augment a) { a.needsSkill = skill; return a; }
        static Augment NeedsPassive(int passive, Augment a) { a.needsPassive = passive; return a; }

        public static readonly Augment[] All = Build();

        static Augment[] Build()
        {
            var finale = new Augment("synergyFinale", "삼위일체", Rarity.Legendary, 1,
                "Q·W·E를 모두 배웠을 때만 등장.\n스킬을 쓰면 나머지 두 스킬의 쿨다운이 1초 줄고, 스킬 피해 +30%",
                E(ModOp.SynergyFinale, 1));
            finale.needsAllSkills = true;

            return new[]
            {
                // 일반
                new Augment("sharp", "예리한 마력", Rarity.Common, 5, "공격력 +3", E(ModOp.DamageAdd, 3)),
                new Augment("quick", "빠른 손놀림", Rarity.Common, 5, "공격 속도 +12%", E(ModOp.AspdPct, 0.12f)),
                new Augment("swift", "질풍", Rarity.Common, 4, "이동 속도 +8%", E(ModOp.MovePct, 0.08f)),
                new Augment("sturdy", "강인함", Rarity.Common, 5, "최대 체력 +20", E(ModOp.MaxHp, 20)),
                new Augment("regen", "재생", Rarity.Common, 5, "초당 체력 회복 +1", E(ModOp.Regen, 1)),
                new Augment("eagle", "매의 눈", Rarity.Common, 3, "평타 사거리 +0.3", E(ModOp.RangeAdd, 0.3f)),
                new Augment("precise", "정밀", Rarity.Common, 5, "치명타 확률 +5%", E(ModOp.CritChance, 0.05f)),
                new Augment("magnet", "자석", Rarity.Common, 3, "경험치 흡수 범위 +30%", E(ModOp.MagnetPct, 0.3f)),
                // 고급
                new Augment("piercing", "날카로운 탄", Rarity.Advanced, 3, "평타가 적을 1명 더 관통합니다", E(ModOp.Pierce, 1)),
                new Augment("vamp", "흡혈", Rarity.Advanced, 3, "평타 피해의 3%만큼 체력 회복", E(ModOp.Lifesteal, 0.03f)),
                new Augment("amplify", "마력 증폭", Rarity.Advanced, 4, "공격력 +12%", E(ModOp.DamagePct, 0.12f)),
                new Augment("haste", "시간 단축", Rarity.Advanced, 3, "스킬 쿨다운 -10%", E(ModOp.SkillCdCut, 0.10f)),
                new Augment("deadly", "치명적 일격", Rarity.Advanced, 3, "치명타 피해 +35%", E(ModOp.CritMult, 0.35f)),
                new Augment("guard", "수호", Rarity.Advanced, 3, "받는 피해 -2 (최소 1)", E(ModOp.Armor, 2)),
                // 희귀 (온힛 효과)
                new Augment("blast", "폭발 탄환", Rarity.Rare, 2, "평타가 맞은 자리에서 터져 주변 적에게 피해의 40%", E(ModOp.ExplodeOnHit, 0.4f)),
                new Augment("chain", "연쇄 번개", Rarity.Rare, 2, "평타 적중 시 25% 확률로 근처 적 3명에게 번개 (피해의 50%)", E(ModOp.Chain, 0.25f)),
                new Augment("aftermath", "치명의 여파", Rarity.Rare, 1, "평타 치명타가 주변 적에게 충격파 (피해의 60%)", E(ModOp.CritAftermath, 0.6f)),
                new Augment("ricochet", "튕기는 탄", Rarity.Rare, 2, "평타가 근처 다른 적에게 2번 튕깁니다", E(ModOp.Ricochet, 2)),
                new Augment("twin", "쌍발", Rarity.Rare, 2, "평타를 쏠 때 사거리 안의 다른 적에게도 1발", E(ModOp.Multishot, 1)),
                new Augment("corpse", "처치 폭발", Rarity.Rare, 1, "처치한 적이 터져 주변에 그 적 최대 체력의 25% 피해", E(ModOp.KillExplode, 0.25f)),
                // 전설
                new Augment("echo", "메아리", Rarity.Legendary, 1, "평타 3번마다 같은 대상에게 한 발 더", E(ModOp.Echo, 3)),
                new Augment("surge", "비전 폭주", Rarity.Legendary, 1, "공격력 +25%, 공격 속도 +25%", E(ModOp.DamagePct, 0.25f), E(ModOp.AspdPct, 0.25f)),
                new Augment("warp", "시간 왜곡", Rarity.Legendary, 1, "스킬 쿨다운 -25%, 스킬 피해 +25%", E(ModOp.SkillCdCut, 0.25f), E(ModOp.SkillDmgPct, 0.25f)),
                new Augment("immortal", "불멸", Rarity.Legendary, 1, "최대 체력 +60, 초당 체력 회복 +3", E(ModOp.MaxHp, 60), E(ModOp.Regen, 3)),
                finale,
                // ---- 스킬 증강: 모든 스킬 ----
                new Augment("wideArea", "광역화", Rarity.Common, 3, "모든 스킬 범위 +15%", E(ModOp.SkillArea, 0.15f)),
                new Augment("longCast", "지속 강화", Rarity.Common, 3, "장판·레이저·감속 지속시간 +25%", E(ModOp.SkillDuration, 0.25f)),
                new Augment("cycle", "순환", Rarity.Common, 4, "모든 스킬 쿨다운 -8%", E(ModOp.SkillCdCut, 0.08f)),
                new Augment("overload", "마력 과부하", Rarity.Advanced, 4, "모든 스킬 피해 +20%", E(ModOp.SkillDmgPct, 0.2f)),
                new Augment("multicast", "다중 시전", Rarity.Rare, 2, "Q 볼트·표창·번개·웅덩이·미사일·레이저·운석 개수 +1", E(ModOp.SkillAmount, 1)),
                new Augment("resonance", "원소 공명", Rarity.Legendary, 1, "패시브 스킬을 3개 이상 가지고 있으면 모든 스킬 피해 +35%", E(ModOp.Resonance, 1)),
                // ---- 스킬 증강: Q ----
                NeedsSkill(0, new Augment("qTriple", "삼연발", Rarity.Rare, 1, "Q 아케인 볼트를 부채꼴로 3발 쏩니다", E(ModOp.QTriple, 1))),
                NeedsSkill(0, new Augment("qGiant", "거대 볼트", Rarity.Advanced, 1, "Q 볼트 크기 +80%, 피해 +25%", E(ModOp.QGiant, 1))),
                NeedsSkill(0, new Augment("qTrail", "마력 잔류", Rarity.Rare, 1, "Q 볼트가 지나간 길에 작은 폭발이 연달아 터집니다", E(ModOp.QTrail, 1))),
                // ---- 스킬 증강: W ----
                NeedsSkill(1, new Augment("wDouble", "이중 파동", Rarity.Rare, 1, "W 파동이 0.4초 뒤 한 번 더 터집니다 (70% 피해)", E(ModOp.WDouble, 1))),
                NeedsSkill(1, new Augment("wPull", "중력 붕괴", Rarity.Rare, 1, "W가 적을 밀어내지 않고 끌어당깁니다. 피해 +20%", E(ModOp.WPull, 1))),
                NeedsSkill(1, new Augment("wWide", "충격 확장", Rarity.Advanced, 2, "W 범위 +25%", E(ModOp.WRadius, 0.25f))),
                // ---- 스킬 증강: E ----
                NeedsSkill(2, new Augment("eCharge", "연속 돌진", Rarity.Rare, 1, "E를 쿨다운 없이 한 번 더 쓸 수 있습니다 (충전 2회)", E(ModOp.ECharge, 1))),
                NeedsSkill(2, new Augment("eBlast", "잔상 폭발", Rarity.Rare, 1, "E로 지나간 길이 0.3초 뒤 폭발합니다 (E 피해의 60%)", E(ModOp.EAfterBlast, 1))),
                NeedsSkill(2, new Augment("eGuard", "무적 돌진", Rarity.Advanced, 1, "E를 쓰고 0.8초 동안 피해를 받지 않습니다", E(ModOp.EGuard, 1))),
                // ---- 스킬 증강: 패시브 ----
                NeedsPassive(Passive.Shuriken, new Augment("shurikenMore", "표창 추가", Rarity.Advanced, 2, "회전 표창 +2개", E(ModOp.PassiveExtra, 2, Passive.Shuriken))),
                NeedsPassive(Passive.Shuriken, new Augment("shurikenFast", "고속 회전", Rarity.Common, 1, "표창 회전 속도 +60%, 피해 +20%", E(ModOp.ShurikenFast, 1))),
                NeedsPassive(Passive.Shuriken, new Augment("shurikenWide", "넓은 궤도", Rarity.Common, 2, "표창 궤도 +30%", E(ModOp.PassiveSize, 0.3f, Passive.Shuriken))),
                NeedsPassive(Passive.Thunder, new Augment("thunderMore", "낙뢰 증폭", Rarity.Advanced, 2, "번개 +2개", E(ModOp.PassiveExtra, 2, Passive.Thunder))),
                NeedsPassive(Passive.Thunder, new Augment("thunderStun", "감전", Rarity.Rare, 1, "번개에 맞은 적이 0.6초 기절합니다", E(ModOp.ThunderStun, 1))),
                NeedsPassive(Passive.Acid, new Augment("acidFlood", "대량 유출", Rarity.Advanced, 2, "산성 웅덩이 +1개, 크기 +25%", E(ModOp.PassiveExtra, 1, Passive.Acid), E(ModOp.PassiveSize, 0.25f, Passive.Acid))),
                NeedsPassive(Passive.Acid, new Augment("acidCorrode", "부식", Rarity.Rare, 1, "웅덩이 위의 적이 40% 느려지고, 웅덩이 피해 +30%", E(ModOp.AcidCorrode, 1))),
                NeedsPassive(Passive.Missile, new Augment("missileMore", "미사일 증설", Rarity.Advanced, 2, "미사일 +2발", E(ModOp.PassiveExtra, 2, Passive.Missile))),
                NeedsPassive(Passive.Missile, new Augment("missileCluster", "집속탄", Rarity.Rare, 1, "미사일 폭발이 작은 폭발 4개로 퍼집니다", E(ModOp.MissileCluster, 1))),
                NeedsPassive(Passive.Frost, new Augment("frostWide", "냉기 확산", Rarity.Common, 2, "냉기 폭발 범위 +30%", E(ModOp.PassiveSize, 0.3f, Passive.Frost))),
                NeedsPassive(Passive.Frost, new Augment("frostFreeze", "절대영도", Rarity.Rare, 1, "냉기에 맞은 적을 1초 동안 얼립니다", E(ModOp.FrostFreeze, 1))),
                NeedsPassive(Passive.Laser, new Augment("laserMore", "레이저 추가", Rarity.Advanced, 2, "레이저 광선 +1줄", E(ModOp.PassiveExtra, 1, Passive.Laser))),
                NeedsPassive(Passive.Laser, new Augment("laserPower", "고출력", Rarity.Rare, 1, "레이저 굵기 ×2, 피해 +30%", E(ModOp.LaserPower, 1))),
                NeedsPassive(Passive.Vortex, new Augment("vortexWide", "사건의 지평선", Rarity.Advanced, 1, "블랙홀 크기 +40%, 당기는 힘 +40%", E(ModOp.PassiveSize, 0.4f, Passive.Vortex))),
                NeedsPassive(Passive.Vortex, new Augment("vortexCollapse", "붕괴 폭발", Rarity.Rare, 1, "블랙홀이 사라질 때 크게 폭발합니다", E(ModOp.VortexCollapse, 1))),
                NeedsPassive(Passive.Meteor, new Augment("meteorMore", "유성우", Rarity.Advanced, 2, "운석 +2개", E(ModOp.PassiveExtra, 2, Passive.Meteor))),
                NeedsPassive(Passive.Meteor, new Augment("meteorBurn", "화염 지대", Rarity.Rare, 1, "운석이 떨어진 자리가 3초 동안 불탑니다", E(ModOp.MeteorBurn, 1))),
            };
        }
    }

    // ==================================================================== 카드

    enum CardKind { Augment, SkillUnlock, SkillLevel, SkillEvolve, PassiveUnlock, PassiveLevel, PassiveEvolve, Heal }

    class Card
    {
        static readonly string[] RarityNames = { "일반", "고급", "희귀", "전설" };

        public CardKind kind;
        public Augment augment;
        public int skill, passive;
        public string title, tag, desc;
        public Color color;

        public static Card ForAugment(Augment a, int have)
        {
            string tag = RarityNames[(int)a.rarity] + (a.maxStacks > 1 ? "   " + have + " / " + a.maxStacks : "");
            return new Card { kind = CardKind.Augment, augment = a, title = a.name, tag = tag, desc = a.desc, color = Palette.Rarity[(int)a.rarity] };
        }

        public static Card Unlock(int i)
        {
            string[] what =
            {
                "커서 방향으로 적을 모두 꿰뚫는 마력탄",
                "주변 원형 범위에 피해를 주고 적을 밀쳐냄",
                "커서 방향으로 돌진하며 지나간 적을 공격. 사용 후 2초간 공격 속도 +0.6",
            };
            string desc = what[i] + "\n\n피해: 공격력 × " + Coef(i, 1).ToString("0.##") + "\n쿨다운 " + Tune.Cooldown(i, 1, 1f).ToString("0.#") + "초";
            return new Card { kind = CardKind.SkillUnlock, skill = i, title = SkillState.Keys[i] + "  " + SkillState.Names[i], tag = "새 스킬", desc = desc, color = Palette.Skill };
        }

        public static Card LevelUp(int i, int lv, float cdMult)
        {
            string desc = "Lv " + lv + " → " + (lv + 1)
                + "\n\n피해: 공격력 × " + Coef(i, lv).ToString("0.##") + " → " + Coef(i, lv + 1).ToString("0.##")
                + "\n쿨다운 " + Tune.Cooldown(i, lv, cdMult).ToString("0.#") + "초 → " + Tune.Cooldown(i, lv + 1, cdMult).ToString("0.#") + "초";
            if (i == 1) desc += "\n범위 " + Tune.WRadius(lv).ToString("0.##") + " → " + Tune.WRadius(lv + 1).ToString("0.##");
            if (i == 2) desc += "\n거리 " + Tune.EDistance(lv).ToString("0.##") + " → " + Tune.EDistance(lv + 1).ToString("0.##");
            return new Card { kind = CardKind.SkillLevel, skill = i, title = SkillState.Keys[i] + "  " + SkillState.Names[i], tag = "스킬 강화", desc = desc, color = Palette.Skill };
        }

        public static Card Evolve(int i)
        {
            string[] what =
            {
                "피해 ×1.6\n끝까지 날아가면 폭발합니다",
                "피해 ×1.5, 범위 +0.7\n맞은 적은 0.6초 기절",
                "피해 ×1.5\n돌진 중 적을 처치하면 쿨다운 70% 회복",
            };
            return new Card { kind = CardKind.SkillEvolve, skill = i, title = SkillState.Keys[i] + "  " + SkillState.Names[i] + " 진화", tag = "진화", desc = what[i], color = Palette.Legendary };
        }

        public static Card PassiveUnlock(int id)
        {
            return new Card { kind = CardKind.PassiveUnlock, passive = id, title = Passive.Names[id], tag = "새 패시브 스킬 · 자동 발동", desc = Passive.What[id] + "\n\n" + Passive.Stats(id, 1), color = Passive.Colors[id] };
        }

        public static Card PassiveLevel(int id, int lv)
        {
            return new Card { kind = CardKind.PassiveLevel, passive = id, title = Passive.Names[id] + "  Lv " + (lv + 1), tag = "패시브 강화", desc = "Lv " + lv + " → " + (lv + 1) + "\n\n" + Passive.Stats(id, lv + 1), color = Passive.Colors[id] };
        }

        public static Card PassiveEvolve(int id)
        {
            return new Card { kind = CardKind.PassiveEvolve, passive = id, title = Passive.EvolveNames[id], tag = "진화 · " + Passive.Names[id], desc = Passive.EvolveWhat[id], color = Palette.Legendary };
        }

        public static Card Heal()
        {
            return new Card { kind = CardKind.Heal, title = "회복", tag = "기본", desc = "체력 +" + Tune.HealCard + "\n(고를 수 있는 증강이 더 없을 때 나옵니다)", color = Palette.Rarity[0] };
        }

        static float Coef(int i, int lv)
        {
            return (i == 0 ? Tune.QDamage(lv) : i == 1 ? Tune.WDamage(lv) : Tune.EDamage(lv)) * Tune.SkillDamageBoost;
        }
    }

    // ==================================================================== 입력

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

        public static bool LeftClickDown()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        public static bool RightClickHeld()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
            return Input.GetMouseButton(1);
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

        public static bool KeyDown(KeyCode key)
        {
#if USE_NEW_INPUT
            var kb = Keyboard.current;
            if (kb == null) return false;
            switch (key)
            {
                case KeyCode.Q: return kb.qKey.wasPressedThisFrame;
                case KeyCode.W: return kb.wKey.wasPressedThisFrame;
                case KeyCode.E: return kb.eKey.wasPressedThisFrame;
                case KeyCode.Alpha1: return kb.digit1Key.wasPressedThisFrame;
                case KeyCode.Alpha2: return kb.digit2Key.wasPressedThisFrame;
                case KeyCode.Alpha3: return kb.digit3Key.wasPressedThisFrame;
                case KeyCode.Alpha4: return kb.digit4Key.wasPressedThisFrame;
                case KeyCode.Return: return kb.enterKey.wasPressedThisFrame;
                case KeyCode.Space: return kb.spaceKey.wasPressedThisFrame;
                case KeyCode.Escape: return kb.escapeKey.wasPressedThisFrame;
                case KeyCode.Tab: return kb.tabKey.wasPressedThisFrame;
                default: return false;
            }
#else
            return Input.GetKeyDown(key);
#endif
        }
    }

    // ==================================================================== 그림

    // 에셋 파일 없이 흰 원·고리·격자 그림을 코드로 만든다. 색은 SpriteRenderer.color 로 입힌다.
    // 미션 3(그래픽 적용)에서 이 부분을 실제 에셋으로 바꾼다.
    static class Art
    {
        public static Sprite Circle { get; private set; }
        public static Sprite Ring { get; private set; }
        public static Sprite HeroIdle { get; private set; }
        public static Sprite HeroStep { get; private set; }
        static Material spriteMaterial;   // URP 프로젝트일 때만 쓰는 조명 없는 재질 (2D 조명 없이도 보이게)

        public static void Init()
        {
            var unlit = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            spriteMaterial = unlit != null ? new Material(unlit) : null;
            Circle = MakeDisc(128, 0f);
            Ring = MakeDisc(128, 0.86f);
            HeroIdle = MakePixelSprite(Concat(HeroTop, HeroLegsIdle));
            HeroStep = MakePixelSprite(Concat(HeroTop, HeroLegsStep));
            PaintMonsters();
            PaintSkillArt();
            XpDrop = PaintXpDrop().ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.45f), false);
        }

        // 도트 그림은 모두 1유닛 = 32픽셀 (플레이어와 같은 밀도)
        public const float PixelsPerUnit = 32f;

        // ---- 플레이어 캐릭터: 16×20 도트, 오른쪽을 보고 있다 (탕탕특공대 느낌의 2등신, 직접 그린 그림) ----
        const int HeroW = 16, HeroH = 20;
        const float HeroPixelsPerUnit = 32f;          // 화면에서 0.5 × 0.625 유닛 (맞는 판정 원보다 크게 보이도록)
        const float HeroPivotY = 0.3f;                // 그림 아래에서 30% 지점이 플레이어 위치
        public const float HeroFeet = HeroH * HeroPivotY / HeroPixelsPerUnit;
        public const float HeroGunX = 7.5f / HeroPixelsPerUnit;   // 총구: 가운데에서 오른쪽 7.5픽셀
        public const float HeroGunY = -0.5f / HeroPixelsPerUnit;

        static readonly string[] HeroTop =
        {
            "................",
            ".....KKKKKK.....",
            "...KKHHHHHHKK...",
            "..KHHHHHHHHHHK..",
            ".KHHHHHHHHHHHHK.",
            "KRRRRRRRRRRRRRK.",
            "RRKHSSSSSSSSHK..",
            "KRKSSSSWESWESK..",
            ".KKSSSSWESWESK..",
            "..KSSppSSSSpSK..",
            "...KSSSSSSSSK...",
            "....KKKKKKKK....",
            "...KBBBBBBBK....",
            "..KBBYBBBBSKKKK.",
            "..KbBYBBBSGGGCCK",
            "..KbBBBBBKKgKKK.",
            "...KTTTTTK......",
        };
        static readonly string[] HeroLegsIdle =
        {
            "...KPPKPPK......",
            "...KOOKOOK......",
            "....KK.KK.......",
        };
        static readonly string[] HeroLegsStep =
        {
            "..KPPK.KPPK.....",
            "..KOOK.KOOK.....",
            "...KK...KK......",
        };

        static Color32 HeroColor(char c)
        {
            switch (c)
            {
                case 'K': return new Color32(27, 21, 48, 255);    // 외곽선
                case 'H': return new Color32(92, 58, 38, 255);    // 머리카락
                case 'R': return new Color32(229, 72, 77, 255);   // 머리띠
                case 'S': return new Color32(255, 214, 176, 255); // 피부
                case 'p': return new Color32(255, 160, 150, 255); // 볼
                case 'W': return new Color32(255, 255, 255, 255); // 눈 흰자
                case 'E': return new Color32(27, 21, 48, 255);    // 눈동자
                case 'B': return new Color32(62, 123, 214, 255);  // 옷
                case 'b': return new Color32(44, 90, 168, 255);   // 옷 그림자
                case 'Y': return new Color32(255, 201, 74, 255);  // 옷 장식
                case 'T': return new Color32(255, 201, 74, 255);  // 허리띠
                case 'G': return new Color32(110, 114, 132, 255); // 총
                case 'g': return new Color32(70, 72, 88, 255);    // 손잡이
                case 'C': return new Color32(190, 140, 255, 255); // 마력 총구
                case 'P': return new Color32(52, 46, 74, 255);    // 바지
                case 'O': return new Color32(120, 78, 48, 255);   // 신발
                default: return new Color32(0, 0, 0, 0);
            }
        }

        static string[] Concat(string[] a, string[] b)
        {
            var all = new string[a.Length + b.Length];
            a.CopyTo(all, 0);
            b.CopyTo(all, a.Length);
            return all;
        }

        // 글자 그림(위쪽 줄이 먼저)을 스프라이트로. 도트가 번지지 않게 Point 필터
        static Sprite MakePixelSprite(string[] rows)
        {
            var tex = new Texture2D(HeroW, HeroH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[HeroW * HeroH];
            for (int row = 0; row < HeroH; row++)
                for (int x = 0; x < HeroW; x++)
                    pixels[(HeroH - 1 - row) * HeroW + x] = HeroColor(rows[row][x]);   // 텍스처는 아래 줄부터
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, HeroW, HeroH), new Vector2(0.5f, HeroPivotY), HeroPixelsPerUnit);
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

        // 지름 1유닛짜리 흰 원(inner = 0) 또는 고리(inner = 구멍 반지름 비율)
        static Sprite MakeDisc(int px, float inner)
        {
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[px * px];
            float c = (px - 1) / 2f, r = px / 2f - 1;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    if (inner > 0) a *= Mathf.Clamp01(d - inner * r + 0.5f);
                    pixels[y * px + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px);
        }

        // ---- 실험실 바닥 (직접 그린 그림) ----
        // FloorUnits × FloorUnits 유닛짜리 무늬 한 장을 이어 붙인다. 무늬 안의 모든 것은 가장자리에서 반대편으로 이어져서 이음매가 없다.
        public const int FloorUnits = 16;
        const int TilePx = 32;

        public static Transform CreateFloor(Transform parent)
        {
            var sr = CreateSprite("LabFloor", parent, PaintLabFloor(), Color.white, -10);
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.size = new Vector2(FloorUnits * 3, FloorUnits * 3);
            return sr.transform;
        }

        static Sprite PaintLabFloor()
        {
            int n = FloorUnits * TilePx;
            var f = new Color32[n * n];
            var rng = new System.Random(20261030);   // 항상 같은 바닥이 나오게 고정된 씨앗

            // 1) 바닥 타일: 대부분 평범한 타일, 가끔 철망·어두운 패널
            for (int ty = 0; ty < FloorUnits; ty++)
                for (int tx = 0; tx < FloorUnits; tx++)
                {
                    int roll = rng.Next(100);
                    bool grate = roll < 8, panel = roll >= 8 && roll < 14;
                    Color32 tile = grate ? C(0x18222A) : panel ? C(0x1D2A33) : Vary(C(0x23313B), rng.Next(-3, 4));
                    for (int y = 0; y < TilePx; y++)
                        for (int x = 0; x < TilePx; x++)
                        {
                            Color32 c = tile;
                            if (x == 0 || y == 0) c = C(0x111920);                                   // 줄눈
                            else if (x == 1 || y == TilePx - 1) c = Vary(tile, 9);                      // 빛 받는 모서리
                            else if (x == TilePx - 1 || y == 1) c = Vary(tile, -7);                     // 그림자 모서리
                            else if (grate && x >= 5 && x < 27 && y >= 5 && y < 27) c = y % 4 < 2 ? C(0x0C1216) : C(0x2B3843);
                            else if (grate && (x == 3 || x == 28) && (y == 3 || y == 28)) c = C(0x56687A); // 나사
                            Put(f, n, tx * TilePx + x, ty * TilePx + y, c);
                        }
                }

            // 2) 바닥에 칠한 통로 선 (무늬 끝까지 이어진다)
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 3; k++)
                {
                    Blend(f, n, i, 5 * TilePx + 14 + k, C(0x6F8596), 0.28f);
                    Blend(f, n, 11 * TilePx + 14 + k, i, C(0x6F8596), 0.28f);
                }

            // 3) 노랑·검정 경고 줄무늬
            for (int s = 0; s < 4; s++)
            {
                int x0 = rng.Next(FloorUnits) * TilePx, y0 = rng.Next(FloorUnits) * TilePx + 12;
                bool horizontal = s % 2 == 0;
                int len = TilePx * rng.Next(2, 5);
                for (int a = 0; a < len; a++)
                    for (int b = 0; b < 8; b++)
                    {
                        bool yellow = ((a + b) / 5) % 2 == 0;
                        Color32 c = b == 0 || b == 7 ? C(0x0E0E10) : yellow ? C(0xE2B33A) : C(0x1B1B1F);
                        if (horizontal) Put(f, n, x0 + a, y0 + b, c); else Put(f, n, y0 + b, x0 + a, c);
                    }
            }

            // 4) 둥근 배수구
            for (int d = 0; d < 3; d++)
            {
                int cx = rng.Next(FloorUnits) * TilePx + TilePx / 2, cy = rng.Next(FloorUnits) * TilePx + TilePx / 2;
                for (int y = -10; y <= 10; y++)
                    for (int x = -10; x <= 10; x++)
                    {
                        float r = Mathf.Sqrt(x * x + y * y);
                        if (r > 10) continue;
                        Color32 c = r > 8.5f ? C(0x495868) : r > 7.5f ? C(0x0A0F12) : (x + 20) % 3 == 0 ? C(0x3A4856) : C(0x0A0F12);
                        Put(f, n, cx + x, cy + y, c);
                    }
            }

            // 5) 바닥 위를 지나가는 전선 두 가닥 (사인 곡선, 주기가 무늬 크기와 맞아서 이어진다)
            for (int cable = 0; cable < 2; cable++)
            {
                int y0 = rng.Next(n), waves = 2 + cable;
                float amp = 20 + cable * 14;
                Color32 dark = cable == 0 ? C(0x24407A) : C(0x7A2E36), light = cable == 0 ? C(0x4C74C2) : C(0xC0505A);
                for (int x = 0; x < n; x++)
                {
                    int y = y0 + Mathf.RoundToInt(amp * Mathf.Sin(x * Mathf.PI * 2f * waves / n));
                    Put(f, n, x, y - 1, C(0x0B0F12));
                    Put(f, n, x, y, dark);
                    Put(f, n, x, y + 1, light);
                    Put(f, n, x, y + 2, dark);
                }
            }

            // 6) 쏟아진 초록 실험액 웅덩이
            for (int p = 0; p < 6; p++)
            {
                int cx = rng.Next(n), cy = rng.Next(n), blobs = rng.Next(3, 6);
                var mask = new bool[n * n];
                for (int b = 0; b < blobs; b++)
                {
                    int bx = cx + rng.Next(-16, 17), by = cy + rng.Next(-10, 11), r = rng.Next(4, 11);
                    for (int y = -r; y <= r; y++)
                        for (int x = -r; x <= r; x++)
                            if (x * x + y * y <= r * r) mask[Wrap(by + y, n) * n + Wrap(bx + x, n)] = true;
                }
                for (int i = 0; i < mask.Length; i++)
                    if (mask[i]) Blend(f, n, i % n, i / n, C(0x2FC46C), 0.5f);
                for (int g = 0; g < 4; g++) Put(f, n, cx + rng.Next(-8, 9), cy + rng.Next(-5, 6), C(0x8CF0B0));   // 반짝이
            }

            // 7) 금 간 자국
            for (int k = 0; k < 7; k++)
            {
                int x = rng.Next(n), y = rng.Next(n), steps = rng.Next(16, 40);
                for (int i = 0; i < steps; i++)
                {
                    Put(f, n, x, y, C(0x0E1418));
                    x += rng.Next(2) == 0 ? 1 : 0;
                    y += rng.Next(-1, 2);
                }
            }

            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            tex.SetPixels32(f);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), TilePx, 0, SpriteMeshType.FullRect);
        }

        static int Wrap(int v, int n) { return ((v % n) + n) % n; }
        static void Put(Color32[] f, int n, int x, int y, Color32 c) { f[Wrap(y, n) * n + Wrap(x, n)] = c; }

        static void Blend(Color32[] f, int n, int x, int y, Color32 c, float a)
        {
            int i = Wrap(y, n) * n + Wrap(x, n);
            Color32 o = f[i];
            f[i] = new Color32((byte)(o.r + (c.r - o.r) * a), (byte)(o.g + (c.g - o.g) * a), (byte)(o.b + (c.b - o.b) * a), 255);
        }

        static Color32 Vary(Color32 c, int d)
        {
            return new Color32((byte)Mathf.Clamp(c.r + d, 0, 255), (byte)Mathf.Clamp(c.g + d, 0, 255), (byte)Mathf.Clamp(c.b + d, 0, 255), c.a);
        }

        public static Color32 C(int rgb)
        {
            return new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);
        }

        // ---- 몬스터: 실험실에서 탈출한 귀여운 실험체들 (직접 그린 그림). 모두 오른쪽을 보고 있다 ----
        public const int LookSlime = 0, LookFlask = 1, LookBrute = 2, LookGoggle = 3, LookMutant = 4;
        public const int EliteOffset = 5;   // 5~7: 황금 엘리트 버전 (슬라임·플라스크·덩치)

        public static Sprite[] Monster { get; private set; }
        public static Sprite[] MonsterFlash { get; private set; }   // 맞았을 때 번쩍이는 흰 실루엣
        public static Sprite XpDrop { get; private set; }

        static readonly Color32 Ink = new Color32(27, 21, 48, 255);
        static readonly Color32 White = new Color32(255, 255, 255, 255);

        struct Tone
        {
            public Color32 body, shade, light;
            public Tone(int body, int shade, int light) { this.body = C(body); this.shade = C(shade); this.light = C(light); }
        }

        static readonly Tone Gold = new Tone(0xF0B429, 0xC08410, 0xFFE38A);

        static void PaintMonsters()
        {
            var canvases = new[]
            {
                PaintSlime(new Tone(0x8A5CC8, 0x6A3FA8, 0xC2A2F2)),
                PaintFlask(new Tone(0x3FC7A0, 0x2A9A7A, 0xA8F5DD)),
                PaintBrute(new Tone(0xC0604F, 0x93422F, 0xE89A84)),
                PaintGoggleSlime(),
                PaintMutant(),
                PaintSlime(Gold),
                PaintFlask(Gold),
                PaintBrute(Gold),
            };
            Monster = new Sprite[canvases.Length];
            MonsterFlash = new Sprite[canvases.Length];
            for (int i = 0; i < canvases.Length; i++)
            {
                Monster[i] = canvases[i].ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.45f), false);
                MonsterFlash[i] = canvases[i].ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.45f), true);
            }
        }

        // 눈: 흰자에 오른쪽을 보는 눈동자
        static void Eye(PixelCanvas c, int x, int y, int size)
        {
            c.Rect(x, y, size, size, White);
            if (size <= 2) c.Rect(x + size - 1, y, 1, size, Ink);
            else { c.Rect(x + 1, y + 1, size - 1, size - 1, Ink); c.Set(x + 1, y + 1, White); }
        }

        // 일반 몬스터: 보라 슬라임 (13×12)
        static PixelCanvas PaintSlime(Tone t)
        {
            var c = new PixelCanvas(13, 12);
            c.Ellipse(6.5f, 7.6f, 5.6f, 3.6f, t.body);
            c.Ellipse(6.5f, 5.2f, 4.4f, 4.0f, t.body);
            c.Shade(t.shade, (x, y) => y >= 9);
            c.Ellipse(4.3f, 3.6f, 1.3f, 1.0f, t.light);
            Eye(c, 6, 5, 2);
            Eye(c, 9, 5, 2);
            c.Set(8, 8, Ink);
            c.Set(9, 8, Ink);
            c.Outline(Ink);
            return c;
        }

        // 빠른 몬스터: 다리 달린 플라스크 (11×13), 액체 색이 몸 색
        static PixelCanvas PaintFlask(Tone t)
        {
            var c = new PixelCanvas(11, 13);
            Color32 glass = C(0xD8EEF5), rim = C(0xA9CBD6);
            c.Rect(4, 1, 3, 4, glass);
            c.Rect(3, 1, 5, 1, rim);
            for (int y = 5; y <= 10; y++)
            {
                float half = 1.6f + (y - 5) * 0.65f;
                for (int x = 0; x < 11; x++)
                    if (Mathf.Abs(x + 0.5f - 5.5f) <= half) c.Set(x, y, glass);
            }
            c.Shade(t.body, (x, y) => y >= 7);
            c.Shade(t.shade, (x, y) => y == 10);
            c.Set(5, 3, t.light);
            c.Set(3, 9, t.light);
            Eye(c, 4, 7, 2);
            Eye(c, 7, 7, 2);
            c.Set(3, 11, Ink);
            c.Set(7, 11, Ink);
            c.Outline(Ink);
            return c;
        }

        // 덩치 몬스터: 뿔 달린 고기 덩어리 (16×16)
        static PixelCanvas PaintBrute(Tone t)
        {
            var c = new PixelCanvas(16, 16);
            Color32 horn = C(0xF2E2C0);
            c.Ellipse(8f, 9.2f, 7.0f, 5.8f, t.body);
            c.Rect(3, 2, 2, 3, horn); c.Set(3, 1, horn);
            c.Rect(11, 2, 2, 3, horn); c.Set(12, 1, horn);
            c.Shade(t.shade, (x, y) => y >= 12);
            c.Ellipse(5f, 6.5f, 1.6f, 1.0f, t.light);
            Eye(c, 7, 7, 2);
            Eye(c, 11, 7, 2);
            c.Set(6, 6, Ink); c.Set(7, 6, Ink);     // 화난 눈썹
            c.Set(12, 6, Ink); c.Set(13, 6, Ink);
            c.Rect(7, 11, 6, 2, Ink);               // 입
            c.Set(8, 11, White); c.Set(11, 11, White);
            c.Outline(Ink);
            return c;
        }

        // 보스: 고글 쓴 분홍 슬라임 (28×26)
        static PixelCanvas PaintGoggleSlime()
        {
            var c = new PixelCanvas(28, 26);
            Color32 body = C(0xE0508C), shade = C(0xB23A6C), light = C(0xFF9CC4);
            Color32 rim = C(0x9AA3B5), lens = C(0x6BD5FF), strap = C(0x3A3352);
            c.Ellipse(14f, 16.5f, 12.6f, 8.4f, body);
            c.Ellipse(14f, 11f, 9.5f, 8.5f, body);
            c.Shade(shade, (x, y) => y >= 21);
            c.Ellipse(8.5f, 5.5f, 2.4f, 1.5f, light);
            c.Shade(strap, (x, y) => y == 10 || y == 11);
            c.Ellipse(11f, 11f, 3.6f, 3.6f, rim);
            c.Ellipse(19f, 11f, 3.6f, 3.6f, rim);
            c.Ellipse(11f, 11f, 2.5f, 2.5f, lens);
            c.Ellipse(19f, 11f, 2.5f, 2.5f, lens);
            c.Rect(11, 11, 2, 2, Ink); c.Rect(19, 11, 2, 2, Ink);
            c.Set(10, 10, White); c.Set(18, 10, White);
            c.Set(11, 17, Ink); c.Rect(12, 18, 7, 2, Ink); c.Set(19, 17, Ink);   // 웃는 입
            c.Rect(14, 19, 3, 1, C(0xFF7AA8));
            c.Outline(Ink);
            return c;
        }

        // 대형 보스: 뇌가 든 유리 돔을 쓴 주황 돌연변이 (42×38)
        static PixelCanvas PaintMutant()
        {
            var c = new PixelCanvas(42, 38);
            Color32 body = C(0xFF7A3D), shade = C(0xD2552A), light = C(0xFFB27E), spot = C(0xE0662F);
            Color32 glass = new Color32(207, 232, 240, 210), brain = C(0xFF9FC0), brainLine = C(0xD9668C), rim = C(0x7D8797);
            c.Ellipse(21f, 25f, 19.5f, 11.6f, body);
            c.Ellipse(21f, 19f, 14f, 10f, body);
            c.Ellipse(21f, 10.5f, 9f, 8.6f, glass);
            c.Ellipse(21f, 12f, 6.5f, 4.8f, brain);
            c.Set(18, 10, brainLine); c.Set(19, 11, brainLine); c.Set(21, 9, brainLine); c.Set(22, 10, brainLine);
            c.Set(24, 12, brainLine); c.Set(17, 13, brainLine); c.Set(21, 14, brainLine);
            c.Rect(15, 4, 1, 4, White); c.Rect(16, 3, 1, 2, White);   // 유리 반사광
            c.Rect(12, 18, 18, 2, rim);
            c.Shade(shade, (x, y) => y >= 33);
            c.Ellipse(8f, 27f, 2f, 1.5f, spot); c.Ellipse(34f, 28f, 2.4f, 1.6f, spot); c.Ellipse(30f, 34f, 1.6f, 1.1f, spot);
            c.Ellipse(9f, 22f, 2.2f, 1.2f, light);
            Eye(c, 12, 22, 3);
            Eye(c, 19, 21, 3);
            Eye(c, 26, 22, 3);
            c.Rect(12, 28, 18, 5, Ink);
            for (int x = 13; x < 28; x += 3) { c.Set(x, 28, White); c.Set(x + 1, 28, White); c.Set(x, 29, White); }
            for (int x = 14; x < 29; x += 3) { c.Set(x, 32, White); c.Set(x + 1, 32, White); c.Set(x + 1, 31, White); }
            c.Outline(Ink);
            return c;
        }

        // ---- 스킬 효과용 그림 ----
        public static Sprite Glow { get; private set; }      // 부드럽게 퍼지는 빛 (반짝이·폭발)
        public static Sprite Pixel { get; private set; }     // 1×1 유닛 사각형 (번개·레이저)
        public static Sprite Star { get; private set; }      // 눈송이·반짝이
        public static Sprite Shuriken { get; private set; }
        public static Sprite Missile { get; private set; }
        public static Sprite Meteor { get; private set; }
        public static Sprite Spiral { get; private set; }    // 블랙홀
        public static Sprite Puddle { get; private set; }    // 산성·불 장판, 그을린 자국 (흰색으로 그려 색을 입힌다)

        static void PaintSkillArt()
        {
            Glow = MakeGlow(64);
            var px = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            px.SetPixels32(new[] { White, White, White, White });
            px.Apply();
            Pixel = Sprite.Create(px, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 2f);

            Star = FromRows(new[]
            {
                "...W...",
                "...W...",
                ".W.W.W.",
                "WWWWWWW",
                ".W.W.W.",
                "...W...",
                "...W...",
            }).ToSprite(PixelsPerUnit / 4f, new Vector2(0.5f, 0.5f), false);

            Shuriken = FromRows(new[]
            {
                ".....K.....",
                "....KWK....",
                "....KLK....",
                "...KLLLK...",
                ".KKKLMLKKK.",
                "KWLLMOMLLWK",
                ".KKKLMLKKK.",
                "...KLLLK...",
                "....KLK....",
                "....KWK....",
                ".....K.....",
            }).ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.5f), false);

            Missile = FromRows(new[]
            {
                "FF.........",
                "KFKKKKKK...",
                "KKWWWWWWRK.",
                "..LWWWWWRRK",
                "KKLLLLLLRK.",
                "KFKKKKKK...",
                "FF.........",
            }).ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.5f), false);

            var rock = new PixelCanvas(12, 12);
            rock.Ellipse(6f, 6f, 5f, 4.6f, C(0x6B4A3A));
            rock.Shade(C(0x4A3128), (x, y) => x + y >= 13);
            rock.Ellipse(4.5f, 4.2f, 1.6f, 1.1f, C(0x9A7058));
            rock.Set(6, 6, C(0xFF9A3C)); rock.Set(7, 7, C(0xFF9A3C)); rock.Set(5, 8, C(0xFFD166)); rock.Set(8, 5, C(0xFF9A3C));
            rock.Outline(Ink);
            Meteor = rock.ToSprite(PixelsPerUnit, new Vector2(0.5f, 0.5f), false);

            Spiral = MakeSpiral(48);
            Puddle = MakePuddle(24);
        }

        static PixelCanvas FromRows(string[] rows)
        {
            var c = new PixelCanvas(rows[0].Length, rows.Length);
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                {
                    switch (rows[y][x])
                    {
                        case 'K': c.Set(x, y, Ink); break;
                        case 'W': c.Set(x, y, White); break;
                        case 'L': c.Set(x, y, C(0xC9D3E0)); break;   // 밝은 강철
                        case 'M': c.Set(x, y, C(0x8A96A8)); break;   // 어두운 강철
                        case 'O': c.Set(x, y, C(0x2A3140)); break;   // 표창 가운데 구멍
                        case 'R': c.Set(x, y, C(0xE5484D)); break;   // 미사일 머리
                        case 'F': c.Set(x, y, C(0xC0392B)); break;   // 미사일 날개
                    }
                }
            return c;
        }

        // 가운데가 밝고 가장자리로 갈수록 투명해지는 흰 빛
        static Sprite MakeGlow(int px)
        {
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[px * px];
            float c = (px - 1) / 2f, r = px / 2f;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / r;
                    float a = Mathf.Clamp01(1f - d);
                    pixels[y * px + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px);
        }

        // 블랙홀: 세 갈래 소용돌이 팔
        static Sprite MakeSpiral(int px)
        {
            var c = new PixelCanvas(px, px);
            float mid = px / 2f, r = px / 2f - 1;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    float dx = x + 0.5f - mid, dy = y + 0.5f - mid;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                    if (d > 1f) continue;
                    float arm = Mathf.Sin(3f * Mathf.Atan2(dy, dx) + d * 9f);
                    if (d < 0.18f) c.Set(x, y, new Color32(20, 10, 40, 255));            // 가운데 검은 구멍
                    else if (arm > 0.35f) c.Set(x, y, new Color32(255, 255, 255, (byte)(255 * (1f - d * 0.6f))));
                    else if (arm > -0.2f) c.Set(x, y, new Color32(160, 150, 190, (byte)(150 * (1f - d))));
                }
            return c.ToSprite(px, new Vector2(0.5f, 0.5f), false);
        }

        // 웅덩이: 동그라미 몇 개를 겹친 얼룩 + 밝은 테두리
        static Sprite MakePuddle(int px)
        {
            var c = new PixelCanvas(px, px);
            Color32 body = new Color32(220, 220, 220, 220), rim = new Color32(255, 255, 255, 255);
            c.Ellipse(12f, 12.5f, 9f, 8f, body);
            c.Ellipse(7f, 9f, 5f, 4.5f, body);
            c.Ellipse(17f, 15f, 5.5f, 5f, body);
            c.Ellipse(15f, 7f, 4.5f, 4f, body);
            c.Outline(rim);
            c.Set(9, 9, rim); c.Set(10, 9, rim); c.Set(15, 14, rim);
            return c.ToSprite(px, new Vector2(0.5f, 0.5f), false);
        }

        // 경험치: 실험 혈청 방울 (흰색으로 그리고 SpriteRenderer.color 로 색을 입힌다)
        static PixelCanvas PaintXpDrop()
        {
            var c = new PixelCanvas(7, 9);
            Color32 body = new Color32(235, 235, 235, 255), shade = new Color32(170, 170, 180, 255);
            c.Set(3, 1, body);
            c.Rect(3, 2, 1, 1, body);
            c.Rect(2, 3, 3, 1, body);
            c.Ellipse(3.5f, 5.6f, 2.6f, 2.5f, body);
            c.Shade(shade, (x, y) => x >= 4 && y >= 6);
            c.Set(2, 4, White);
            c.Outline(new Color32(40, 44, 60, 255));
            return c;
        }
    }

    // ==================================================================== 도트 그리기 도구

    // 작은 도트 그림을 코드로 그리는 캔버스. 좌표는 왼쪽 위가 (0, 0)
    class PixelCanvas
    {
        public readonly int w, h;
        readonly Color32[] px;

        public PixelCanvas(int w, int h)
        {
            this.w = w; this.h = h;
            px = new Color32[w * h];
        }

        bool Inside(int x, int y) { return x >= 0 && y >= 0 && x < w && y < h; }
        int Index(int x, int y) { return (h - 1 - y) * w + x; }   // 텍스처는 아래 줄부터 저장된다

        public void Set(int x, int y, Color32 c) { if (Inside(x, y)) px[Index(x, y)] = c; }
        bool Filled(Color32[] a, int x, int y) { return Inside(x, y) && a[Index(x, y)].a > 0; }

        public void Ellipse(float cx, float cy, float rx, float ry, Color32 c)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                    if (dx * dx + dy * dy <= 1f) Set(x, y, c);
                }
        }

        public void Rect(int x0, int y0, int rw, int rh, Color32 c)
        {
            for (int y = y0; y < y0 + rh; y++)
                for (int x = x0; x < x0 + rw; x++) Set(x, y, c);
        }

        // 이미 칠한 곳 중 조건에 맞는 칸만 다시 칠한다
        public void Shade(Color32 c, System.Func<int, int, bool> where)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[Index(x, y)].a > 0 && where(x, y)) Set(x, y, c);
        }

        // 칠한 모양 바깥쪽에 1픽셀 외곽선
        public void Outline(Color32 c)
        {
            var before = (Color32[])px.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (!Filled(before, x, y) && (Filled(before, x - 1, y) || Filled(before, x + 1, y) || Filled(before, x, y - 1) || Filled(before, x, y + 1)))
                        Set(x, y, c);
        }

        // silhouette = true 면 모양만 흰색으로 (맞았을 때 번쩍임용)
        public Sprite ToSprite(float ppu, Vector2 pivot, bool silhouette)
        {
            var pixels = (Color32[])px.Clone();
            if (silhouette)
                for (int i = 0; i < pixels.Length; i++)
                    if (pixels[i].a > 0) pixels[i] = new Color32(255, 255, 255, pixels[i].a);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, ppu);
        }
    }
}
